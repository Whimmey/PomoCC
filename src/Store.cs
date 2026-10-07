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
        /// 迁移完成标记。**只有**标记存在才算迁移结束：
        /// 中途失败（某个文件复制不了、配置读不出来）就不写标记，下次启动继续重试，
        /// 不会因为"新目录里已经有 config.json"而永久跳过剩下的文件。
        /// </summary>
        internal const string MigrateMarkerName = "migrated.json";

        /// <summary>
        /// 数据目录改名：%APPDATA%\PomodoroSupervisor → %APPDATA%\PomoCC。
        ///
        /// 可靠的迁移顺序（任何一步失败都不写标记，下次启动继续）：
        ///   1. 每个文件先复制成 `xxx.migrating`，**验证内容**（配置/统计能反序列化、
        ///      历史逐行可解析、日志存在）后再原子落位 —— 绝不直接写正式文件；
        ///   2. 目标文件已存在但**验证不过**（上次复制被中断留下的残缺文件）→ 重新复制；
        ///   3. 标记必须写入成功**并读得回来**，才允许删旧目录；
        ///   4. 旧目录删不掉只记日志（下次启动会再试），不影响数据安全。
        /// </summary>
        internal static void MigrateLegacyData(string newDir)
        {
            try
            {
                string marker = Path.Combine(newDir, MigrateMarkerName);
                string oldDir = Path.Combine(RoamingRoot, LegacyDataFolderName);

                if (File.Exists(marker))
                {
                    // 已经迁移完成：如果旧目录还在（上次删失败），这里补删一次
                    if (Directory.Exists(oldDir))
                    {
                        try
                        {
                            Directory.Delete(oldDir, true);
                            AppendLogDirect(newDir, "已清理迁移残留的旧数据目录 " + LegacyDataFolderName);
                        }
                        catch (Exception ex)
                        {
                            AppendLogDirect(newDir, "旧数据目录删除失败，下次启动再试：" + ex.Message);
                        }
                    }
                    return;
                }

                if (!Directory.Exists(oldDir))
                {
                    WriteMigrateMarker(marker, "没有旧目录，无需迁移");
                    return;
                }
                if (!File.Exists(Path.Combine(oldDir, ConfigFileName)))
                {
                    if (!WriteMigrateMarker(marker, "旧目录里没有配置，无需迁移")) return;
                    try { Directory.Delete(oldDir, true); } catch { }
                    return;
                }

                string[] files = { ConfigFileName, "stats.json", "history.jsonl", "app.log" };
                int copied = 0;
                for (int i = 0; i < files.Length; i++)
                {
                    string src = Path.Combine(oldDir, files[i]);
                    string dst = Path.Combine(newDir, files[i]);
                    if (!File.Exists(src)) continue;

                    // 目标已有而且内容有效 → 说明是用户自己的（或上次已搬好），绝不覆盖
                    if (File.Exists(dst) && ValidateMigratedFile(files[i], dst)) continue;

                    string tmp = dst + ".migrating";
                    TryDelete(tmp);
                    File.Copy(src, tmp, true);                       // 失败会抛 → 不写标记 → 下次重试
                    if (!ValidateMigratedFile(files[i], tmp))
                    {
                        TryDelete(tmp);                              // 复制不完整：不落位，保留旧目录
                        return;
                    }
                    if (File.Exists(dst)) TryDelete(dst);            // 目标损坏：删掉残缺文件后再落位
                    File.Move(tmp, dst);
                    copied++;
                }

                // 标记写入并读回验证成功，才允许删旧目录
                if (!WriteMigrateMarker(marker, string.Format("从 {0} 迁移了 {1} 个文件", LegacyDataFolderName, copied)))
                    return;

                try { Directory.Delete(oldDir, true); }
                catch (Exception ex) { AppendLogDirect(newDir, "迁移完成，但旧数据目录删除失败：" + ex.Message); }

                AppendLogDirect(newDir, string.Format("数据目录已从 {0} 迁移到 {1}（{2} 个文件）",
                    LegacyDataFolderName, DataFolderName, copied));
            }
            catch
            {
                // 迁移失败：不写标记，下次启动继续（绝不在这里删旧目录）
            }
        }

        /// <summary>校验迁移过来的文件内容（"文件存在"不等于"内容完整"）。</summary>
        private static bool ValidateMigratedFile(string file, string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                if (file == ConfigFileName)
                    return Ser.Deserialize<Settings>(File.ReadAllText(path, Encoding.UTF8)) != null;
                if (file == "stats.json")
                    return Ser.Deserialize<DailyStats>(File.ReadAllText(path, Encoding.UTF8)) != null;
                if (file == "history.jsonl")
                {
                    int bad;
                    return VerifyHistoryLines(path, out bad) && bad == 0;
                }
                return true;                                          // app.log：存在即可
            }
            catch { return false; }
        }

        /// <summary>历史文件逐行校验：返回"非空行都读得出来"，并给出损坏行数。</summary>
        private static bool VerifyHistoryLines(string path, out int badLines)
        {
            badLines = 0;
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Trim().Length == 0) continue;
                try
                {
                    if (Ser.Deserialize<HistoryEntry>(lines[i]) == null) badLines++;
                }
                catch { badLines++; }
            }
            return badLines == 0;
        }

        /// <summary>写迁移标记：写入后必须能读回同样的内容才算成功。</summary>
        private static bool WriteMigrateMarker(string marker, string note)
        {
            string text = "{\"migratedAt\":\"" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\",\"note\":\"" + note + "\"}\r\n";
            try
            {
                File.WriteAllText(marker, text, Encoding.UTF8);
                string back = File.ReadAllText(marker, Encoding.UTF8);
                return back == text;
            }
            catch { return false; }
        }

        /// <summary>迁移过程中写日志：不能走 Log()，否则 Log→Dir→Migrate 递归。</summary>
        private static void AppendLogDirect(string dir, string msg)
        {
            try
            {
                File.AppendAllText(Path.Combine(dir, "app.log"),
                    string.Format("{0:yyyy-MM-dd HH:mm:ss}  {1}\r\n", DateTime.Now, msg), Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>供自检：验证配置文件内容。</summary>
        internal static bool VerifyConfigForTest(string path) { return ValidateMigratedFile(ConfigFileName, path); }
        /// <summary>供自检：验证统计文件内容。</summary>
        internal static bool VerifyStatsForTest(string path) { return ValidateMigratedFile("stats.json", path); }
        /// <summary>供自检：验证历史文件内容。</summary>
        internal static bool VerifyHistoryForTest(string path, out int badLines)
        {
            try { return VerifyHistoryLines(path, out badLines); }
            catch { badLines = -1; return false; }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }

        // ---------- 原子写入（配置 / 统计）----------

        /// <summary>
        /// 先写 `xxx.tmp` → 验证内容 → 原子替换正式文件。
        /// 失败时**不动原正式文件**，临时文件也留着方便排查/恢复。
        /// </summary>
        private static bool WriteAtomic(string path, string text, Func<string, bool> verify)
        {
            string tmp = path + ".tmp";
            try { File.WriteAllText(tmp, text, Encoding.UTF8); }
            catch { return false; }

            if (verify != null)
            {
                bool ok;
                try { ok = verify(tmp); }
                catch { ok = false; }
                if (!ok) return false;                                 // 不替换正式文件
            }

            try
            {
                if (File.Exists(path)) File.Replace(tmp, path, null, true);
                else File.Move(tmp, path);
                return true;
            }
            catch { return false; }                                    // 原文件保持原样
        }

        private static bool VerifySettingsText(string path)
        {
            return Ser.Deserialize<Settings>(File.ReadAllText(path, Encoding.UTF8)) != null;
        }

        private static bool VerifyStatsText(string path)
        {
            return Ser.Deserialize<DailyStats>(File.ReadAllText(path, Encoding.UTF8)) != null;
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

        /// <summary>原子写配置：写入失败返回 false，原配置保持可用。</summary>
        public static bool SaveSettings(Settings s)
        {
            bool ok = WriteAtomic(ConfigPath, Ser.Serialize(s), VerifySettingsText);
            if (!ok) Log("写入配置失败（原配置未改动，临时文件保留为 config.json.tmp）");
            return ok;
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

        /// <summary>原子写统计：写入失败返回 false，原统计文件保持可用。</summary>
        public static bool SaveStats(DailyStats s)
        {
            bool ok = WriteAtomic(StatsPath, Ser.Serialize(s), VerifyStatsText);
            if (!ok) Log("写入统计失败（原文件未改动，临时文件保留为 stats.json.tmp）");
            return ok;
        }

        // ---------- 告状记录 ----------

        /// <summary>进程内写锁：多个后台发送线程同时写 JSONL 时不能交错。</summary>
        private static readonly object historyLock = new object();

        public static void AppendHistory(HistoryEntry e)
        {
            lock (historyLock)
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
        }

        public static List<HistoryEntry> ReadHistory(int max)
        {
            List<HistoryEntry> list = new List<HistoryEntry>();
            try
            {
                if (!File.Exists(HistoryPath)) return list;
                string[] lines = File.ReadAllLines(HistoryPath, Encoding.UTF8);
                int skipped = 0;
                for (int i = lines.Length - 1; i >= 0 && list.Count < max; i--)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0) continue;
                    try
                    {
                        HistoryEntry e = Ser.Deserialize<HistoryEntry>(line);
                        if (e != null) list.Add(e);
                        else skipped++;
                    }
                    catch { skipped++; }
                }
                if (skipped > 0) Log(string.Format("读取告状记录时跳过了 {0} 行损坏内容", skipped));
            }
            catch (Exception ex)
            {
                Log("读取告状记录失败：" + ex.Message);
            }
            return list;
        }
    }
}
