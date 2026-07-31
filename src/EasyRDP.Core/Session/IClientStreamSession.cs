namespace EasyRDP.Core.Session
{
    using System;
    using EasyRDP.Core.Protocol;
    using EasyRDP.Core.Transport;
    /// <summary>
    /// 客户端视频流会话。双线程：接收→解码→FrameBuffer，渲染→RenderTarget。
    /// </summary>
    public interface IClientStreamSession : IDisposable
    {
        /// <summary>Gets the negotiated video codec identifier.</summary>
        CodecId Codec { get; }
        /// <summary>Gets the current frame width in pixels.</summary>
        int FrameWidth { get; }
        /// <summary>Gets the current frame height in pixels.</summary>
        int FrameHeight { get; }
        /// <summary>Gets the total number of frames received since the session started.</summary>
        long FrameCount { get; }
        /// <summary>Gets or sets the render target for displaying decoded frames.</summary>
        Rendering.IRenderTarget RenderTarget { get; set; }
        /// <summary>Starts the stream session with the given transport and begins receive/decode/render loops.</summary>
        void Start(ITransportClient transport);
        /// <summary>Stops the session, terminates threads, and releases unmanaged resources.</summary>
        void Stop();
        /// <summary>Raised when a non-recoverable error occurs in the stream session.</summary>
        event EventHandler<ErrorEventArgs> FatalError;
    }
}
