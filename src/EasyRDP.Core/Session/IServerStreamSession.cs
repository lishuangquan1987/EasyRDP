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
        /// <summary>Gets the codec used by this stream session.</summary>
        CodecId Codec { get; }
        /// <summary>Gets or sets the delay in milliseconds between frame encodes (controls output frame rate).</summary>
        int FrameDelayMs { get; set; }
        /// <summary>Gets or sets the interval (in frames) between keyframes.</summary>
        int KeyframeInterval { get; set; }
        /// <summary>Gets or sets the target bitrate for the video encoder in bits per second.</summary>
        int TargetBitrate { get; set; }
        /// <summary>Gets or sets the capacity of the capture-to-encode frame queue.</summary>
        int FrameQueueCapacity { get; set; }
        /// <summary>Gets or sets the capacity of the encode-to-send queue.</summary>
        int SendQueueCapacity { get; set; }
        /// <summary>Gets the number of frames currently waiting in the send queue.</summary>
        int PendingFrames { get; }

        /// <summary>Starts the stream session. Begins encoding screen captures and sending them over the transport.</summary>
        void Start(uint sessionId, CodecId codec);
        /// <summary>Stops the session, terminates encode/send threads, and disposes the encoder.</summary>
        void Stop();
        /// <summary>Applies a global load-shedding level to reduce encoding workload under high concurrency.</summary>
        void ApplyGlobalLoadLevel(int level);

        /// <summary>Raised when a non-recoverable error occurs in the stream session.</summary>
        event EventHandler<ErrorEventArgs> FatalError;
    }
}
