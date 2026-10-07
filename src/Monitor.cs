using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace PomoCC
{
    /// <summary>一个被监视的程序实例。</summary>
    public class WatchProcess
    {
        public string Exe { get; set; }
        public string DisplayName { get; set; }
        public string Name { get; set; }             // 用户在设置里起的名称
        public int Pid { get; set; }
        public DateTime StartTime { get; set; }
        public int SessionSeconds { get; set; }      // 本次专注窗口内累计运行秒数
        public int LimitMinutes { get; set; }        // 命中规则允许的时长
        public DateTime FirstSeen { get; set; }
        public DateTime LastSeen { get; set; }

        /// <summary>邮件与界面显示用：优先用户起的名称，并带上进程名便于核对。</summary>
        public string Label
        {
            get { return WatchRule.BuildLabel(string.IsNullOrEmpty(Name) ? DisplayName : Name, Exe); }
        }
    }

    public static class ProcessMonitor
    {
        /// <summary>把用户填的条目统一成 "xxx.exe" 小写形式。</summary>
        public static string NormalizeName(string raw)
        {
            if (raw == null) return "";
            string s = raw.Trim();
            if (s.Length == 0) return "";
            s = s.Replace('/', '\\');
            int i = s.LastIndexOf('\\');
            if (i >= 0) s = s.Substring(i + 1);
            s = s.Trim().Trim('"');
            if (s.Length == 0) return "";
            if (!s.ToLowerInvariant().EndsWith(".exe")) s = s + ".exe";
            return s.ToLowerInvariant();
        }

        public static List<string> NormalizeList(IEnumerable<string> raw)
        {
            List<string> list = new List<string>();
            if (raw == null) return list;
            foreach (string r in raw)
            {
                foreach (string part in SplitLines(r))
                {
                    string n = NormalizeName(part);
                    if (n.Length > 0 && !list.Contains(n)) list.Add(n);
                }
            }
            return list;
        }

        private static IEnumerable<string> SplitLines(string s)
        {
            if (s == null) yield break;
            string[] parts = s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (string p in parts) yield return p;
        }

        /// <summary>枚举当前正在运行、且命中监督规则的进程。</summary>
        public static List<WatchProcess> Scan(IEnumerable<WatchRule> rules)
        {
            Dictionary<string, WatchRule> want = new Dictionary<string, WatchRule>();
            if (rules != null)
            {
                foreach (WatchRule r in rules)
                {
                    if (r == null || !r.Enabled) continue;
                    string exe = NormalizeName(r.Exe);
                    if (exe.Length == 0) continue;
                    if (!want.ContainsKey(exe)) want[exe] = r;
                }
            }

            List<WatchProcess> found = new List<WatchProcess>();
            if (want.Count == 0) return found;

            Process[] all;
            try { all = Process.GetProcesses(); }
            catch { return found; }

            foreach (Process p in all)
            {
                try
                {
                    string exe = (p.ProcessName + ".exe").ToLowerInvariant();
                    WatchRule rule;
                    if (!want.TryGetValue(exe, out rule)) continue;

                    DateTime st = DateTime.MinValue;
                    try { st = p.StartTime; }
                    catch { st = DateTime.MinValue; }

                    WatchProcess w = new WatchProcess();
                    w.Exe = exe;
                    w.DisplayName = rule.DisplayName;
                    w.Name = rule.Name;
                    w.LimitMinutes = rule.LimitMinutes;
                    w.Pid = p.Id;
                    w.StartTime = st;
                    found.Add(w);
                }
                catch
                {
                    // 某些系统进程读不到信息，直接跳过
                }
                finally
                {
                    try { p.Dispose(); } catch { }
                }
            }
            return found;
        }

        public static int CountAll()
        {
            try { return Process.GetProcesses().Length; }
            catch { return -1; }
        }
    }
}
