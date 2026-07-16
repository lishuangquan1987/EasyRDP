using System;

namespace EasyRDP.Core.Transport
{
    /// <summary>
    /// 客户端传输层抽象接口。
    /// 每种传输方式（TCP、UDP 等）各自实现此接口。
    /// 同一时刻仅使用一种传输实现。
    /// </summary>
    public interface ITransportClient : IDisposable
    {
        /// <summary>
        /// 连接到服务端。
        /// </summary>
        /// <param name="host">服务端地址</param>
        /// <param name="port">服务端口</param>
        /// <param name="timeoutMs">连接超时（毫秒）</param>
        /// <returns>连接成功返回 true</returns>
        bool Connect(string host, int port, int timeoutMs);

        /// <summary>
        /// 断开连接。
        /// </summary>
        void Disconnect();

        /// <summary>
        /// 发送数据。返回是否发送成功。
        /// </summary>
        bool Send(byte[] data);

        /// <summary>
        /// 是否已连接。
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// 收到消息时触发。
        /// </summary>
        event EventHandler<MessageReceivedEventArgs> MessageReceived;

        /// <summary>
        /// 连接断开时触发。
        /// </summary>
        event EventHandler Disconnected;

        /// <summary>
        /// 日志输出回调。
        /// </summary>
        LogCallback OnLog { get; set; }
    }
}
