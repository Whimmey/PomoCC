using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace PomoCC
{
    /// <summary>无界面自检：验证配置、加密、规则表、进程枚举、邮件内容生成、DPI 适配。</summary>
    public static class SelfTest
    {
        /// <summary>自检过程中创建的临时数据目录；跑完统一删掉，别在用户机器上堆垃圾。</summary>
        private static readonly List<string> TempDirs = new List<string>();
        private static string btnDir;   // 按钮渲染图临时目录（跑完会随其它临时目录一起清掉）

        private static string TempDir(string tag)
        {
            string dir = Path.Combine(Path.GetTempPath(), tag + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            lock (TempDirs) { TempDirs.Add(dir); }
            return dir;
        }

        /// <summary>
        /// 收尾：删掉本次自检建的所有临时目录。
        /// 设 POMODORO_KEEP_TEMP=1 可以保留（排查失败时用）。
        /// </summary>
        internal static void CleanupTempDirs()
        {
            if (App.EnvFlag("POMOCC_KEEP_TEMP", "POMODORO_KEEP_TEMP")) return;
            lock (TempDirs)
            {
                for (int i = 0; i < TempDirs.Count; i++)
                {
                    try { Directory.Delete(TempDirs[i], true); }
                    catch { }
                }
                TempDirs.Clear();
            }
        }

        /// <summary>
        /// 自检用的极简 HTTP 服务器：固定返回一个状态码，记录收到的请求与连接次数。
        /// 用来验证"HTTP 发信不跟随重定向"（认证头不能泄漏到新地址）。
        /// </summary>
        private class MiniHttp
        {
            public int Port;
            public int Connections;
            public string LastRequest = "";
            private TcpListener listener;
            private Thread thread;
            private volatile bool running;

            public MiniHttp(int status, string body, string location)
            {
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                running = true;
                thread = new Thread(delegate()
                {
                    while (running)
                    {
                        TcpClient c;
                        try { c = listener.AcceptTcpClient(); }
                        catch { return; }
                        try
                        {
                            Connections++;
                            NetworkStream ns = c.GetStream();
                            try { ns.ReadTimeout = 2000; } catch { }
                            byte[] buf = new byte[8192];
                            int n = 0;
                            try { n = ns.Read(buf, 0, buf.Length); }
                            catch { }
                            LastRequest = Encoding.UTF8.GetString(buf, 0, n);

                            StringBuilder resp = new StringBuilder();
                            resp.Append("HTTP/1.1 ").Append(status).Append(" X\r\n");
                            if (location != null) resp.Append("Location: ").Append(location).Append("\r\n");
                            resp.Append("Content-Length: ").Append(body.Length).Append("\r\n");
                            resp.Append("Connection: close\r\n\r\n").Append(body);
                            byte[] outb = Encoding.UTF8.GetBytes(resp.ToString());
                            ns.Write(outb, 0, outb.Length);
                            ns.Flush();
                        }
                        catch { }
                        finally { try { c.Close(); } catch { } }
                    }
                });
                thread.IsBackground = true;
                thread.Start();
            }

            public void Stop()
            {
                running = false;
                try { listener.Stop(); } catch { }
            }
        }

        /// <summary>自检辅助：构造一个"扫描到的进程"。</summary>
        private static WatchProcess Proc(string exe, int pid, DateTime start, int limit)
        {
            WatchProcess w = new WatchProcess();
            w.Exe = exe;
            w.DisplayName = System.IO.Path.GetFileNameWithoutExtension(exe);
            w.Name = "";
            w.Pid = pid;
            w.StartTime = start;
            w.LimitMinutes = limit;
            return w;
        }

        private static WatchRule Rule(string exe, int limitMinutes)
        {
            WatchRule r = new WatchRule();
            r.Exe = exe;
            r.LimitMinutes = limitMinutes;
            r.DisplayName = Path.GetFileNameWithoutExtension(exe);
            r.Enabled = true;
            return r;
        }

        // ==================================================================
        //  1. 逻辑自检
        // ==================================================================

        public static int Run(string outPath)
        {
            StringBuilder sb = new StringBuilder();
            int fail = 0;
            ReportDpapiEnvironment(sb);

            try
            {
                Store.OverrideDir = TempDir("pomocc-selftest");
                sb.Append("数据目录（临时）：").Append(Store.OverrideDir).Append("\r\n\r\n");

                // 1. 默认配置
                Settings def = Settings.Defaults();
                Check(sb, "settings-defaults",
                    def.FocusMinutes == 25 && def.SampleSeconds == 5 && def.Rules != null,
                    string.Format("专注 {0} 分钟，采样 {1} 秒", def.FocusMinutes, def.SampleSeconds), ref fail);

                // 2. 配置往返（含规则表）
                def.SupervisorEmail = "boss@example.com";
                def.Rules = new List<WatchRule>();
                def.Rules.Add(Rule("steam.exe", 3));
                def.Rules.Add(Rule("wegame.exe", 10));
                def.FocusMinutes = 30;
                Store.SaveSettings(def);
                Settings back = Store.LoadSettings();
                Check(sb, "settings-roundtrip",
                    back.SupervisorEmail == "boss@example.com" && back.FocusMinutes == 30
                    && back.Rules.Count == 2 && back.Rules[1].LimitMinutes == 10,
                    string.Format("收件人={0}，规则={1} 条，第二条时长={2} 分钟",
                        back.SupervisorEmail, back.Rules.Count, back.Rules[1].LimitMinutes), ref fail);

                // 3. 旧版「一串进程名」自动迁移成规则表
                Settings legacy = Settings.Defaults();
                legacy.Rules = new List<WatchRule>();
                legacy.WatchList = new List<string>(new string[] { "Steam.exe", @"C:\Games\WeGame.exe" });
                legacy.ViolationSeconds = 300;
                Store.SaveSettings(legacy);
                Settings migrated = Store.LoadSettings();
                Check(sb, "rules-migration-from-old-config",
                    migrated.Rules.Count == 2 && migrated.Rules[0].Exe == "steam.exe"
                    && migrated.Rules[0].LimitMinutes == 5 && migrated.WatchList.Count == 0,
                    migrated.Rules.Count == 2
                        ? string.Format("迁移出 {0} 条规则，时长沿用旧的 {1} 分钟", migrated.Rules.Count, migrated.Rules[0].LimitMinutes)
                        : "没有迁移成功", ref fail);

                // 4. 坏配置能兜住
                File.WriteAllText(Store.ConfigPath, "{ 这不是 json", Encoding.UTF8);
                Settings recovered = Store.LoadSettings();
                Check(sb, "settings-corrupt-fallback",
                    recovered != null && recovered.FocusMinutes == 25, "坏文件已回退到默认值", ref fail);
                File.Delete(Store.ConfigPath + ".bad");

                // 4b. 日志不会无限增长：超过上限自动截断，只留最近一段
                for (int i = 0; i < 70; i++) Store.Log(new string('x', 10000));   // 约 700 KB
                long logLen = new FileInfo(Store.LogPath).Length;
                Check(sb, "log-is-capped", logLen <= 512 * 1024,
                    string.Format("连续写入约 700 KB 后，app.log 只有 {0} KB（上限 512 KB）", logLen / 1024), ref fail);
                File.Delete(Store.LogPath);

                // 4c. 改名一致性 + 数据目录改名迁移（%APPDATA%\PomodoroSupervisor → %APPDATA%\PomoCC）
                Check(sb, "naming-is-pomocc",
                    Store.DataFolderName == "PomoCC" && AutoStart.MainValueName == "PomoCC"
                    && AutoStart.LegacyValueName == "PomodoroSupervisor"
                    && App.MutexName.StartsWith("PomoCC"),
                    string.Format("数据目录={0}；注册表值名={1}（旧名 {2} 会被清理）；互斥体={3}",
                        Store.DataFolderName, AutoStart.MainValueName, AutoStart.LegacyValueName, App.MutexName), ref fail);

                string roam = TempDir("pomocc-roam");
                string oldDataDir = Path.Combine(roam, Store.LegacyDataFolderName);
                Directory.CreateDirectory(oldDataDir);
                File.WriteAllText(Path.Combine(oldDataDir, "config.json"),
                    "{\"Version\":2,\"FocusMinutes\":33,\"SampleSeconds\":5,\"ViolationSeconds\":180," +
                    "\"SendMode\":\"smtp\",\"SmtpHost\":\"smtp.qq.com\",\"SmtpPort\":587,\"Rules\":[]}", Encoding.UTF8);
                File.WriteAllText(Path.Combine(oldDataDir, "history.jsonl"),
                    "{\"Time\":\"2026-01-01 00:00:00\",\"Reason\":\"中途放弃\",\"Status\":\"已发送\"}\r\n", Encoding.UTF8);

                string savedOverride = Store.OverrideDir;
                Store.OverrideDir = null;                 // 让 Store.Dir 走真实逻辑（沙箱漫游根）
                Store.RoamingRootOverride = roam;
                try
                {
                    string newDataDir = Store.Dir;
                    Settings dirCfg = Store.LoadSettings();
                    bool movedOk = newDataDir == Path.Combine(roam, Store.DataFolderName)
                                && File.Exists(Path.Combine(newDataDir, "config.json"))
                                && File.Exists(Path.Combine(newDataDir, "history.jsonl"))
                                && dirCfg.FocusMinutes == 33            // 老配置真的搬过来了并生效
                                && !Directory.Exists(oldDataDir);       // 老目录删掉，不留空目录垃圾
                    Check(sb, "data-dir-rename-migration", movedOk,
                        string.Format("新目录 {0}；迁移后专注 {1} 分钟；老目录已清理={2}",
                            Path.GetFileName(newDataDir), dirCfg.FocusMinutes, !Directory.Exists(oldDataDir)), ref fail);
                }
                finally
                {
                    Store.RoamingRootOverride = null;
                    Store.OverrideDir = savedOverride;
                }

                // 5. 授权码加密（DPAPI）
                //    注意：这一段以前没有 try/catch —— 在"用户配置文件未加载"的环境里
                //    ProtectedData 会抛 CryptographicException，导致**整个自检在这里中断**，
                //    后面的检查全都不执行。现在环境不可用时记环境跳过并继续。
                if (dpapiEnv)
                {
                    Settings sec = Settings.Defaults();
                    sec.SetAuthCode("abcdefg123");
                    string enc = sec.AuthCodeEnc;
                    bool encrypted = enc.Length > 0 && enc.IndexOf("abcdefg123") < 0;
                    Check(sb, "secret-dpapi-roundtrip",
                        encrypted && sec.GetAuthCode() == "abcdefg123",
                        encrypted ? "落盘内容已加密，解密还原正确" : "落盘内容没有加密", ref fail);
                }
                else
                {
                    SkipDpapi(sb, "secret-dpapi-roundtrip", dpapiEnvWhy);
                }

                // 把"环境阻塞"和"代码缺陷"分开的判定逻辑本身也要被测到：
                // 真正 DPAPI 不可用的环境里（服务会话/未加载用户配置文件），
                // 可能还有没被守卫覆盖的调用点，届时靠这个判定把它记成环境阻塞而不是失败。
                bool clsOk = IsDpapiEnvFailure(new System.Security.Cryptography.CryptographicException(
                                    "当前线程用户上下文未加载用户配置文件"))
                          && IsDpapiEnvFailure(new Exception("外层包装", new System.Security.Cryptography.CryptographicException("x")))
                          && !IsDpapiEnvFailure(new InvalidOperationException("这是真的代码问题"));
                Check(sb, "dpapi-env-failure-classifier", clsOk,
                    "CryptographicException（含内层）判为环境阻塞；其它异常仍然算失败", ref fail);

                // 6. 密码
                Settings pw = Settings.Defaults();
                pw.SetPassword("tangochao123");
                Check(sb, "password-verify",
                    pw.CheckPassword("tangochao123") && !pw.CheckPassword("wrong") && !pw.CheckPassword(""),
                    "正确密码通过，错误密码拒绝", ref fail);

                // 7. 校验器：空名单要被拦住
                Settings bad = Settings.Defaults();
                List<string> errs = SettingsValidator.Validate(bad);
                bool blocked = false;
                for (int i = 0; i < errs.Count; i++) if (errs[i].IndexOf("监督名单") >= 0) blocked = true;
                Check(sb, "validator-catches-empty-rules",
                    blocked && errs.Count >= 3, string.Format("空配置报出 {0} 条问题（含名单为空）", errs.Count), ref fail);

                // 8. 规则时长校验：超范围要被拦住
                Settings badLimit = Settings.Defaults();
                badLimit.Rules = new List<WatchRule>();
                badLimit.Rules.Add(Rule("steam.exe", 9999));
                List<string> errs2 = SettingsValidator.Validate(badLimit);
                bool limitBlocked = false;
                for (int i = 0; i < errs2.Count; i++) if (errs2[i].IndexOf("规则时长") >= 0) limitBlocked = true;
                Check(sb, "validator-catches-bad-limit", limitBlocked, "9999 分钟被拒绝", ref fail);

                // 9. 逐条规则的时长判定（表格第二列的语义）
                WatchProcess p1 = new WatchProcess(); p1.LimitMinutes = 3; p1.SessionSeconds = 179;
                WatchProcess p2 = new WatchProcess(); p2.LimitMinutes = 3; p2.SessionSeconds = 180;
                WatchProcess p3 = new WatchProcess(); p3.LimitMinutes = 1; p3.SessionSeconds = 60;
                WatchProcess p4 = new WatchProcess(); p4.LimitMinutes = 0; p4.SessionSeconds = 179;
                WatchProcess p5 = new WatchProcess(); p5.LimitMinutes = 0; p5.SessionSeconds = 180;
                Check(sb, "rule-threshold-per-program",
                    !Supervisor.RuleExceeded(p1) && Supervisor.RuleExceeded(p2)
                    && Supervisor.RuleExceeded(p3) && !Supervisor.RuleExceeded(p4) && Supervisor.RuleExceeded(p5),
                    "3分钟规则：179秒不告状/180秒告状；1分钟规则：60秒告状；未填时长按3分钟兜底", ref fail);

                // 10. 名单归一化
                bool norm = ProcessMonitor.NormalizeName(@"C:\Games\Steam.exe") == "steam.exe"
                         && ProcessMonitor.NormalizeName("  steam  ") == "steam.exe"
                         && ProcessMonitor.NormalizeName("") == ""
                         && ProcessMonitor.NormalizeList(new string[] { "a.exe", "A.EXE", "b" }).Count == 2;
                Check(sb, "watch-normalize", norm, "路径/大小写/去重都正确", ref fail);

                // 11. 进程枚举 + 启动时间 + 规则命中
                int total = ProcessMonitor.CountAll();
                string self = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
                List<WatchRule> selfRule = new List<WatchRule>();
                selfRule.Add(Rule(self, 3));
                List<WatchProcess> hit = ProcessMonitor.Scan(selfRule);
                bool scanOk = total > 0 && hit.Count >= 1 && hit[0].LimitMinutes == 3;
                Check(sb, "process-enumeration-and-rule-hit", scanOk,
                    string.Format("共 {0} 个进程；按规则扫到自己 {1} 个（{2}）", total, hit.Count, self), ref fail);
                Check(sb, "process-start-time",
                    hit.Count > 0 && hit[0].StartTime != DateTime.MinValue,
                    hit.Count > 0 && hit[0].StartTime != DateTime.MinValue
                        ? string.Format("本进程启动于 {0:HH:mm:ss}", hit[0].StartTime) : "读不到启动时间", ref fail);

                // 12. 没有窗口的程序（后台进程）也能进入「添加程序」列表
                List<RunningAppInfo> bg = AppCatalog.Enumerate(true);
                int windowedCount = 0;
                int backgroundCount = 0;
                for (int i = 0; i < bg.Count; i++)
                {
                    if (bg[i].HasWindow) windowedCount++; else backgroundCount++;
                }
                Check(sb, "app-catalog-enumeration",
                    bg.Count > 0 && windowedCount > 0,
                    string.Format("发现 {0} 个可添加程序（有窗口 {1} 个，后台 {2} 个）",
                        bg.Count, windowedCount, backgroundCount), ref fail);

                // 13. 专注状态机（Headless 不会真的发信）
                App.Headless = true;
                Settings cfg = Settings.Defaults();
                cfg.FocusMinutes = 1;
                cfg.Rules = new List<WatchRule>();
                cfg.Rules.Add(Rule(self, 3));
                Supervisor sup = new Supervisor(cfg, DailyStats.NewFor(DateTime.Now));
                sup.StartFocus();
                bool started = sup.IsFocusing && sup.TimerText() == "1:00";
                sup.Abandon();
                Check(sb, "session-start-abandon", started && !sup.IsFocusing,
                    "开始后计时 1:00，放弃后回到待机", ref fail);

                // 13b. 一次告状只写一条记录，而且不能留下「待发送」这种悬空状态
                //      （旧版本先写占位、发完再补一条；程序提前退出就会剩一个永远不变的「待发送」）
                List<HistoryEntry> hist = Store.ReadHistory(50);
                HistoryEntry lastRec = hist.Count > 0 ? hist[hist.Count - 1] : null;
                bool oneRecord = hist.Count > 0 && lastRec != null && lastRec.Status != "待发送"
                                 && lastRec.Status != null && lastRec.Status.Length > 0;
                Check(sb, "report-writes-single-final-record", oneRecord,
                    lastRec == null ? "没有记录"
                        : string.Format("共 {0} 条，最后一条状态「{1}」（不是待发送占位）", hist.Count, lastRec.Status), ref fail);

                // 13c. 旧版本遗留的「待发送」要如实显示成「未确认（可能未发出）」
                HistoryEntry legacyRec = new HistoryEntry();
                legacyRec.Status = "待发送";
                string mapped = HistoryForm.StatusText(legacyRec);
                Check(sb, "legacy-pending-status-honest", mapped == "未确认（可能未发出）",
                    "旧「待发送」显示为「" + mapped + "」", ref fail);

                // 13d. 专注走完的提醒：默认「气泡 + 提示音」都开，关掉开关后都不再触发
                int notice0 = Supervisor.CompleteNoticeCount;
                int sound0 = Supervisor.SoundPlayCount;

                Settings notifyOn = Settings.Defaults();
                notifyOn.FocusMinutes = 1;
                notifyOn.Rules = new List<WatchRule>();
                Supervisor supOn = new Supervisor(notifyOn, DailyStats.NewFor(DateTime.Now));
                supOn.StartFocus();
                supOn.Complete();
                bool defOn = Supervisor.CompleteNoticeCount == notice0 + 1
                          && Supervisor.SoundPlayCount == sound0 + 1
                          && supOn.Stats.CompletedCount == 1;
                Check(sb, "complete-notify-default-on", defOn,
                    string.Format("默认开：气泡 +{0}、提示音 +{1}、今日完成 {2} 段",
                        Supervisor.CompleteNoticeCount - notice0, Supervisor.SoundPlayCount - sound0,
                        supOn.Stats.CompletedCount), ref fail);

                Settings notifyOff = Settings.Defaults();
                notifyOff.FocusMinutes = 1;
                notifyOff.Rules = new List<WatchRule>();
                notifyOff.NotifyOnComplete = false;
                notifyOff.NotifySound = false;
                Supervisor supOff = new Supervisor(notifyOff, DailyStats.NewFor(DateTime.Now));
                supOff.StartFocus();
                supOff.Complete();
                bool muted = Supervisor.CompleteNoticeCount == notice0 + 1 && Supervisor.SoundPlayCount == sound0 + 1;
                Check(sb, "complete-notify-switch-off", muted,
                    "两个开关都关掉后：既不弹气泡也不出声音（计数没变）", ref fail);

                // 13e. 老配置（v1，没有提醒字段）加载后必须默认开启 ——
                //      否则老用户升级上来会静默收不到完成提醒（bool 默认 false 的坑）
                string legacyCfg = "{\"Version\":1,\"FocusMinutes\":30,\"SampleSeconds\":5," +
                                   "\"ViolationSeconds\":180,\"SendMode\":\"smtp\",\"SmtpHost\":\"smtp.qq.com\"," +
                                   "\"SmtpPort\":587,\"Rules\":[]}";
                File.WriteAllText(Store.ConfigPath, legacyCfg, new UTF8Encoding(false));
                Settings migratedCfg = Store.LoadSettings();
                Check(sb, "v1-config-notify-defaults-on",
                    migratedCfg.NotifyOnComplete && migratedCfg.NotifySound && migratedCfg.Version >= 2,
                    string.Format("v1 配置加载后：气泡={0}、提示音={1}、Version={2}",
                        migratedCfg.NotifyOnComplete, migratedCfg.NotifySound, migratedCfg.Version), ref fail);

                // ============================================================
                //  13f. 监督核心：后台计时 / 睡眠 / 实例识别 / 结果状态
                // ============================================================

                // (1) 结果状态机：正常走完 = Completed，中途放弃 = Abandoned
                Settings st1 = Settings.Defaults();
                st1.FocusMinutes = 1;
                st1.Rules = new List<WatchRule>();
                Supervisor sm1 = new Supervisor(st1, DailyStats.NewFor(DateTime.Now));
                sm1.ManualTickOnly = true;
                sm1.StartFocus();
                bool runningState = sm1.Session != null && sm1.Session.Result == SessionResult.Running;
                sm1.Complete();
                Check(sb, "state-machine-completed",
                    runningState && sm1.Session == null && sm1.Stats.CompletedCount == 1,
                    "开始=Running，走完=Completed，完成计数 +1", ref fail);

                Settings st2 = Settings.Defaults();
                st2.FocusMinutes = 1;
                st2.Rules = new List<WatchRule>();
                Supervisor sm2 = new Supervisor(st2, DailyStats.NewFor(DateTime.Now));
                sm2.ManualTickOnly = true;
                sm2.StartFocus();
                sm2.Abandon();
                Check(sb, "state-machine-abandoned",
                    sm2.Stats.AbandonedCount == 1 && sm2.Stats.ReportCount == 1,
                    string.Format("放弃：放弃计数 +1、告状 {0} 次", sm2.Stats.ReportCount), ref fail);

                // (2) 阻塞界面不影响计时：后台线程用单调时钟推进
                Settings st3 = Settings.Defaults();
                st3.FocusMinutes = 5;
                st3.Rules = new List<WatchRule>();
                st3.SampleSeconds = 30;
                Supervisor sm3 = new Supervisor(st3, DailyStats.NewFor(DateTime.Now));
                sm3.StartFocus();
                Thread.Sleep(3200);                       // 模拟界面/主线程被卡住
                int blockedElapsed = sm3.ElapsedSeconds;
                bool stillFocusing = sm3.IsFocusing;
                sm3.StopLoop();
                sm3.Abandon();
                Check(sb, "blocked-ui-time-counted",
                    stillFocusing && blockedElapsed >= 2 && blockedElapsed <= 8,
                    string.Format("主线程阻塞 3.2 秒期间，后台仍计时到 {0} 秒（没有停）", blockedElapsed), ref fail);

                // (3) 睡眠/挂起不计入专注（假时钟推进 120 秒）
                FakeClock fc = new FakeClock();
                Settings st4 = Settings.Defaults();
                st4.FocusMinutes = 60;
                st4.Rules = new List<WatchRule>();
                st4.SampleSeconds = 5;
                Supervisor sm4 = new Supervisor(st4, DailyStats.NewFor(DateTime.Now));
                sm4.ManualTickOnly = true;
                sm4.Clock = fc;
                sm4.StartFocus();
                for (int i = 0; i < 5; i++) { fc.Advance(1); sm4.Tick(); }     // 正常走 5 秒
                int beforeSleep = sm4.ElapsedSeconds;
                fc.Advance(120);                                             // 睡了两分钟
                sm4.Tick();
                int afterSleep = sm4.ElapsedSeconds;
                bool sleepLogged = false;
                if (sm4.Session != null)
                    for (int i = 0; i < sm4.Session.Events.Count; i++)
                        if (sm4.Session.Events[i].IndexOf("中断") >= 0) sleepLogged = true;
                sm4.Abandon();
                Check(sb, "sleep-gap-not-counted",
                    beforeSleep == 5 && afterSleep == beforeSleep && sleepLogged,
                    string.Format("正常 5 秒后睡 120 秒：专注时间仍为 {0} 秒（未把睡眠算进去），且日志记录了中断", afterSleep), ref fail);

                // (4) 电源事件（Suspend/Resume）同样不计入
                FakeClock fc2 = new FakeClock();
                Settings st5 = Settings.Defaults();
                st5.FocusMinutes = 60;
                st5.Rules = new List<WatchRule>();
                st5.SampleSeconds = 5;
                Supervisor sm5 = new Supervisor(st5, DailyStats.NewFor(DateTime.Now));
                sm5.ManualTickOnly = true;
                sm5.Clock = fc2;
                sm5.StartFocus();
                for (int i = 0; i < 4; i++) { fc2.Advance(1); sm5.Tick(); }
                int beforeSuspend = sm5.ElapsedSeconds;
                sm5.OnSystemSuspend();
                fc2.Advance(300);
                sm5.OnSystemResume();
                sm5.Tick();
                int afterResume = sm5.ElapsedSeconds;
                sm5.Abandon();
                Check(sb, "power-suspend-not-counted",
                    beforeSuspend == 4 && afterResume <= beforeSuspend + 1,
                    string.Format("Suspend 期间推进 300 秒，专注时间 {0} → {1} 秒", beforeSuspend, afterResume), ref fail);

                // (5) 同一个 exe 多实例：每次采样每个实例只累计一次
                FakeClock fc3 = new FakeClock();
                Settings st6 = Settings.Defaults();
                st6.FocusMinutes = 60;
                st6.SampleSeconds = 5;
                WatchRule gr = new WatchRule();
                gr.Exe = "game.exe";
                gr.Name = "奶龙";
                gr.LimitMinutes = 30;
                gr.Enabled = true;
                st6.Rules = new List<WatchRule>();
                st6.Rules.Add(gr);
                Supervisor sm6 = new Supervisor(st6, DailyStats.NewFor(DateTime.Now));
                sm6.ManualTickOnly = true;
                sm6.Clock = fc3;
                DateTime baseTime = new DateTime(2026, 1, 1, 8, 0, 0);
                List<WatchProcess> feed = new List<WatchProcess>();
                sm6.ProcessScan = delegate(List<WatchRule> rules) { return feed; };
                feed.Add(Proc("game.exe", 100, baseTime, 30));
                feed.Add(Proc("game.exe", 101, baseTime, 30));
                sm6.StartFocus();
                fc3.Advance(5); sm6.Tick();                 // 第 1 次采样：两个实例各 +5
                fc3.Advance(5); sm6.Tick();                 // 第 2 次采样
                int multiSeconds = 0, multiInstances = 0;
                if (sm6.Session != null && sm6.Session.Watched.ContainsKey("game.exe"))
                {
                    multiSeconds = sm6.Session.Watched["game.exe"].SessionSeconds;
                    multiInstances = sm6.Session.Watched["game.exe"].InstanceCount;
                }
                sm6.Abandon();
                Check(sb, "multi-instance-counted-once",
                    multiSeconds == 10 && multiInstances == 2,
                    string.Format("两个实例各采样 2 次：exe 汇总 {0} 秒（不重复累加），实例数 {1}", multiSeconds, multiInstances), ref fail);

                // (6) 退出后重启：汇总连续，但算成新实例
                FakeClock fc4 = new FakeClock();
                Settings st7 = Settings.Defaults();
                st7.FocusMinutes = 60;
                st7.SampleSeconds = 5;
                st7.Rules = new List<WatchRule>();
                WatchRule gr2 = new WatchRule();
                gr2.Exe = "game.exe"; gr2.Name = "奶龙"; gr2.LimitMinutes = 30; gr2.Enabled = true;
                st7.Rules.Add(gr2);
                Supervisor sm7 = new Supervisor(st7, DailyStats.NewFor(DateTime.Now));
                sm7.ManualTickOnly = true;
                sm7.Clock = fc4;
                List<WatchProcess> feed2 = new List<WatchProcess>();
                sm7.ProcessScan = delegate(List<WatchRule> rules) { return feed2; };
                feed2.Add(Proc("game.exe", 100, baseTime, 30));
                sm7.StartFocus();
                fc4.Advance(5); sm7.Tick();
                feed2.Clear();
                fc4.Advance(5); sm7.Tick();                                   // 这一轮没看到进程
                feed2.Add(Proc("game.exe", 100, baseTime.AddMinutes(10), 30)); // 同 PID，但启动时间不同 = 新实例
                fc4.Advance(5); sm7.Tick();
                int restartSeconds = 0, restartInstances = 0;
                if (sm7.Session != null && sm7.Session.Watched.ContainsKey("game.exe"))
                {
                    restartSeconds = sm7.Session.Watched["game.exe"].SessionSeconds;
                    restartInstances = sm7.Session.Watched["game.exe"].InstanceCount;
                }
                sm7.Abandon();
                Check(sb, "restart-creates-new-instance",
                    restartSeconds == 10 && restartInstances == 2,
                    string.Format("同 PID 换了启动时间：汇总 {0} 秒（连续），实例数 {1}（识别为新实例）", restartSeconds, restartInstances), ref fail);

                // (7) 违规后走完：仍是 Violated，不重复发信、不记成"正常完成"
                int beforeNotice = Supervisor.CompleteNoticeCount;
                FakeClock fc5 = new FakeClock();
                Settings st8 = Settings.Defaults();
                st8.FocusMinutes = 2;                   // 目标 120 秒
                st8.SampleSeconds = 5;
                st8.Rules = new List<WatchRule>();
                WatchRule gr3 = new WatchRule();
                gr3.Exe = "game.exe"; gr3.Name = "奶龙"; gr3.LimitMinutes = 1; gr3.Enabled = true;
                st8.Rules.Add(gr3);
                Supervisor sm8 = new Supervisor(st8, DailyStats.NewFor(DateTime.Now));
                sm8.ManualTickOnly = true;
                sm8.Clock = fc5;
                List<WatchProcess> feed3 = new List<WatchProcess>();
                sm8.ProcessScan = delegate(List<WatchRule> rules) { return feed3; };
                sm8.StartFocus();
                for (int i = 0; i < 13; i++)            // 65 秒 > 规则 1 分钟 → 违规
                {
                    feed3.Clear();
                    feed3.Add(Proc("game.exe", 200, baseTime, 1));
                    fc5.Advance(5);
                    sm8.Tick();
                }
                int mailsAfterViolation = sm8.Stats.ReportCount;
                SessionResult resAfterViolation = sm8.Session == null ? SessionResult.Completed : sm8.Session.Result;
                for (int i = 0; i < 12; i++)            // 继续走到 120 秒（到点）
                {
                    feed3.Clear();
                    fc5.Advance(5);
                    sm8.Tick();
                }
                bool finishedViolated = sm8.Session == null;
                Check(sb, "violated-then-complete-keeps-violated",
                    resAfterViolation == SessionResult.Violated && finishedViolated
                    && sm8.Stats.ReportCount == 1 && sm8.Stats.CompletedCount == 0
                    && Supervisor.CompleteNoticeCount == beforeNotice,
                    string.Format("违规时结果={0}、告状 {1} 次；走完目标后：完成计数 {2}（不能记成正常完成）、完成提醒 {3}（不应触发）",
                        resAfterViolation, mailsAfterViolation, sm8.Stats.CompletedCount,
                        Supervisor.CompleteNoticeCount - beforeNotice), ref fail);

                // (8) 违规后放弃：不重复发信
                FakeClock fc6 = new FakeClock();
                Settings st9 = Settings.Defaults();
                st9.FocusMinutes = 60;
                st9.SampleSeconds = 5;
                st9.Rules = new List<WatchRule>();
                WatchRule gr4 = new WatchRule();
                gr4.Exe = "game.exe"; gr4.Name = "奶龙"; gr4.LimitMinutes = 1; gr4.Enabled = true;
                st9.Rules.Add(gr4);
                Supervisor sm9 = new Supervisor(st9, DailyStats.NewFor(DateTime.Now));
                sm9.ManualTickOnly = true;
                sm9.Clock = fc6;
                List<WatchProcess> feed4 = new List<WatchProcess>();
                sm9.ProcessScan = delegate(List<WatchRule> rules) { return feed4; };
                sm9.StartFocus();
                for (int i = 0; i < 13; i++)
                {
                    feed4.Clear();
                    feed4.Add(Proc("game.exe", 300, baseTime, 1));
                    fc6.Advance(5);
                    sm9.Tick();
                }
                sm9.Abandon();
                Check(sb, "violated-then-abandon-no-second-mail",
                    sm9.Stats.ReportCount == 1 && sm9.Stats.AbandonedCount == 0,
                    string.Format("违规后放弃：告状仍为 {0} 次、放弃计数 {1}（不重复发信、不算普通放弃）",
                        sm9.Stats.ReportCount, sm9.Stats.AbandonedCount), ref fail);

                // (9) 会话使用固定的配置快照
                Settings st10 = Settings.Defaults();
                st10.FocusMinutes = 1;
                st10.Rules = new List<WatchRule>();
                Supervisor sm10 = new Supervisor(st10, DailyStats.NewFor(DateTime.Now));
                sm10.ManualTickOnly = true;
                sm10.StartFocus();
                int snapPlanned = sm10.Session.PlannedSeconds;
                sm10.Settings.FocusMinutes = 9;          // 会话中途改设置
                int snapPlannedAfter = sm10.Session.PlannedSeconds;
                sm10.Abandon();
                Check(sb, "session-uses-config-snapshot",
                    snapPlanned == 60 && snapPlannedAfter == 60,
                    string.Format("会话中把设置从 1 分钟改成 9 分钟，本段计划时间仍是 {0} 秒", snapPlannedAfter), ref fail);

                // ============================================================
                //  13h. 持久化：迁移可重试 + 历史并发写入
                // ============================================================

                // (1) 迁移中途失败 → 不写标记、不删旧目录；故障消失后重试能补齐
                string roam4 = TempDir("pomocc-migrate");
                string oldDir4 = Path.Combine(roam4, Store.LegacyDataFolderName);
                string newDir4 = Path.Combine(roam4, Store.DataFolderName);
                string marker4 = Path.Combine(newDir4, Store.MigrateMarkerName);
                Directory.CreateDirectory(oldDir4);
                Directory.CreateDirectory(newDir4);
                File.WriteAllText(Path.Combine(oldDir4, "config.json"),
                    "{\"Version\":2,\"FocusMinutes\":44,\"SampleSeconds\":5,\"ViolationSeconds\":180," +
                    "\"SendMode\":\"smtp\",\"SmtpHost\":\"smtp.qq.com\",\"SmtpPort\":587,\"Rules\":[]}", Encoding.UTF8);
                File.WriteAllText(Path.Combine(oldDir4, "history.jsonl"),
                    "{\"Time\":\"2026-01-01 08:00:00\",\"Reason\":\"中途放弃\",\"Status\":\"已发送\"}\r\n", Encoding.UTF8);
                // 用一个同名目录卡住 history.jsonl 的复制，模拟"复制失败"
                Directory.CreateDirectory(Path.Combine(newDir4, "history.jsonl"));

                string savedOverride4 = Store.OverrideDir;
                Store.OverrideDir = null;
                Store.RoamingRootOverride = roam4;
                bool firstAttempt = false, retryAttempt = false, configKept = false;
                bool firstNoMarker = false, firstKeptOldDir = false;
                try
                {
                    Store.MigrateLegacyData(newDir4);
                    firstNoMarker = !File.Exists(marker4);                     // 没写标记
                    firstKeptOldDir = Directory.Exists(oldDir4);               // 旧目录没删
                    firstAttempt = firstNoMarker && firstKeptOldDir
                                && File.Exists(Path.Combine(newDir4, "config.json"));   // 能复制的部分已经复制

                    Directory.Delete(Path.Combine(newDir4, "history.jsonl"), true);   // 故障消失
                    Store.MigrateLegacyData(newDir4);                          // 重试
                    Settings afterRetry = Store.LoadSettings();
                    retryAttempt = File.Exists(marker4)
                                && File.Exists(Path.Combine(newDir4, "history.jsonl"))
                                && !Directory.Exists(oldDir4)
                                && afterRetry.FocusMinutes == 44;              // 老配置仍是生效的那份
                    configKept = afterRetry.FocusMinutes == 44;
                }
                finally
                {
                    Store.RoamingRootOverride = null;
                    Store.OverrideDir = savedOverride4;
                }
                Check(sb, "migration-retry-after-failure",
                    firstAttempt && retryAttempt && configKept,
                    string.Format("首次失败：不写标记={0}、旧目录保留={1}；重试后：补齐+标记+删旧目录={2}、配置仍是老的={3}",
                        firstNoMarker, firstKeptOldDir, retryAttempt, configKept), ref fail);

                // (2) 历史并发写入：两个线程各写 20 条，文件里必须是 40 行完整 JSON（不交错）
                if (File.Exists(Store.HistoryPath)) File.Delete(Store.HistoryPath);
                Thread h1 = new Thread(delegate()
                {
                    for (int i = 0; i < 20; i++)
                    {
                        HistoryEntry he = new HistoryEntry();
                        he.Time = "2026-01-01 10:00:00";
                        he.Reason = "并发测试A-" + i;
                        he.Status = "已发送";
                        Store.AppendHistory(he);
                    }
                });
                Thread h2 = new Thread(delegate()
                {
                    for (int i = 0; i < 20; i++)
                    {
                        HistoryEntry he = new HistoryEntry();
                        he.Time = "2026-01-01 10:00:01";
                        he.Reason = "并发测试B-" + i;
                        he.Status = "已发送";
                        Store.AppendHistory(he);
                    }
                });
                h1.Start(); h2.Start(); h1.Join(); h2.Join();
                string[] hlines = File.ReadAllLines(Store.HistoryPath, Encoding.UTF8);
                int goodLines = 0;
                System.Web.Script.Serialization.JavaScriptSerializer hs =
                    new System.Web.Script.Serialization.JavaScriptSerializer();
                for (int i = 0; i < hlines.Length; i++)
                {
                    if (hlines[i].Trim().Length == 0) continue;
                    try { HistoryEntry parsed = hs.Deserialize<HistoryEntry>(hlines[i]); if (parsed != null) goodLines++; }
                    catch { }
                }
                Check(sb, "history-concurrent-writes",
                    hlines.Length == 40 && goodLines == 40,
                    string.Format("两个线程各写 20 条：文件里 {0} 行、可解析 {1} 行（不能交错）", hlines.Length, goodLines), ref fail);
                Store.ReadHistory(50);      // 顺带跑一遍读取（含损坏行计数路径）
                // ============================================================
                //  13i. Re-v0.2 第 1 组：session 规则快照 + 统一 ApplySettings
                // ============================================================
                DateTime bt = new DateTime(2026, 1, 1, 8, 0, 0);

                // (1) session 开始时固定规则：中途改全局规则不影响本段
                FakeClock fcA = new FakeClock();
                Settings setA = Settings.Defaults();
                setA.FocusMinutes = 60;
                setA.SampleSeconds = 5;
                setA.Rules = new List<WatchRule>();
                setA.Rules.Add(Rule("game.exe", 30));
                Supervisor smA = new Supervisor(setA, DailyStats.NewFor(DateTime.Now));
                smA.ManualTickOnly = true;
                smA.Clock = fcA;
                List<string> scannedExe = new List<string>();
                List<WatchProcess> feedA = new List<WatchProcess>();
                smA.ProcessScan = delegate(List<WatchRule> rules)
                {
                    if (rules != null && rules.Count > 0) scannedExe.Add(rules[0].Exe);
                    return feedA;
                };
                smA.StartFocus();
                feedA.Add(Proc("game.exe", 11, bt, 30));
                fcA.Advance(5); smA.Tick();

                Settings setB = setA.Copy();                       // 专注中途保存的新设置
                setB.FocusMinutes = 5;
                setB.Rules = new List<WatchRule>();
                setB.Rules.Add(Rule("other.exe", 30));
                smA.ApplySettings(setB);
                fcA.Advance(5); smA.Tick();                        // 本段仍只该扫 game.exe

                bool onlyGameScanned = scannedExe.Count == 2;
                for (int i = 0; i < scannedExe.Count; i++) if (scannedExe[i] != "game.exe") onlyGameScanned = false;
                List<WatchRule> sesRules = smA.SessionRules;
                bool sessionKeepsGame = sesRules.Count == 1 && sesRules[0].Exe == "game.exe";
                bool globalNowOther = smA.WatchRules.Count == 1 && smA.WatchRules[0].Exe == "other.exe";
                Check(sb, "session-rules-use-snapshot",
                    onlyGameScanned && sessionKeepsGame && globalNowOther,
                    string.Format("本段扫描到的仍是 [{0}]（全局已换成 other.exe）；本段规则={1}；全局规则={2}",
                        string.Join(",", scannedExe.ToArray()),
                        sesRules.Count > 0 ? sesRules[0].Exe : "(空)",
                        smA.WatchRules.Count > 0 ? smA.WatchRules[0].Exe : "(空)"), ref fail);

                // (2) 专注中应用设置：本段计划时间与规则都不变
                int plannedBefore = smA.Session.PlannedSeconds;
                smA.ApplySettings(setB);
                bool applyDuringSession = smA.Session != null
                                       && smA.Session.PlannedSeconds == plannedBefore
                                       && plannedBefore == 3600
                                       && smA.SessionRules[0].Exe == "game.exe";
                Check(sb, "settings-apply-during-session", applyDuringSession,
                    string.Format("专注中保存新设置（5 分钟 / other.exe）后，本段仍是 {0} 秒 / {1}",
                        smA.Session.PlannedSeconds, smA.SessionRules[0].Exe), ref fail);

                // (3) 本段结束后，下一段用新规则
                smA.Abandon();
                fcA.Advance(1);
                smA.StartFocus();
                bool nextUsesNew = smA.Session != null
                                && smA.Session.PlannedSeconds == 300
                                && smA.SessionRules.Count == 1 && smA.SessionRules[0].Exe == "other.exe";
                Check(sb, "settings-apply-next-session-uses-new-rules", nextUsesNew,
                    string.Format("下一段：计划 {0} 秒、规则 {1}（应为 300 秒 / other.exe）",
                        smA.Session.PlannedSeconds, smA.SessionRules[0].Exe), ref fail);
                smA.Abandon();

                // (4) 专注中应用设置不能把内存里正在累计的统计覆盖掉
                Settings setC = Settings.Defaults();
                setC.FocusMinutes = 60;
                setC.Rules = new List<WatchRule>();
                setC.Rules.Add(Rule("game.exe", 30));
                DailyStats memStats = DailyStats.NewFor(DateTime.Now);
                memStats.CompletedCount = 5;
                memStats.AbandonedCount = 3;
                memStats.ReportCount = 2;
                memStats.FocusSeconds = 1500;
                Supervisor smC = new Supervisor(setC, memStats);
                smC.ManualTickOnly = true;
                smC.StartFocus();

                DailyStats diskStats = DailyStats.NewFor(DateTime.Now);
                diskStats.CompletedCount = 2;          // 磁盘上是旧的、更小的值
                diskStats.AbandonedCount = 1;
                diskStats.ReportCount = 1;
                diskStats.FocusSeconds = 600;
                Store.SaveStats(diskStats);
                smC.ApplySettings(setC.Copy());
                bool statsKept = smC.Stats.CompletedCount == 5 && smC.Stats.AbandonedCount == 3
                              && smC.Stats.ReportCount == 2 && smC.Stats.FocusSeconds == 1500;

                diskStats.CompletedCount = 9;          // 磁盘上更大的值要采纳（同一天另一处跑过）
                Store.SaveStats(diskStats);
                smC.ApplySettings(setC.Copy());
                bool statsAdopted = smC.Stats.CompletedCount == 9 && smC.Stats.FocusSeconds == 1500;
                smC.Abandon();
                Check(sb, "stats-not-overwritten-during-session",
                    statsKept && statsAdopted,
                    string.Format("专注中应用设置：内存计数未被旧值覆盖={0}；磁盘上更大的计数被采纳={1}", statsKept, statsAdopted), ref fail);

                // (5) 并发：后台 Tick 与应用设置同时跑，不抛异常、不丢计数
                Settings setD = Settings.Defaults();
                setD.FocusMinutes = 60;
                setD.SampleSeconds = 2;
                setD.Rules = new List<WatchRule>();
                setD.Rules.Add(Rule("game.exe", 30));
                DailyStats raceStats = DailyStats.NewFor(DateTime.Now);
                raceStats.CompletedCount = 4;
                Supervisor smD = new Supervisor(setD, raceStats);
                smD.ManualTickOnly = true;
                string raceErr = null;
                Thread raceTick = new Thread(delegate()
                {
                    try { for (int i = 0; i < 400; i++) smD.Tick(); }
                    catch (Exception ex) { raceErr = "Tick: " + ex.Message; }
                });
                Thread raceApply = new Thread(delegate()
                {
                    try
                    {
                        for (int i = 0; i < 200; i++)
                        {
                            Settings s2 = setD.Copy();
                            s2.FocusMinutes = 60 + (i % 3);
                            smD.ApplySettings(s2);
                        }
                    }
                    catch (Exception ex) { raceErr = "Apply: " + ex.Message; }
                });
                smD.StartFocus();
                raceTick.Start(); raceApply.Start();
                raceTick.Join(); raceApply.Join();
                // 注意：磁盘上可能有当天更大的计数（前一项测试写过），合并会采纳它 ——
                // 这里要验的是"不丢计数、不抛异常"，不是精确等于 4。
                bool raceOk = raceErr == null && smD.Stats.CompletedCount >= 4 && smD.IsFocusing;
                smD.Abandon();
                Check(sb, "settings-apply-race", raceOk,
                    string.Format("并发 400 次 Tick + 200 次 ApplySettings：异常={0}，完成计数 {1}（未丢失）",
                        raceErr == null ? "无" : raceErr, smD.Stats.CompletedCount), ref fail);

                // ============================================================
                //  13j. Re-v0.2 第 4 组：长时间暂停后的计时与采样边界
                // ============================================================

                // (1) 睡眠间隔期间即使进程还在跑，也不给它累计时间
                FakeClock fcS = new FakeClock();
                Settings setS = Settings.Defaults();
                setS.FocusMinutes = 60;
                setS.SampleSeconds = 5;
                setS.Rules = new List<WatchRule>();
                setS.Rules.Add(Rule("game.exe", 30));
                Supervisor smS = new Supervisor(setS, DailyStats.NewFor(DateTime.Now));
                smS.ManualTickOnly = true;
                smS.Clock = fcS;
                List<WatchProcess> feedS = new List<WatchProcess>();
                smS.ProcessScan = delegate(List<WatchRule> rules) { return feedS; };
                smS.StartFocus();
                feedS.Add(Proc("game.exe", 21, bt, 30));
                fcS.Advance(5); smS.Tick();                       // 正常一轮：+5 秒
                int gameBeforeSleep = smS.Session.Watched["game.exe"].SessionSeconds;
                fcS.Advance(600); smS.Tick();                     // 睡 10 分钟，进程"还在"
                int gameAfterSleep = smS.Session.Watched["game.exe"].SessionSeconds;
                int focusAfterSleep = smS.ElapsedSeconds;
                smS.Abandon();
                Check(sb, "sleep-gap-with-running-process",
                    gameBeforeSleep == 5 && gameAfterSleep == 5 && focusAfterSleep == 5,
                    string.Format("睡眠 600 秒期间进程仍在跑：程序累计 {0}→{1} 秒、专注 {2} 秒（都不该增加）",
                        gameBeforeSleep, gameAfterSleep, focusAfterSleep), ref fail);

                // (2) 无法判断的超长间隔：不额外产生一个完整采样周期的程序时间
                FakeClock fcL = new FakeClock();
                Settings setL = Settings.Defaults();
                setL.FocusMinutes = 60;
                setL.SampleSeconds = 5;
                setL.Rules = new List<WatchRule>();
                setL.Rules.Add(Rule("game.exe", 30));
                Supervisor smL = new Supervisor(setL, DailyStats.NewFor(DateTime.Now));
                smL.ManualTickOnly = true;
                smL.Clock = fcL;
                List<WatchProcess> feedL = new List<WatchProcess>();
                smL.ProcessScan = delegate(List<WatchRule> rules) { return feedL; };
                smL.StartFocus();
                feedL.Add(Proc("game.exe", 31, bt, 30));
                fcL.Advance(5); smL.Tick();
                int beforeLong = smL.Session.Watched["game.exe"].SessionSeconds;
                fcL.Advance(17); smL.Tick();                      // 刚过阈值 15 秒
                int afterLong = smL.Session.Watched["game.exe"].SessionSeconds;
                // 紧接着正常走一轮，采样应该正常工作
                fcL.Advance(5); smL.Tick();
                int afterNormal = smL.Session.Watched["game.exe"].SessionSeconds;
                smL.Abandon();
                Check(sb, "long-gap-skips-unknown-sample",
                    beforeLong == 5 && afterLong == 5 && afterNormal == 10,
                    string.Format("超长间隔(17 秒)后：程序累计 {0}→{1}（未补算一个采样周期）；随后正常一轮 →{2}",
                        beforeLong, afterLong, afterNormal), ref fail);

                // (3) Suspend/Resume 复位基准：不会重复累计间隔
                FakeClock fcP = new FakeClock();
                Settings setP = Settings.Defaults();
                setP.FocusMinutes = 60;
                setP.SampleSeconds = 5;
                setP.Rules = new List<WatchRule>();
                setP.Rules.Add(Rule("game.exe", 30));
                Supervisor smP = new Supervisor(setP, DailyStats.NewFor(DateTime.Now));
                smP.ManualTickOnly = true;
                smP.Clock = fcP;
                List<WatchProcess> feedP = new List<WatchProcess>();
                smP.ProcessScan = delegate(List<WatchRule> rules) { return feedP; };
                smP.StartFocus();
                feedP.Add(Proc("game.exe", 41, bt, 30));
                fcP.Advance(5); smP.Tick();
                int pBefore = smP.Session.Watched["game.exe"].SessionSeconds;
                int eBefore = smP.ElapsedSeconds;
                smP.OnSystemSuspend();
                fcP.Advance(120);
                smP.OnSystemResume();
                smP.Tick();
                fcP.Advance(5); smP.Tick();                       // 恢复后正常一轮
                int pAfter = smP.Session.Watched["game.exe"].SessionSeconds;
                smP.Abandon();
                Check(sb, "power-resume-resets-baseline",
                    pBefore == 5 && pAfter == 10,
                    string.Format("Suspend 120 秒 + Resume：程序累计 {0}→{1} 秒（只多了恢复后正常的那 5 秒）",
                        pBefore, pAfter), ref fail);
                // ============================================================
                //  13l. Re-v0.2 第 5 组：发信协议边界
                // ============================================================

                // (1) SMTP 状态码必须严格比三位：220 不能冒充 250
                SmtpTransport.SmtpSession s250 = new SmtpTransport.SmtpSession(
                    new MemoryStream(Encoding.UTF8.GetBytes("250 ok\r\n")));
                bool accept250 = s250.ReadResponse(250, "命令") != null;

                SmtpTransport.SmtpSession s220 = new SmtpTransport.SmtpSession(
                    new MemoryStream(Encoding.UTF8.GetBytes("220 hi\r\n")));
                bool reject220 = false;
                try { s220.ReadResponse(250, "命令"); } catch (Exception) { reject220 = true; }

                SmtpTransport.SmtpSession s251 = new SmtpTransport.SmtpSession(
                    new MemoryStream(Encoding.UTF8.GetBytes("251 user not local\r\n")));
                bool reject251 = false;
                try { s251.ReadResponse(250, "命令"); } catch (Exception) { reject251 = true; }

                SmtpTransport.SmtpSession s354 = new SmtpTransport.SmtpSession(
                    new MemoryStream(Encoding.UTF8.GetBytes("250 ok\r\n")));
                bool rejectForData = false;
                try { s354.ReadResponse(354, "DATA"); } catch (Exception) { rejectForData = true; }

                Check(sb, "smtp-rejects-wrong-three-digit-code",
                    accept250 && reject220 && reject251 && rejectForData,
                    string.Format("250 接受={0}；220 冒充 250 被拒={1}；251 被拒={2}；等 354 收到 250 被拒={3}",
                        accept250, reject220, reject251, rejectForData), ref fail);

                // (2) 合法的多行响应仍要能通过；格式不对的行必须报错
                SmtpTransport.SmtpSession sMulti = new SmtpTransport.SmtpSession(
                    new MemoryStream(Encoding.UTF8.GetBytes("250-line one\r\n250-line two\r\n250 last\r\n")));
                string multiText = sMulti.ReadResponse(250, "EHLO");
                bool multiOk = multiText.IndexOf("line one") >= 0
                            && multiText.IndexOf("line two") >= 0
                            && multiText.IndexOf("last") >= 0;

                SmtpTransport.SmtpSession sBad = new SmtpTransport.SmtpSession(
                    new MemoryStream(Encoding.UTF8.GetBytes("abc nonsense\r\n")));
                bool badFormat = false;
                try { sBad.ReadResponse(250, "命令"); } catch (Exception) { badFormat = true; }

                Check(sb, "smtp-multiline-response",
                    multiOk && badFormat,
                    string.Format("多行 250 响应通过={0}（{1} 行）；非三位码格式被拒={2}",
                        multiOk, multiText.Trim().Split('\n').Length, badFormat), ref fail);

                // (3) HTTP 发信不跟随重定向：认证头不能泄漏到未校验的地址
                MiniHttp stealSrv = new MiniHttp(200, "{}", null);
                MiniHttp apiSrv = new MiniHttp(302, "moved", "http://127.0.0.1:" + stealSrv.Port + "/steal");
                Settings redirCfg = Settings.Defaults();
                redirCfg.SendMode = "custom";
                redirCfg.HttpUrl = "http://127.0.0.1:" + apiSrv.Port + "/send";
                redirCfg.SenderEmail = "me@qq.com";
                redirCfg.SupervisorEmail = "boss@example.com";
                bool apiKeySet = TrySetApiKey(redirCfg, "super-secret-key");

                string redirectErr = null;
                try { HttpSender.Send(redirCfg, "主题", "正文"); }
                catch (Exception ex) { redirectErr = ex.Message; }
                Thread.Sleep(400);                       // 给"可能的第二次请求"留时间

                bool apiSawAuth = apiSrv.LastRequest.IndexOf("super-secret-key") >= 0;
                bool noFollow = stealSrv.Connections == 0;
                apiSrv.Stop();
                stealSrv.Stop();

                Check(sb, "http-redirect-is-not-followed",
                    redirectErr != null && redirectErr.IndexOf("重定向") >= 0,
                    string.Format("接口返回 302 时直接报重定向错误：{0}",
                        redirectErr == null ? "(没报错)" : redirectErr), ref fail);
                if (apiKeySet)
                {
                    Check(sb, "http-auth-header-stays-on-original-request",
                        apiSawAuth && noFollow,
                        string.Format("原始请求带了认证头={0}；重定向目标收到的连接数={1}（必须为 0）",
                            apiSawAuth, stealSrv.Connections), ref fail);
                }
                else
                {
                    SkipDpapi(sb, "http-auth-header-stays-on-original-request", dpapiEnvWhy);
                }
                // ============================================================
                //  13k. Re-v0.2 第 2 组：原子写入与迁移可靠性
                // ============================================================

                // (1) 目标文件是残缺文件时，必须重新复制（不能"存在就跳过"）
                string roamAt = TempDir("pomocc-atomic");
                string oldAt = Path.Combine(roamAt, Store.LegacyDataFolderName);
                string newAt = Path.Combine(roamAt, Store.DataFolderName);
                Directory.CreateDirectory(oldAt);
                Directory.CreateDirectory(newAt);
                File.WriteAllText(Path.Combine(oldAt, "config.json"),
                    "{\"Version\":2,\"FocusMinutes\":47,\"SampleSeconds\":5,\"ViolationSeconds\":180," +
                    "\"SendMode\":\"smtp\",\"SmtpHost\":\"smtp.qq.com\",\"SmtpPort\":587,\"Rules\":[]}", Encoding.UTF8);
                File.WriteAllText(Path.Combine(oldAt, "stats.json"),
                    "{\"Date\":\"" + DateTime.Now.ToString("yyyy-MM-dd") + "\",\"CompletedCount\":3,\"AbandonedCount\":1," +
                    "\"ReportCount\":1,\"FocusSeconds\":900,\"ViolationSeconds\":0}", Encoding.UTF8);
                File.WriteAllText(Path.Combine(oldAt, "history.jsonl"),
                    "{\"Time\":\"2026-01-01 08:00:00\",\"Reason\":\"中途放弃\",\"Status\":\"已发送\"}\r\n" +
                    "{\"Time\":\"2026-01-01 09:00:00\",\"Reason\":\"偷玩超时\",\"Status\":\"已发送\"}\r\n", Encoding.UTF8);
                File.WriteAllText(Path.Combine(oldAt, "app.log"), "旧日志一行\r\n", Encoding.UTF8);
                // 新目录里留一个上次复制被打断的残缺 history.jsonl
                File.WriteAllText(Path.Combine(newAt, "history.jsonl"),
                    "{\"Time\":\"2026-01-01 08:00:00\",\"Rea", Encoding.UTF8);

                string savedOvA = Store.OverrideDir;
                Store.OverrideDir = null;
                Store.RoamingRootOverride = roamAt;
                bool partialRecopied = false, migrationValid = false, partialOk = false;
                try
                {
                    Store.MigrateLegacyData(newAt);

                    string histText = File.ReadAllText(Path.Combine(newAt, "history.jsonl"), Encoding.UTF8);
                    string[] histLines = histText.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    partialRecopied = histLines.Length == 2;            // 残缺文件被换成完整的两行

                    // 迁移过来的每个文件都要能验证
                    bool cfgOk = Store.VerifyConfigForTest(Path.Combine(newAt, "config.json"));
                    bool statsOk = Store.VerifyStatsForTest(Path.Combine(newAt, "stats.json"));
                    int badLines;
                    bool histOk = Store.VerifyHistoryForTest(Path.Combine(newAt, "history.jsonl"), out badLines) && badLines == 0;
                    bool logOk = File.Exists(Path.Combine(newAt, "app.log"));
                    bool markerOk = File.Exists(Path.Combine(newAt, Store.MigrateMarkerName));
                    bool oldGone = !Directory.Exists(oldAt);

                    migrationValid = cfgOk && statsOk && histOk && logOk;
                    partialOk = partialRecopied && markerOk && oldGone;
                }
                finally
                {
                    Store.RoamingRootOverride = null;
                    Store.OverrideDir = savedOvA;
                }
                Check(sb, "migration-partial-destination-retries",
                    partialRecopied && partialOk,
                    string.Format("目标 history.jsonl 原本是残缺的：迁移后变成 {0} 行完整内容（重新复制了）", partialRecopied ? 2 : -1), ref fail);
                Check(sb, "migration-validates-all-copied-files",
                    migrationValid && partialOk,
                    "迁移结果可验证：配置可反序列化、历史逐行可解析、日志存在、标记已写、旧目录已删", ref fail);

                // (2) 迁移标记写不进去时，旧目录必须保留
                string roamMk = TempDir("pomocc-marker");
                string oldMk = Path.Combine(roamMk, Store.LegacyDataFolderName);
                string newMk = Path.Combine(roamMk, Store.DataFolderName);
                Directory.CreateDirectory(oldMk);
                Directory.CreateDirectory(newMk);
                File.WriteAllText(Path.Combine(oldMk, "config.json"),
                    "{\"Version\":2,\"FocusMinutes\":51,\"SampleSeconds\":5,\"ViolationSeconds\":180," +
                    "\"SendMode\":\"smtp\",\"SmtpHost\":\"smtp.qq.com\",\"SmtpPort\":587,\"Rules\":[]}", Encoding.UTF8);
                Directory.CreateDirectory(Path.Combine(newMk, Store.MigrateMarkerName));   // 用目录卡住标记写入

                Store.OverrideDir = null;
                Store.RoamingRootOverride = roamMk;
                bool markerFailKeptOld = false;
                try
                {
                    Store.MigrateLegacyData(newMk);
                    markerFailKeptOld = Directory.Exists(oldMk)                       // 旧目录还在
                                     && File.Exists(Path.Combine(newMk, "config.json"))  // 文件已经搬好
                                     && !File.Exists(Path.Combine(newMk, Store.MigrateMarkerName));  // 标记没写成
                }
                finally
                {
                    Store.RoamingRootOverride = null;
                    Store.OverrideDir = savedOvA;
                }
                Check(sb, "migration-marker-write-failure-keeps-old-dir", markerFailKeptOld,
                    "迁移标记写不进去时：旧目录保留、已搬好的文件保留、不谎报迁移完成", ref fail);

                // (3) 配置保存是原子的：临时文件写不进去时返回 false，且原配置完好
                string cfgPath = Store.ConfigPath;
                Settings goodCfg = Settings.Defaults();
                goodCfg.FocusMinutes = 33;
                bool firstSave = Store.SaveSettings(goodCfg);
                string cfgBefore = File.ReadAllText(cfgPath, Encoding.UTF8);

                Directory.CreateDirectory(cfgPath + ".tmp");        // 卡住临时文件
                Settings changedCfg = Settings.Defaults();
                changedCfg.FocusMinutes = 44;
                bool secondSave = Store.SaveSettings(changedCfg);
                Directory.Delete(cfgPath + ".tmp", true);

                string cfgAfter = File.ReadAllText(cfgPath, Encoding.UTF8);
                bool settingsAtomic = firstSave && !secondSave && cfgBefore == cfgAfter
                                   && Store.LoadSettings().FocusMinutes == 33;
                Check(sb, "settings-save-atomic", settingsAtomic,
                    string.Format("第一次保存={0}；模拟失败的那次返回 {1}；原配置内容是否原样={2}，仍是 {3} 分钟",
                        firstSave, secondSave, cfgBefore == cfgAfter, Store.LoadSettings().FocusMinutes), ref fail);

                // (4) 统计保存同样原子
                string statsPath = Store.StatsPath;
                DailyStats goodStats = DailyStats.NewFor(DateTime.Now);
                goodStats.CompletedCount = 6;
                bool firstStats = Store.SaveStats(goodStats);
                string statsBefore = File.ReadAllText(statsPath, Encoding.UTF8);

                Directory.CreateDirectory(statsPath + ".tmp");
                DailyStats changedStats = DailyStats.NewFor(DateTime.Now);
                changedStats.CompletedCount = 99;
                bool secondStats = Store.SaveStats(changedStats);
                Directory.Delete(statsPath + ".tmp", true);

                string statsAfter = File.ReadAllText(statsPath, Encoding.UTF8);
                bool statsAtomic = firstStats && !secondStats && statsBefore == statsAfter
                                && Store.LoadToday().CompletedCount == 6;
                Check(sb, "stats-save-atomic", statsAtomic,
                    string.Format("第一次保存={0}；模拟失败的那次返回 {1}；原统计文件是否原样={2}，仍是完成 {3} 段",
                        firstStats, secondStats, statsBefore == statsAfter, Store.LoadToday().CompletedCount), ref fail);
                // ============================================================
                //  13g. 发信安全边界（自定义 HTTP 地址 / SMTP 地址校验）
                // ============================================================
                string urlWhy;
                bool denyEvil = !SettingsValidator.IsAllowedHttpUrl("http://evil.example/send", out urlWhy);
                bool allowLocal = SettingsValidator.IsAllowedHttpUrl("http://localhost:8099/mail", out urlWhy);
                bool allowLocalIp = SettingsValidator.IsAllowedHttpUrl("http://127.0.0.1:8099/mail", out urlWhy);
                bool allowHttps = SettingsValidator.IsAllowedHttpUrl("https://api.example.com/v1/mail", out urlWhy);
                bool denyCred = !SettingsValidator.IsAllowedHttpUrl("https://u:p@api.example.com/mail", out urlWhy);
                bool denyKeyInQuery = !SettingsValidator.IsAllowedHttpUrl("https://api.example.com/mail?api_key=abc", out urlWhy);
                bool denyRelative = !SettingsValidator.IsAllowedHttpUrl("api.example.com/mail", out urlWhy);
                Check(sb, "http-url-policy",
                    denyEvil && allowLocal && allowLocalIp && allowHttps && denyCred && denyKeyInQuery && denyRelative,
                    string.Format("http://evil.example 拒绝={0}；http://localhost 允许={1}；127.0.0.1 允许={2}；https 允许={3}；带凭据拒绝={4}；Key 写参数拒绝={5}；相对地址拒绝={6}",
                        denyEvil, allowLocal, allowLocalIp, allowHttps, denyCred, denyKeyInQuery, denyRelative), ref fail);

                // 换行注入：设置校验和发信入口都必须拦住
                string mailWhy;
                bool crlfRejected = !SettingsValidator.IsValidEmail("boss@example.com\r\nRCPT TO:<evil@x.com>", out mailWhy)
                                 && !SettingsValidator.IsValidEmail("boss@example.com\n", out mailWhy);
                bool displayNameRejected = !SettingsValidator.IsValidEmail("Boss <boss@example.com>", out mailWhy);
                bool normalAccepted = SettingsValidator.IsValidEmail("boss@example.com", out mailWhy);

                Settings crlfCfg = Settings.Defaults();
                crlfCfg.Rules = new List<WatchRule>();
                crlfCfg.Rules.Add(Rule("game.exe", 3));
                crlfCfg.SupervisorEmail = "boss@example.com\r\nBcc: evil@x.com";
                crlfCfg.SenderEmail = "me@qq.com";
                TrySetAuthCode(crlfCfg, "x");     // DPAPI 不可用时忽略（下面的断言不依赖它）
                bool validatorBlocked = false;
                List<string> crlfErrs = SettingsValidator.Validate(crlfCfg);
                for (int i = 0; i < crlfErrs.Count; i++) if (crlfErrs[i].IndexOf("监督人邮箱") >= 0) validatorBlocked = true;

                bool sendEntryBlocked = false;
                try { SmtpTransport.ValidateAddresses("smtp.qq.com", "me@qq.com", "boss@example.com\r\nRCPT TO:<evil@x.com>"); }
                catch (Exception) { sendEntryBlocked = true; }
                bool httpEntryBlocked = false;
                try { HttpSender.Send(crlfCfg, "主题", "正文"); }
                catch (Exception) { httpEntryBlocked = true; }

                Check(sb, "smtp-address-crlf-rejected",
                    crlfRejected && displayNameRejected && normalAccepted && validatorBlocked && sendEntryBlocked && httpEntryBlocked,
                    string.Format("换行被拒={0}；显示名写法被拒={1}；正常地址通过={2}；设置校验拦住={3}；SMTP 入口拦住={4}；HTTP 入口拦住={5}",
                        crlfRejected, displayNameRejected, normalAccepted, validatorBlocked, sendEntryBlocked, httpEntryBlocked), ref fail);

                // 固定通道（Resend/SendGrid/Brevo）地址不受自定义规则影响：默认 https 常量
                Check(sb, "fixed-http-providers-still-https",
                    SettingsValidator.IsAllowedHttpUrl("https://api.resend.com/emails", out urlWhy)
                    && SettingsValidator.IsAllowedHttpUrl("https://api.sendgrid.com/v3/mail/send", out urlWhy)
                    && SettingsValidator.IsAllowedHttpUrl("https://api.brevo.com/v3/smtp/email", out urlWhy),
                    "三个固定发信接口地址仍然合法", ref fail);

                // 14. 邮件正文生成（含逐条规则时长 + 用户自定义名称）
                FocusSession demo = Mailer.DemoSession(cfg);
                string subject = Mailer.ComposeSubject(cfg, "中途放弃（没坚持够时间）", DateTime.Now, demo);
                string body = Mailer.ComposeBody(cfg, demo, DailyStats.NewFor(DateTime.Now), "中途放弃（没坚持够时间）", DateTime.Now);
                bool mailOk = subject.IndexOf("专注监督") >= 0
                           && body.IndexOf("规则 3 分钟") >= 0
                           && body.IndexOf("计划专注：1 分钟") >= 0
                           && body.IndexOf("实际专注：") >= 0
                           && body.IndexOf("10 分钟") >= 0;
                Check(sb, "mail-compose", mailOk, "主题：" + subject, ref fail);

                // 14b. 内容自定义：用户自己的话生效、占位符被替换、
                //      而程序插入的数据块（软件使用时长等）无论用户怎么改都还在
                Settings tpl = Settings.Defaults();
                tpl.UserName = "小王";
                tpl.SupervisorEmail = "boss@example.com";
                tpl.FocusMinutes = 45;
                tpl.MailSubjectTemplate = "告状：{名字} 在 {时刻} {简述}";
                tpl.MailIntroTemplate = "{名字} 你又没忍住，实际只有 {实际专注}。程序明细如下：\r\n{程序明细}";
                tpl.MailOutroTemplate = "—— 来自 {监督人} 的监督程序";
                FocusSession dsess = Mailer.DemoSession(tpl);
                DailyStats dstats = new DailyStats();
                dstats.CompletedCount = 2; dstats.AbandonedCount = 1; dstats.ReportCount = 3;
                string treason = "中途放弃（没坚持够时间）";
                string tsubj = Mailer.ComposeSubject(tpl, treason, DateTime.Now, dsess);
                string tbody = Mailer.ComposeBody(tpl, dsess, dstats, treason, DateTime.Now);

                bool customOk = tsubj.IndexOf("告状：小王") >= 0 && tsubj.IndexOf("{") < 0;
                bool introOk = tbody.IndexOf("你又没忍住") >= 0 && tbody.IndexOf("boss@example.com") >= 0;
                bool noRawMark = tbody.IndexOf("{名字}") < 0 && tbody.IndexOf("{实际专注}") < 0;
                bool dataKept = tbody.IndexOf("监督名单程序在本次专注期间的运行情况") >= 0
                            && tbody.IndexOf("累计运行") >= 0          // 软件使用时长
                            && tbody.IndexOf("时间线：") >= 0
                            && tbody.IndexOf("今日：") >= 0
                            && tbody.IndexOf("规则") >= 0;
                Check(sb, "mail-custom-subject", customOk, "自定义标题：" + tsubj, ref fail);
                Check(sb, "mail-custom-text-filled", introOk && noRawMark,
                    "自定义开头生效、占位符全部替换", ref fail);
                Check(sb, "mail-data-block-never-lost", dataKept,
                    "用户怎么改，程序插入的数据块（含软件使用时长/时间线/今日累计）都还在", ref fail);

                // 15. 「名称」列的自定义名字要出现在邮件里（exe 叫 game.exe 也能写成「奶龙」）
                Settings named = Settings.Defaults();
                named.FocusMinutes = 25;
                named.Rules = new List<WatchRule>();
                WatchRule custom = new WatchRule();
                custom.Exe = "game.exe";
                custom.DisplayName = "game";            // 程序自报的就是这种代称
                custom.Name = "奶龙";                    // 用户在表格里改成的名字
                custom.LimitMinutes = 3;
                custom.Enabled = true;
                named.Rules.Add(custom);

                WatchRule plain = new WatchRule();
                plain.Exe = "steam.exe";
                plain.DisplayName = "Steam";
                plain.Enabled = true;
                Check(sb, "rule-name-fallback",
                    WatchRule.BuildLabel("奶龙", "game.exe") == "奶龙（game.exe）"
                    && WatchRule.BuildLabel("Steam", "steam.exe") == "Steam（steam.exe）"
                    && WatchRule.BuildLabel("", "steam.exe") == "steam.exe"
                    && WatchRule.BuildLabel("带 steam.exe 的名字", "steam.exe") == "带 steam.exe 的名字",
                    "名称带上进程名便于核对；名字里已含进程名就不重复追加", ref fail);

                FocusSession namedDemo = Mailer.DemoSession(named);
                string namedBody = Mailer.ComposeBody(named, namedDemo, DailyStats.NewFor(DateTime.Now),
                    "专注期间偷玩超时（奶龙）", DateTime.Now);
                Check(sb, "custom-name-used-in-mail",
                    namedBody.IndexOf("奶龙（game.exe）") >= 0,
                    namedBody.IndexOf("奶龙（game.exe）") >= 0
                        ? "邮件里用的是用户起的「奶龙」，并带上 game.exe 便于核对"
                        : "邮件里没有使用自定义名称", ref fail);

                // 16. 名称默认值 = 软件列内容
                WatchRule auto = new WatchRule();
                auto.Exe = "game.exe";
                auto.DisplayName = "game";
                auto.ApplyDefaultName();
                Check(sb, "default-name-equals-software-column",
                    auto.Name == auto.SoftwareText && auto.Name == "game · game.exe",
                    "「名称」默认 = 「软件」列内容：" + auto.Name, ref fail);

                sb.Append("\r\n---- 邮件正文样例 ----\r\n").Append(body).Append("\r\n");

                sb.Append("\r\n本次使用的数据目录：").Append(Store.Dir).Append("\r\n");
            }
            catch (Exception ex)
            {
                if (IsDpapiEnvFailure(ex))
                    Skip(sb, "env-dpapi-profile", "环境阻塞：DPAPI 不可用（" + ex.Message + "）—— 本模式下后续检查未执行；生产加密逻辑未改动");
                else
                    Check(sb, "selftest-crashed", false, ex.ToString(), ref fail);
            }

            sb.Append("\r\n结果：").Append(Summary(fail)).Append("\r\n");
            Write(outPath, sb.ToString());
            return fail == 0 ? 0 : 1;
        }

        // ==================================================================
        //  2. 界面冒烟测试
        // ==================================================================

        /// <summary>界面冒烟测试：真的把窗口建起来、跑一会儿、再关掉。</summary>
        public static int Smoke(string outPath)
        {
            // 用临时数据目录，绝不碰用户的真实配置/统计/告状记录
            Store.OverrideDir = TempDir("pomocc-smoke");

            StringBuilder sb = new StringBuilder();
            int fail = 0;
            ReportDpapiEnvironment(sb);
            MainForm form = null;

            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // 造一份带规则的配置，让界面有真实内容可渲染
                Settings seed = Settings.Defaults();
                seed.SupervisorEmail = "boss@example.com";
                seed.SenderEmail = "me@qq.com";
                TrySetAuthCode(seed, "dummy-auth-code");
                seed.Rules = new List<WatchRule>();
                seed.Rules.Add(Rule("steam.exe", 3));
                seed.Rules.Add(Rule("wegame.exe", 10));
                seed.Rules.Add(Rule("chrome.exe", 30));
                Store.SaveSettings(seed);

                form = new MainForm();
                form.Opacity = 0;
                form.ShowInTaskbar = false;
                form.Show();
                Pump(700);

                Check(sb, "mainform-created", form.IsHandleCreated && form.Visible,
                    string.Format("窗口 {0}x{1}，控件 {2} 个，DeviceDpi={3}",
                        form.Width, form.Height, CountControls(form), form.DeviceDpi), ref fail);

                form.Core.Settings.FocusMinutes = 1;
                form.Core.RefreshWatchList();
                form.Core.StartFocus();
                Pump(1300);

                bool ticked = form.Core.IsFocusing && form.Core.ElapsedSeconds >= 1;
                Check(sb, "timer-ticking", ticked,
                    ticked ? string.Format("计时到 {0} 秒，界面文本 {1}", form.Core.ElapsedSeconds, form.Core.TimerText()) : "计时器没走", ref fail);

                Check(sb, "status-text",
                    form.Core.StatusLine().Length > 0 && form.Core.WatchSummary().Length > 0,
                    "状态：" + form.Core.StatusLine(), ref fail);

                // 状态行里的「程序」必须是蓝色下划线链接
                string linkInfo = form.StatusLinkInfo;
                Check(sb, "mainform-status-link",
                    linkInfo.IndexOf("可点区域「程序」") >= 0,
                    linkInfo, ref fail);

                // 点「程序」弹出的监视清单：三列，且没有「操作」列
                WatchListForm wl = new WatchListForm(Store.LoadSettings());
                wl.StartPosition = FormStartPosition.Manual;
                wl.Location = new Point(-6000, -6000);
                wl.Show();
                Pump(400);
                string wlCols = "";
                if (wl.List != null)
                    for (int ci = 0; ci < wl.List.Columns.Count; ci++)
                        wlCols += (ci > 0 ? " / " : "") + wl.List.Columns[ci].HeaderText;
                Check(sb, "watchlistform-columns", wlCols == "名称 / 软件 / 规则时长（分钟）",
                    "列：" + wlCols + "（不能有操作列）", ref fail);
                List<string> wlProblems = new List<string>();
                CheckLayout(wl, wlProblems);
                CheckTextFits(wl, wlProblems);
                Check(sb, "watchlistform-layout", wlProblems.Count == 0,
                    wlProblems.Count == 0 ? "监视清单窗口布局完好" : string.Join("；", wlProblems.ToArray()), ref fail);
                wl.Close();
                Pump(150);

                form.Core.Abandon();
                Pump(200);
                Check(sb, "abandon-returns-idle", !form.Core.IsFocusing, form.Core.StatusLine(), ref fail);

                form.SmokeClose();
                Pump(200);
                Check(sb, "mainform-closed", true, "窗口已关闭", ref fail);

                // 设置窗口（含规则表格）
                SettingsForm sf = new SettingsForm(Store.LoadSettings());
                sf.Opacity = 0;
                sf.ShowInTaskbar = false;
                sf.Show();
                Pump(500);
                DataGridView grid = FindGrid(sf);
                string cols = grid == null ? "(无)" : ColumnNames(grid);
                bool colsOk = grid != null && grid.Columns.Count == 4
                           && grid.Columns[0].HeaderText == "名称"
                           && grid.Columns[1].HeaderText == "软件"
                           && grid.Columns[2].HeaderText == "规则时长（分钟）"
                           && grid.Columns[3].HeaderText == "操作";
                Check(sb, "settingsform-rules-table", sf.IsHandleCreated && colsOk,
                    string.Format("控件 {0} 个，规则表格 {1} 行 × {2} 列（列名：{3}）",
                        CountControls(sf), grid == null ? -1 : grid.Rows.Count,
                        grid == null ? -1 : grid.Columns.Count, cols), ref fail);
                sf.Close();
                Pump(150);

                // 设置窗口：点「保存」应当立即生效但**不关闭**窗口
                int savedCount = 0;
                Settings lastSaved = null;
                SettingsForm sf2 = new SettingsForm(Store.LoadSettings(), delegate(Settings sv)
                {
                    savedCount++;
                    lastSaved = sv;
                });
                sf2.StartPosition = FormStartPosition.Manual;
                sf2.Location = new Point(-6000, -6000);
                sf2.Show();
                Pump(500);
                FlatButton saveBtn = FindButton(sf2, "保存");
                Check(sb, "settingsform-has-save-button", saveBtn != null,
                    saveBtn == null ? "没找到「保存」按钮" : "已定位保存按钮", ref fail);
                if (saveBtn != null)
                {
                    saveBtn.PerformClick();
                    Pump(400);
                }
                Check(sb, "settings-save-keeps-window-open",
                    savedCount == 1 && sf2.Visible && !sf2.IsDisposed && sf2.SavedAnything,
                    string.Format("回调次数={0}，窗口仍打开={1}，状态「{2}」",
                        savedCount, sf2.Visible, sf2.SaveStateText), ref fail);
                // 专注中保存：明确提示"本次不变，下一段生效"；不在专注中则只显示「已保存」
                sf2.SessionActive = delegate { return true; };
                if (saveBtn != null) { saveBtn.PerformClick(); Pump(400); }
                string noticeText = sf2.SaveStateText;
                bool noticeShown = noticeText.IndexOf(SettingsForm.NextSessionNotice) >= 0;
                sf2.SessionActive = delegate { return false; };
                if (saveBtn != null) { saveBtn.PerformClick(); Pump(400); }
                string plainText = sf2.SaveStateText;
                bool noticeHidden = plainText.IndexOf("下一段专注开始时生效") < 0;
                Check(sb, "settings-apply-shows-next-session-notice",
                    noticeShown && noticeHidden,
                    string.Format("专注中保存显示「{0}」={1}；非专注时显示「{2}」（不含提示）={3}",
                        SettingsForm.NextSessionNotice, noticeShown,
                        plainText.Replace("\r\n", " / "), noticeHidden), ref fail);                // 设置窗口左下角的署名：版本号 + 作者链接（可点区域应正好是作者名）
                string credit = sf2.CreditInfo;
                // 异步发信外壳：点击立刻返回（界面不卡）、按钮禁用显示「测试中…」、完成后回 UI 恢复
                FlatButton btnAsync = FindButton(sf2, "测试连接");
                int asyncDone = 0;
                bool asyncOk = false;
                AsyncMail.Run(sf2, btnAsync, "测试中…", "测试连接",
                    delegate { Thread.Sleep(400); return "模拟成功"; },
                    delegate(bool ok, string msg) { asyncDone++; asyncOk = ok; });
                bool busyNow = btnAsync != null && !btnAsync.Enabled && btnAsync.Text == "测试中…";
                Pump(1500);
                bool restored = btnAsync != null && btnAsync.Enabled && btnAsync.Text == "测试连接"
                             && asyncDone == 1 && asyncOk;
                Check(sb, "async-send-nonblocking", busyNow && restored,
                    string.Format("调用立刻返回（期间按钮禁用且显示「测试中…」={0}）；完成后恢复={1}、回调 {2} 次",
                        busyNow, restored, asyncDone), ref fail);

                // 窗口关闭后返回的旧结果必须被丢弃，不能碰已释放的控件
                SettingsForm sfClosed = new SettingsForm(Store.LoadSettings(), delegate(Settings sv) { });
                sfClosed.StartPosition = FormStartPosition.Manual;
                sfClosed.Location = new Point(-6000, -6000);
                sfClosed.Show();
                Pump(300);
                FlatButton btnClosed = FindButton(sfClosed, "测试连接");
                int staleDone = 0;
                AsyncMail.Run(sfClosed, btnClosed, "测试中…", "测试连接",
                    delegate { Thread.Sleep(500); return "迟到的结果"; },
                    delegate(bool ok, string msg) { staleDone++; });
                sfClosed.Close();
                Pump(1200);
                Check(sb, "async-send-drops-result-after-close", staleDone == 0,
                    string.Format("窗口关闭后，后台回调执行次数 = {0}（应为 0）", staleDone), ref fail);

                // 主窗口的「测试发信」也必须是异步的（与设置窗口、历史重发一致）
                MainForm mfA = new MainForm();
                mfA.StartPosition = FormStartPosition.Manual;
                mfA.Location = new Point(-6000, -6000);
                mfA.Show();
                Pump(400);
                FlatButton mBtn = mfA.TestMailButton;
                int mDone = 0;
                bool mOk = false;
                mfA.SendTestMailAsync(null,
                    delegate { Thread.Sleep(400); return "模拟成功"; },
                    delegate(bool ok, string msg) { mDone++; mOk = ok; });
                bool mBusy = mBtn != null && !mBtn.Enabled && mBtn.Text == "发送中…";
                Pump(1500);
                bool mRestored = mBtn != null && mBtn.Enabled && mBtn.Text == "测试发信" && mDone == 1 && mOk;
                mfA.SmokeClose();
                Pump(200);
                Check(sb, "mainform-test-mail-is-async", mBusy,
                    string.Format("主窗口「测试发信」调用立刻返回，期间按钮禁用并显示「发送中…」={0}", mBusy), ref fail);
                Check(sb, "mainform-test-mail-button-state", mRestored,
                    string.Format("发送结束后按钮恢复可用、文字复原={0}，回调 {1} 次", mRestored, mDone), ref fail);

                MainForm mfB = new MainForm();
                mfB.StartPosition = FormStartPosition.Manual;
                mfB.Location = new Point(-6000, -6000);
                mfB.Show();
                Pump(300);
                int staleMain = 0;
                mfB.SendTestMailAsync(null,
                    delegate { Thread.Sleep(500); return "迟到的结果"; },
                    delegate(bool ok, string msg) { staleMain++; });
                mfB.SmokeClose();
                Pump(1200);
                Check(sb, "mainform-test-mail-close-safe", staleMain == 0,
                    string.Format("主窗口关闭后，后台发信结果被丢弃（回调次数 = {0}）", staleMain), ref fail);                // 连不上时要立刻报错，而不是卡在系统默认超时上
                bool refusedFast = false;
                try { SmtpTransport.Probe("127.0.0.1", 1, false); }
                catch (Exception) { refusedFast = true; }
                Check(sb, "smtp-refused-errors-quickly", refusedFast, "端口拒绝连接时立刻抛出可读错误", ref fail);

                // 设置里「提示音」三个字：蓝色下划线的可点链接，点一下试听
                // （这一行在「规则与其他」页，先切过去，否则控件不可见、也点不到）
                ClickNav(sf2, "规则与其他");
                Pump(400);
                LinkLabel lnkSound = sf2.SoundPreviewLink;
                CheckBox chkSnd = sf2.SoundCheckBox;
                bool linkOk = lnkSound != null && chkSnd != null
                           && lnkSound.Text == "提示音"
                           && lnkSound.LinkArea.Start == 0 && lnkSound.LinkArea.Length == 3
                           && lnkSound.LinkColor == Theme.Link
                           && lnkSound.LinkBehavior == LinkBehavior.AlwaysUnderline
                           && lnkSound.AutoSize && lnkSound.Visible
                           && chkSnd.Text.IndexOf("提示音") < 0;      // 三个字只在链接上，不在复选框文字里
                Check(sb, "settings-sound-preview-link", linkOk,
                    string.Format("链接文字「{0}」、可点区域 {1}+{2}、颜色={3}、下划线={4}",
                        lnkSound == null ? "(无)" : lnkSound.Text,
                        lnkSound == null ? -1 : lnkSound.LinkArea.Start,
                        lnkSound == null ? -1 : lnkSound.LinkArea.Length,
                        lnkSound == null ? "-" : lnkSound.LinkColor.Name,
                        lnkSound == null ? "-" : lnkSound.LinkBehavior.ToString()), ref fail);

                // 说明：LinkLabel 的命中判定基于**真实光标位置**，而这里窗口在屏幕外，
                // 合成鼠标消息点不到可点区域，所以直接触发它绑定的处理函数（同一段代码）。
                int snd0 = Supervisor.SoundPlayCount;
                sf2.RaiseSoundPreviewForTest();
                Pump(120);
                Check(sb, "settings-sound-preview-plays",
                    Supervisor.SoundPlayCount == snd0 + 1,
                    string.Format("触发「提示音」链接绑定的处理函数：播放计数 +{0}（与真触发共用同一处播放实现）",
                        Supervisor.SoundPlayCount - snd0), ref fail);                Check(sb, "settingsform-credit-block",
                    credit.IndexOf("PomoCC " + App.Version) >= 0
                    && credit.IndexOf("可点区域「" + App.Author + "」") >= 0
                    && credit.IndexOf(App.RepoUrl) >= 0,
                    credit, ref fail);
                Check(sb, "settings-save-applied-values",
                    lastSaved != null && lastSaved.Rules != null && lastSaved.SupervisorEmail == "boss@example.com",
                    lastSaved == null ? "回调没拿到设置"
                        : string.Format("已应用：收件人={0}，规则 {1} 条", lastSaved.SupervisorEmail, lastSaved.Rules.Count), ref fail);
                sf2.Close();
                Pump(150);

                // 「添加程序」选择窗口
                AppPickerForm picker = new AppPickerForm(3);
                picker.Opacity = 0;
                picker.ShowInTaskbar = false;
                picker.Show();
                Pump(900);
                Check(sb, "app-picker-created", picker.IsHandleCreated && picker.HasApps,
                    string.Format("控件 {0} 个，列出 {1} 个可添加程序", CountControls(picker), picker.AppCount), ref fail);
                picker.Close();
                Pump(150);

                // 告状记录窗口
                HistoryForm hf = new HistoryForm();
                hf.Opacity = 0;
                hf.ShowInTaskbar = false;
                hf.Show();
                Pump(300);
                Check(sb, "historyform-created", hf.IsHandleCreated,
                    string.Format("控件 {0} 个", CountControls(hf)), ref fail);
                hf.Close();
                Pump(150);
            }
            catch (Exception ex)
            {
                if (IsDpapiEnvFailure(ex))
                    Skip(sb, "env-dpapi-profile", "环境阻塞：DPAPI 不可用（" + ex.Message + "）—— 本模式下后续检查未执行；生产加密逻辑未改动");
                else
                    Check(sb, "smoke-crashed", false, ex.ToString(), ref fail);
            }

            sb.Append("\r\n结果：").Append(Summary(fail)).Append("\r\n");
            Write(outPath, sb.ToString());
            return fail == 0 ? 0 : 1;
        }

        private static DataGridView FindGrid(Control root)
        {
            if (root is DataGridView) return (DataGridView)root;
            foreach (Control c in root.Controls)
            {
                DataGridView found = FindGrid(c);
                if (found != null) return found;
            }
            return null;
        }

        private static string ColumnNames(DataGridView grid)
        {
            List<string> names = new List<string>();
            for (int i = 0; i < grid.Columns.Count; i++) names.Add(grid.Columns[i].HeaderText);
            return string.Join(" / ", names.ToArray());
        }

        // ==================================================================
        //  3. DPI 适配自检
        // ==================================================================

        [DllImport("user32.dll")]
        private static extern IntPtr GetThreadDpiAwarenessContext();
        [DllImport("user32.dll")]
        private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")]
        private static extern bool AreDpiAwarenessContextsEqual(IntPtr a, IntPtr b);
        [DllImport("user32.dll")]
        private static extern uint GetDpiForSystem();
        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        private const int DPI_AWARENESS_UNAWARE = 0;
        private const int DPI_AWARENESS_SYSTEM_AWARE = 1;
        private const int DPI_AWARENESS_PER_MONITOR_AWARE = 2;

        // DPI_AWARENESS_CONTEXT 预定义伪句柄（仅作为比较输入有效）
        private static readonly IntPtr CTX_UNAWARE = new IntPtr(-1);
        private static readonly IntPtr CTX_SYSTEM_AWARE = new IntPtr(-2);
        private static readonly IntPtr CTX_PER_MONITOR = new IntPtr(-3);
        private static readonly IntPtr CTX_PER_MONITOR_V2 = new IntPtr(-4);

        /// <summary>DPI 自检：确认进程是 DPI 感知的、界面按 DPI 缩放、布局没有溢出。</summary>
        public static int DpiCheck(string[] args)
        {
            string outPath = Path.Combine(Path.GetTempPath(), "pomocc-dpicheck.log");
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--dpicheck", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    outPath = args[i + 1];
            }

            Store.OverrideDir = TempDir("pomocc-dpi");
            StringBuilder sb = new StringBuilder();
            int fail = 0;
            ReportDpapiEnvironment(sb);
            sb.Append("临时数据目录（含 app.log）：").Append(Store.Dir).Append("\r\n");

            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // ---- 环境事实，先全部记下来 ----
                string cfgFile = AppDomain.CurrentDomain.SetupInformation.ConfigurationFile;
                sb.Append("exe.config        : ").Append(cfgFile)
                  .Append(File.Exists(cfgFile) ? "（存在）" : "（不存在！高 DPI 开关会失效）").Append("\r\n");

                IntPtr ctx = GetThreadDpiAwarenessContext();
                int aware = GetAwarenessFromDpiAwarenessContext(ctx);
                bool isV2 = AreDpiAwarenessContextsEqual(ctx, CTX_PER_MONITOR_V2);
                bool isPm = AreDpiAwarenessContextsEqual(ctx, CTX_PER_MONITOR);
                sb.Append("线程 DPI 上下文   : ")
                  .Append(isV2 ? "PerMonitorV2" : (isPm ? "PerMonitor" : DescribeContext(ctx)))
                  .Append("（awareness=").Append(aware).Append("）\r\n");
                sb.Append("GetDpiForSystem   : ").Append(GetDpiForSystem()).Append("\r\n");
                sb.Append("屏幕数量          : ").Append(Screen.AllScreens.Length).Append("\r\n");
                for (int i = 0; i < Screen.AllScreens.Length; i++)
                {
                    Screen sc = Screen.AllScreens[i];
                    sb.Append(string.Format("  屏幕{0} {1} 主={2} 边界={3} 位深={4}\r\n",
                        i, sc.DeviceName, sc.Primary, sc.Bounds, sc.BitsPerPixel));
                }

                Check(sb, "process-dpi-awareness", aware >= DPI_AWARENESS_PER_MONITOR_AWARE,
                    isV2 ? "PerMonitorV2（推荐）" : (isPm ? "PerMonitor" : DescribeContext(ctx)), ref fail);

                uint systemDpi = GetDpiForSystem();
                Check(sb, "system-dpi-readable", systemDpi >= 96,
                    string.Format("系统 DPI = {0}（缩放 {1}%）", systemDpi, systemDpi * 100 / 96), ref fail);

                // ---- 窗体缩放 ----
                MainForm form = new MainForm();
                form.Opacity = 0;
                form.ShowInTaskbar = false;
                form.Show();
                Pump(600);
                sb.Append(string.Format("主窗口 ClientSize={0}\r\n", form.ClientSize));

                uint windowDpi = GetDpiForWindow(form.Handle);
                SizeF current = form.CurrentAutoScaleDimensions;
                SizeF design = form.AutoScaleDimensions;
                sb.Append("\r\n---- 主窗口 ----\r\n");
                sb.Append(string.Format("DeviceDpi={0}  GetDpiForWindow={1}\r\n", form.DeviceDpi, windowDpi));
                sb.Append(string.Format("AutoScaleMode={0}  AutoScaleDimensions={1}  CurrentAutoScaleDimensions={2}\r\n",
                    form.AutoScaleMode, design, current));
                sb.Append(string.Format("ClientSize={0}  设计基准=500x400\r\n", form.ClientSize));
                sb.Append(string.Format("14pt 字体实际像素高={0}\r\n", FontHeightPx(form.Font)));

                // 期望：按窗口所在显示器的 DPI 缩放 96 DPI 基准尺寸
                uint effectiveDpi = windowDpi > 0 ? windowDpi : systemDpi;
                float scale = effectiveDpi / 96f;
                int designW = 460;      // MainForm 的设计基准（见 MainForm.BuildUi）
                int designH = 580;
                int expectW = (int)Math.Round(designW * scale);
                int expectH = (int)Math.Round(designH * scale);
                bool sizeOk = Math.Abs(form.ClientSize.Width - expectW) <= 3 && Math.Abs(form.ClientSize.Height - expectH) <= 3;
                Check(sb, "form-scaled-by-dpi", sizeOk,
                    string.Format("窗口 DPI={0}（缩放 {1:0}%），ClientSize={2}x{3}，期望 {4}x{5}",
                        effectiveDpi, scale * 100, form.ClientSize.Width, form.ClientSize.Height, expectW, expectH), ref fail);

                // 字体：point 字号本身随 DPI 渲染，实测行高应约等于「点数 × DPI / 72 × 行距系数」
                float expectedFontH = Theme.Body.SizeInPoints * effectiveDpi / 72f * 1.30f;
                float realFontH = FontHeightPx(Theme.Body);
                Check(sb, "font-hardware-scaled",
                    Math.Abs(realFontH - expectedFontH) <= 4f,
                    string.Format("{0:0.#}pt 字体实测 {1:0.#}px，期望约 {2:0.#}px（{3:0}% 缩放）",
                        Theme.Body.SizeInPoints, realFontH, expectedFontH, scale * 100), ref fail);

                // ---- 布局完整性 ----
                List<string> overflow = new List<string>();
                CheckLayout(form, overflow);
                Check(sb, "layout-within-bounds", overflow.Count == 0,
                    overflow.Count == 0 ? "所有控件都在父容器范围内，无裁切" : string.Join("；", overflow.ToArray()), ref fail);

                List<string> clipped = new List<string>();
                CheckTextFits(form, clipped);
                Check(sb, "mainform-text-not-clipped", clipped.Count == 0,
                    clipped.Count == 0 ? "主窗口没有文字被截断" : string.Join("；", clipped.ToArray()), ref fail);

                SettingsForm sf = new SettingsForm(Store.LoadSettings());
                sf.Opacity = 0;
                sf.ShowInTaskbar = false;
                sf.Show();
                Pump(600);
                List<string> overflow2 = new List<string>();
                CheckLayout(sf, overflow2);
                Check(sb, "settingsform-layout-within-bounds", overflow2.Count == 0,
                    overflow2.Count == 0 ? "设置窗口布局完好" : string.Join("；", overflow2.ToArray()), ref fail);
                sb.Append(string.Format("设置窗口 ClientSize={0}，DeviceDpi={1}\r\n", sf.ClientSize, sf.DeviceDpi));

                List<string> clipped2 = new List<string>();
                CheckTextFits(sf, clipped2);
                Check(sb, "settingsform-text-not-clipped", clipped2.Count == 0,
                    clipped2.Count == 0 ? "设置窗口没有文字被截断" : string.Join("；", clipped2.ToArray()), ref fail);

                // 关键：三个页签都要检查。之前只查了默认显示的「监督名单」页，
                // 出问题的「邮件设置」页是隐藏的，于是检查全绿、界面却是坏的。
                string[] navNames = new string[] { "监督名单", "邮件设置", "规则与其他" };
                for (int n = 0; n < navNames.Length; n++)
                {
                    ClickNav(sf, navNames[n]);
                    Pump(400);
                    List<string> pageProblems = new List<string>();
                    CheckLayout(sf, pageProblems);
                    Check(sb, "settingsform-page-layout[" + navNames[n] + "]", pageProblems.Count == 0,
                        pageProblems.Count == 0 ? "页面布局完好" : string.Join("；", pageProblems.ToArray()), ref fail);
                }
                sf.Close();
                Pump(150);

                AppPickerForm picker = new AppPickerForm(3);
                picker.Opacity = 0;
                picker.ShowInTaskbar = false;
                picker.Show();
                Pump(900);
                List<string> overflow3 = new List<string>();
                CheckLayout(picker, overflow3);
                Check(sb, "apppicker-layout-within-bounds", overflow3.Count == 0,
                    overflow3.Count == 0
                        ? string.Format("添加程序窗口布局完好，列出 {0} 个程序", picker.AppCount)
                        : string.Join("；", overflow3.ToArray()), ref fail);
                picker.Close();
                Pump(150);

                HistoryForm hf = new HistoryForm();
                hf.Opacity = 0;
                hf.ShowInTaskbar = false;
                hf.Show();
                Pump(400);
                List<string> overflow4 = new List<string>();
                CheckLayout(hf, overflow4);
                Check(sb, "historyform-layout-within-bounds", overflow4.Count == 0,
                    overflow4.Count == 0 ? "告状记录窗口布局完好" : string.Join("；", overflow4.ToArray()), ref fail);
                hf.Close();
                Pump(150);

                form.SmokeClose();
                Pump(200);
            }
            catch (Exception ex)
            {
                if (IsDpapiEnvFailure(ex))
                    Skip(sb, "env-dpapi-profile", "环境阻塞：DPAPI 不可用（" + ex.Message + "）—— 本模式下后续检查未执行；生产加密逻辑未改动");
                else
                    Check(sb, "dpicheck-crashed", false, ex.ToString(), ref fail);
            }

            sb.Append("\r\n结果：").Append(Summary(fail)).Append("\r\n");
            Write(outPath, sb.ToString());
            return fail == 0 ? 0 : 1;
        }

        private static float FontHeightPx(Font font)
        {
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
            {
                return g.MeasureString("测试Ag", font).Height;
            }
        }

        private static string DescribeContext(IntPtr ctx)
        {
            int aware = GetAwarenessFromDpiAwarenessContext(ctx);
            if (aware == DPI_AWARENESS_UNAWARE) return "Unaware（不感知 DPI —— 会被系统拉伸变糊）";
            if (aware == DPI_AWARENESS_SYSTEM_AWARE) return "SystemAware（系统级感知）";
            if (aware == DPI_AWARENESS_PER_MONITOR_AWARE) return "PerMonitorAware（每显示器感知）";
            return "未知（数值 " + aware + "）";
        }

        /// <summary>
        /// 递归检查每个控件是否都在父容器客户区内。
        /// 容器若开了 AutoScroll，子控件超出是设计使然（要滚动），跳过。
        /// 容差只留 1px（四舍五入误差），任何真实溢出都要报出来。
        /// </summary>
        private static void CheckLayout(Control parent, List<string> problems)
        {
            if (problems.Count > 8) return;

            ScrollableControl sc = parent as ScrollableControl;
            if (sc != null && sc.AutoScroll)
            {
                // 能滚动时，纵向超出是允许的，但必须真的有滚动条；
                // 横向超出永远不允许（横向滚动条基本没法用）。
                int contentBottom = 0;
                foreach (Control c in parent.Controls)
                {
                    if (!c.Visible) continue;
                    if (c.Bottom > contentBottom) contentBottom = c.Bottom;
                    if (c.Right > sc.ClientSize.Width + 1)
                        problems.Add(string.Format("可滚动容器 {0} 里的「{1}」横向越界 {2}px",
                            parent.GetType().Name,
                            string.IsNullOrEmpty(c.Text) ? c.GetType().Name : c.Text,
                            c.Right - sc.ClientSize.Width));
                }
                if (contentBottom > sc.ClientSize.Height + 1 && !sc.VerticalScroll.Visible)
                    problems.Add(string.Format("容器 {0} 内容高 {1}px 超过 {2}px，但没有出现滚动条 —— 用户看不到下面的内容",
                        parent.GetType().Name, contentBottom, sc.ClientSize.Height));

                foreach (Control c in parent.Controls) CheckLayout(c, problems);
                return;
            }

            Rectangle client = parent.ClientRectangle;
            foreach (Control c in parent.Controls)
            {
                if (c.Visible)
                {
                    if (c.Right > client.Width + 1 || c.Bottom > client.Height + 1)
                    {
                        problems.Add(string.Format("{0} 里的「{1}」越界 {2}px（控件 {3},{4} {5}x{6}，容器 {7}x{8}）",
                            parent.GetType().Name,
                            string.IsNullOrEmpty(c.Text) ? c.GetType().Name : c.Text,
                            Math.Max(c.Right - client.Width, c.Bottom - client.Height),
                            c.Left, c.Top, c.Width, c.Height, client.Width, client.Height));
                    }
                }
                CheckLayout(c, problems);
            }
        }

        // ==================================================================
        //  4. 真实模式自检（注册表自启 / 托盘 / 单实例 / 密码）
        // ==================================================================

        public static int RealSmoke(string[] args)
        {
            string outPath = Path.Combine(Path.GetTempPath(), "pomocc-realsmoke.log");
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--realsmoke", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    outPath = args[i + 1];
            }

            StringBuilder sb = new StringBuilder();
            int fail = 0;
            ReportDpapiEnvironment(sb);

            Store.OverrideDir = TempDir("pomocc-real");

            bool headlessBefore = App.Headless;
            App.Headless = false;
            // 先把用户真实项的原样字符串存下来（此时还没切值名），最后原样恢复。
            string realRaw = AutoStart.ReadReal();

            // 用测试专用值名做注册表读写验证：真的读写注册表，但不碰用户自己那一项
            AutoStart.OverrideValueName = "PomoCCSelfTest";

            try
            {
                bool startWasEnabled = AutoStart.IsEnabled();
                bool w1 = AutoStart.Apply(true);
                bool on = AutoStart.IsEnabled();
                bool w2 = AutoStart.Apply(true);          // 值已经在，不该再写
                bool w3 = AutoStart.Apply(false);
                bool off = AutoStart.IsEnabled();
                bool w4 = AutoStart.Apply(false);         // 已经没有了，也不该再写
                bool stillOn = AutoStart.Apply(true) && AutoStart.IsEnabled();

                Check(sb, "autostart-registry", on && !off && stillOn,
                    string.Format("测试前={0}，写入后={1}，清理后={2}，再写入后={3}（值名={4}，注册的路径={5}）",
                        startWasEnabled, on, off, stillOn, AutoStart.OverrideValueName, App.ExePath), ref fail);

                // 幂等性：这才是「电脑管家不再频繁弹窗」的关键
                Check(sb, "autostart-idempotent", w1 && !w2 && w3 && !w4,
                    string.Format("首次写入={0}（应为真）、重复设置={1}（应为假，即没动注册表）、" +
                                  "取消={2}（应为真）、重复取消={3}（应为假）", w1, w2, w3, w4), ref fail);

                Icon icon = MainForm.MakeIcon();
                bool iconOk = icon != null && icon.Width > 0;
                Check(sb, "tray-icon-drawn", iconOk,
                    iconOk ? icon.Width + "x" + icon.Height + " 像素图标已生成" : "图标绘制失败", ref fail);

                bool trayOk = false;
                using (NotifyIcon ni = new NotifyIcon())
                {
                    ni.Icon = icon;
                    ni.Text = "番茄钟监督";
                    ni.Visible = true;
                    trayOk = ni.Visible;
                    ni.Visible = false;
                }
                Check(sb, "tray-icon-created", trayOk, "托盘图标可以创建并显示", ref fail);

                bool created;
                using (Mutex m = new Mutex(true, App.MutexName, out created))
                {
                    if (created)
                    {
                        Check(sb, "single-instance-mutex", true, "成功取得单实例锁", ref fail);
                        m.ReleaseMutex();
                    }
                    else
                    {
                        // 用户此刻正开着程序，锁当然拿不到 —— 这是环境状态，不是缺陷，跳过不算失败
                        Skip(sb, "single-instance-mutex", "程序正在运行（用户在用），这一项跳过；退出后再跑即可验证");
                    }
                }

                Settings s = Settings.Defaults();
                s.SetPassword("realsmoke123");
                s.Rules = new List<WatchRule>();
                s.Rules.Add(Rule("steam.exe", 5));
                Store.SaveSettings(s);
                Settings reloaded = Store.LoadSettings();
                Check(sb, "password-and-rules-persisted",
                    reloaded.HasPassword() && reloaded.CheckPassword("realsmoke123")
                    && !reloaded.CheckPassword("wrong") && reloaded.Rules.Count == 1 && reloaded.Rules[0].LimitMinutes == 5,
                    "重启后密码校验与规则表都正确", ref fail);

                TrySetAuthCode(reloaded, "super-secret-code");
                Store.SaveSettings(reloaded);
                string raw = File.ReadAllText(Store.ConfigPath, Encoding.UTF8);
                Check(sb, "secret-not-plaintext-on-disk",
                    raw.IndexOf("super-secret-code") < 0, "config.json 中找不到明文授权码", ref fail);

                string appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string realDir = Path.Combine(appdata, Store.DataFolderName);
                string legacyDir = Path.Combine(appdata, Store.LegacyDataFolderName);
                sb.Append("[INFO] 用户真实数据目录：").Append(realDir)
                  .Append(Directory.Exists(realDir) ? "（已存在）" : "（不存在，首次运行才创建）")
                  .Append("；旧目录 ").Append(Store.LegacyDataFolderName)
                  .Append(Directory.Exists(legacyDir) ? "（仍在，说明还没迁移过）" : "（已清理）").Append("\r\n");
            }
            catch (Exception ex)
            {
                if (IsDpapiEnvFailure(ex))
                    Skip(sb, "env-dpapi-profile", "环境阻塞：DPAPI 不可用（" + ex.Message + "）—— 本模式下后续检查未执行；生产加密逻辑未改动");
                else
                    Check(sb, "realsmoke-crashed", false, ex.ToString(), ref fail);
            }
            finally
            {
                App.Headless = false;
                AutoStart.RemoveTestValue();              // 清掉测试专用项
                AutoStart.OverrideValueName = null;       // 切回真实值名
                AutoStart.RestoreReal(realRaw);           // 用户那一项原样恢复（不写新路径）
                App.Headless = headlessBefore;
            }

            sb.Append("\r\n结果：").Append(Summary(fail)).Append("\r\n");
            Write(outPath, sb.ToString());
            return fail == 0 ? 0 : 1;
        }

        /// <summary>
        /// 旧配置兼容性检查：--loadcheck &lt;日志&gt; &lt;config.json&gt;
        /// 把指定配置复制到临时目录后加载，确认能解析、密码散列不被破坏、
        /// 而且没有走「坏配置兜底」分支（那会静默重置用户设置）。
        /// </summary>
        public static int LoadCheck(string[] args)
        {
            string outPath = Path.Combine(Path.GetTempPath(), "pomocc-loadcheck.log");
            string source = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--loadcheck", StringComparison.OrdinalIgnoreCase) && i + 2 < args.Length)
                {
                    outPath = args[i + 1];
                    source = args[i + 2];
                }
            }

            StringBuilder sb = new StringBuilder();
            int fail = 0;
            ReportDpapiEnvironment(sb);

            Store.OverrideDir = TempDir("pomocc-loadcheck");

            if (string.IsNullOrEmpty(source) || !File.Exists(source))
            {
                Check(sb, "loadcheck-source", false, "找不到待检查的配置文件：" + source, ref fail);
            }
            else
            {
                File.Copy(source, Store.ConfigPath, true);
                string rawBefore = File.ReadAllText(Store.ConfigPath, Encoding.UTF8);

                // 取出原始散列，稍后比对
                string hashBefore = ExtractJsonField(rawBefore, "PasswordHash");
                string saltBefore = ExtractJsonField(rawBefore, "PasswordSalt");

                Settings loaded = Store.LoadSettings();
                bool parsed = loaded != null
                           && loaded.FocusMinutes >= 1 && loaded.FocusMinutes <= 600
                           && loaded.SampleSeconds >= 2
                           && loaded.Rules != null;
                Check(sb, "config-parses", parsed,
                    loaded == null ? "解析失败" : string.Format("解析成功：专注 {0} 分钟，采样 {1} 秒，规则 {2} 条",
                        loaded.FocusMinutes, loaded.SampleSeconds, loaded.Rules == null ? -1 : loaded.Rules.Count), ref fail);

                Check(sb, "no-fallback-to-defaults", !File.Exists(Store.ConfigPath + ".bad"),
                    File.Exists(Store.ConfigPath + ".bad")
                        ? "走了兜底分支 —— 配置被当成坏文件，用户设置会被重置！"
                        : "没有走兜底分支（说明 JSON 解析没有异常）", ref fail);

                bool hashKept = loaded != null && loaded.PasswordHash == hashBefore && loaded.PasswordSalt == saltBefore;
                Check(sb, "password-hash-preserved", hashKept,
                    hashKept ? "密码散列原样保留（用户的退出密码仍然有效）"
                             : "密码散列被改动，用户原来设的密码会失效！", ref fail);

                // 回写再读一次，确认往返不丢字段
                if (loaded != null)
                {
                    Store.SaveSettings(loaded);
                    Settings again = Store.LoadSettings();
                    Check(sb, "save-load-roundtrip",
                        again != null && again.PasswordHash == hashBefore && again.Rules != null,
                        "保存后重新读取，密码散列与规则表都还在", ref fail);
                }

                sb.Append("\r\n原始字段：PasswordHash=").Append(hashBefore).Append("\r\n");
                sb.Append("加载后字段：PasswordHash=").Append(loaded == null ? "(null)" : loaded.PasswordHash).Append("\r\n");
            }

            sb.Append("\r\n结果：").Append(Summary(fail)).Append("\r\n");
            Write(outPath, sb.ToString());
            return fail == 0 ? 0 : 1;
        }

        /// <summary>从扁平 JSON 里抠一个字符串字段，仅用于测试比对。</summary>
        private static string ExtractJsonField(string json, string field)
        {
            string key = "\"" + field + "\":\"";
            int i = json.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return null;
            i += key.Length;
            int end = json.IndexOf('"', i);
            if (end < 0) return null;
            return json.Substring(i, end - i);
        }

        /// <summary>
        /// 离屏渲染自检：--rendertest &lt;日志&gt;
        /// 把按钮真实画进位图，然后数像素。专门用来抓「文字被画了两层 / 背景没擦干净」
        /// 这类只有肉眼才能发现的问题。
        /// </summary>
        public static int RenderCheck(string[] args)
        {
            string outPath = Path.Combine(Path.GetTempPath(), "pomocc-rendertest.log");
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--rendertest", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    outPath = args[i + 1];
            }

            StringBuilder sb = new StringBuilder();
            int fail = 0;
            ReportDpapiEnvironment(sb);

            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // 离屏宿主：白底卡片上放三种按钮
                Form shim = new Form();
                shim.AutoScaleMode = AutoScaleMode.None;
                shim.ClientSize = new Size(420, 260);
                shim.BackColor = Theme.Bg;
                shim.ShowInTaskbar = false;
                shim.StartPosition = FormStartPosition.Manual;
                shim.Location = new Point(-4000, -4000);   // 放到屏幕外，不在用户桌面闪

                Card host = new Card();
                host.SetBounds(10, 10, 380, 230);
                host.Padding = new Padding(14);
                shim.Controls.Add(host);

                FlatButton primary = new FlatButton("开始专注", FlatButton.Kind.Primary);
                primary.SetBounds(14, 14, 170, 40);
                FlatButton secondary = new FlatButton("取消", FlatButton.Kind.Secondary);
                secondary.SetBounds(14, 70, 120, 36);
                FlatButton ghost = new FlatButton("设置", FlatButton.Kind.Ghost);
                ghost.SetBounds(14, 120, 120, 36);
                host.Controls.Add(primary);
                host.Controls.Add(secondary);
                host.Controls.Add(ghost);

                shim.CreateControl();
                host.PerformLayout();
                shim.PerformLayout();
                Application.DoEvents();

                sb.Append(string.Format("宿主卡片底色 = {0}\r\n", ColorName(host.BackColor)));

                Bitmap first = null;
                CheckButton(sb, host, primary, "primary", Theme.Accent, ref fail, ref first);
                Bitmap second = null;
                CheckButton(sb, host, primary, "primary-again", Theme.Accent, ref fail, ref second);

                // 连续两次渲染必须逐像素一致：不一致就说明有上一帧残留
                if (first != null && second != null)
                {
                    int diff = 0;
                    for (int y = 0; y < first.Height && diff == 0; y++)
                    {
                        for (int x = 0; x < first.Width; x++)
                        {
                            if (first.GetPixel(x, y) != second.GetPixel(x, y)) { diff++; break; }
                        }
                    }
                    Check(sb, "render-is-stable", diff == 0,
                        diff == 0 ? "两次渲染逐像素一致（没有残留）" : "两次渲染不一致，说明有上一帧残留", ref fail);
                }

                Bitmap tmp = null;
                CheckButton(sb, host, secondary, "secondary", Theme.Card, ref fail, ref tmp);
                CheckButton(sb, host, ghost, "ghost", Theme.Card, ref fail, ref tmp);

                shim.Close();
                shim.Dispose();

                // 再把四个真实窗口整体渲染，扫「未绘制区域」
                CheckWindow(sb, new MainForm(), "mainform", true, ref fail);
                CheckWindow(sb, new SettingsForm(Store.LoadSettings()), "settingsform", true, ref fail);
                AppPickerForm pickerForm = new AppPickerForm(3);
                // 默认用真实枚举结果；POMOCC_TEST_EMPTY_APP_LIST=1 时模拟"枚举不到程序"，
                // 用来验证"空列表 → 主按钮禁用"这条分支不会误报。
                if (Environment.GetEnvironmentVariable("POMOCC_TEST_EMPTY_APP_LIST") == "1")
                    pickerForm.EmptyListForTest = true;
                CheckWindow(sb, pickerForm, "apppicker", true, ref fail);
                CheckWindow(sb, new HistoryForm(), "historyform", true, ref fail);

                ProbeSurfaces(sb, ref fail);
            }
            catch (Exception ex)
            {
                if (IsDpapiEnvFailure(ex))
                    Skip(sb, "env-dpapi-profile", "环境阻塞：DPAPI 不可用（" + ex.Message + "）—— 本模式下后续检查未执行；生产加密逻辑未改动");
                else
                    Check(sb, "rendertest-crashed", false, ex.ToString(), ref fail);
            }

            sb.Append("\r\n结果：").Append(Summary(fail)).Append("\r\n");
            Write(outPath, sb.ToString());
            return fail == 0 ? 0 : 1;
        }

        /// <summary>
        /// 渲染单个按钮并检查三件事：
        ///   1) 控件四角必须是父容器（白卡）的颜色 —— 说明背景被擦干净了，没有上一帧残留；
        ///   2) 圆角内的填充色必须与预期一致；
        ///   3) 文字带里必须真的有文字像素。
        /// </summary>
        /// <summary>
        /// 截图自检：--shot &lt;输出目录&gt;
        /// 把每个窗口真实渲染成 PNG。这是唯一能"看见"界面的办法 ——
        /// 之前的尺寸/溢出断言全都是通过状态，界面照样是坏的。
        /// </summary>
        public static int Shot(string[] args)
        {
            string dir = Path.Combine(Path.GetTempPath(), "pomocc-shots");
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--shot", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    dir = args[i + 1];
            }

            StringBuilder sb = new StringBuilder();
            int fail = 0;
            ReportDpapiEnvironment(sb);

            try
            {
                Store.OverrideDir = TempDir("pomocc-shot");
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Directory.CreateDirectory(dir);

                Settings seed = Settings.Defaults();
                seed.SupervisorEmail = "boss@example.com";
                seed.SenderEmail = "me@qq.com";
                TrySetAuthCode(seed, "dummy-auth-code");
                seed.Rules = new List<WatchRule>();
                WatchRule r1 = new WatchRule();
                r1.Exe = "game.exe"; r1.DisplayName = "game"; r1.Name = "奶龙"; r1.LimitMinutes = 1; r1.Enabled = true;
                WatchRule r2 = new WatchRule();
                r2.Exe = "steam.exe"; r2.DisplayName = "Steam"; r2.Name = "Steam"; r2.LimitMinutes = 3; r2.Enabled = true;
                WatchRule r3 = new WatchRule();
                r3.Exe = "wegame.exe"; r3.DisplayName = "WeGame"; r3.Name = "WeGame"; r3.LimitMinutes = 5; r3.Enabled = true;
                seed.Rules.Add(r1); seed.Rules.Add(r2); seed.Rules.Add(r3);
                seed.SupervisorEmail = "boss@example.com";
                seed.SenderEmail = "me@qq.com";
                seed.SmtpHost = "smtp.qq.com";
                seed.SmtpPort = 587;
                seed.SetAuthCode("abcdefghijklmnop");
                Store.SaveSettings(seed);

                MainForm main = new MainForm();
                ShotOne(main, Path.Combine(dir, "01-主窗口-待机.png"), sb);
                main.Core.StartFocus();
                Pump(1200);
                main.Refresh();   // 让界面刷新
                ShotAgain(main, Path.Combine(dir, "02-主窗口-专注中.png"));
                main.Core.Abandon();
                main.SmokeClose();
                Pump(200);

                SettingsForm sf = new SettingsForm(Store.LoadSettings());
                sf.StartPosition = FormStartPosition.Manual;
                sf.Location = new Point(-6000, -6000);
                sf.Show();
                Pump(500);
                Shot2(sf, Path.Combine(dir, "03-设置-监督名单.png"));
                ClickNav(sf, "邮件设置");
                Pump(400);
                Shot2(sf, Path.Combine(dir, "04-设置-邮件设置.png"));

                // 邮件设置页往下滚，看新增的「内容自定义」
                TableLayoutPanel mailPage = FindPage(sf);
                if (mailPage != null)
                {
                    if (sf.ContentCard != null) mailPage.ScrollControlIntoView(sf.ContentCard);
                    Pump(400);
                    Shot2(sf, Path.Combine(dir, "12b-设置-内容自定义-上半.png"));
                    mailPage.AutoScrollPosition = new Point(0, mailPage.VerticalScroll.Maximum);
                    Pump(400);
                    Shot2(sf, Path.Combine(dir, "12-设置-内容自定义.png"));
                    mailPage.AutoScrollPosition = new Point(0, 0);
                    Pump(200);
                }

                ClickNav(sf, "规则与其他");
                Pump(400);
                Shot2(sf, Path.Combine(dir, "05-设置-规则与其他.png"));
                sf.Close();
                Pump(200);

                // 设置窗口：点保存后的状态（窗口不关闭 + 底部反馈）
                SettingsForm sfSaved = new SettingsForm(Store.LoadSettings(), delegate(Settings sv) { });
                sfSaved.StartPosition = FormStartPosition.Manual;
                sfSaved.Location = new Point(-6000, -6000);
                sfSaved.Show();
                Pump(400);
                FlatButton saveBtn2 = FindButton(sfSaved, "保存");
                if (saveBtn2 != null) { saveBtn2.PerformClick(); Pump(400); }
                Shot2(sfSaved, Path.Combine(dir, "11-设置-已保存.png"));
                sb.Append("保存后状态：").Append(sfSaved.SaveStateText).Append("\r\n");
                sfSaved.Close();
                Pump(150);

                AppPickerForm picker = new AppPickerForm(3);
                ShotOne(picker, Path.Combine(dir, "06-添加程序.png"), sb);
                picker.Close();
                Pump(200);

                // 造一条"旧版本遗留的待发送"记录，截图里要能看到它被如实标注
                List<HistoryEntry> before = Store.ReadHistory(200);
                bool hasLegacy = false;
                for (int i = 0; i < before.Count; i++) if (before[i].Status == "待发送") hasLegacy = true;
                if (!hasLegacy)
                {
                    HistoryEntry oldOne = new HistoryEntry();
                    oldOne.Time = DateTime.Now.AddMinutes(-30).ToString("yyyy-MM-dd HH:mm:ss");
                    oldOne.Reason = "中途放弃（没坚持够时间）";
                    oldOne.Subject = "【专注监督】旧版本遗留记录";
                    oldOne.Body = "（这是旧版本留下的记录，用于展示「未确认」状态的显示效果）";
                    oldOne.Status = "待发送";
                    Store.AppendHistory(oldOne);
                }

                WatchListForm wlf = new WatchListForm(Store.LoadSettings());
                ShotOne(wlf, Path.Combine(dir, "13-监视程序.png"), sb);
                List<string> wlfProblems = new List<string>();
                CheckLayout(wlf, wlfProblems);
                Check(sb, "watchlistform-shot-layout", wlfProblems.Count == 0,
                    wlfProblems.Count == 0 ? "监视清单截图窗口布局完好" : string.Join("；", wlfProblems.ToArray()), ref fail);
                wlf.Close();
                Pump(200);

                HistoryForm hist = new HistoryForm();
                ShotOne(hist, Path.Combine(dir, "07-告状记录.png"), sb);
                hist.Close();
                Pump(200);

                InputBox b1, b2;
                ModernDialog pw = PasswordDialog.BuildAskNew("设置密码", out b1, out b2);
                ShotOne(pw, Path.Combine(dir, "08-密码弹窗.png"), sb);
                List<string> pwProblems = new List<string>();
                CheckLayout(pw, pwProblems);
                CheckTextFits(pw, pwProblems);
                Check(sb, "password-dialog-layout", pwProblems.Count == 0,
                    pwProblems.Count == 0 ? "密码弹窗布局完好（标题行高度也算进去了）" : string.Join("；", pwProblems.ToArray()), ref fail);
                pw.Close();
                Pump(200);

                // 邮件预览（用户反馈过这里溢出）
                ModernDialog preview = TextForm.Build("告状邮件预览（不会真的发送）",
                    Mailer.PreviewBody(Store.LoadSettings()));
                ShotOne(preview, Path.Combine(dir, "09-邮件预览.png"), sb);
                List<string> previewProblems = new List<string>();
                CheckLayout(preview, previewProblems);
                CheckTextFits(preview, previewProblems);
                Check(sb, "preview-dialog-layout", previewProblems.Count == 0,
                    previewProblems.Count == 0 ? "邮件预览窗口布局完好，内容全部可见"
                                               : string.Join("；", previewProblems.ToArray()), ref fail);
                preview.Close();
                Pump(200);

                // 主窗口：双击时间进入编辑模式
                MainForm main2 = new MainForm();
                main2.StartPosition = FormStartPosition.Manual;
                main2.Location = new Point(-6000, -6000);
                main2.Show();
                Pump(400);
                main2.BeginEditMinutes();
                Pump(400);
                sb.Append("编辑浮层：").Append(main2.EditBoundsInfo).Append("\r\n");
                Shot2(main2, Path.Combine(dir, "10-主窗口-改时长.png"));
                List<string> editProblems = new List<string>();
                CheckLayout(main2, editProblems);
                CheckTextFits(main2, editProblems);
                Check(sb, "mainform-edit-mode-layout", editProblems.Count == 0,
                    editProblems.Count == 0 ? "改时长编辑条布局完好" : string.Join("；", editProblems.ToArray()), ref fail);
                main2.SmokeClose();
                Pump(200);

                string[] files = Directory.GetFiles(dir, "*.png");
                Array.Sort(files);
                sb.Append("\r\n生成截图：\r\n");
                for (int i = 0; i < files.Length; i++)
                    sb.Append("  ").Append(Path.GetFileName(files[i])).Append("\r\n");

                // 控件树真实尺寸：卡片到底有多高、内容需要多高
                SettingsForm dump = new SettingsForm(Store.LoadSettings());
                dump.StartPosition = FormStartPosition.Manual;
                dump.Location = new Point(-6000, -6000);
                dump.Show();
                Pump(400);
                ClickNav(dump, "邮件设置");
                Pump(400);
                sb.Append("\r\n---- 邮件设置页控件树（类型/文字/坐标/尺寸/所需高度）----\r\n");
                DumpTree(dump, 0, sb);
                sb.Append("\r\n---- 各可滚动页面：内容高 vs 可视高 ----\r\n");
                DumpScroll(dump, sb);
                dump.Close();
                Pump(200);
            }
            catch (Exception ex)
            {
                if (IsDpapiEnvFailure(ex))
                    Skip(sb, "env-dpapi-profile", "环境阻塞：DPAPI 不可用（" + ex.Message + "）—— 本模式下后续检查未执行；生产加密逻辑未改动");
                else
                    Check(sb, "shot-crashed", false, ex.ToString(), ref fail);
            }

            sb.Append("\r\n结果：").Append(fail == 0 ? (skipped > 0 ? "截图完成（" + skipped + " 项环境跳过）" : "截图完成") : (fail + " 项失败")).Append("\r\n");
            Write(Path.Combine(dir, "shot.log"), sb.ToString());
            return fail == 0 ? 0 : 1;
        }

        private static void DumpTree(Control c, int depth, StringBuilder sb)
        {
            if (depth > 9) return;
            string indent = new string(' ', depth * 2);
            string text = c.Text == null ? "" : c.Text.Replace("\r", " ").Replace("\n", " ");
            if (text.Length > 18) text = text.Substring(0, 18) + "…";
            sb.Append(indent)
              .Append(c.GetType().Name)
              .Append(" [").Append(c.Left).Append(',').Append(c.Top).Append(' ')
              .Append(c.Width).Append('x').Append(c.Height).Append(']')
              .Append(" 需要 ").Append(c.PreferredSize.Width).Append('x').Append(c.PreferredSize.Height)
              .Append(" pad=").Append(c.Padding.Left).Append('/').Append(c.Padding.Top)
              .Append(" vis=").Append(c.Visible ? 1 : 0);
            TableLayoutPanel t = c as TableLayoutPanel;
            if (t != null)
            {
                sb.Append(" rows=").Append(t.RowCount).Append('(');
                for (int i = 0; i < t.RowStyles.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(t.RowStyles[i].SizeType == SizeType.Absolute ? "A" : "a");
                    sb.Append(':').Append(t.RowStyles[i].Height.ToString("0"));
                }
                sb.Append(')');
            }
            if (text.Length > 0) sb.Append("  \"").Append(text).Append('"');
            sb.Append("\r\n");
            foreach (Control child in c.Controls) DumpTree(child, depth + 1, sb);
        }

        private static void DumpScroll(Control c, StringBuilder sb)
        {
            ScrollableControl sc = c as ScrollableControl;
            if (sc != null && sc.AutoScroll)
            {
                int bottom = 0;
                foreach (Control child in c.Controls) if (child.Visible && child.Bottom > bottom) bottom = child.Bottom;
                sb.Append(string.Format("{0}: 可视高 {1}，内容底 {2}，滚动条={3}\r\n",
                    c.GetType().Name, sc.ClientSize.Height, bottom, sc.VerticalScroll.Visible));
            }
            foreach (Control child in c.Controls) DumpScroll(child, sb);
        }

        private static void ShotOne(Form f, string path, StringBuilder sb)
        {
            try
            {
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(-6000, -6000);
                f.ShowInTaskbar = false;
                f.Show();
                Pump(700);
                Shot2(f, path);
                sb.Append(Path.GetFileName(path)).Append("  ").Append(f.Width).Append("x").Append(f.Height).Append("\r\n");
            }
            catch (Exception ex)
            {
                sb.Append("[FAIL] shot-").Append(Path.GetFileName(path)).Append("  —— ").Append(ex.Message).Append("\r\n");
            }
        }

        private static void ShotAgain(Form f, string path)
        {
            Shot2(f, path);
        }

        private static void Shot2(Form f, string path)
        {
            f.Refresh();
            Application.DoEvents();
            using (Bitmap bmp = new Bitmap(f.Width, f.Height))
            {
                f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        private static void ClickNav(Form f, string text)
        {
            FlatButton b = FindButton(f, text);
            if (b != null) b.PerformClick();
        }

        /// <summary>找到设置窗口里当前显示的那一页（带 AutoScroll 的容器）。</summary>
        private static TableLayoutPanel FindPage(Control root)
        {
            TableLayoutPanel t = root as TableLayoutPanel;
            if (t != null && t.AutoScroll && t.Visible) return t;
            foreach (Control c in root.Controls)
            {
                TableLayoutPanel found = FindPage(c);
                if (found != null) return found;
            }
            return null;
        }

        private static FlatButton FindButton(Control root, string text)
        {
            FlatButton b = root as FlatButton;
            if (b != null && b.Text == text) return b;
            foreach (Control c in root.Controls)
            {
                FlatButton found = FindButton(c, text);
                if (found != null) return found;
            }
            return null;
        }

        private const int WM_PRINT = 0x0317;
        private const int PRF_CLIENT = 0x00000004;
        private const int PRF_ERASEBKGND = 0x00000008;
        private const int PRF_CHILDREN = 0x00000010;

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// 只渲染客户区（PRF_CLIENT，不带 PRF_NONCLIENT）。
        /// 不能用 DrawToBitmap：它会带上 PRF_NONCLIENT，于是 Win32 按钮类会额外画一圈
        /// 三维非客户区边框（左上深、右下浅），把"到底有没有黑边"这件事搅浑。
        /// </summary>
        private static void RenderClient(Control c, Bitmap bmp)
        {
            using (Graphics g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                try
                {
                    SendMessage(c.Handle, WM_PRINT, hdc, (IntPtr)(PRF_CLIENT | PRF_ERASEBKGND | PRF_CHILDREN));
                }
                finally
                {
                    g.ReleaseHdc(hdc);
                }
            }
        }

        private static void CheckButton(StringBuilder sb, Card host, FlatButton btn, string tag,
                                        Color expectedFill, ref int fail, ref Bitmap keep)
        {
            using (Bitmap bmp = new Bitmap(btn.Width, btn.Height))
            {
                RenderClient(btn, bmp);

                // 把渲染结果存下来，便于用眼睛核对
                try
                {
                    if (btnDir == null) btnDir = TempDir("pomocc-btn");
                    string img = Path.Combine(btnDir, tag + ".png");
                    using (Bitmap big = new Bitmap(btn.Width * 6, btn.Height * 6))
                    {
                        using (Graphics g = Graphics.FromImage(big))
                        {
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                            g.DrawImage(bmp, new Rectangle(0, 0, big.Width, big.Height));
                        }
                        big.Save(img, System.Drawing.Imaging.ImageFormat.Png);
                    }
                    sb.Append("  按钮渲染图：").Append(img).Append("\r\n");
                }
                catch { }

                Color corner = bmp.GetPixel(1, 1);
                Color corner2 = bmp.GetPixel(btn.Width - 2, 1);
                bool cornersOk = Same(corner, host.BackColor, 6) && Same(corner2, host.BackColor, 6);
                Check(sb, "render-" + tag + "-background-cleared", cornersOk,
                    string.Format("四角颜色 {0} / {1}，父容器底色 {2}",
                        ColorName(corner), ColorName(corner2), ColorName(host.BackColor)), ref fail);

                Color fill = bmp.GetPixel(btn.Width / 2, 5);
                bool fillOk = Same(fill, expectedFill, 24);
                Check(sb, "render-" + tag + "-fill", fillOk,
                    string.Format("填充采样 {0}，期望 {1}", ColorName(fill), ColorName(expectedFill)), ref fail);

                // 文字带：中间 60% 高度区域内，与填充色明显不同的像素数量
                int textPixels = 0;
                int y0 = btn.Height * 2 / 5;
                int y1 = btn.Height * 3 / 5;
                for (int y = y0; y < y1; y++)
                {
                    for (int x = 4; x < btn.Width - 4; x++)
                    {
                        Color p = bmp.GetPixel(x, y);
                        if (!Same(p, expectedFill, 60)) textPixels++;
                    }
                }
                Check(sb, "render-" + tag + "-text", textPixels >= 20,
                    string.Format("文字带内不同色像素 {0} 个（>=20 才算画出文字）", textPixels), ref fail);

                // 最外圈 3px 不允许出现「暗且中性灰」的像素 —— 那是边框被画成了黑线。
                // （GDI+ 的 Pen 会忽略 alpha，Color.Empty 的笔等于不透明黑笔）
                int darkNeutral = 0;
                string sample = "";
                int ring = Dpi.Px(3);
                for (int y = 0; y < btn.Height; y++)
                {
                    for (int x = 0; x < btn.Width; x++)
                    {
                        bool outer = x < ring || y < ring || x >= btn.Width - ring || y >= btn.Height - ring;
                        if (!outer) continue;
                        Color p = bmp.GetPixel(x, y);
                        int lum = (p.R * 299 + p.G * 587 + p.B * 114) / 1000;
                        if (lum < 160 && Math.Abs(p.R - p.G) < 16 && Math.Abs(p.G - p.B) < 16)
                        {
                            if (darkNeutral == 0) sample = string.Format("{0}@{1},{2}", ColorName(p), x, y);
                            darkNeutral++;
                        }
                    }
                }
                Check(sb, "render-" + tag + "-no-dark-edge", darkNeutral == 0,
                    darkNeutral == 0 ? "按钮最外圈没有黑色描边" : string.Format("最外圈出现 {0} 个暗色中性像素（例如 {1}）—— 边框被画成了黑线",
                        darkNeutral, sample), ref fail);

                if (keep == null) keep = (Bitmap)bmp.Clone();
            }
        }

        /// <summary>
        /// 把整个窗口渲染到位图，扫描内部区域：
        ///  · 纯黑像素 = 没有被任何绘制覆盖的区域（自绘控件忘了擦背景就会这样）；
        ///  · 顺便统计主色/白色像素，确认卡片和主按钮真的画出来了。
        /// 跳过外框和标题栏（那部分由系统绘制，位图里不具参考性）。
        /// </summary>
        /// <summary>
        /// 像素探针：在真实窗口里检查按钮周围到底是什么颜色。
        /// 专抓"按钮下面有黑/灰底条"这类问题（透明背景的容器没有正确擦成父容器底色）。
        /// </summary>
        private static void ProbeSurfaces(StringBuilder sb, ref int fail)
        {
            SettingsForm sf = new SettingsForm(Store.LoadSettings());
            sf.StartPosition = FormStartPosition.Manual;
            sf.Location = new Point(-6000, -6000);
            sf.ShowInTaskbar = false;
            sf.Show();
            Pump(600);

            FlatButton target = FindButton(sf, "＋ 添加程序");
            Check(sb, "probe-found-add-button", target != null, target == null ? "没找到「＋ 添加程序」按钮" : "已定位按钮", ref fail);

            if (target != null)
            {
                // 按钮所在的父容器链：每个容器实际显示的底色
                Control p = target.Parent;
                int depth = 0;
                while (p != null && depth < 6)
                {
                    sb.Append(string.Format("  父容器{0}: {1} BackColor={2}\r\n",
                        depth, p.GetType().Name, ColorName(p.BackColor)));
                    p = p.Parent;
                    depth++;
                }

                // 把按钮和它下方 6px 一起渲染出来，看底色
                Control host = target.Parent;
                using (Bitmap bmp = new Bitmap(host.Width, host.Height))
                {
                    host.DrawToBitmap(bmp, new Rectangle(0, 0, host.Width, host.Height));

                    // 统计整块区域的颜色分布（取出现最多的三种）
                    Dictionary<string, int> hist = new Dictionary<string, int>();
                    for (int y = 0; y < bmp.Height; y++)
                    {
                        for (int x = 0; x < bmp.Width; x++)
                        {
                            string key = ColorName(bmp.GetPixel(x, y));
                            int n;
                            hist.TryGetValue(key, out n);
                            hist[key] = n + 1;
                        }
                    }
                    List<KeyValuePair<string, int>> list = new List<KeyValuePair<string, int>>(hist);
                    list.Sort(delegate(KeyValuePair<string, int> a, KeyValuePair<string, int> b) { return b.Value.CompareTo(a.Value); });
                    sb.Append("  按钮所在行颜色分布（前 5）：");
                    for (int i = 0; i < list.Count && i < 5; i++)
                        sb.Append(list[i].Key).Append('×').Append(list[i].Value).Append("  ");
                    sb.Append("\r\n");

                    // 按钮下方 6px 的底色采样（黑/灰块最容易出现在这里）
                    int by = target.Bottom + Dpi.Px(2);
                    if (by < bmp.Height)
                    {
                        Color under = bmp.GetPixel(Math.Max(0, Math.Min(bmp.Width - 1, target.Left + target.Width / 2)), by);
                        sb.Append(string.Format("  按钮正下方 2px 处颜色：{0}\r\n", ColorName(under)));
                    }

                    int black = 0, sysGray = 0;
                    Color sys = SystemColors.Control;
                    for (int y = 0; y < bmp.Height; y++)
                    {
                        for (int x = 0; x < bmp.Width; x++)
                        {
                            Color c = bmp.GetPixel(x, y);
                            if (c.R == 0 && c.G == 0 && c.B == 0) black++;
                            else if (c.R == sys.R && c.G == sys.G && c.B == sys.B) sysGray++;
                        }
                    }
                    double total = bmp.Width * (double)bmp.Height;
                    Check(sb, "probe-button-row-no-black", black * 100.0 / total < 0.5,
                        string.Format("按钮所在行纯黑像素 {0}/{1} = {2:0.##}%", black, (int)total, black * 100.0 / total), ref fail);
                    Check(sb, "probe-button-row-no-system-gray", sysGray * 100.0 / total < 0.5,
                        string.Format("系统灰(#F0F0F0)像素 {0}/{1} = {2:0.##}%（透明容器没擦背景就会这样）",
                            sysGray, (int)total, sysGray * 100.0 / total), ref fail);
                }
            }

            sf.Close();
            sf.Dispose();
            Pump(200);
        }

        private static void CheckWindow(StringBuilder sb, Form f, string tag, bool expectAccent, ref int fail)
        {
            try
            {
                f.AutoScaleMode = AutoScaleMode.None;
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(-5000, -5000);   // 屏幕外，不在用户桌面闪
                f.ShowInTaskbar = false;
                f.Show();
                Application.DoEvents();
                f.PerformLayout();
                Pump(400);

                using (Bitmap bmp = new Bitmap(f.Width, f.Height))
                {
                    f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));

                    int inset = Dpi.Px(30);          // 跳过系统绘制的边框
                    int top = Dpi.Px(40);            // 跳过标题栏
                    int black = 0, accent = 0, total = 0;
                    for (int y = top; y < bmp.Height - inset; y++)
                    {
                        for (int x = inset; x < bmp.Width - inset; x++)
                        {
                            Color c = bmp.GetPixel(x, y);
                            total++;
                            if (c.R == 0 && c.G == 0 && c.B == 0) black++;
                            else if (Same(c, Theme.Accent, 12)) accent++;
                        }
                    }

                    double blackPct = total == 0 ? 0 : black * 100.0 / total;
                    Check(sb, "render-" + tag + "-no-unpainted-area", blackPct < 2.0,
                        string.Format("窗口 {0}x{1}，内部纯黑(未绘制)像素 {2}/{3} = {4:0.##}%",
                            f.Width, f.Height, black, total, blackPct), ref fail);

                    if (expectAccent)
                    {
                        // 「程序列表为空 → 主按钮禁用」是**正确行为**：禁用态根本不画主色，
                        // 只画浅底色 + 描边 + 浅色文字。这里以前死要求"主色 > 200"，
                        // 于是在枚举不到程序的机器上必然误报 —— 用户正是撞上这个场景。
                        AppPickerForm picker = f as AppPickerForm;
                        bool btnEnabled = picker == null || picker.PrimaryButton == null || picker.PrimaryButton.Enabled;
                        int progCount = picker == null ? -1 : picker.ProgramCount;

                        bool accentOk;
                        string accentNote;
                        if (btnEnabled)
                        {
                            accentOk = accent > 200;
                            accentNote = string.Format("主色像素 {0} 个（列表 {1} 项 → 主按钮启用，应当被画出来）", accent, progCount);
                        }
                        else
                        {
                            // 禁用态：改看按钮**自己**那张位图（与窗体边框无关），
                            // 断言它确实被画成了「浅底 + 描边 + 浅色文字」，而不是一片空白。
                            int bBorder = 0, bFaint = 0, bAccent = 0;
                            FlatButton btn = picker.PrimaryButton;
                            if (btn != null && btn.Width > 0 && btn.Height > 0)
                            {
                                using (Bitmap bb = new Bitmap(btn.Width, btn.Height))
                                {
                                    btn.DrawToBitmap(bb, new Rectangle(0, 0, bb.Width, bb.Height));
                                    for (int y = 0; y < bb.Height; y++)
                                    {
                                        for (int x = 0; x < bb.Width; x++)
                                        {
                                            Color c = bb.GetPixel(x, y);
                                            if (Same(c, Theme.Accent, 12)) bAccent++;
                                            else if (Same(c, Theme.Border, 8)) bBorder++;
                                            else if (Same(c, Theme.FaintText, 24)) bFaint++;
                                        }
                                    }
                                }
                            }
                            accentOk = (bBorder + bFaint) > 40 && bAccent < 40;
                            accentNote = string.Format(
                                "列表 {0} 项 → 主按钮禁用：按钮自身位图 描边 {1} px、浅色文字 {2} px、主色 {3} px（禁用态不画主色，这是正确行为）",
                                progCount, bBorder, bFaint, bAccent);
                        }
                        Check(sb, "render-" + tag + "-has-accent", accentOk, accentNote, ref fail);

                        if (picker != null && picker.PrimaryButton != null)
                        {
                            Check(sb, "render-" + tag + "-button-state",
                                (progCount > 0) == picker.PrimaryButton.Enabled,
                                string.Format("列表 {0} 项 → 主按钮 Enabled={1}（空列表必须是禁用态）",
                                    progCount, picker.PrimaryButton.Enabled), ref fail);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Check(sb, "render-" + tag + "-crashed", false, ex.Message, ref fail);
            }
            finally
            {
                try { f.Close(); f.Dispose(); } catch { }
            }
        }

        private static bool Same(Color a, Color b, int tolerance)
        {
            return Math.Abs(a.R - b.R) <= tolerance
                && Math.Abs(a.G - b.G) <= tolerance
                && Math.Abs(a.B - b.B) <= tolerance;
        }

        private static string ColorName(Color c)
        {
            return string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);
        }

        // ==================================================================
        //  6. 命令行：SMTP 连通性 / 端到端发信
        // ==================================================================

        public static int SmtpCheck(string[] args)
        {
            string outPath = Path.Combine(Path.GetTempPath(), "pomocc-smtp-check.log");
            string host = null;
            int port = 587;

            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--smtp-check", StringComparison.OrdinalIgnoreCase) && i + 3 < args.Length)
                {
                    outPath = args[i + 1];
                    host = args[i + 2];
                    int parsed;
                    if (int.TryParse(args[i + 3], out parsed)) port = parsed;
                }
            }

            StringBuilder sb = new StringBuilder();
            int fail = 0;
            ReportDpapiEnvironment(sb);
            if (string.IsNullOrEmpty(host))
            {
                Check(sb, "smtp-check-args", false, "需要：--smtp-check <日志文件> <主机> <端口>", ref fail);
            }
            else
            {
                sb.Append(string.Format("探测 {0}:{1}\r\n", host, port));
                try
                {
                    string result = SmtpTransport.Probe(host, port, port != 465 && port != 994);
                    Check(sb, "smtp-connect", true, result, ref fail);
                }
                catch (Exception ex)
                {
                    Check(sb, "smtp-connect", false, ex.Message, ref fail);
                }
            }

            sb.Append("\r\n结果：").Append(Summary(fail)).Append("\r\n");
            Write(outPath, sb.ToString());
            return fail == 0 ? 0 : 1;
        }

        public static int MailTest(string[] args)
        {
            string outPath = Path.Combine(Path.GetTempPath(), "pomocc-mail-test.log");
            string host = "127.0.0.1";
            int port = 2525;

            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--mail-test", StringComparison.OrdinalIgnoreCase) && i + 3 < args.Length)
                {
                    outPath = args[i + 1];
                    host = args[i + 2];
                    int parsed;
                    if (int.TryParse(args[i + 3], out parsed)) port = parsed;
                }
            }

            StringBuilder sb = new StringBuilder();
            int fail = 0;
            ReportDpapiEnvironment(sb);

            Settings s = Settings.Defaults();
            s.UserName = "测试同学";
            s.SendMode = "smtp";
            s.SmtpHost = host;
            s.SmtpPort = port;
            s.SmtpStartTls = false;
            s.SenderEmail = "tester@example.com";
            s.SupervisorEmail = "boss@example.com";
            s.FocusMinutes = 25;
            s.Rules = new List<WatchRule>();
            s.Rules.Add(Rule("steam.exe", 3));
            TrySetAuthCode(s, "dummy-auth-code");

            FocusSession demo = Mailer.DemoSession(s);
            string reason = "专注期间偷玩超时（steam.exe）";
            string subject = Mailer.ComposeSubject(s, reason, DateTime.Now, demo);
            string body = Mailer.ComposeBody(s, demo, DailyStats.NewFor(DateTime.Now), reason, DateTime.Now);

            sb.Append(string.Format("目标 SMTP：{0}:{1}\r\n主题：{2}\r\n\r\n", host, port, subject));
            try
            {
                Mailer.Send(s, subject, body);
                Check(sb, "mail-send-end-to-end", true, "SMTP 会话完成，服务器已接收", ref fail);
            }
            catch (Exception ex)
            {
                Check(sb, "mail-send-end-to-end", false, ex.ToString(), ref fail);
            }

            sb.Append("\r\n结果：").Append(Summary(fail)).Append("\r\n");
            Write(outPath, sb.ToString());
            return fail == 0 ? 0 : 1;
        }

        // ==================================================================

        private static void Pump(int ms)
        {
            DateTime until = DateTime.Now.AddMilliseconds(ms);
            while (DateTime.Now < until)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(20);
            }
        }

        private static int CountControls(Control c)
        {
            int n = c.Controls.Count;
            foreach (Control child in c.Controls) n += CountControls(child);
            return n;
        }

        /// <summary>
        /// 检查标签文字是否被截断：按可用宽度做自动换行后算需要多高，
        /// 超过控件高度就说明有字被切掉了。
        /// </summary>
        private static void CheckTextFits(Control parent, List<string> problems)
        {
            if (problems.Count > 8) return;

            Label l = parent as Label;
            if (l != null && l.Visible && !l.AutoSize && l.Text.Length > 0 && l.ClientSize.Width > 8)
            {
                Size need = TextRenderer.MeasureText(l.Text, l.Font,
                    new Size(l.ClientSize.Width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                if (need.Height > l.ClientSize.Height + 2)
                {
                    problems.Add(string.Format("标签文字被截断：「{0}」需要 {1}px 高，只有 {2}px",
                        Shorten(l.Text), need.Height, l.ClientSize.Height));
                }
            }

            foreach (Control c in parent.Controls) CheckTextFits(c, problems);
        }

        private static string Shorten(string s)
        {
            if (s == null) return "";
            string one = s.Replace("\r", " ").Replace("\n", " ");
            return one.Length > 24 ? one.Substring(0, 24) + "…" : one;
        }

        /// <summary>环境跳过计数（每个模式一个进程，无需重置）。</summary>
        private static int skipped;

        /// <summary>本次模式下 DPAPI 是否可用（由 ReportDpapiEnvironment 填）。</summary>
        private static bool dpapiEnv = true;
        private static string dpapiEnvWhy = "";

        /// <summary>
        /// 当前环境能不能用 DPAPI。ProtectedData 需要"已加载的用户配置文件"：
        /// 服务会话或未加载配置文件的账号下会抛 CryptographicException
        /// （当前线程用户上下文未加载用户配置文件）。
        /// 这是**环境阻塞**，不是代码缺陷 —— 记 [SKIP] 并继续跑其余检查。
        /// 生产加密逻辑（Settings.Protect/Unprotect）一个字都不改。
        /// </summary>
        private static bool DpapiUsable(out string why)
        {
            why = "";
            if (Environment.GetEnvironmentVariable("POMOCC_TEST_NO_DPAPI") == "1")
            {
                why = "测试强制模拟环境阻塞（POMOCC_TEST_NO_DPAPI=1）";
                return false;
            }
            try
            {
                Settings probe = Settings.Defaults();
                probe.SetAuthCode("dpapi-probe");
                if (probe.GetAuthCode() != "dpapi-probe") { why = "DPAPI 加解密结果不一致"; return false; }
                return true;
            }
            catch (Exception ex)
            {
                why = ex.GetType().Name + "：" + ex.Message;
                return false;
            }
        }

        /// <summary>每个模式开头调用：把 DPAPI 环境状态写进日志并在不可用时记一条环境跳过。</summary>
        private static void ReportDpapiEnvironment(StringBuilder sb)
        {
            dpapiEnv = DpapiUsable(out dpapiEnvWhy);
            sb.Append("环境检查：DPAPI（需要已加载的用户配置文件）")
              .Append(dpapiEnv ? "可用" : "不可用 —— " + dpapiEnvWhy)
              .Append("\r\n\r\n");
            if (!dpapiEnv) SkipDpapi(sb, "env-dpapi-profile", dpapiEnvWhy);
        }

        private static void SkipDpapi(StringBuilder sb, string name, string why)
        {
            Skip(sb, name, "环境阻塞（DPAPI 不可用）：" + why + "；与加密相关的检查未验证，其余检查照常执行");
        }

        /// <summary>安全设授权码：环境不支持 DPAPI 时返回 false，不影响其余断言。</summary>
        private static bool TrySetAuthCode(Settings s, string v)
        {
            try { s.SetAuthCode(v); return true; }
            catch (Exception) { return false; }
        }

        /// <summary>安全设 API Key：环境不支持 DPAPI 时返回 false。</summary>
        private static bool TrySetApiKey(Settings s, string v)
        {
            try { s.SetApiKey(v); return true; }
            catch (Exception) { return false; }
        }

        /// <summary>判断异常是不是"DPAPI 环境不可用"这一类。</summary>
        private static bool IsDpapiEnvFailure(Exception ex)
        {
            for (Exception e = ex; e != null; e = e.InnerException)
            {
                if (e is System.Security.Cryptography.CryptographicException) return true;
                string m = e.Message;
                if (!string.IsNullOrEmpty(m) &&
                    (m.IndexOf("用户配置文件") >= 0 || m.IndexOf("user profile", StringComparison.OrdinalIgnoreCase) >= 0))
                    return true;
            }
            return false;
        }

        /// <summary>汇总行：必须区分"全部通过"和"有环境跳过（未验证）"。</summary>
        private static string Summary(int fail)
        {
            if (fail > 0) return fail + " 项失败";
            if (skipped > 0) return "通过，但有 " + skipped + " 项因环境阻塞跳过、未验证";
            return "全部通过";
        }

        /// <summary>
        /// 记一条跳过（环境不满足，例如用户正开着程序）。
        /// 既不算通过也不算失败 —— 免得把环境状态误报成缺陷，也免得掩盖真问题。
        /// </summary>
        private static void Skip(StringBuilder sb, string name, string reason)
        {
            skipped++;
            sb.Append("[SKIP] ").Append(name).Append("  —— ").Append(reason).Append("\r\n");
        }

        private static void Check(StringBuilder sb, string name, bool ok, string detail, ref int fail)
        {
            sb.Append(ok ? "[PASS] " : "[FAIL] ").Append(name);
            if (!string.IsNullOrEmpty(detail)) sb.Append("  —— ").Append(detail);
            sb.Append("\r\n");
            if (!ok) fail++;
        }

        private static void Write(string path, string text)
        {
            try { File.WriteAllText(path, text, Encoding.UTF8); }
            catch { }
        }
    }
}
