using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;

namespace PomoCC
{
    /// <summary>一个可以加入监督名单的程序（供选择界面和自检使用）。</summary>
    public class RunningAppInfo
    {
        public string Exe;
        public string Path;
        public string Description;
        public string Title;
        public bool HasWindow;
        public Icon Icon;
        public IntPtr Handle;

        public string Caption
        {
            get { return string.IsNullOrEmpty(Description) ? Exe : Description; }
        }
    }

    /// <summary>枚举本机正在运行的程序，供「从正在运行的中选」使用。</summary>
    public static class AppCatalog
    {
        /// <param name="includeBackground">true 时连没有窗口的后台进程一起列出</param>
        public static List<RunningAppInfo> Enumerate(bool includeBackground)
        {
            List<RunningAppInfo> result = new List<RunningAppInfo>();
            Dictionary<string, int> index = new Dictionary<string, int>();

            Process[] all;
            try { all = Process.GetProcesses(); }
            catch { return result; }

            string self = "";
            try { self = (Process.GetCurrentProcess().ProcessName + ".exe").ToLowerInvariant(); }
            catch { }

            foreach (Process p in all)
            {
                try
                {
                    string exe = (p.ProcessName + ".exe").ToLowerInvariant();
                    if (exe == self) continue;

                    string title = "";
                    try { title = p.MainWindowTitle == null ? "" : p.MainWindowTitle; }
                    catch { title = ""; }
                    bool hasWindow = title.Length > 0;
                    if (!hasWindow && !includeBackground) continue;

                    int existing;
                    if (index.TryGetValue(exe, out existing))
                    {
                        // 同名进程只留一条，优先保留有窗口/有标题的那条
                        if (hasWindow && !result[existing].HasWindow)
                        {
                            result[existing].HasWindow = true;
                            result[existing].Title = title;
                            result[existing].Handle = p.MainWindowHandle;
                        }
                        continue;
                    }

                    string path = "";
                    try { path = p.MainModule.FileName; }
                    catch { path = ""; }

                    RunningAppInfo a = new RunningAppInfo();
                    a.Exe = exe;
                    a.Path = path;
                    a.Title = title;
                    a.HasWindow = hasWindow;
                    a.Handle = p.MainWindowHandle;
                    a.Description = Describe(path);
                    a.Icon = LoadIcon(path, hasWindow);

                    index[exe] = result.Count;
                    result.Add(a);
                }
                catch
                {
                }
                finally
                {
                    try { p.Dispose(); } catch { }
                }
            }

            result.Sort(delegate(RunningAppInfo x, RunningAppInfo y)
            {
                if (x.HasWindow != y.HasWindow) return x.HasWindow ? -1 : 1;
                return string.Compare(x.Caption, y.Caption, StringComparison.CurrentCultureIgnoreCase);
            });
            return result;
        }

        /// <summary>从 exe 里读一个给人看的名字。</summary>
        public static string Describe(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try
            {
                FileVersionInfo vi = FileVersionInfo.GetVersionInfo(path);
                if (!string.IsNullOrEmpty(vi.FileDescription)) return vi.FileDescription.Trim();
                if (!string.IsNullOrEmpty(vi.ProductName)) return vi.ProductName.Trim();
                return Path.GetFileNameWithoutExtension(path);
            }
            catch
            {
                return "";
            }
        }

        /// <summary>只给有窗口的程序取图标，避免为几百个后台进程读文件拖慢列表。</summary>
        public static Icon LoadIcon(string path, bool hasWindow)
        {
            if (!hasWindow || string.IsNullOrEmpty(path)) return SystemIcons.Application;
            try
            {
                Icon ic = Icon.ExtractAssociatedIcon(path);
                if (ic != null) return ic;
            }
            catch { }
            return SystemIcons.Application;
        }
    }
}
