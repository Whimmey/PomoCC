using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PomodoroSupervisor
{
    public static class App
    {
        /// <summary>产品名与版本（窗口标题、托盘提示、关于信息都用它）。</summary>
        public const string ProductName = "番茄钟监督";
        public const string Version = "0.1";

        /// <summary>项目主页与作者（设置窗口左下角的署名链接用它）。</summary>
        public const string Author = "Whimmey";
        public const string RepoUrl = "https://github.com/Whimmey/PomoCC";

        /// <summary>自检/冒烟测试模式：不弹窗、不发邮件、不写注册表。</summary>
        public static bool Headless;
        /// <summary>调试用：启动后直接打开设置窗口（截图自检用）。</summary>
        public static bool OpenSettingsOnStart;
        /// <summary>调试用：启动后直接进入「改时长」编辑模式（截图自检用）。</summary>
        public static bool EditMinutesOnStart;
        public static string ExePath
        {
            get
            {
                try
                {
                    Assembly a = Assembly.GetEntryAssembly();
                    if (a != null && !string.IsNullOrEmpty(a.Location)) return a.Location;
                }
                catch { }
                return Application.ExecutablePath;
            }
        }
    }

    public static class AutoStart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "PomodoroSupervisor";

        /// <summary>
        /// 自检用：把注册表值名换成测试专用名。
        /// 这样端到端自检可以真的验证注册表读写，却**永远不会碰用户自己那一项**
        /// （之前自检会改写用户真实的自启项，安全软件就会弹「企图开机自启」）。
        /// </summary>
        internal static string OverrideValueName;

        private static string Name
        {
            get { return string.IsNullOrEmpty(OverrideValueName) ? ValueName : OverrideValueName; }
        }

        public static bool IsEnabled()
        {
            return ReadRaw(Name) != null;
        }

        private static string ReadRaw(string name)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (k == null) return null;
                    object v = k.GetValue(name);
                    if (v == null) return null;
                    string s = v.ToString();
                    return s.Length == 0 ? null : s;
                }
            }
            catch { return null; }
        }

        /// <summary>
        /// 设置/取消开机自启。返回 true 表示**确实动了注册表**（值有变化）。
        /// 值没变时返回 false 且一个字节都不写 —— 安全软件只要看到 Run 键被写就会
        /// 弹「某程序企图开机自启动」，所以这里必须幂等。
        /// </summary>
        public static bool Apply(bool enabled)
        {
            // Headless（自检）不写注册表；截图自检用 POMODORO_NO_REGISTRY=1 再兜一层。
            if (App.Headless) return false;
            if (Environment.GetEnvironmentVariable("POMODORO_NO_REGISTRY") == "1") return false;
            try
            {
                string want = enabled ? "\"" + App.ExePath + "\" --tray" : null;
                string have = ReadRaw(Name);

                if (want == null)
                {
                    if (have == null) return false;                 // 本来就没有，不用写
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                    {
                        if (k == null) return false;
                        k.DeleteValue(Name, false);
                    }
                    Store.Log("已取消开机自启");
                    return true;
                }

                if (have == want) return false;                     // 已经就是这个值，静默跳过

                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (k == null) return false;
                    k.SetValue(Name, want);
                }
                Store.Log("开机自启已设置为：开");
                return true;
            }
            catch (Exception ex)
            {
                Store.Log("设置开机自启失败：" + ex.Message);
                return false;
            }
        }

        /// <summary>自检收尾用：清掉测试专用项。</summary>
        internal static void RemoveTestValue()
        {
            if (string.IsNullOrEmpty(OverrideValueName)) return;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k != null) k.DeleteValue(OverrideValueName, false);
                }
            }
            catch { }
        }

        /// <summary>读用户真实项的原样字符串（null = 原本没有这一项）。自检前保存、自检后原样恢复。</summary>
        internal static string ReadReal()
        {
            return ReadRaw(ValueName);
        }

        /// <summary>
        /// 把用户真实项恢复成**原样的字符串**。
        /// 不能用 Apply(startWasEnabled) 恢复：那会用「当前 exe 路径」重写一遍，
        /// 自检跑的是 -dev.exe 时就会把用户的自启项改成指向一个临时文件。
        /// </summary>
        internal static void RestoreReal(string raw)
        {
            try
            {
                string have = ReadRaw(ValueName);
                if (raw == null)
                {
                    if (have == null) return;
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                    {
                        if (k != null) k.DeleteValue(ValueName, false);
                    }
                    Store.Log("已取消开机自启");
                    return;
                }
                if (have == raw) return;              // 一模一样就一个字节都不写
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (k != null) k.SetValue(ValueName, raw);
                }
            }
            catch { }
        }
    }

    internal static class Program
    {
        private static Mutex singleInstance;

        [STAThread]
        private static int Main(string[] args)
        {
            App.Headless = Has(args, "--selftest") || Has(args, "--smoketest") || Has(args, "--smtp-check")
                        || Has(args, "--mail-test") || Has(args, "--dpicheck") || Has(args, "--loadcheck")
                        || Has(args, "--rendertest") || Has(args, "--shot");

            try
            {
                return RunMode(args);
            }
            finally
            {
                // 不管成功失败都收拾干净：删临时目录、清测试专用注册表项
                SelfTest.CleanupTempDirs();
                AutoStart.RemoveTestValue();
            }
        }

        private static int RunMode(string[] args)
        {
            if (Has(args, "--selftest"))
                return SelfTest.Run(Value(args, "--selftest"));

            if (Has(args, "--smoketest"))
                return SelfTest.Smoke(Value(args, "--smoketest"));

            if (Has(args, "--smtp-check"))
                return SelfTest.SmtpCheck(args);

            if (Has(args, "--mail-test"))
                return SelfTest.MailTest(args);

            if (Has(args, "--realsmoke"))
                return SelfTest.RealSmoke(args);

            if (Has(args, "--dpicheck"))
                return SelfTest.DpiCheck(args);

            if (Has(args, "--loadcheck"))
                return SelfTest.LoadCheck(args);

            if (Has(args, "--rendertest"))
                return SelfTest.RenderCheck(args);

            if (Has(args, "--shot"))
                return SelfTest.Shot(args);

            bool created;
            singleInstance = new Mutex(true, "PomodoroSupervisor.SingleInstance.v1", out created);
            if (!created)
            {
                MessageBox.Show("「番茄钟监督」已经在运行了（看右下角托盘图标）。", "番茄钟监督",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }

            // 独立数据目录（截图自检用，不影响用户真实配置）
            string dataDir = Environment.GetEnvironmentVariable("POMODORO_DATA_DIR");
            if (!string.IsNullOrEmpty(dataDir)) Store.OverrideDir = dataDir;
            App.OpenSettingsOnStart = Has(args, "--opensettings");
            App.EditMinutesOnStart = Has(args, "--editminutes");

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            try
            {
                MainForm form = new MainForm();
                if (Has(args, "--tray")) form.StartHiddenInTray();
                Application.Run(form);
            }
            catch (Exception ex)
            {
                Store.Log("主程序异常退出：" + ex.ToString());
                MessageBox.Show("程序出错了：\r\n" + ex.Message + "\r\n\r\n日志在：\r\n" + Store.LogPath,
                    "番茄钟监督", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            GC.KeepAlive(singleInstance);
            return 0;
        }

        private static bool Has(string[] args, string name)
        {
            if (args == null) return false;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>取 --xxx 后面那个参数；没有就跟一个默认路径。</summary>
        private static string Value(string[] args, string name)
        {
            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                        return args[i + 1];
                }
            }
            return Path.Combine(Path.GetTempPath(), "pomodoro-selftest.log");
        }
    }
}
