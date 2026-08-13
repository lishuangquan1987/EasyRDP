namespace EasyRDP.Core.Transport
{
    /// <summary>客户端建连抽象。按 endpoint 建立一条连接并返回 ITransport 实例。</summary>
    /// <remarks>
    /// endpoint 格式由各实现定义：TCP 为 "host:port"；命名管道为 "\\.\pipe\name" 等。
    /// 客户端编排层只依赖本接口，换 WebSocket/QUIC 后端时仅需替换 connector 实例，
    /// 无需改动会话/UI 代码。
    /// 返回的 ITransport 处于「已连接但未开始接收」状态：调用方先订阅 MessageReceived/Disconnected，
    /// 再调 transport.Start()。连接失败（endpoint 解析失败/超时/拒绝）返回 null，
    /// 失败详情经 OnLog 回调 + 实现内部日志记录；调用方判空处理。
    /// </remarks>
    public interface ITransportConnector
    {
        ITransport Connect(string endpoint, int timeoutMs);

        LogCallback OnLog { get; set; }
    }
}
