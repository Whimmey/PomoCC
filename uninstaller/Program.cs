using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PomoCCUninstaller
{
    internal static class Program
    {
        private const string MainExeName = "PomoCC-番茄钟监督.exe";
        private const string UninstallerName = "uninstall.exe";
        private const string ManualName = "使用说明.txt";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string CurrentRunValueName = "PomoCC";
        private const string LegacyRunValueName = "PomodoroSupervisor";
        private static readonly string[] OwnedDataFolderNames = { "PomoCC", "PomodoroSupervisor" };

        [STAThread]
        private static int Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string installDir = Path.GetDirectoryName(Application.ExecutablePath);
            string mainExe = Path.Combine(installDir, MainExeName);

            // Check before showing the destructive-action confirmation. The second check
            // below closes the race where the user starts the app while the dialog is open.
            if (IsMainProcessRunning(mainExe))
            {
                ShowMainProcessRunningMessage();
                return 1;
            }

            DialogResult answer = MessageBox.Show(
                "卸载将删除程序、开机自启项(本程序注册表信息)，以及本机保存的所有设置、邮箱授权码、" +
                "统计数据、告状历史和日志。删除后无法恢复。\n\n" +
                "如果只是升级，请直接把下载的目录覆盖进来，不需要运行卸载程序。\n\n" +
                "确定要继续卸载吗？",
                "卸载 PomoCC",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (answer != DialogResult.Yes)
            {
                return 0;
            }

            if (IsMainProcessRunning(mainExe))
            {
                ShowMainProcessRunningMessage();
                return 1;
            }

            List<string> errors = new List<string>();
            RemoveRunValues(errors);
            DeleteDataDirectories(errors);
            DeleteProgramFiles(installDir, errors);

            if (errors.Count != 0)
            {
                MessageBox.Show(
                    "卸载未完全完成。部分文件、设置或注册表信息未能清理，uninstall.exe 会保留，方便重试：\n\n" +
                    string.Join("\n", errors.ToArray()),
                    "卸载未完全完成",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return 1;
            }

            string workerError;
            if (!TryStartCleanupWorker(installDir, out workerError))
            {
                MessageBox.Show(
                    "程序和数据已清理，但无法自动删除 uninstall.exe。\n\n" + workerError,
                    "卸载未完全完成",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return 1;
            }

            MessageBox.Show(
                "卸载已完成。uninstall.exe 会在本窗口关闭后自动删除。",
                "卸载完成",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return 0;
        }

        private static bool IsMainProcessRunning(string mainExe)
        {
            string processName = Path.GetFileNameWithoutExtension(mainExe);
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(processName);
            }
            catch
            {
                return true;
            }

            foreach (Process process in processes)
            {
                using (process)
                {
                    try
                    {
                        string runningPath = process.MainModule == null ? null : process.MainModule.FileName;
                        if (string.IsNullOrEmpty(runningPath))
                        {
                            return true;
                        }

                        if (string.Equals(
                            Path.GetFullPath(runningPath),
                            Path.GetFullPath(mainExe),
                            StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                    catch
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static void ShowMainProcessRunningMessage()
        {
            MessageBox.Show(
                "请先退出 PomoCC-番茄钟监督（包括系统托盘中的程序），然后重新运行 uninstall.exe。\n\n" +
                "为避免数据被占用或删除不完整，卸载程序不会强制结束它。",
                "无法卸载",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private static void RemoveRunValues(List<string> errors)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null)
                    {
                        return;
                    }

                    key.DeleteValue(CurrentRunValueName, false);
                    key.DeleteValue(LegacyRunValueName, false);
                }
            }
            catch (Exception ex)
            {
                errors.Add("开机自启注册表项：" + ex.Message);
            }
        }

        private static void DeleteDataDirectories(List<string> errors)
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(appData))
            {
                errors.Add("无法定位当前用户的 AppData\\Roaming 目录");
                return;
            }

            foreach (string folderName in OwnedDataFolderNames)
            {
                try
                {
                    string target = GetDataDirectory(appData, folderName);
                    if (!Directory.Exists(target))
                    {
                        continue;
                    }

                    // This is the only recursive cleanup, and target is restricted to
                    // an exact child of the current user's AppData folder. Never follow
                    // a junction/symlink during the cleanup.
                    ValidateOwnedTree(target);
                    DeleteOwnedTree(target);
                    if (Directory.Exists(target))
                    {
                        errors.Add("数据目录未能删除：" + target);
                    }
                }
                catch (Exception ex)
                {
                    errors.Add("数据目录 " + folderName + "：" + ex.Message);
                }
            }
        }

        private static void ValidateOwnedTree(string directory)
        {
            EnsureNotReparsePoint(directory);

            foreach (string file in Directory.GetFiles(directory))
            {
                EnsureNotReparsePoint(file);
            }

            foreach (string child in Directory.GetDirectories(directory))
            {
                EnsureNotReparsePoint(child);
                ValidateOwnedTree(child);
            }
        }

        private static void DeleteOwnedTree(string directory)
        {
            EnsureNotReparsePoint(directory);

            foreach (string file in Directory.GetFiles(directory))
            {
                EnsureNotReparsePoint(file);
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }

            foreach (string child in Directory.GetDirectories(directory))
            {
                EnsureNotReparsePoint(child);
                DeleteOwnedTree(child);
            }

            File.SetAttributes(directory, FileAttributes.Normal);
            Directory.Delete(directory, false);
        }

        private static void EnsureNotReparsePoint(string path)
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("拒绝删除符号链接或目录联接：" + path);
            }
        }

        private static void DeleteProgramFiles(string installDir, List<string> errors)
        {
            string mainExe = Path.Combine(installDir, MainExeName);
            string[] files =
            {
                mainExe,
                mainExe + ".config",
                Path.Combine(installDir, ManualName),
                Path.Combine(installDir, UninstallerName) + ".config"
            };

            foreach (string file in files)
            {
                try
                {
                    if (!File.Exists(file))
                    {
                        continue;
                    }

                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                    if (File.Exists(file))
                    {
                        errors.Add("程序文件未能删除：" + file);
                    }
                }
                catch (Exception ex)
                {
                    errors.Add("程序文件 " + Path.GetFileName(file) + "：" + ex.Message);
                }
            }
        }

        private static string GetDataDirectory(string appData, string folderName)
        {
            if (!string.Equals(folderName, "PomoCC", StringComparison.Ordinal) &&
                !string.Equals(folderName, "PomodoroSupervisor", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("数据目录名称不在卸载白名单中");
            }

            string root = Path.GetFullPath(appData).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string target = Path.GetFullPath(Path.Combine(root, folderName));
            string prefix = root + Path.DirectorySeparatorChar;
            if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFileName(target), folderName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("数据目录路径校验失败");
            }

            return target;
        }

        private static bool TryStartCleanupWorker(string installDir, out string error)
        {
            error = null;
            string scriptPath = Path.Combine(Path.GetTempPath(), ".PomoCC-uninstall-" + Guid.NewGuid().ToString("N") + ".cmd");

            try
            {
                // Keep the script outside the install directory so that the directory can
                // be removed too. The Unicode install path is passed through an environment
                // variable while the script itself remains ASCII.
                File.WriteAllText(scriptPath, BuildCleanupScript(), new ASCIIEncoding());

                string commandProcessor = Environment.GetEnvironmentVariable("ComSpec");
                if (string.IsNullOrEmpty(commandProcessor))
                {
                    commandProcessor = "cmd.exe";
                }

                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = commandProcessor,
                    Arguments = "/d /c \"\"" + scriptPath + "\"\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Path.GetTempPath()
                };
                startInfo.EnvironmentVariables["POMOCC_UNINSTALL_DIR"] = installDir;
                Process.Start(startInfo);
                return true;
            }
            catch (Exception ex)
            {
                try
                {
                    if (File.Exists(scriptPath))
                    {
                        File.Delete(scriptPath);
                    }
                }
                catch
                {
                    // Keep the original error; the cleanup script is only temporary.
                }

                error = ex.Message;
                return false;
            }
        }

        private static string BuildCleanupScript()
        {
            StringBuilder script = new StringBuilder();
            script.AppendLine("@echo off");
            script.AppendLine("setlocal DisableDelayedExpansion");
            script.AppendLine("set \"install_dir=%POMOCC_UNINSTALL_DIR%\"");
            script.AppendLine("set /a attempts=0 >nul");
            script.AppendLine(":delete_uninstaller");
            script.AppendLine("del /f /q \"%install_dir%\\uninstall.exe\" >nul 2>&1");
            script.AppendLine("if not exist \"%install_dir%\\uninstall.exe\" goto uninstaller_deleted");
            script.AppendLine("set /a attempts+=1 >nul");
            script.AppendLine("if %attempts% geq 120 goto cleanup_finished");
            script.AppendLine("timeout /t 1 /nobreak >nul 2>&1");
            script.AppendLine("goto delete_uninstaller");
            script.AppendLine(":uninstaller_deleted");
            script.AppendLine("rmdir /q \"%install_dir%\" >nul 2>&1");
            script.AppendLine("del /f /q \"%~f0\" >nul 2>&1");
            script.AppendLine(":cleanup_finished");
            return script.ToString();
        }
    }
}
