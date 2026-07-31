namespace EasyRDP.Core.Transport
{
    using System;
    /// <summary>
    /// 传输客户端接口。连向服务端，收发分片字节。
    /// </summary>
    public interface ITransportClient : IDisposable
    {
        bool Connect(string host, int port, int timeoutMs);
        void Disconnect();

        /// <summary>
        /// 尽力发送已构好的完整线格式分片字节。
        /// 返回 true=已写入底层；false=连接已断/发送失败。
        /// </summary>
        bool Send(byte[] data);

        bool IsConnected { get; }

        /// <summary>收到一个线格式分片字节时触发（可能乱序/重复/丢失）。</summary>
        event EventHandler<FragmentReceivedEventArgs> DataReceived;

        event EventHandler Disconnected;

        LogCallback OnLog { get; set; }
    }
}
