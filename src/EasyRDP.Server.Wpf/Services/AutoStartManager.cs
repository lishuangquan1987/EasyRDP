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

        // 任务管理器/设置里的"启动应用"开关实际落在 StartupApproved\Run（REG_BINARY）。
        // Windows 只在该标记为"启用"（或缺省）时才执行 Run 项——被禁用后 Run 值仍存在，
        // 系统却跳过启动：表现为"程序里勾选了开机自启，重启后没生效"（复选框仍显示已勾选）。
        // XP 无此键（OpenSubKey 返回 null），按"未禁用"处理，行为与旧系统一致。
        private const string StartupApprovedKeyPath =
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

        /// <summary>
        /// 查询当前用户是否已登记开机自启（以系统实际生效状态为准）。
        /// Run 值存在且未被 StartupApproved 禁用 → true；
        /// Run 值缺失或被任务管理器禁用 → false（复选框据此回显，杜绝"假勾选"）。
        /// </summary>
        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (key == null) return false;
                    string value = key.GetValue(ValueName) as string;
                    if (string.IsNullOrEmpty(value)) return false;
                    // Run 值存在还要看是否被 StartupApproved 禁用（任务管理器-启动页开关）
                    return !IsDisabledByStartupApproved();
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Read auto start registry value failed");
                return false;
            }
        }

        /// <summary>
        /// 判断"Run 值存在但被任务管理器/系统启动设置禁用"的状态
        ///（StartupApproved 禁用标记）。供启动自愈逻辑区分两种失效：
        /// Run 值丢失（卸载/重装清除，可按保存的设置自愈重写）与
        /// 系统层禁用（用户明确关掉，必须尊重、不强行恢复）。
        /// </summary>
        public static bool IsDisabledByTaskManager()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (key == null) return false;
                    string value = key.GetValue(ValueName) as string;
                    // Run 值都没有 → 不是"系统层禁用"场景（IsEnabled 已返回 false）
                    if (string.IsNullOrEmpty(value)) return false;
                    return IsDisabledByStartupApproved();
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Read StartupApproved state failed");
                return false;
            }
        }

        /// <summary>
        /// 启用或禁用开机自启。
        /// 启用时同时清除 StartupApproved 的禁用标记（用户此前在任务管理器禁用过的话，
        /// 仅写 Run 值无法恢复启动）；禁用时删除 Run 值即可。
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
                    {
                        key.SetValue(ValueName, BuildCommandLine(), RegistryValueKind.String);
                        // 恢复被任务管理器禁用的启动项：删除禁用标记后 Windows 按"启用"处理
                        try { ClearStartupApprovedDisable(); }
                        catch (Exception ex)
                        {
                            // 清除失败不回滚 Run 值：多数情况下 Run 值本身就是启动依据，
                            // 仅 Win7+ 任务管理器禁用场景需要标记修复
                            Logger.Warn(ex, "Clear StartupApproved disable flag failed");
                        }
                    }
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

        /// <summary>
        /// 读取 StartupApproved\Run 中本启动项的禁用标记。
        /// 标记为 REG_BINARY：首字节 0x03（奇数）= 禁用，0x02（偶数）= 启用
        ///（Autoruns/NirSoft 逆向结论；后 8 字节为禁用操作时间戳，不参与判定）。
        /// 键不存在（XP）或无本项值 → 未禁用。
        /// </summary>
        private static bool IsDisabledByStartupApproved()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(StartupApprovedKeyPath, false))
            {
                if (key == null) return false;
                byte[] data = key.GetValue(ValueName) as byte[];
                if (data == null || data.Length < 4) return false;
                // 首字节低位为 1 视为禁用（0x03=禁用 / 0x02=启用 的公共判定）
                return (data[0] & 0x01) != 0;
            }
        }

        /// <summary>
        /// 删除 StartupApproved\Run 中本启动项的禁用标记（恢复系统默认"启用"）。
        /// 仅在标记存在且为禁用状态时调用才有意义；删除不存在值时静默（throwOnMissing=false）。
        /// </summary>
        private static void ClearStartupApprovedDisable()
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(StartupApprovedKeyPath))
            {
                if (key == null) return;
                if (IsDisabledByStartupApproved())
                {
                    key.DeleteValue(ValueName, false);
                    Logger.Info("StartupApproved disable flag cleared for {0}", ValueName);
                }
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
