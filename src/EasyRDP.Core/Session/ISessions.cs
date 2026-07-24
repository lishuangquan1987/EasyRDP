namespace EasyRDP.Core.Session
{
    using System;
    using EasyRDP.Core.Protocol;
    using EasyRDP.Core.Transport;
    /// <summary>
    /// 服务端视频流会话。每个客户端连接对应一个实例。
    /// 线程模型（D8+D9）：截屏线程入队 → 编码线程编码 → 发送线程发送。
    /// </summary>
    public interface IServerStreamSession : IDisposable
    {
        CodecId Codec { get; }
        int FrameDelayMs { get; set; }
        int KeyframeInterval { get; set; }
        int TargetBitrate { get; set; }
        int FrameQueueCapacity { get; set; }
        int SendQueueCapacity { get; set; }
        int PendingFrames { get; }

        void Start(uint sessionId, CodecId codec);
        void Stop();
        void ApplyGlobalLoadLevel(int level);

        event EventHandler<ErrorEventArgs> FatalError;
    }

    /// <summary>
    /// 服务端输入会话。事件驱动同步调用，无独立线程。
    /// </summary>
    public interface IServerInputSession : IDisposable
    {
        bool HandleInput(InputEventMessage msg);
    }

    /// <summary>
    /// 客户端视频流会话。双线程：接收→解码→FrameBuffer，渲染→RenderTarget。
    /// </summary>
    public interface IClientStreamSession : IDisposable
    {
        CodecId Codec { get; }
        int FrameWidth { get; }
        int FrameHeight { get; }
        long FrameCount { get; }
        Rendering.IRenderTarget RenderTarget { get; set; }
        void Start(ITransportClient transport);
        void Stop();
        event EventHandler<ErrorEventArgs> FatalError;
    }

    /// <summary>
    /// 客户端输入会话。捕获本地输入并发送给服务端。
    /// </summary>
    public interface IClientInputSession : IDisposable
    {
        void Start(ITransportClient transport, int screenWidth, int screenHeight);
        void OnResolutionChanged(int newWidth, int newHeight);
        void Stop();
    }
}
