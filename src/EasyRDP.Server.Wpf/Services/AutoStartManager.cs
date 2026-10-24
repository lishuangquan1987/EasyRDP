#nullable disable
using System;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;
using NLog;

namespace EasyRDP.Server.Wpf.Services
{
    /// <summary>
    /// 开机自启管理：写入 HKCU\Software\Microsoft\Windows\CurrentVersion\Run。
    /// 仅对当前用户生效，不需要管理员权限，Windows XP 起的全部版本均支持。
    /// </summary>
    public static class AutoStartManager
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        /// <summary>注册表启动项名称。同名重复写入即覆盖，不会产生重复启动项。</summary>
        public const string ValueName = "EasyRDP Server";

        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        /// <summary>
        /// 查询当前用户是否已登记开机自启。
        /// 以注册表实际状态为准（用户可能在任务管理器/注册表中自行禁用）。
        /// </summary>
        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (key == null) return false;
                    string value = key.GetValue(ValueName) as string;
                    return !string.IsNullOrEmpty(value);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Read auto start registry value failed");
                return false;
            }
        }

        /// <summary>
        /// 启用或禁用开机自启。
        /// </summary>
        /// <returns>操作后的实际状态；写入失败时返回注册表中的当前状态，便于 UI 回滚勾选。</returns>
        public static bool SetEnabled(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (key == null)
                    {
                        Logger.Warn("Open HKCU Run key returned null");
                        return IsEnabled();
                    }

                    if (enabled)
                        key.SetValue(ValueName, BuildCommandLine(), RegistryValueKind.String);
                    else
                        key.DeleteValue(ValueName, false);
                }
                Logger.Info("Auto start {0}", enabled ? "enabled" : "disabled");
                return enabled;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Update auto start registry value failed");
                return IsEnabled();
            }
        }

        /// <summary>生成启动命令行：exe 完整路径加引号（安装路径常含空格）。</summary>
        private static string BuildCommandLine()
        {
            string exePath = GetExecutablePath();
            if (string.IsNullOrEmpty(exePath))
                throw new InvalidOperationException("Unable to resolve the executable path");
            return "\"" + exePath + "\"";
        }

        /// <summary>解析当前进程的可执行文件路径，失败时回退到入口程序集位置。</summary>
        private static string GetExecutablePath()
        {
            try
            {
                using (Process process = Process.GetCurrentProcess())
                {
                    if (process.MainModule != null)
                        return process.MainModule.FileName;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Resolve process main module failed");
            }

            Assembly entry = Assembly.GetEntryAssembly();
            return entry != null ? entry.Location : null;
        }
    }
}
