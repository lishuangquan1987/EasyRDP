namespace EasyRDP.Core.Transport
{
    using System;

    /// <summary>
    /// 传输连接抽象。发送/接收的单位是「完整消息字节」（framing 外层 + payload），
    /// 不感知分片——分片是各传输实现的内部细节。
    /// 与客户端/服务端角色无关：TCP 客户端 Connect 得到的连接与服务端 Accept 得到的连接
    /// 共用本接口。
    /// </summary>
    public interface ITransport : IDisposable
    {
        /// <summary>
        /// 开始接收循环（幂等）。连接建立后不自动开始：调用方须先订阅
        /// MessageReceived/Disconnected，再调 Start()，避免首包在订阅前到达而丢失。
        /// </summary>
        void Start();

        /// <summary>
        /// 发送一条完整消息（Magic+Type+PayloadLen+Payload）。不返回成功标志：
        /// 写入失败/连接已断通过 Disconnected 事件与 OnLog 上报，调用方依赖 IsConnected 判断。
        /// </summary>
        void Send(byte[] message);

        bool IsConnected { get; }

        /// <summary>
        /// 优雅关闭连接并触发 Disconnected 事件；幂等。
        /// IDisposable.Dispose 等价调用本方法并释放资源。
        /// </summary>
        void Disconnect();

        /// <summary>收到一条完整消息时触发（MessageType + payload）。</summary>
        event EventHandler<MessageReceivedEventArgs> MessageReceived;

        event EventHandler Disconnected;

        LogCallback OnLog { get; set; }
    }
}
