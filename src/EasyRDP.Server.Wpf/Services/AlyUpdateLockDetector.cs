#nullable disable
using System;
using System.Diagnostics;
using System.IO;
using NLog;

namespace EasyRDP.Server.Wpf.Services
{
    /// <summary>
    /// aly 更新锁检测器：判断"aly 更新器是否正在执行 apply_update / rollback"。
    ///
    /// 用途（端口占用根因修复）：aly 更新器在替换文件前会向主进程发 WM_CLOSE 请求
    /// 优雅退出；但更新可能并非经程序内"确认应用"路径发起（旧版自动应用、
    /// aly-client-ui 手动应用、发布 CLI 等），此时 ExitRequestedByUpdate 标志未置位，
    /// 主窗口会把 WM_CLOSE 当成普通关闭——隐藏到托盘继续监听，更新器 rename 目录
    /// （Windows 允许 rename 运行中 exe 的目录）也无需杀进程——结果旧实例带着监听
    /// socket 一直存活，更新器拉起的新实例绑定端口失败（生产日志实证：
    /// 11:48:24 旧实例隐藏托盘 → 11:48:38/11:49:53 新实例两次报"Port in use"）。
    ///
    /// 修复：aly 的 apply_update / rollback 全程持有全局更新锁
    ///（UpdateFolder\.aly.lock，内容 {"pid","ts","cmd","token"}，见 aly cmd/lock.go）。
    /// 主窗口在"隐藏到托盘"前检测该锁：锁存在且持锁进程存活 → 本进程正被更新器
    /// 要求退出 → 走真正的优雅退出路径（停止监听 → 端口随进程干净释放）。
    /// </summary>
    public static class AlyUpdateLockDetector
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        /// <summary>aly 更新锁文件名（位于 UpdateFolder 下，见 aly cmd/lock.go updateLockPath）。</summary>
        private const string LockFileName = ".aly.lock";

        /// <summary>锁文件所在目录名（与主程序目录 MainFolder 同级，见 aly config client.json 约定）。</summary>
        private const string UpdateFolderName = "UpdateFolder";

        /// <summary>
        /// 判断 aly 更新器是否正在执行 apply_update / rollback（本进程应配合退出）。
        /// 判定条件（全部满足）：锁文件存在 + cmd 为 apply_update/rollback + 持锁进程存活。
        /// check_update / download_update 只读不替换文件，不要求主进程退出，返回 false。
        /// </summary>
        public static bool IsUpdateInProgress()
        {
            try
            {
                string lockPath = ResolveLockPath();
                if (string.IsNullOrEmpty(lockPath) || !File.Exists(lockPath))
                    return false;

                string json = File.ReadAllText(lockPath);
                if (string.IsNullOrEmpty(json))
                    return false;

                // cmd 判定：仅 apply_update / rollback 需要主进程退出配合
                string cmd = ExtractJsonStringField(json, "cmd");
                bool applyOrRollback = string.Equals(cmd, "apply_update", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(cmd, "rollback", StringComparison.OrdinalIgnoreCase);
                if (!applyOrRollback)
                    return false;

                // 持锁进程存活判定：aly-client 已退出（锁残留/崩溃）时不需要配合退出。
                // PID 复用风险：锁带 30 分钟 TTL（aly 侧保证），且 WM_CLOSE 到达时
                // apply 刚开始（秒级新鲜），PID 复用窗口可忽略。
                int pid = ExtractJsonIntField(json, "pid");
                if (pid <= 0)
                    return false;
                if (!IsProcessAlive(pid))
                    return false;

                Logger.Info("aly update lock active (cmd={0}, pid={1}) — updater is replacing files", cmd, pid);
                return true;
            }
            catch (Exception ex)
            {
                // 检测失败不阻断窗口关闭判定：按"无更新"处理（行为与无锁一致）
                Logger.Warn(ex, "Detect aly update lock failed");
                return false;
            }
        }

        /// <summary>
        /// 解析 aly 更新锁文件路径：主程序 exe 位于 MainFolder（如 ...\Debug\net40\），
        /// 更新器位于同级 UpdateFolder（如 ...\Debug\UpdateFolder\），锁文件在其下。
        /// 开发布局（bin\Debug\net40 + bin\Debug\UpdateFolder）与安装布局
        ///（{app}\ApplicationFolder + {app}\UpdateFolder）同构。
        /// </summary>
        private static string ResolveLockPath()
        {
            string exeDir = GetExecutableDirectory();
            if (string.IsNullOrEmpty(exeDir))
                return null;
            // MainFolder 的父目录 + UpdateFolder\.aly.lock
            string parent = Path.GetDirectoryName(exeDir);
            if (string.IsNullOrEmpty(parent))
                return null;
            return Path.Combine(parent, UpdateFolderName, LockFileName);
        }

        /// <summary>取当前进程可执行文件目录（失败回退 AppDomain 基目录）。</summary>
        private static string GetExecutableDirectory()
        {
            try
            {
                using (Process process = Process.GetCurrentProcess())
                {
                    if (process.MainModule != null && !string.IsNullOrEmpty(process.MainModule.FileName))
                        return Path.GetDirectoryName(process.MainModule.FileName);
                }
            }
            catch
            {
                // XP 受限环境可能取不到主模块：回退基目录
            }
            try
            {
                return AppDomain.CurrentDomain.BaseDirectory;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 从迷你 JSON 中提取字符串字段值（"key":"value" 形式）。
        /// aly 锁文件字段值固定为无空格紧凑格式（Go json.Marshal 输出），
        /// 无需完整 JSON 解析（与 ServerSettingsStore 的迷你解析风格一致，
        /// 避免 net40 引入 JSON 依赖）。
        /// </summary>
        private static string ExtractJsonStringField(string json, string key)
        {
            string marker = "\"" + key + "\":\"";
            int start = json.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return null;
            start += marker.Length;
            int end = json.IndexOf('"', start);
            if (end < 0) return null;
            return json.Substring(start, end - start);
        }

        /// <summary>从迷你 JSON 中提取整数字段值（"key":123 形式）。失败返回 -1。</summary>
        private static int ExtractJsonIntField(string json, string key)
        {
            string marker = "\"" + key + "\":";
            int start = json.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return -1;
            start += marker.Length;
            int end = start;
            while (end < json.Length && (char.IsDigit(json[end]) || (end == start && json[end] == '-')))
                end++;
            int value;
            if (int.TryParse(json.Substring(start, end - start), out value))
                return value;
            return -1;
        }

        /// <summary>判断指定 PID 进程是否存活（不存在时 GetProcessById 抛异常）。</summary>
        private static bool IsProcessAlive(int pid)
        {
            try
            {
                using (Process process = Process.GetProcessById(pid))
                {
                    return !process.HasExited;
                }
            }
            catch (ArgumentException)
            {
                // 进程不存在：锁为崩溃残留
                return false;
            }
            catch (Exception ex)
            {
                // 访问受限等：保守视为存活（宁可配合退出，不冒险占着端口）
                Logger.Warn(ex, "Check lock holder process {0} failed — assume alive", pid);
                return true;
            }
        }
    }
}
