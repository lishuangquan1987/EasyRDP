using System;

namespace EasyRDP.Core.Transport
{
    /// <summary>
    /// 传输通道抽象接口——可替换的实现（TCP / UDP / WebSocket / NamedPipe / QUIC ...）
    /// </summary>
    public interface ITransportChannel : IDisposable
    {
        /// <summary>绑定本地端口并开始监听（服务端模式）</summary>
        void Bind(int port);

        /// <summary>连接到远程端点（客户端模式）。返回是否成功</summary>
        bool Connect(string host, int port, int timeoutMs);

        /// <summary>使用平台原生客户端对象启动接收（服务端 Accept 回调后调用）</summary>
        void StartWithClient(object nativeClient);

        /// <summary>发送数据。返回是否发送成功</summary>
        bool Send(byte[] data);

        /// <summary>关闭通道</summary>
        void Close();

        /// <summary>收到完整消息时触发</summary>
        event EventHandler<MessageReceivedEventArgs> MessageReceived;

        /// <summary>连接断开时触发</summary>
        event EventHandler Disconnected;

        /// <summary>通道是否已连接</summary>
        bool IsConnected { get; }

        /// <summary>日志回调</summary>
        LogCallback OnLog { get; set; }
    }

    /// <summary>
    /// 传输模式枚举——控制服务端/客户端使用哪些传输通道组合
    /// </summary>
    public enum TransportMode : byte
    {
        /// <summary>仅 TCP（默认）。所有数据走 TCP，可靠性优先</summary>
        Tcp = 0,

        /// <summary>TCP + UDP 双通道。TCP 承载控制消息，UDP 承载屏幕帧等实时数据</summary>
        TcpAndUdp = 1

        // 后续可扩展：WebSocket = 2, NamedPipe = 3, QUIC = 4 ...
    }
}
