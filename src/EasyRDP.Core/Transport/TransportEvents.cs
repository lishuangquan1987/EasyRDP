using System;

namespace EasyRDP.Core.Transport
{
    /// <summary>
    /// 网络连接事件参数
    /// </summary>
    public class ConnectionEventArgs : EventArgs
    {
        /// <summary>远程端点地址</summary>
        public string RemoteEndPoint;

        /// <summary>会话 ID</summary>
        public uint SessionId;

        public ConnectionEventArgs()
        {
            RemoteEndPoint = string.Empty;
        }
    }

    /// <summary>
    /// 消息接收事件参数
    /// </summary>
    public class MessageReceivedEventArgs : EventArgs
    {
        /// <summary>接收到的消息</summary>
        public Protocol.Message Message;

        /// <summary>消息来源的客户端会话 ID（服务端接收时有效）</summary>
        public uint SessionId;

        public MessageReceivedEventArgs()
        {
            Message = null;
        }
    }

    /// <summary>
    /// 日志级别
    /// </summary>
    public enum LogLevel
    {
        /// <summary>调试</summary>
        Debug,

        /// <summary>信息</summary>
        Info,

        /// <summary>警告</summary>
        Warning,

        /// <summary>错误</summary>
        Error
    }

    /// <summary>
    /// 传输层日志回调
    /// </summary>
    /// <param name="level">日志级别</param>
    /// <param name="message">日志内容</param>
    public delegate void LogCallback(LogLevel level, string message);
}
