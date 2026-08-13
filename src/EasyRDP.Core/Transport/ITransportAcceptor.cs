namespace EasyRDP.Core.Transport
{
    using System;

    /// <summary>传输监听抽象。监听 endpoint，接受新连接并产出 ITransport 实例。</summary>
    /// <remarks>
    /// endpoint 格式由各实现定义：TCP 为 "port"（监听 0.0.0.0）或 "host:port"；
    /// 命名管道为 "\\.\pipe\name"；Unix Socket 为路径。
    /// </remarks>
    public interface ITransportAcceptor : IDisposable
    {
        void Start(string endpoint);

        void Stop();

        /// <summary>新连接到达时触发，事件参数携带该连接的 ITransport 实例（已连接未开始接收）。</summary>
        event EventHandler<TransportAcceptedEventArgs> ClientConnected;

        LogCallback OnLog { get; set; }
    }
}
