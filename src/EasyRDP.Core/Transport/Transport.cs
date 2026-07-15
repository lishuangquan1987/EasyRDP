using System;

namespace EasyRDP.Core.Transport
{
    /// <summary>
    /// 网络连接事件参数
    /// </summary>
    public class ConnectionEventArgs : EventArgs
    {
        public string RemoteEndPoint;
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
        public Protocol.Message Message;
        /// <summary>消息来源的客户端会话 ID（服务端接收时有效，客户端发送时为 0）</summary>
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
        Debug,
        Info,
        Warning,
        Error
    }

    /// <summary>
    /// 传输层日志回调
    /// </summary>
    /// <param name="level">日志级别</param>
    /// <param name="message">日志内容</param>
    public delegate void LogCallback(LogLevel level, string message);

    /// <summary>
    /// 服务端传输层接口
    /// </summary>
    public interface IServerTransport : IDisposable
    {
        /// <summary>启动服务端监听</summary>
        /// <param name="port">监听端口</param>
        /// <param name="mode">传输模式（默认 TCP）</param>
        void Start(int port, TransportMode mode);

        /// <summary>停止服务端</summary>
        void Stop();

        /// <summary>向指定客户端发送数据</summary>
        void SendTo(uint sessionId, byte[] data);

        /// <summary>向所有客户端广播数据（UDP 模式预留）</summary>
        void Broadcast(byte[] data);

        /// <summary>断开指定客户端</summary>
        void Disconnect(uint sessionId);

        /// <summary>客户端连接事件</summary>
        event EventHandler<ConnectionEventArgs> ClientConnected;

        /// <summary>客户端断开事件</summary>
        event EventHandler<ConnectionEventArgs> ClientDisconnected;

        /// <summary>消息接收事件</summary>
        event EventHandler<MessageReceivedEventArgs> MessageReceived;

        /// <summary>日志输出回调</summary>
        LogCallback OnLog { get; set; }
    }

    /// <summary>
    /// 客户端传输层接口
    /// </summary>
    public interface IClientTransport : IDisposable
    {
        /// <summary>连接到服务端</summary>
        /// <param name="host">服务端地址</param>
        /// <param name="port">服务端口</param>
        /// <param name="mode">传输模式（默认 TCP）</param>
        /// <param name="timeoutMs">超时毫秒</param>
        bool Connect(string host, int port, TransportMode mode, int timeoutMs);

        /// <summary>断开连接</summary>
        void Disconnect();

        /// <summary>发送数据，返回是否发送成功</summary>
        bool Send(byte[] data);

        /// <summary>消息接收事件</summary>
        event EventHandler<MessageReceivedEventArgs> MessageReceived;

        /// <summary>连接断开事件</summary>
        event EventHandler Disconnected;

        /// <summary>日志输出回调</summary>
        LogCallback OnLog { get; set; }

        /// <summary>是否已连接</summary>
        bool IsConnected { get; }
    }
}
