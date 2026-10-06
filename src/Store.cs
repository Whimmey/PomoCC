using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace PomodoroSupervisor
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

        private static JavaScriptSerializer Ser = new JavaScriptSerializer();

        public static string Dir
        {
            get
            {
                if (!string.IsNullOrEmpty(OverrideDir)) return OverrideDir;
                string d = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "PomodoroSupervisor");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        public static string ConfigPath { get { return Path.Combine(Dir, "config.json"); } }
        public static string StatsPath { get { return Path.Combine(Dir, "stats.json"); } }
        public static string HistoryPath { get { return Path.Combine(Dir, "history.jsonl"); } }
        public static string LogPath { get { return Path.Combine(Dir, "app.log"); } }

        public static void Log(string msg)
        {
            try
            {
                File.AppendAllText(LogPath,
                    string.Format("{0:yyyy-MM-dd HH:mm:ss}  {1}\r\n", DateTime.Now, msg),
                    Encoding.UTF8);
            }
            catch { }
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
