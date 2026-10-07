using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace PomoCC
{
    /// <summary>一段专注的最终结果。不再用单个 bool 混着表达"发过邮件"和"结果"。</summary>
    public enum SessionResult
    {
        Running,      // 进行中
        Completed,    // 正常走完且没违规
        Abandoned,    // 中途放弃
        Violated      // 程序超时（告过状）
    }

    /// <summary>
    /// 一个被监视程序的**实例**。身份 = Exe + PID + 启动时间：
    /// PID 会被系统复用，靠启动时间才能区分"同一个 exe 又开了一次"。
    /// </summary>
    public class ProcessInstance
    {
        public int Pid { get; set; }
        public DateTime StartTime { get; set; }
        public double Seconds { get; set; }        // 本实例在本次专注内累计的秒数
        public DateTime FirstSeen { get; set; }
        public DateTime LastSeen { get; set; }
    }

    public class FocusSession
    {
        public DateTime StartedAt { get; set; }
        public int PlannedSeconds { get; set; }
        public int ElapsedSeconds { get; set; }
        public SessionResult Result { get; set; }
        /// <summary>是否已经触发过告状（与 Result 分开：结果可能是"违规后仍走完"）。</summary>
        public bool ReportTriggered { get; set; }
        public string ReportReason { get; set; }
        public Dictionary<string, WatchProcess> Watched { get; set; }
        public List<string> Events { get; set; }
        /// <summary>本段专注固定使用的配置快照，避免后台读到一半更新的配置。</summary>
        public Settings Snapshot { get; set; }
        /// <summary>
        /// 本段专注固定使用的**活动规则**快照。
        /// 设置窗口在专注期间改了规则只影响下一段 —— 这一段继续用开始时的规则。
        /// </summary>
        public List<WatchRule> RuleSnapshot { get; set; }
        /// <summary>后台线程只碰这些字段，界面通过 Supervisor.Snapshot() 读。</summary>
        internal double ElapsedAcc;
        internal double LastMarkSeconds;
        internal double LastSampleSeconds;

        public FocusSession()
        {
            Watched = new Dictionary<string, WatchProcess>();
            Events = new List<string>();
            Result = SessionResult.Running;
        }

        /// <summary>兼容旧调用：是否已经告过状。</summary>
        public bool Reported { get { return ReportTriggered; } }
    }

    /// <summary>时钟抽象：正式跑用单调时钟（Stopwatch），自检用可任意推进的假时钟。</summary>
    internal interface IClock
    {
        double Seconds { get; }     // 单调递增秒数，只用于算时间差
        DateTime Now { get; }       // 墙上时间，只用于显示与记录
    }

    internal class StopwatchClock : IClock
    {
        private readonly Stopwatch sw = Stopwatch.StartNew();
        public double Seconds { get { return sw.Elapsed.TotalSeconds; } }
        public DateTime Now { get { return DateTime.Now; } }
    }

    /// <summary>自检专用：时间完全由测试推进，睡眠/卡顿可以瞬间模拟。</summary>
    internal class FakeClock : IClock
    {
        public double Value;
        public DateTime Base = new DateTime(2026, 1, 1, 9, 0, 0);
        public double Seconds { get { return Value; } }
        public DateTime Now { get { return Base.AddSeconds(Value); } }
        public void Advance(double seconds) { Value += seconds; }
    }

    /// <summary>
    /// 番茄钟 + 监督规则 + 告状判定的核心逻辑。
    ///
    /// 计时不依赖 WinForms 的 Timer：
    ///   · 自己用后台线程 + 单调时钟（Stopwatch）推进，界面卡住不影响专注计时；
    ///   · 界面只通过 Snapshot() 读状态，再用 BeginInvoke 更新控件；
    ///   · 所有状态变更走同一把锁，事件回调一律在锁外触发。
    /// </summary>
    public class Supervisor : IDisposable
    {
        public Settings Settings;
        public DailyStats Stats;
        public FocusSession Session;

        /// <summary>状态变化（可能来自后台线程，界面需自行 BeginInvoke）。</summary>
        public event Action Changed;
        /// <summary>告状邮件已生成（含未发送的预览内容）。</summary>
        public event Action<HistoryEntry> ReportReady;
        /// <summary>给人看的提示消息。</summary>
        public event Action<string> Notified;

        /// <summary>间隔超过这个秒数就认为中间发生了睡眠/挂起，该段不计入专注。</summary>
        public const double SleepGapSeconds = 15.0;

        private readonly object gate = new object();
        private readonly List<Action> pending = new List<Action>();

        private List<WatchRule> watchRules = new List<WatchRule>();
        private Thread loop;
        private volatile bool loopRunning;
        private bool powerHooked;

        /// <summary>自检可以换成 FakeClock。</summary>
        internal IClock Clock = new StopwatchClock();

        /// <summary>自检专用：替换进程枚举，注入"可控的正在运行的进程"。</summary>
        internal Func<List<WatchRule>, List<WatchProcess>> ProcessScan = ProcessMonitor.Scan;

        /// <summary>自检专用：只允许手工 Tick()，不启动后台线程（避免假时钟被真线程推进）。</summary>
        internal bool ManualTickOnly;

        public Supervisor(Settings settings, DailyStats stats)
        {
            Settings = settings;
            Stats = stats;
            RefreshWatchList();
        }

        public bool IsFocusing { get { lock (gate) { return Session != null; } } }

        public void RefreshWatchList()
        {
            lock (gate) { watchRules = SettingsValidator.ActiveRules(Settings); }
        }

        /// <summary>
        /// 统一的应用设置入口（界面不许直接写 Settings / Stats）。
        ///
        /// · 专注中也能保存，但**本段 session 继续用它开始时的规则与计划时间**，新设置下一段生效；
        /// · 统计与磁盘上的当天计数做"取较大值"合并，绝不把内存里正在累计的计数覆盖掉。
        /// </summary>
        public void ApplySettings(Settings settings)
        {
            if (settings == null) return;
            lock (gate)
            {
                Settings = settings;
                watchRules = SettingsValidator.ActiveRules(Settings);

                DailyStats disk = null;
                try { disk = Store.LoadToday(); }
                catch { }
                MergeStatsLocked(disk);

                Queue(Raise);
            }
            FlushPending();
        }

        /// <summary>合并磁盘上的当天统计：只补高不覆盖，保证内存里正在累计的计数不丢。</summary>
        private void MergeStatsLocked(DailyStats disk)
        {
            if (disk == null) return;
            if (Stats == null) { Stats = disk; return; }
            if (disk.Date != Stats.Date) return;          // 跨天：不动内存里的这一天
            Stats.CompletedCount = Math.Max(Stats.CompletedCount, disk.CompletedCount);
            Stats.AbandonedCount = Math.Max(Stats.AbandonedCount, disk.AbandonedCount);
            Stats.ReportCount = Math.Max(Stats.ReportCount, disk.ReportCount);
            Stats.FocusSeconds = Math.Max(Stats.FocusSeconds, disk.FocusSeconds);
            Stats.ViolationSeconds = Math.Max(Stats.ViolationSeconds, disk.ViolationSeconds);
        }

        /// <summary>本段 session 正在使用的规则快照（只读副本，供自检与界面展示）。</summary>
        public List<WatchRule> SessionRules
        {
            get
            {
                lock (gate)
                {
                    List<WatchRule> list = new List<WatchRule>();
                    if (Session != null && Session.RuleSnapshot != null) list.AddRange(Session.RuleSnapshot);
                    return list;
                }
            }
        }

        public List<WatchRule> WatchRules { get { lock (gate) { return new List<WatchRule>(watchRules); } } }

        public List<string> WatchNames
        {
            get
            {
                lock (gate)
                {
                    List<string> names = new List<string>();
                    for (int i = 0; i < watchRules.Count; i++) names.Add(watchRules[i].Exe);
                    return names;
                }
            }
        }

        // ------------------------------------------------------------ 锁外事件

        private void Queue(Action a)
        {
            if (a != null) pending.Add(a);
        }

        /// <summary>把锁内排队的副作用（事件、提示）拿到锁外执行，避免 UI 回调造成死锁。</summary>
        private void FlushPending()
        {
            Action[] arr;
            lock (gate)
            {
                if (pending.Count == 0) return;
                arr = pending.ToArray();
                pending.Clear();
            }
            for (int i = 0; i < arr.Length; i++)
            {
                try { arr[i](); }
                catch { }
            }
        }

        public void Raise()
        {
            Action h = Changed;
            if (h != null) h();
        }

        private void Notify(string msg)
        {
            Action<string> h = Notified;
            if (h != null) h(msg);
        }

        // ------------------------------------------------------------ 专注生命周期

        public void StartFocus()
        {
            lock (gate)
            {
                if (Session != null) return;

                FocusSession s = new FocusSession();
                s.StartedAt = Clock.Now;
                s.Snapshot = Settings.Copy();                 // 本段专注固定配置
                s.RuleSnapshot = SettingsValidator.ActiveRules(s.Snapshot);   // 本段专用规则（副本）
                s.PlannedSeconds = s.Snapshot.FocusMinutes * 60;
                s.LastMarkSeconds = Clock.Seconds;
                s.LastSampleSeconds = Clock.Seconds;
                s.Events.Add(string.Format("{0:HH:mm:ss} 开始专注，目标 {1} 分钟，监督 {2} 个程序",
                    Clock.Now, s.Snapshot.FocusMinutes, s.RuleSnapshot.Count));
                Session = s;

                Store.Log(string.Format("开始专注：{0} 分钟，监督 {1} 个程序",
                    s.Snapshot.FocusMinutes, s.RuleSnapshot.Count));
                Queue(Raise);
            }
            FlushPending();
            StartLoop();
        }

        /// <summary>后台循环推进一步（正常由后台线程调用，自检里手工调用）。</summary>
        public void Tick()
        {
            lock (gate) { TickLocked(); }
            FlushPending();
        }

        private void TickLocked()
        {
            FocusSession s = Session;
            if (s == null) return;

            double nowSec = Clock.Seconds;
            double delta = nowSec - s.LastMarkSeconds;
            s.LastMarkSeconds = nowSec;
            if (delta < 0) delta = 0;

            if (delta > SleepGapSeconds)
            {
                // 睡眠/休眠/被挂起（或电源事件丢失、线程恢复延迟）：
                // 这段时间既不算专注，**也不采样** —— 否则会把未知的长时间算成
                // 监督程序的运行时间，可能直接导致误判违规。
                s.Events.Add(string.Format("{0:HH:mm:ss} 检测到中断 {1:0} 秒（可能睡眠），本段不计时、不采样",
                    Clock.Now, delta));
                Store.Log(string.Format("检测到计时中断 {0:0} 秒：不计入专注，并跳过本次采样", delta));
                s.LastSampleSeconds = nowSec;         // 重置采样基准，下一轮从零开始
                return;
            }

            s.ElapsedAcc += delta;
            s.ElapsedSeconds = (int)Math.Round(s.ElapsedAcc);

            if (nowSec - s.LastSampleSeconds >= s.Snapshot.SampleSeconds)
            {
                DoSampleLocked(s, nowSec);
                s.LastSampleSeconds = nowSec;
            }

            // 采样过程中可能已经判违规并结束会话
            s = Session;
            if (s == null) return;

            if (s.ElapsedSeconds >= s.PlannedSeconds)
            {
                FinishLocked(true);
            }
        }

        /// <summary>采样：按实例累计，按 exe 汇总判断规则。用的是**本段 session 的规则快照**。</summary>
        private void DoSampleLocked(FocusSession s, double nowSec)
        {
            List<WatchRule> rules = s.RuleSnapshot;
            if (rules == null || rules.Count == 0) return;

            double gap = nowSec - s.LastSampleSeconds;
            if (gap <= 0 || gap > s.Snapshot.SampleSeconds * 3) gap = s.Snapshot.SampleSeconds;

            List<WatchProcess> running = ProcessScan(rules);
            // 同一个 exe 同时跑多个实例时，**这一轮采样只给它的规则预算累加一次**（按墙上时间），
            // 否则开两个实例会让"允许玩多久"的预算被双倍消耗。
            // 每个实例自己的运行时长仍然各自累计，仅用于显示。
            HashSet<string> creditedThisSample = new HashSet<string>();
            for (int i = 0; i < running.Count; i++)
            {
                WatchProcess w = running[i];
                if (s.Snapshot.OnlyCountAfterStart && w.StartTime != DateTime.MinValue && w.StartTime < s.StartedAt)
                    continue;

                WatchProcess agg;
                if (!s.Watched.TryGetValue(w.Exe, out agg))
                {
                    w.FirstSeen = Clock.Now;
                    w.SessionSeconds = 0;
                    s.Watched[w.Exe] = w;
                    agg = w;
                    s.Events.Add(string.Format("{0:HH:mm:ss} 发现 {1} 正在运行（规则 {2} 分钟）",
                        Clock.Now, w.Label, w.LimitMinutes));
                    Store.Log("专注期间发现 " + w.Exe);
                }
                else
                {
                    if (agg.LimitMinutes <= 0) agg.LimitMinutes = w.LimitMinutes;
                    agg.Name = w.Name;              // 用户可能在会话期间改了名称
                    agg.DisplayName = w.DisplayName;
                }

                // 实例身份 = PID + 启动时间；同一个 exe 多次启动会得到多个实例
                ProcessInstance inst = agg.FindInstance(w.Pid, w.StartTime);
                if (inst == null)
                {
                    inst = new ProcessInstance();
                    inst.Pid = w.Pid;
                    inst.StartTime = w.StartTime;
                    inst.FirstSeen = Clock.Now;
                    agg.Instances.Add(inst);
                    if (agg.Instances.Count > 1)
                    {
                        s.Events.Add(string.Format("{0:HH:mm:ss} {1} 又启动了一个实例（PID {2}）",
                            Clock.Now, w.Label, w.Pid));
                    }
                }

                inst.Seconds += gap;
                inst.LastSeen = Clock.Now;
                agg.LastSeen = Clock.Now;
                agg.Pid = w.Pid;                       // 兼容旧字段：最近一次看到的 PID
                if (agg.StartTime == DateTime.MinValue) agg.StartTime = w.StartTime;
                if (!creditedThisSample.Contains(agg.Exe))
                {
                    agg.SessionSeconds += (int)Math.Round(gap);
                    creditedThisSample.Add(agg.Exe);
                }
            }

            if (s.ReportTriggered) return;

            // 按「每条规则各自的时长」判定，符合表格里逐行配置的语义（按 exe 汇总）
            foreach (KeyValuePair<string, WatchProcess> kv in s.Watched)
            {
                WatchProcess w = kv.Value;
                if (!RuleExceeded(w)) continue;
                Stats.ViolationSeconds += w.SessionSeconds;
                SaveStats();
                ReportNowLocked(string.Format("专注期间偷玩超时（{0} 已跑 {1}，超过规则 {2} 分钟）",
                    w.Label, FormatDuration(w.SessionSeconds),
                    w.LimitMinutes > 0 ? w.LimitMinutes : DefaultLimitMinutes));
                break;
            }
        }

        /// <summary>默认规则时长（分钟）：规则里没填时的兜底。</summary>
        public const int DefaultLimitMinutes = 3;

        /// <summary>该程序是否已经超过它自己那条规则的时长。抽出来便于单测。</summary>
        public static bool RuleExceeded(WatchProcess w)
        {
            if (w == null) return false;
            int limit = w.LimitMinutes > 0 ? w.LimitMinutes : DefaultLimitMinutes;
            return w.SessionSeconds >= limit * 60;
        }

        /// <summary>正常走完（到点了）。</summary>
        public void Complete()
        {
            lock (gate) { FinishLocked(true); }
            FlushPending();
        }

        /// <summary>中途放弃。</summary>
        public void Abandon()
        {
            lock (gate) { FinishLocked(false); }
            FlushPending();
        }

        /// <summary>
        /// 结束本次专注。
        /// 结果规则（对应任务清单 1.4）：
        ///   · 到点且没违规        → Completed，发完成提醒
        ///   · 到点但已经违规      → 仍是 Violated，不再显示"未告状"，也不重复发信
        ///   · 中途放弃且没违规    → Abandoned，发一次告状
        ///   · 中途放弃但已经违规  → 仍是 Violated，不重复发信
        /// </summary>
        private void FinishLocked(bool reachedGoal)
        {
            FocusSession s = Session;
            if (s == null) return;

            if (s.Result == SessionResult.Violated)
            {
                // 已经告过状：既不重复发信，也不记成"正常完成"
                Stats.FocusSeconds += s.ElapsedSeconds;
                SaveStats();
                Store.Log(string.Format("专注结束（{0}），本段已告状，不计正常完成；实际 {1}",
                    reachedGoal ? "走完全程" : "中途放弃", FormatDuration(s.ElapsedSeconds)));
                Session = null;
                Queue(Raise);
                Queue(delegate { StopLoop(); });     // 本段结束，后台线程可以停了
                return;
            }

            if (reachedGoal)
            {
                s.Result = SessionResult.Completed;
                int done = s.Snapshot.FocusMinutes;
                Stats.CompletedCount += 1;
                Stats.FocusSeconds += s.PlannedSeconds;
                SaveStats();
                Store.Log("完成专注 " + done + " 分钟，未告状");
                Session = null;
                Queue(Raise);
                Queue(delegate { StopLoop(); });
                Settings snap = s.Snapshot;
                Queue(delegate { NotifyComplete(snap, done); });
                return;
            }

            s.Result = s.ReportTriggered ? s.Result : SessionResult.Abandoned;
            Stats.AbandonedCount += 1;
            Stats.FocusSeconds += s.ElapsedSeconds;
            SaveStats();
            if (!s.ReportTriggered) ReportNowLocked("中途放弃（没坚持够时间）");
            Session = null;
            Queue(Raise);
            Queue(delegate { StopLoop(); });
        }

        /// <summary>专注走完时的提醒：托盘气泡 + 系统提示音，两项可各自在设置里关掉（默认都开）。</summary>
        private void NotifyComplete(Settings snapshot, int minutes)
        {
            if (snapshot != null && snapshot.NotifyOnComplete)
            {
                CompleteNoticeCount++;
                Notify(string.Format("专注完成：{0} 分钟走完了，休息一下。", minutes));
            }
            if (snapshot != null && snapshot.NotifySound)
            {
                SoundPlayCount++;
                if (!App.Headless)
                {
                    try { System.Media.SystemSounds.Asterisk.Play(); }
                    catch { }
                }
            }
        }

        /// <summary>供自检：气泡提醒 / 提示音各自触发了多少次。</summary>
        internal static int CompleteNoticeCount;
        internal static int SoundPlayCount;

        /// <summary>按需求：只有"没坚持完"或"偷玩过久"才告状，且一段专注最多一封。锁内调用。</summary>
        private void ReportNowLocked(string reason)
        {
            FocusSession s = Session;
            if (s == null || s.ReportTriggered) return;

            s.ReportTriggered = true;
            s.Result = SessionResult.Violated;
            s.ReportReason = reason;
            s.Events.Add(string.Format("{0:HH:mm:ss} 触发告状：{1}", Clock.Now, reason));

            DateTime now = Clock.Now;
            Settings snap = s.Snapshot ?? Settings;
            string subject = Mailer.ComposeSubject(snap, reason, now, s);
            string body = Mailer.ComposeBody(snap, s, Stats, reason, now);

            Stats.ReportCount += 1;
            SaveStats();
            Store.Log("触发告状：" + reason);

            HistoryEntry entry = new HistoryEntry();
            entry.Time = now.ToString("yyyy-MM-dd HH:mm:ss");
            entry.Reason = reason;
            entry.Subject = subject;
            entry.Body = body;

            if (App.Headless)
            {
                // 自检模式：不发送，但留下一条明确标注的记录（不留"待发送"这种悬空状态）
                entry.Status = "仅记录（自检未发送）";
                Store.AppendHistory(entry);
                Queue(delegate { Action<HistoryEntry> h = ReportReady; if (h != null) h(entry); });
                Queue(Raise);
                return;
            }

            // 关键：只在**拿到最终结果后**才写记录，避免出现永远停在「待发送」的悬空记录。
            // 界面先用内存里的 entry 立即提示，不再提前落盘占位。
            Queue(delegate { Action<HistoryEntry> h = ReportReady; if (h != null) h(entry); });
            Queue(Raise);

            lock (sendLock) { pendingSends++; }
            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                try
                {
                    Mailer.Send(snap, subject, body);
                    entry.Status = "已发送";
                    entry.Error = "";
                    Store.Log("告状邮件已发送至 " + snap.SupervisorEmail);
                    Notify("已发送告状邮件给 " + snap.SupervisorEmail);
                }
                catch (Exception ex)
                {
                    entry.Status = "发送失败";
                    entry.Error = ex.Message;
                    Store.Log("告状邮件发送失败：" + ex.Message);
                    Notify("告状邮件发送失败：" + ex.Message + "（记录里保留邮件全文，可手动重发）");
                }
                finally
                {
                    Store.AppendHistory(entry);
                    lock (sendLock) { pendingSends--; }
                }
            });
        }

        private void SaveStats()
        {
            try { Store.SaveStats(Stats); }
            catch { }
        }

        // ------------------------------------------------------------ 后台循环

        /// <summary>启动后台计时线程（重复调用安全）。界面卡顿不影响计时。</summary>
        public void StartLoop()
        {
            if (ManualTickOnly) return;          // 自检：由测试自己推进
            lock (gate)
            {
                if (loopRunning) return;
                loopRunning = true;
                loop = new Thread(LoopBody);
                loop.IsBackground = true;
                loop.Name = "PomoCC.supervisor";
            }
            HookPower();
            try { loop.Start(); }
            catch { lock (gate) { loopRunning = false; } }
        }

        public void StopLoop()
        {
            lock (gate) { loopRunning = false; }
            UnhookPower();
        }

        private void LoopBody()
        {
            while (true)
            {
                lock (gate) { if (!loopRunning) return; }
                try { Tick(); }
                catch (Exception ex) { try { Store.Log("监督线程异常：" + ex.Message); } catch { } }
                Thread.Sleep(200);
            }
        }

        // ------------------------------------------------------------ 电源事件

        private void HookPower()
        {
            if (App.Headless) return;      // 自检里不接系统事件（没有消息泵）
            lock (gate)
            {
                if (powerHooked) return;
                powerHooked = true;
            }
            try
            {
                Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
            }
            catch
            {
                lock (gate) { powerHooked = false; }
            }
        }

        private void UnhookPower()
        {
            lock (gate)
            {
                if (!powerHooked) return;
                powerHooked = false;
            }
            try { Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged; }
            catch { }
        }

        private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
        {
            if (e.Mode == Microsoft.Win32.PowerModes.Suspend) OnSystemSuspend();
            else if (e.Mode == Microsoft.Win32.PowerModes.Resume) OnSystemResume();
        }

        /// <summary>系统要睡了：复位计时基准，醒来后不会把睡眠时间算进去。</summary>
        internal void OnSystemSuspend()
        {
            lock (gate)
            {
                FocusSession s = Session;
                if (s == null) return;
                s.LastMarkSeconds = Clock.Seconds;
                s.LastSampleSeconds = Clock.Seconds;
                s.Events.Add(string.Format("{0:HH:mm:ss} 系统进入睡眠/休眠，暂停本段计时", Clock.Now));
                Store.Log("系统进入睡眠/休眠，暂停专注计时");
            }
        }

        /// <summary>系统醒了：重新建立基准（与睡眠复位等效，双保险）。</summary>
        internal void OnSystemResume()
        {
            lock (gate)
            {
                FocusSession s = Session;
                if (s == null) return;
                s.LastMarkSeconds = Clock.Seconds;
                s.LastSampleSeconds = Clock.Seconds;
                s.Events.Add(string.Format("{0:HH:mm:ss} 系统已恢复，继续计时", Clock.Now));
                Store.Log("系统已恢复，继续专注计时");
            }
        }

        public void Dispose()
        {
            StopLoop();
        }

        // ------------------------------------------------------------ 退出前等在途邮件

        private static readonly object sendLock = new object();
        private static int pendingSends;

        /// <summary>正在发送中的告状邮件数量。</summary>
        public static int PendingSends
        {
            get { lock (sendLock) { return pendingSends; } }
        }

        /// <summary>等待所有在途邮件发送结束（退出前调用）。返回 true 表示都结束了。</summary>
        public static bool WaitForSendsToFinish(int timeoutMs)
        {
            DateTime until = DateTime.Now.AddMilliseconds(timeoutMs);
            while (DateTime.Now < until)
            {
                if (PendingSends == 0) return true;
                Thread.Sleep(50);
            }
            return PendingSends == 0;
        }

        // ------------------------------------------------------------ 界面只读快照

        /// <summary>界面用来刷新的只读快照，避免直接读后台正在改的对象。</summary>
        public class Snapshot
        {
            public bool Focusing;
            public int ElapsedSeconds;
            public int PlannedSeconds;
            public int ProgressPercent;
            public string TimerText;
            public string StatusLine;
            public string WatchSummary;
            public string TodayText;
            public SessionResult Result;
        }

        public Snapshot Read()
        {
            Snapshot snap = new Snapshot();
            lock (gate)
            {
                snap.Focusing = Session != null;
                snap.ElapsedSeconds = Session == null ? 0 : Session.ElapsedSeconds;
                snap.PlannedSeconds = Session == null ? Settings.FocusMinutes * 60 : Session.PlannedSeconds;
                snap.ProgressPercent = snap.PlannedSeconds <= 0 ? 0
                    : Math.Min(100, snap.ElapsedSeconds * 100 / snap.PlannedSeconds);
                snap.Result = Session == null ? SessionResult.Completed : Session.Result;
                snap.TimerText = TimerTextLocked();
                snap.StatusLine = StatusLineLocked();
                snap.WatchSummary = WatchSummaryLocked();
                snap.TodayText = TodayTextLocked();
            }
            return snap;
        }

        public int ProgressPercent { get { return Read().ProgressPercent; } }

        public int ElapsedSeconds { get { return Read().ElapsedSeconds; } }

        // ------------------------------------------------------------------

        public string StatusLine()
        {
            lock (gate) { return StatusLineLocked(); }
        }

        private string StatusLineLocked()
        {
            if (Session == null)
            {
                return watchRules.Count == 0
                    ? "待机中 · 还没添加监督程序"
                    : string.Format("待机中 · 正在监视 {0} 个程序", watchRules.Count);
            }
            return "专注中 · 监督 " + watchRules.Count + " 个程序";
        }

        public string WatchSummary()
        {
            lock (gate) { return WatchSummaryLocked(); }
        }

        private string WatchSummaryLocked()
        {
            if (Session == null)
            {
                List<WatchProcess> running = ProcessScan(watchRules);
                if (running.Count == 0) return "监督名单里的程序当前都没在运行";
                StringBuilder sb0 = new StringBuilder("当前正在运行：");
                for (int i = 0; i < running.Count; i++)
                {
                    if (i > 0) sb0.Append("、");
                    sb0.Append(running[i].Label);
                    if (running[i].StartTime != DateTime.MinValue)
                        sb0.Append("（已运行 ").Append(FormatDuration((int)(DateTime.Now - running[i].StartTime).TotalSeconds)).Append("）");
                }
                return sb0.ToString();
            }

            if (Session.Watched.Count == 0) return "本次专注期间未发现监督名单里的程序";

            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, WatchProcess> kv in Session.Watched)
            {
                WatchProcess w = kv.Value;
                if (sb.Length > 0) sb.Append("　");
                int limit = w.LimitMinutes > 0 ? w.LimitMinutes : DefaultLimitMinutes;
                sb.Append("⚠ ").Append(w.Exe).Append(" 已 ").Append(FormatDuration(w.SessionSeconds));
                sb.Append(" / 规则 ").Append(limit).Append(" 分钟");
                if (w.InstanceCount > 1) sb.Append("（").Append(w.InstanceCount).Append(" 个实例）");
                if (w.LastSeen >= Clock.Now.AddSeconds(-Math.Max(3, Session.Snapshot.SampleSeconds * 3)))
                    sb.Append("（运行中）");
            }
            return sb.ToString();
        }

        public string TimerText()
        {
            lock (gate) { return TimerTextLocked(); }
        }

        private string TimerTextLocked()
        {
            int total = Session == null ? Settings.FocusMinutes : Session.Snapshot.FocusMinutes;
            if (Session == null) return string.Format("{0}:00", total);
            int left = Session.PlannedSeconds - Session.ElapsedSeconds;
            if (left < 0) left = 0;
            return string.Format("{0}:{1:00}", left / 60, left % 60);
        }

        public string TodayText()
        {
            lock (gate) { return TodayTextLocked(); }
        }

        private string TodayTextLocked()
        {
            return string.Format("今日：完成 {0} 段 · 放弃 {1} 段 · 告状 {2} 次 · 专注 {3}",
                Stats.CompletedCount, Stats.AbandonedCount, Stats.ReportCount, FormatDuration(Stats.FocusSeconds));
        }

        public static string FormatDuration(int seconds)
        {
            if (seconds < 0) seconds = 0;
            if (seconds < 60) return seconds + " 秒";
            int m = seconds / 60;
            int s = seconds % 60;
            return s == 0 ? m + " 分钟" : string.Format("{0} 分 {1} 秒", m, s);
        }

        public static string FormatMinutes(int seconds)
        {
            if (seconds < 0) seconds = 0;
            return Math.Round(seconds / 60.0, 1).ToString("0.#") + " 分钟";
        }
    }
}
