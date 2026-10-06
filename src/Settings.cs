using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace PomodoroSupervisor
{
    /// <summary>一条监督规则：某个程序 + 它在专注期间允许跑多久。</summary>
    public class WatchRule
    {
        public string Exe { get; set; }            // 归一化进程名，如 steam.exe
        public string DisplayName { get; set; }    // 程序自报的名字（可能是 game 这种代称）
        public string Name { get; set; }           // 用户自己起的名称，邮件里用它
        public string ExePath { get; set; }        // 可执行文件路径（用于图标/识别，可为空）
        public int LimitMinutes { get; set; }      // 规则时长：专注期间允许累计运行多少分钟
        public bool Enabled { get; set; }

        public WatchRule()
        {
            Exe = "";
            DisplayName = "";
            Name = "";
            ExePath = "";
            LimitMinutes = 3;
            Enabled = true;
        }

        /// <summary>表格「软件」列显示的文字：程序自报的名字 + 进程名（作为身份标识）。</summary>
        [ScriptIgnore]
        public string SoftwareText
        {
            get
            {
                string d = DisplayName == null ? "" : DisplayName.Trim();
                if (d.Length > 0 && !string.Equals(d, Exe, StringComparison.OrdinalIgnoreCase))
                    return d + " · " + Exe;
                return Exe;
            }
        }

        /// <summary>邮件与界面里实际使用的名字：优先用户起的「名称」。</summary>
        [ScriptIgnore]
        public string DisplayLabel
        {
            get { return BuildLabel(string.IsNullOrEmpty(Name) ? DisplayName : Name, Exe); }
        }

        /// <summary>新加程序时「名称」列的默认值：与「软件」列一致。</summary>
        public void ApplyDefaultName()
        {
            if (string.IsNullOrEmpty(Name)) Name = SoftwareText;
        }

        /// <summary>名字里已经带了进程名就不再重复追加，否则补成「奶龙（game.exe）」。</summary>
        public static string BuildLabel(string name, string exe)
        {
            string n = name == null ? "" : name.Trim();
            if (n.Length == 0) n = exe;
            if (string.IsNullOrEmpty(exe)) return n;
            if (n.IndexOf(exe, StringComparison.OrdinalIgnoreCase) >= 0) return n;
            return n + "（" + exe + "）";
        }

        public WatchRule Clone()
        {
            WatchRule r = new WatchRule();
            r.Exe = Exe;
            r.DisplayName = DisplayName;
            r.Name = Name;
            r.ExePath = ExePath;
            r.LimitMinutes = LimitMinutes;
            r.Enabled = Enabled;
            return r;
        }
    }

    /// <summary>全部用户设置。密码/授权码只以加密或散列形式落盘。</summary>
    public class Settings
    {
        public int Version { get; set; }
        public string UserName { get; set; }
        public string SupervisorEmail { get; set; }
        public string SendMode { get; set; }          // smtp | resend | sendgrid | brevo | custom
        public string SenderEmail { get; set; }
        public string SmtpHost { get; set; }
        public int SmtpPort { get; set; }
        public bool SmtpStartTls { get; set; }
        public string AuthCodeEnc { get; set; }       // DPAPI 保护的授权码
        public string HttpUrl { get; set; }
        public string ApiKeyEnc { get; set; }         // DPAPI 保护的 API Key
        public List<WatchRule> Rules { get; set; }    // 监督规则表：软件 / 规则时长
        public List<string> WatchList { get; set; }   // 旧版格式（仅用于迁移到 Rules）
        public int FocusMinutes { get; set; }
        public int ViolationSeconds { get; set; }
        public int SampleSeconds { get; set; }
        public bool OnlyCountAfterStart { get; set; }
        public bool AutoStart { get; set; }
        public bool TrayOnClose { get; set; }

        // ---------- 专注完成时的提醒（配置 v2 起新增，默认开） ----------
        // 老 v1 配置里没有这两个字段，反序列化后是 false（等于"关"），
        // 所以 Store.Normalize 里按 Version < 2 显式补成 true，否则升级后提醒会静默失效。
        public bool NotifyOnComplete { get; set; }     // 托盘气泡
        public bool NotifySound { get; set; }          // 系统提示音

        public string PasswordHash { get; set; }
        public string PasswordSalt { get; set; }

        // ---------- 告状邮件「内容自定义」（空 = 用默认模板） ----------
        // 这三段是用户自己的话，可以含占位符（{名字}、{实际专注} 等）。
        // 邮件中间那块「程序自动填写」的数据（软件使用时长、时间线等）在代码里固定生成，
        // 不在这三个模板里 —— 所以无论用户怎么改，这些数据都不会丢。
        public string MailSubjectTemplate { get; set; }
        public string MailIntroTemplate { get; set; }
        public string MailOutroTemplate { get; set; }

        public static Settings Defaults()
        {
            Settings s = new Settings();
            s.Version = 2;
            s.UserName = Environment.UserName;
            s.SupervisorEmail = "";
            s.SendMode = "smtp";
            s.SenderEmail = "";
            s.SmtpHost = "smtp.qq.com";
            s.SmtpPort = 587;
            s.SmtpStartTls = true;
            s.AuthCodeEnc = "";
            s.HttpUrl = "";
            s.ApiKeyEnc = "";
            s.Rules = new List<WatchRule>();
            s.WatchList = new List<string>();
            s.FocusMinutes = 25;
            s.ViolationSeconds = 180;
            s.SampleSeconds = 5;
            s.OnlyCountAfterStart = false;
            s.AutoStart = true;
            s.TrayOnClose = true;
            s.NotifyOnComplete = true;
            s.NotifySound = true;
            s.PasswordHash = "";
            s.PasswordSalt = "";
            s.MailSubjectTemplate = "";
            s.MailIntroTemplate = "";
            s.MailOutroTemplate = "";
            return s;
        }

        public Settings Copy()
        {
            JavaScriptSerializer ser = new JavaScriptSerializer();
            return ser.Deserialize<Settings>(ser.Serialize(this));
        }

        // ---------- 授权码 / API Key（DPAPI，仅当前用户可解密） ----------

        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            byte[] enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(enc);
        }

        public static string Unprotect(string b64)
        {
            if (string.IsNullOrEmpty(b64)) return "";
            try
            {
                byte[] dec = ProtectedData.Unprotect(Convert.FromBase64String(b64), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(dec);
            }
            catch
            {
                return "";
            }
        }

        public string GetAuthCode() { return Unprotect(AuthCodeEnc); }
        public void SetAuthCode(string v) { AuthCodeEnc = Protect(v == null ? "" : v.Trim()); }
        public string GetApiKey() { return Unprotect(ApiKeyEnc); }
        public void SetApiKey(string v) { ApiKeyEnc = Protect(v == null ? "" : v.Trim()); }

        // ---------- 退出/设置密码 ----------

        public bool HasPassword() { return !string.IsNullOrEmpty(PasswordHash); }

        public void SetPassword(string pw)
        {
            byte[] salt = new byte[16];
            using (RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider())
            {
                rng.GetBytes(salt);
            }
            PasswordSalt = Convert.ToBase64String(salt);
            PasswordHash = HashPassword(pw, salt);
        }

        public bool CheckPassword(string pw)
        {
            if (!HasPassword()) return true;
            if (pw == null) return false;
            byte[] salt;
            try { salt = Convert.FromBase64String(PasswordSalt); }
            catch { return false; }
            string got = HashPassword(pw, salt);
            return FixedTimeEquals(got, PasswordHash);
        }

        internal static string HashPassword(string pw, byte[] salt)
        {
            using (Rfc2898DeriveBytes kdf = new Rfc2898DeriveBytes(pw == null ? "" : pw, salt, 60000))
            {
                return Convert.ToBase64String(kdf.GetBytes(32));
            }
        }

        private static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        [ScriptIgnore]
        public string ModeDisplay
        {
            get
            {
                switch (SendMode)
                {
                    case "resend": return "Resend 邮件 API";
                    case "sendgrid": return "SendGrid 邮件 API";
                    case "brevo": return "Brevo 邮件 API";
                    case "custom": return "自定义 HTTP 接口";
                    default: return "邮箱授权码（SMTP）";
                }
            }
        }
    }

    /// <summary>保存/发送前的必填项检查。</summary>
    public static class SettingsValidator
    {
        public static List<string> Validate(Settings s)
        {
            List<string> errs = new List<string>();
            if (string.IsNullOrEmpty(s.SupervisorEmail) || s.SupervisorEmail.IndexOf('@') < 0)
                errs.Add("请填写监督人邮箱（告状邮件的收件人）。");
            if (string.IsNullOrEmpty(s.SenderEmail) || s.SenderEmail.IndexOf('@') < 0)
                errs.Add("请填写你自己的发件邮箱。");

            if (s.SendMode == "smtp")
            {
                if (string.IsNullOrEmpty(s.SmtpHost)) errs.Add("请填写 SMTP 服务器地址。");
                if (s.SmtpPort <= 0 || s.SmtpPort > 65535) errs.Add("SMTP 端口不合法（常用 587 或 465）。");
                if (string.IsNullOrEmpty(s.GetAuthCode())) errs.Add("请填写邮箱授权码（不是登录密码）。");
            }
            else
            {
                if (string.IsNullOrEmpty(s.HttpUrl) && s.SendMode == "custom") errs.Add("请填写自定义接口地址。");
                if (string.IsNullOrEmpty(s.GetApiKey())) errs.Add("请填写 API Key。");
            }

            if (s.FocusMinutes < 1 || s.FocusMinutes > 600) errs.Add("专注时长请在 1~600 分钟之间。");
            if (s.ViolationSeconds < 10) errs.Add("偷玩判定的秒数太小（至少 10 秒）。");
            if (s.SampleSeconds < 2) errs.Add("采样间隔太小（至少 2 秒）。");

            List<WatchRule> active = ActiveRules(s);
            if (active.Count == 0) errs.Add("监督名单是空的，请至少添加一个程序（点「添加程序」从正在运行的程序里选）。");

            for (int i = 0; i < active.Count; i++)
            {
                if (active[i].LimitMinutes < 1 || active[i].LimitMinutes > 600)
                    errs.Add(string.Format("「{0}」的规则时长要在 1~600 分钟之间。", active[i].SoftwareText));
            }

            return errs;
        }

        /// <summary>取出启用的、进程名合法的规则。</summary>
        public static List<WatchRule> ActiveRules(Settings s)
        {
            List<WatchRule> list = new List<WatchRule>();
            if (s == null || s.Rules == null) return list;
            foreach (WatchRule r in s.Rules)
            {
                if (r == null || !r.Enabled) continue;
                string exe = ProcessMonitor.NormalizeName(r.Exe);
                if (exe.Length == 0) continue;
                WatchRule copy = r.Clone();
                copy.Exe = exe;
                list.Add(copy);
            }
            return list;
        }
    }
}
