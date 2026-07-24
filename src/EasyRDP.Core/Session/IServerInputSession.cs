namespace EasyRDP.Core.Session
{
    using System;
    using EasyRDP.Core.Protocol;
    using EasyRDP.Core.Transport;
    /// <summary>
    /// 服务端输入会话。事件驱动同步调用，无独立线程。
    /// </summary>
    public interface IServerInputSession : IDisposable
    {
        /// <summary>Processes an input event message (keyboard, mouse, or wheel) and simulates it on the server.</summary>
        bool HandleInput(InputEventMessage msg);
    }
}
