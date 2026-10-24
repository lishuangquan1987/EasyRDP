#nullable disable
using System;
using System.IO;
using System.Text;
using NLog;

namespace EasyRDP.Server.Wpf.Services
{
    /// <summary>
    /// 会话审计日志。与 NLog 运行日志分离，按天写到 Logs/audit-YYYY-MM-DD.log，
    /// 便于长期留存和导出（不受 NLog 级别/归档策略影响）。
    /// 记录：连接、断开（含时长）、认证失败、审批通过/拒绝、被踢、文件收发。
    /// </summary>
    public class AuditLogger
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly object _lock = new object();
        private readonly string _directory;

        public AuditLogger()
            : this(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs"))
        {
        }

        public AuditLogger(string directory)
        {
            _directory = directory;
        }

        /// <summary>
        /// 写入一条审计记录。失败只记 NLog 警告，绝不影响业务线程。
        /// </summary>
        /// <param name="eventName">事件名（CONNECT/DISCONNECT/AUTH_FAIL/APPROVED/DENIED/FILE_SEND/FILE_RECV）。</param>
        /// <param name="sessionId">会话 ID（握手失败可用 0）。</param>
        /// <param name="username">客户端用户名。</param>
        /// <param name="remote">客户端远端地址。</param>
        /// <param name="detail">附加信息（codec/分辨率/文件大小/失败原因）。</param>
        public void Log(string eventName, uint sessionId, string username, string remote, string detail)
        {
            try
            {
                string line = string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} | {1} | session={2} | user={3} | remote={4} | {5}{6}",
                    DateTime.Now,
                    Clean(eventName),
                    sessionId,
                    Clean(username),
                    Clean(remote),
                    Clean(detail),
                    Environment.NewLine);

                string file = Path.Combine(_directory, "audit-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");
                lock (_lock)
                {
                    Directory.CreateDirectory(_directory);
                    File.AppendAllText(file, line, Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Audit log write failed: {0}", eventName);
            }
        }

        /// <summary>会话断开记录：附加连接时长。</summary>
        public void LogDisconnect(uint sessionId, string username, string remote, string detail, TimeSpan duration)
        {
            string dur = string.Format("{0:00}:{1:00}:{2:00}", (int)duration.TotalHours, duration.Minutes, duration.Seconds);
            Log("DISCONNECT", sessionId, username, remote, detail + " | duration=" + dur);
        }

        /// <summary>字段清洗：去掉会破坏行结构的竖线与换行。</summary>
        private static string Clean(string value)
        {
            if (string.IsNullOrEmpty(value)) return "-";
            return value.Replace("|", "/").Replace("\r", " ").Replace("\n", " ");
        }
    }
}
