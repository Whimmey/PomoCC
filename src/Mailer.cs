using System;
using System.Collections.Generic;
using System.Text;

namespace PomoCC
{
    /// <summary>占位符说明（设置界面里展示给用户看）。</summary>
    public class PlaceholderInfo
    {
        public string Key;
        public string Meaning;
        public bool DataBlock;      // true = 程序统计出来的多行数据，永远会出现在邮件里
        public PlaceholderInfo(string key, string meaning, bool dataBlock)
        {
            Key = key; Meaning = meaning; DataBlock = dataBlock;
        }
    }

    public static class Mailer
    {
        // ---------- 默认模板（用户留空时使用） ----------

        public const string DefaultSubject = "【专注监督】{日期} {时刻} · {简述}";
        public const string DefaultIntro = "{名字} 的这一次专注没有达标。";
        public const string DefaultOutro = "本邮件由该电脑上的「番茄钟监督」程序自动发送，无法手动编辑。";

        public static string TemplateOrDefault(string custom, string fallback)
        {
            return string.IsNullOrEmpty(custom) ? fallback : custom;
        }

        /// <summary>所有可用占位符。程序自动填的数据块在这里列全，界面上会说明它们不会丢。</summary>
        public static List<PlaceholderInfo> Placeholders()
        {
            List<PlaceholderInfo> list = new List<PlaceholderInfo>();
            list.Add(new PlaceholderInfo("{名字}", "你的署名（设置里填的那个）", false));
            list.Add(new PlaceholderInfo("{时间}", "触发时间，如 2026-10-06 19:57", false));
            list.Add(new PlaceholderInfo("{日期}", "日期，如 10月6日", false));
            list.Add(new PlaceholderInfo("{时刻}", "时刻，如 19:57", false));
            list.Add(new PlaceholderInfo("{原因}", "告状原因，如 中途放弃（没坚持够时间）", false));
            list.Add(new PlaceholderInfo("{简述}", "原因简称：中途放弃 / 偷玩超时", false));
            list.Add(new PlaceholderInfo("{计划专注}", "计划专注时长，如 45 分钟", false));
            list.Add(new PlaceholderInfo("{实际专注}", "实际专注了多久，如 6.2 分钟", false));
            list.Add(new PlaceholderInfo("{监督人}", "监督人邮箱", false));
            list.Add(new PlaceholderInfo("{程序明细}", "监督名单程序本次的运行情况（含每个软件的使用时长）", true));
            list.Add(new PlaceholderInfo("{今日累计}", "今日完成 / 放弃 / 告状次数", true));
            list.Add(new PlaceholderInfo("{时间线}", "本次专注的时间线", true));
            return list;
        }

        /// <summary>把模板里的占位符替换成真实数据。</summary>
        public static string Fill(string template, Settings s, FocusSession session, DailyStats stats,
                                  string reason, DateTime when)
        {
            if (string.IsNullOrEmpty(template)) return "";

            double actualMin = session == null ? 0 : session.ElapsedSeconds / 60.0;
            string shortReason = ShortReason(reason);
            string subjTail = "";
            if (session != null)
            {
                subjTail = string.Format("（实际 {0}/{1} 分钟）",
                    Math.Round(actualMin, 1).ToString("0.#"), s.FocusMinutes);
            }

            string text = template;
            text = text.Replace("{名字}", string.IsNullOrEmpty(s.UserName) ? Settings.DefaultUserName : s.UserName);
            text = text.Replace("{时间}", when.ToString("yyyy-MM-dd HH:mm"));
            text = text.Replace("{日期}", when.ToString("M月d日"));
            text = text.Replace("{时刻}", when.ToString("HH:mm"));
            text = text.Replace("{原因}", reason == null ? "" : reason);
            text = text.Replace("{简述}", shortReason + subjTail);
            text = text.Replace("{计划专注}", s.FocusMinutes + " 分钟");
            text = text.Replace("{实际专注}", Supervisor.FormatMinutes(session == null ? 0 : session.ElapsedSeconds));
            text = text.Replace("{监督人}", s.SupervisorEmail == null ? "" : s.SupervisorEmail);
            text = text.Replace("{程序明细}", ProgramBlock(session));
            text = text.Replace("{今日累计}", TodayBlock(stats));
            text = text.Replace("{时间线}", TimelineBlock(session));
            return text;
        }

        private static string ShortReason(string reason)
        {
            if (reason == null) return "";
            return reason.IndexOf("放弃") >= 0 ? "中途放弃" : "偷玩超时";
        }

        /// <summary>程序自动统计、永远会写进邮件的「监督程序运行情况」块。</summary>
        public static string ProgramBlock(FocusSession session)
        {
            StringBuilder sb = new StringBuilder();
            if (session == null || session.Watched.Count == 0)
            {
                sb.Append("  （无）");
                return sb.ToString();
            }
            bool first = true;
            foreach (KeyValuePair<string, WatchProcess> kv in session.Watched)
            {
                if (!first) sb.Append("\r\n");
                first = false;
                WatchProcess w = kv.Value;
                int limit = w.LimitMinutes > 0 ? w.LimitMinutes : 3;
                sb.Append(string.Format("  · {0}：累计运行 {1}（规则 {2} 分钟）", w.Label, Supervisor.FormatDuration(w.SessionSeconds), limit));
                if (w.FirstSeen != DateTime.MinValue)
                    sb.Append(string.Format("（首次发现 {0:HH:mm:ss}", w.FirstSeen));
                if (w.FirstSeen != DateTime.MinValue && w.StartTime != DateTime.MinValue)
                    sb.Append(string.Format("，该程序本次启动于 {0:HH:mm:ss}", w.StartTime));
                if (w.FirstSeen != DateTime.MinValue)
                    sb.Append("）");
            }
            return sb.ToString();
        }

        public static string TodayBlock(DailyStats stats)
        {
            if (stats == null) return "";
            return string.Format("今日：完成 {0} 段 · 放弃 {1} 段 · 告状 {2} 次",
                stats.CompletedCount, stats.AbandonedCount, stats.ReportCount);
        }

        public static string TimelineBlock(FocusSession session)
        {
            StringBuilder sb = new StringBuilder();
            if (session != null)
            {
                for (int i = 0; i < session.Events.Count; i++)
                {
                    if (i > 0) sb.Append("\r\n");
                    sb.Append("  ").Append(session.Events[i]);
                }
            }
            return sb.ToString();
        }

        public static string ComposeSubject(Settings s, string reason, DateTime when, FocusSession session)
        {
            string tpl = TemplateOrDefault(s == null ? null : s.MailSubjectTemplate, DefaultSubject);
            string text = Fill(tpl, s, session, null, reason, when);
            if (text.Trim().Length == 0) text = "【专注监督】";
            return text;
        }

        /// <summary>
        /// 程序固定生成的数据块（含每个软件的累计使用时长、时间线等）。
        /// 用户的自定义内容不参与这一块，所以这些数据永远不会被改丢。
        /// </summary>
        public static string FixedBlock(Settings s, FocusSession session, DailyStats stats, string reason, DateTime when)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(string.Format("时间：{0:yyyy-MM-dd HH:mm}\r\n", when));
            sb.Append(string.Format("原因：{0}\r\n", reason));
            sb.Append(string.Format("计划专注：{0} 分钟\r\n", s.FocusMinutes));
            sb.Append(string.Format("实际专注：{0}\r\n", Supervisor.FormatMinutes(session == null ? 0 : session.ElapsedSeconds)));
            sb.Append("\r\n");
            sb.Append("监督名单程序在本次专注期间的运行情况：\r\n");
            sb.Append(ProgramBlock(session)).Append("\r\n\r\n");
            if (stats != null) sb.Append(TodayBlock(stats)).Append("\r\n\r\n");
            sb.Append("时间线：\r\n");
            sb.Append(TimelineBlock(session)).Append("\r\n\r\n");
            sb.Append("————————————————");
            return sb.ToString();
        }

        /// <summary>
        /// 组装邮件正文 = 用户自定义的开头 + 程序固定生成的数据块 + 用户自定义的结尾。
        /// </summary>
        public static string ComposeBody(Settings s, FocusSession session, DailyStats stats, string reason, DateTime when)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("【专注监督提醒】\r\n\r\n");

            string intro = Fill(TemplateOrDefault(s.MailIntroTemplate, DefaultIntro), s, session, stats, reason, when);
            if (intro.Trim().Length > 0)
            {
                sb.Append(intro.TrimEnd()).Append("\r\n\r\n");
            }

            sb.Append(FixedBlock(s, session, stats, reason, when)).Append("\r\n");

            string outro = Fill(TemplateOrDefault(s.MailOutroTemplate, DefaultOutro), s, session, stats, reason, when);
            if (outro.Trim().Length > 0) sb.Append(outro.TrimEnd()).Append("\r\n");
            else sb.Append(DefaultOutro).Append("\r\n");

            return sb.ToString();
        }

        /// <summary>设置界面里展示「程序固定插入的内容」用：用示例数据渲染同一块。</summary>
        public static string FixedBlockPreview(Settings s)
        {
            FocusSession demo = DemoSession(s);
            DailyStats stats = new DailyStats();
            stats.CompletedCount = 2; stats.AbandonedCount = 1; stats.ReportCount = 3;
            return FixedBlock(s, demo, stats, "中途放弃（没坚持够时间）", DateTime.Now);
        }

        /// <summary>设置界面的"预览"用：构造一段示例专注。</summary>
        public static FocusSession DemoSession(Settings s)
        {
            FocusSession demo = new FocusSession();
            demo.StartedAt = DateTime.Now.AddMinutes(-12);
            demo.PlannedSeconds = Math.Max(60, s.FocusMinutes * 60);
            demo.ElapsedSeconds = (int)(demo.PlannedSeconds * 0.25);

            string name = "steam.exe";
            string display = "Steam";
            string custom = "";
            int limit = 3;
            if (s.Rules != null && s.Rules.Count > 0)
            {
                for (int i = 0; i < s.Rules.Count; i++)
                {
                    if (s.Rules[i] == null || !s.Rules[i].Enabled) continue;
                    string exe = ProcessMonitor.NormalizeName(s.Rules[i].Exe);
                    if (exe.Length == 0) continue;
                    name = exe;
                    display = s.Rules[i].DisplayName;
                    custom = s.Rules[i].Name;
                    limit = s.Rules[i].LimitMinutes;
                    break;
                }
            }

            WatchProcess w = new WatchProcess();
            w.Exe = name;
            w.DisplayName = display;
            w.Name = custom;
            w.LimitMinutes = limit;
            w.Pid = 12345;
            w.StartTime = DateTime.Now.AddMinutes(-11);
            w.FirstSeen = DateTime.Now.AddMinutes(-11);
            w.LastSeen = DateTime.Now.AddMinutes(-1);
            w.SessionSeconds = 600;
            demo.Watched[name] = w;
            demo.Events.Add(string.Format("{0:HH:mm:ss} 开始专注，目标 {1} 分钟", demo.StartedAt, s.FocusMinutes));
            demo.Events.Add(string.Format("{0:HH:mm:ss} 发现 {1} 正在运行（规则 {2} 分钟）", demo.StartedAt.AddMinutes(1), w.Label, limit));
            demo.Events.Add(string.Format("{0:HH:mm:ss} 触发告状：专注期间偷玩超时（{1}）", demo.StartedAt.AddMinutes(10), w.Label));
            return demo;
        }

        public static string PreviewBody(Settings s)
        {
            FocusSession demo = DemoSession(s);

            DailyStats stats = new DailyStats();
            stats.CompletedCount = 2;
            stats.AbandonedCount = 1;
            stats.ReportCount = 3;

            string reason = "专注期间偷玩超时";
            foreach (KeyValuePair<string, WatchProcess> kv in demo.Watched)
            {
                reason = "专注期间偷玩超时（" + kv.Value.Label + "）";
                break;
            }

            return string.Format("主题：{0}\r\n\r\n{1}",
                ComposeSubject(s, reason, DateTime.Now, demo),
                ComposeBody(s, demo, stats, reason, DateTime.Now));
        }

        public static void Send(Settings s, string subject, string body)
        {
            if (s == null) throw new InvalidOperationException("没有设置。");
            if (string.IsNullOrEmpty(s.SupervisorEmail)) throw new InvalidOperationException("没有填写监督人邮箱。");

            string mode = s.SendMode == null ? "smtp" : s.SendMode;
            if (mode == "smtp")
            {
                SmtpTransport.Send(
                    s.SmtpHost,
                    s.SmtpPort,
                    s.SmtpStartTls,
                    s.SenderEmail,
                    s.GetAuthCode(),
                    s.SenderEmail,
                    s.SupervisorEmail,
                    subject,
                    body);
                return;
            }
            HttpSender.Send(s, subject, body);
        }
    }
}
