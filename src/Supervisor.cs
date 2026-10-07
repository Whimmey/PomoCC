using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace PomoCC
{
    public class FocusSession
    {
        public DateTime StartedAt { get; set; }
        public int PlannedSeconds { get; set; }
        public int ElapsedSeconds { get; set; }
        public bool Reported { get; set; }
        public string ReportReason { get; set; }
        public Dictionary<string, WatchProcess> Watched { get; set; }
        public List<string> Events { get; set; }

        public FocusSession()
        {
            Watched = new Dictionary<string, WatchProcess>();
            Events = new List<string>();
        }
    }

    /// <summary>番茄钟 + 监督规则 + 告状判定的核心逻辑（与界面无关，便于自检）。</summary>
    public class Supervisor
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

        private List<WatchRule> watchRules;
        private DateTime lastSample = DateTime.MinValue;
        private DateTime lastTick = DateTime.MinValue;

        public Supervisor(Settings settings, DailyStats stats)
        {
            Settings = settings;
            Stats = stats;
            RefreshWatchList();
        }

        public bool IsFocusing { get { return Session != null; } }

        public void RefreshWatchList()
        {
            watchRules = SettingsValidator.ActiveRules(Settings);
        }

        public List<WatchRule> WatchRules { get { return new List<WatchRule>(watchRules); } }

        public List<string> WatchNames
        {
            get
            {
                List<string> names = new List<string>();
                for (int i = 0; i < watchRules.Count; i++) names.Add(watchRules[i].Exe);
                return names;
            }
        }

        public void Raise()
        {
            if (Changed != null) Changed();
        }

        private void Notify(string msg)
        {
            if (Notified != null) Notified(msg);
        }

        // ------------------------------------------------------------------

        public void StartFocus()
        {
            if (Session != null) return;
            Session = new FocusSession();
            Session.StartedAt = DateTime.Now;
            Session.PlannedSeconds = Settings.FocusMinutes * 60;
            lastSample = DateTime.MinValue;
            lastTick = DateTime.MinValue;
            Session.Events.Add(string.Format("{0:HH:mm:ss} 开始专注，目标 {1} 分钟，监督 {2} 个程序",
                DateTime.Now, Settings.FocusMinutes, watchRules.Count));
            Store.Log(string.Format("开始专注：{0} 分钟，监督 {1} 个程序", Settings.FocusMinutes, watchRules.Count));
            Raise();
        }

        public void Tick()
        {
            if (Session == null) return;

            DateTime now = DateTime.Now;
            double gap = lastTick == DateTime.MinValue ? 1.0 : (now - lastTick).TotalSeconds;
            lastTick = now;

            if (gap > 15.0)
            {
                // 睡眠/挂起：不把睡过去的时间算成专注，避免"睡一觉就完成"
                // 阈值取 15 秒，普通界面卡顿不会误判成中断
                Session.Events.Add(string.Format("{0:HH:mm:ss} 检测到中断 {1:0} 秒（可能睡眠），本段不计时", now, gap));
                Store.Log(string.Format("检测到计时中断 {0:0} 秒，不计入专注", gap));
            }
            else
            {
                Session.ElapsedSeconds += 1;
            }

            if (lastSample == DateTime.MinValue || (now - lastSample).TotalSeconds >= Settings.SampleSeconds)
            {
                DoSample(now);
                lastSample = now;
            }

            if (Session != null && Session.ElapsedSeconds >= Session.PlannedSeconds)
            {
                Complete();
            }
        }

        private void DoSample(DateTime now)
        {
            if (Session == null) return;
            if (watchRules.Count == 0) return;

            double gap = lastSample == DateTime.MinValue ? Settings.SampleSeconds : (now - lastSample).TotalSeconds;
            if (gap <= 0 || gap > Settings.SampleSeconds * 3) gap = Settings.SampleSeconds;

            List<WatchProcess> running = ProcessMonitor.Scan(watchRules);
            for (int i = 0; i < running.Count; i++)
            {
                WatchProcess w = running[i];
                if (Settings.OnlyCountAfterStart && w.StartTime != DateTime.MinValue && w.StartTime < Session.StartedAt)
                    continue;

                WatchProcess cur;
                if (!Session.Watched.ContainsKey(w.Exe))
                {
                    w.FirstSeen = now;
                    w.SessionSeconds = 0;
                    Session.Watched[w.Exe] = w;
                    cur = w;
                    Session.Events.Add(string.Format("{0:HH:mm:ss} 发现 {1} 正在运行（规则 {2} 分钟）",
                        now, w.Label, w.LimitMinutes));
                    Store.Log("专注期间发现 " + w.Exe);
                }
                else
                {
                    cur = Session.Watched[w.Exe];
                    if (cur.StartTime == DateTime.MinValue) cur.StartTime = w.StartTime;
                    cur.Pid = w.Pid;
                    if (cur.LimitMinutes <= 0) cur.LimitMinutes = w.LimitMinutes;
                    cur.Name = w.Name;              // 用户可能在会话期间改了名称
                    cur.DisplayName = w.DisplayName;
                }
                cur.LastSeen = now;
                cur.SessionSeconds += (int)Math.Round(gap);
            }

            if (Session.Reported) return;

            // 按「每条规则各自的时长」判定，符合表格里逐行配置的语义
            foreach (KeyValuePair<string, WatchProcess> kv in Session.Watched)
            {
                WatchProcess w = kv.Value;
                if (!RuleExceeded(w)) continue;
                Stats.ViolationSeconds += w.SessionSeconds;
                Store.SaveStats(Stats);
                ReportNow(string.Format("专注期间偷玩超时（{0} 已跑 {1}，超过规则 {2} 分钟）",
                    w.Label, FormatDuration(w.SessionSeconds), w.LimitMinutes > 0 ? w.LimitMinutes : DefaultLimitMinutes));
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

        public void Complete()
        {
            if (Session == null) return;
            int done = Settings.FocusMinutes;
            Stats.CompletedCount += 1;
            Stats.FocusSeconds += Session.PlannedSeconds;
            Store.SaveStats(Stats);
            Store.Log("完成专注 " + done + " 分钟，未告状");
            Session = null;
            Raise();
            NotifyComplete(done);
        }

        /// <summary>专注走完时的提醒：托盘气泡 + 系统提示音，两项可各自在设置里关掉（默认都开）。</summary>
        private void NotifyComplete(int minutes)
        {
            if (Settings.NotifyOnComplete)
            {
                CompleteNoticeCount++;
                Notify(string.Format("专注完成：{0} 分钟走完了，休息一下。", minutes));
            }
            if (Settings.NotifySound)
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

        public void Abandon()
        {
            if (Session == null) return;
            Stats.AbandonedCount += 1;
            Store.SaveStats(Stats);
            if (!Session.Reported) ReportNow("中途放弃（没坚持够时间）");
            Session = null;
            Raise();
        }

        /// <summary>按需求：只有"没坚持完"或"偷玩过久"才告状，且一段专注最多一封。</summary>
        private void ReportNow(string reason)
        {
            if (Session == null || Session.Reported) return;
            Session.Reported = true;
            Session.ReportReason = reason;
            Session.Events.Add(string.Format("{0:HH:mm:ss} 触发告状：{1}", DateTime.Now, reason));

            DateTime now = DateTime.Now;
            string subject = Mailer.ComposeSubject(Settings, reason, now, Session);
            string body = Mailer.ComposeBody(Settings, Session, Stats, reason, now);

            Stats.ReportCount += 1;
            Store.SaveStats(Stats);
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
                if (ReportReady != null) ReportReady(entry);
                Raise();
                return;
            }

            // 关键：只在**拿到最终结果后**才写记录，避免出现永远停在「待发送」的悬空记录。
            // 界面先用内存里的 entry 立即提示，不再提前落盘占位。
            if (ReportReady != null) ReportReady(entry);
            Raise();

            Settings snapshot = Settings;
            lock (sendLock) { pendingSends++; }
            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                try
                {
                    Mailer.Send(snapshot, subject, body);
                    entry.Status = "已发送";
                    entry.Error = "";
                    Store.Log("告状邮件已发送至 " + snapshot.SupervisorEmail);
                    Notify("已发送告状邮件给 " + snapshot.SupervisorEmail);
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

        // ---------- 退出前把还在发的邮件等完，避免又留下没结果的记录 ----------

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

        // ------------------------------------------------------------------

        public string StatusLine()
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
            if (Session == null)
            {
                List<WatchProcess> running = ProcessMonitor.Scan(watchRules);
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
                int limit = w.LimitMinutes > 0 ? w.LimitMinutes : 3;
                sb.Append("⚠ ").Append(w.Exe).Append(" 已 ").Append(FormatDuration(w.SessionSeconds));
                sb.Append(" / 规则 ").Append(limit).Append(" 分钟");
                if (w.LastSeen >= DateTime.Now.AddSeconds(-Math.Max(3, Settings.SampleSeconds * 3)))
                    sb.Append("（运行中）");
            }
            return sb.ToString();
        }

        public string TimerText()
        {
            if (Session == null) return string.Format("{0}:00", Settings.FocusMinutes);
            int left = Session.PlannedSeconds - Session.ElapsedSeconds;
            if (left < 0) left = 0;
            return string.Format("{0}:{1:00}", left / 60, left % 60);
        }

        public string TodayText()
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
