namespace EasyRDP.Core.Transport
{
    using System;
    /// <summary>
    /// 传输服务端接口。监听端口，管理多客户端连接。
    /// </summary>
    public interface ITransportServer : IDisposable
    {
        void Start(int port);
        void Stop();

        /// <summary>
        /// 向指定客户端尽力发送一个线格式分片字节。
        /// 公平性约束：各 Session 发送应写入对应 Socket，不得有全局发送锁。
        /// </summary>
        void SendTo(uint sessionId, byte[] data);

        void Disconnect(uint sessionId);

        event EventHandler<ConnectionEventArgs> ClientConnected;
        event EventHandler<ConnectionEventArgs> ClientDisconnected;
        event EventHandler<FragmentReceivedEventArgs> DataReceived;

        LogCallback OnLog { get; set; }
    }
}
