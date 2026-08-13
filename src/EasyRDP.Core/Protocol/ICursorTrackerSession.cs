namespace EasyRDP.Core.Protocol
{
    using System;

    /// <summary>
    /// 会话级光标控制。由 ICursorTracker 为指定 sessionId 派生，注入到对应 ServerStreamSession。
    /// 只能控制本 Session 的光标订阅，无法影响其他客户端。
    /// </summary>
    public interface ICursorTrackerSession
    {
        /// <summary>
        /// 注入本会话的发送回调。光标变化时通过它发送 CursorUpdateMessage 的完整线格式字节。
        /// sessionId 路由已由调用方（TransportHost/ServerStreamSession）在闭包中捕获，此处不再携带。
        /// </summary>
        void AttachSendTo(Action<byte[]> sendTo);

        /// <summary>启动本会话的光标追踪。</summary>
        void Start();

        /// <summary>停止本会话的光标追踪。</summary>
        void Stop();
    }
}
