namespace EasyRDP.Core.Protocol
{
    using System;

    /// <summary>
    /// 视频解码器抽象。客户端使用，镜像 IVideoEncoder。
    /// 状态机：构造 → Initialize → Decode（循环）→ Reset（可选）→ 重新 Initialize → ... → Dispose
    /// </summary>
    public interface IVideoDecoder : IDisposable
    {
        /// <summary>解码器类型。</summary>
        CodecId Codec { get; }

        /// <summary>解码器是否可用（原生 DLL 已加载且功能正常）。</summary>
        bool IsAvailable { get; }

        /// <summary>初始化解码器。width/height 来自 HandshakeRes。</summary>
        void Initialize(int width, int height);

        /// <summary>
        /// 解码一帧。返回 DecodeResult：
        ///   Status=NeedMoreInput：解码器启动缓冲，正常，调用方静默等待下一帧；
        ///   Status=Ok+Pixels：解码成功，返回 BGRA32 像素；
        ///   Status=Failed：可恢复解码错误，调用方计数并跳过（连续失败达阈值才断连）；
        ///   native 层致命错误时实现设 IsAvailable=false，调用方检测后触发断连。
        /// </summary>
        DecodeResult Decode(byte[] data);

        /// <summary>
        /// 解码到调用方提供的输出缓冲（省拷贝优化）。
        /// outputBuffer 须 >= width*height*4；解码成功时 Status=Ok 且 Pixels 引用 outputBuffer 本身
        /// （实现直接写入，不另分配），调用方可省去一次 BlockCopy。输出缓冲不足时返回 Failed。
        /// </summary>
        DecodeResult Decode(byte[] data, byte[] outputBuffer);

        /// <summary>重置解码器内部状态。Reset 后须重新 Initialize 才可解码。</summary>
        void Reset();
    }
}
