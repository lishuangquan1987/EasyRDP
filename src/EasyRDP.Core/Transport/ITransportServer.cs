using System;

namespace EasyRDP.Core.Transport
{
    /// <summary>
    /// 服务端传输层抽象接口。
    /// 每种传输方式（TCP、UDP 等）各自实现此接口。
    /// 同一时刻仅使用一种传输实现，管理多个客户端会话。
    /// </summary>
    public interface ITransportServer : IDisposable
    {
        /// <summary>
        /// 启动服务端监听。
        /// </summary>
        /// <param name="port">监听端口</param>
        void Start(int port);

        /// <summary>
        /// 停止服务端。
        /// </summary>
        void Stop();

        /// <summary>
        /// 向指定客户端发送数据。
        /// </summary>
        /// <param name="sessionId">目标会话 ID</param>
        /// <param name="data">要发送的数据</param>
        void SendTo(uint sessionId, byte[] data);

        /// <summary>
        /// 断开指定客户端。
        /// </summary>
        /// <param name="sessionId">目标会话 ID</param>
        void Disconnect(uint sessionId);

        /// <summary>
        /// 新客户端连接时触发。
        /// </summary>
        event EventHandler<ConnectionEventArgs> ClientConnected;

        /// <summary>
        /// 客户端断开时触发。
        /// </summary>
        event EventHandler<ConnectionEventArgs> ClientDisconnected;

        /// <summary>
        /// 收到消息时触发。
        /// </summary>
        event EventHandler<MessageReceivedEventArgs> MessageReceived;

        /// <summary>
        /// 日志输出回调。
        /// </summary>
        LogCallback OnLog { get; set; }
    }
}
