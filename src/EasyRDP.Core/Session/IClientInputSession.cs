namespace EasyRDP.Core.Session
{
    using System;
    using EasyRDP.Core.Protocol;
    using EasyRDP.Core.Transport;
    /// <summary>
    /// 客户端输入会话。捕获本地输入并发送给服务端。
    /// </summary>
    public interface IClientInputSession : IDisposable
    {
        /// <summary>Starts the input session with the given transport and screen dimensions for coordinate mapping.</summary>
        void Start(ITransportClient transport, int screenWidth, int screenHeight);
        /// <summary>Notifies the session that the remote screen resolution has changed, updating coordinate mapping.</summary>
        void OnResolutionChanged(int newWidth, int newHeight);
        /// <summary>Stops the input session and releases the transport reference.</summary>
        void Stop();
    }
}
