using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace PomoCC
{
    public class DailyStats
    {
        public string Date { get; set; }
        public int CompletedCount { get; set; }
        public int AbandonedCount { get; set; }
        public int ReportCount { get; set; }
        public int FocusSeconds { get; set; }
        public int ViolationSeconds { get; set; }

        public static DailyStats NewFor(DateTime day)
        {
            DailyStats s = new DailyStats();
            s.Date = day.ToString("yyyy-MM-dd");
            return s;
        }
    }

    public class HistoryEntry
    {
        public string Time { get; set; }
        public string Reason { get; set; }
        public string Subject { get; set; }
        public string Body { get; set; }
        public string Status { get; set; }
        public string Error { get; set; }
    }

    public static class Store
    {
        /// <summary>仅供自检使用，指向临时目录以免污染真实数据。</summary>
        public static string OverrideDir;

        /// <summary>仅供自检：替换 %APPDATA% 这个「漫游根」，从而能验证目录改名迁移。</summary>
        internal static string RoamingRootOverride;

        /// <summary>数据目录名。</summary>
        public const string DataFolderName = "PomoCC";
        /// <summary>老版本的数据目录名，仅用于一次性迁移。</summary>
        internal const string LegacyDataFolderName = "PomodoroSupervisor";

        private const string ConfigFileName = "config.json";

        private static JavaScriptSerializer Ser = new JavaScriptSerializer();

        private static string RoamingRoot
        {
            get
            {
                return string.IsNullOrEmpty(RoamingRootOverride)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                    : RoamingRootOverride;
            }
        }

        public static string Dir
        {
            get
            {
                if (!string.IsNullOrEmpty(OverrideDir)) return OverrideDir;
                string d = Path.Combine(RoamingRoot, DataFolderName);
                Directory.CreateDirectory(d);
                MigrateLegacyData(d);
                return d;
            }
        }

        /// <summary>
        /// 数据目录改名：%APPDATA%\PomodoroSupervisor → %APPDATA%\PomoCC。
        /// 第一次运行时把老目录里的文件搬过来（配置/统计/记录/日志），确认能读出来之后删掉老目录 ——
        /// 不搬的话用户会以为"设置、密码、授权码全丢了"，不删的话机器上会多留一个空目录。
        /// </summary>
        private static void MigrateLegacyData(string newDir)
        {
            try
            {
                string newCfg = Path.Combine(newDir, ConfigFileName);
                if (File.Exists(newCfg)) return;                  // 新目录已有配置：绝不覆盖
                string oldDir = Path.Combine(RoamingRoot, LegacyDataFolderName);
                if (!Directory.Exists(oldDir)) return;
                if (!File.Exists(Path.Combine(oldDir, ConfigFileName))) return;   // 老目录没配置，没什么可搬

                string[] files = { ConfigFileName, "stats.json", "history.jsonl", "app.log" };
                for (int i = 0; i < files.Length; i++)
                {
                    string src = Path.Combine(oldDir, files[i]);
                    if (File.Exists(src)) File.Copy(src, Path.Combine(newDir, files[i]), true);
                }

                // 确认新配置真的读得出来，再删老目录
                string text = File.ReadAllText(newCfg, Encoding.UTF8);
                if (text.Trim().Length == 0) return;
                try { Directory.Delete(oldDir, true); }
                catch { return; }                                 // 删不掉就留着，至少数据已经搬过来了

                // 注意：这里不能调 Log() —— Log 会走 LogPath → Dir → 重新进入本方法。直接写文件。
                try
                {
                    File.AppendAllText(Path.Combine(newDir, "app.log"),
                        string.Format("{0:yyyy-MM-dd HH:mm:ss}  数据目录已从 {1} 迁移到 {2}\r\n",
                            DateTime.Now, LegacyDataFolderName, DataFolderName), Encoding.UTF8);
                }
                catch { }
            }
            catch { }
        }

        public static string ConfigPath { get { return Path.Combine(Dir, "config.json"); } }
        public static string StatsPath { get { return Path.Combine(Dir, "stats.json"); } }
        public static string HistoryPath { get { return Path.Combine(Dir, "history.jsonl"); } }
        public static string LogPath { get { return Path.Combine(Dir, "app.log"); } }

        // 日志只追加不清会慢慢变大（每次启动、每段专注都会写几行）。
        // 超过上限就只保留最近一段，避免在用户机器上无限增长。
        private const long LogMaxBytes = 512 * 1024;
        private const long LogKeepBytes = 128 * 1024;

        public static void Log(string msg)
        {
            try
            {
                FileInfo fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > LogMaxBytes) TrimLog();
                File.AppendAllText(LogPath,
                    string.Format("{0:yyyy-MM-dd HH:mm:ss}  {1}\r\n", DateTime.Now, msg),
                    Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>把日志截断成「只留最后一段」，并从第一个完整行开始（不切出半行）。</summary>
        private static void TrimLog()
        {
            using (FileStream fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                long start = Math.Max(0, fs.Length - LogKeepBytes);
                fs.Seek(start, SeekOrigin.Begin);
                byte[] buf = new byte[fs.Length - start];
                int read = fs.Read(buf, 0, buf.Length);
                string text = Encoding.UTF8.GetString(buf, 0, read);
                int nl = text.IndexOf('\n');
                if (nl >= 0) text = text.Substring(nl + 1);
                File.WriteAllText(LogPath,
                    "（日志超过上限，已截断只保留最近一段）\r\n" + text, Encoding.UTF8);
            }
        }

        // ---------- 设置 ----------

        public static Settings LoadSettings()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath, Encoding.UTF8);
                    Settings s = Ser.Deserialize<Settings>(json);
                    if (s != null)
                    {
                        Normalize(s);
                        return s;
                    }
                }
            }
            catch (Exception ex)
            {
                Log("读取配置失败，改用默认配置：" + ex.Message);
                try
                {
                    File.Copy(ConfigPath, ConfigPath + ".bad", true);
                }
                catch { }
            }

            Settings def = Settings.Defaults();
            try { SaveSettings(def); } catch { }
            return def;
        }

        private static void Normalize(Settings s)
        {
            Settings d = Settings.Defaults();
            if (s.WatchList == null) s.WatchList = new List<string>();
            if (s.Rules == null) s.Rules = new List<WatchRule>();
            if (s.Version == 0) s.Version = 1;

            // v2：新增「专注完成时提醒」（托盘气泡 + 提示音），默认开启。
            // v1 配置里没有这两个字段，反序列化后是 false —— 必须显式补成 true，
            // 否则升级上来的人会莫名其妙收不到完成提醒（而且是静默失效，很难发现）。
            if (s.Version < 2)
            {
                s.NotifyOnComplete = true;
                s.NotifySound = true;
                s.Version = 2;
            }
            if (s.FocusMinutes <= 0) s.FocusMinutes = d.FocusMinutes;
            if (s.ViolationSeconds <= 0) s.ViolationSeconds = d.ViolationSeconds;
            if (s.SampleSeconds <= 0) s.SampleSeconds = d.SampleSeconds;
            if (string.IsNullOrEmpty(s.SendMode)) s.SendMode = d.SendMode;
            if (string.IsNullOrEmpty(s.SmtpHost)) s.SmtpHost = d.SmtpHost;
            if (s.SmtpPort <= 0) s.SmtpPort = d.SmtpPort;
            if (string.IsNullOrEmpty(s.UserName)) s.UserName = d.UserName;

            // 旧版「一串进程名」自动迁移成带规则时长的规则表，不丢用户已填的名字
            if (s.Rules.Count == 0 && s.WatchList.Count > 0)
            {
                int limit = Math.Max(1, (int)Math.Round(s.ViolationSeconds / 60.0));
                foreach (string raw in s.WatchList)
                {
                    string exe = ProcessMonitor.NormalizeName(raw);
                    if (exe.Length == 0) continue;
                    WatchRule r = new WatchRule();
                    r.Exe = exe;
                    r.LimitMinutes = limit;
                    s.Rules.Add(r);
                }
                s.WatchList = new List<string>();
                Store.Log(string.Format("已把旧版监督名单迁移为 {0} 条规则", s.Rules.Count));
            }

            for (int i = 0; i < s.Rules.Count; i++)
            {
                if (s.Rules[i] == null) { s.Rules[i] = new WatchRule(); continue; }
                s.Rules[i].Exe = ProcessMonitor.NormalizeName(s.Rules[i].Exe);
                if (s.Rules[i].LimitMinutes <= 0) s.Rules[i].LimitMinutes = Math.Max(1, (int)Math.Round(s.ViolationSeconds / 60.0));
            }
            s.Rules.RemoveAll(delegate(WatchRule r) { return r == null || r.Exe.Length == 0; });
        }

        public static void SaveSettings(Settings s)
        {
            File.WriteAllText(ConfigPath, Ser.Serialize(s), Encoding.UTF8);
        }

        // ---------- 每日统计 ----------

        public static DailyStats LoadToday()
        {
            try
            {
                if (File.Exists(StatsPath))
                {
                    DailyStats s = Ser.Deserialize<DailyStats>(File.ReadAllText(StatsPath, Encoding.UTF8));
                    if (s != null && s.Date == DateTime.Now.ToString("yyyy-MM-dd")) return s;
                }
            }
            catch (Exception ex)
            {
                Log("读取统计失败：" + ex.Message);
            }
            return DailyStats.NewFor(DateTime.Now);
        }

        public static void SaveStats(DailyStats s)
        {
            try
            {
                File.WriteAllText(StatsPath, Ser.Serialize(s), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Log("写入统计失败：" + ex.Message);
            }
        }

        // ---------- 告状记录 ----------

        public static void AppendHistory(HistoryEntry e)
        {
            try
            {
                File.AppendAllText(HistoryPath, Ser.Serialize(e) + "\r\n", Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Log("写入告状记录失败：" + ex.Message);
            }
        }

        public static List<HistoryEntry> ReadHistory(int max)
        {
            List<HistoryEntry> list = new List<HistoryEntry>();
            try
            {
                if (!File.Exists(HistoryPath)) return list;
                string[] lines = File.ReadAllLines(HistoryPath, Encoding.UTF8);
                for (int i = lines.Length - 1; i >= 0 && list.Count < max; i--)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0) continue;
                    try
                    {
                        HistoryEntry e = Ser.Deserialize<HistoryEntry>(line);
                        if (e != null) list.Add(e);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Log("读取告状记录失败：" + ex.Message);
            }
            return list;
        }
    }
}
