using System;
using EasyRDP.Core.Transport;

namespace EasyRDP.Server.Wpf.Models
{
    /// <summary>
    /// 日志条目。
    /// </summary>
    public class LogEntry
    {
        public DateTime Timestamp { get; set; }
        public LogLevel Level { get; set; }
        public string Message { get; set; }

        /// <summary>格式化后的显示文本。</summary>
        public string DisplayText
        {
            get
            {
                return string.Format("{0:HH:mm:ss} [{1}] {2}", Timestamp, Level, Message);
            }
        }
    }
}
