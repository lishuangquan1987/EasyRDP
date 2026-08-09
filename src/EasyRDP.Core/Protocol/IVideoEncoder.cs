namespace EasyRDP.Core.Protocol
{
    using System;

    /// <summary>
    /// 视频编码器抽象。服务端使用。
    /// 状态机：构造 → Initialize → Encode（循环）→ Reset（可选）→ 重新 Initialize → ... → Dispose
    /// </summary>
    public interface IVideoEncoder : IDisposable
    {
        /// <summary>编码器类型。</summary>
        CodecId Codec { get; }

        /// <summary>编码器是否可用（原生 DLL 已加载且功能正常）。</summary>
        bool IsAvailable { get; }

        /// <summary>
        /// 初始化编码器。width/height 绑定后不可变——需分辨率变更时先 Reset 再 Initialize。
        /// </summary>
        void Initialize(int width, int height, int targetBitrate);

        /// <summary>
        /// 编码一帧 BGRA32 像素。返回中性 EncodedFrame（仅压缩数据，不含协议字段）；
        /// 返回 null 表示编码失败——调用方应跳过并计数。连续失败 30 帧视为编码器故障。
        /// 协议消息由编排层负责包装，编码层不感知协议。
        /// </summary>
        EncodedFrame Encode(byte[] pixels, bool forceKeyframe);

        /// <summary>
        /// 运行时调整目标码率（bps）。实现应尽量不重建编码器（避免丢参考帧/强制关键帧）；
        /// 无码率概念的编码器（如无损 ZRLE）应空实现。供 D11 自适应流控使用。
        /// </summary>
        void SetTargetBitrate(int bitrateBps);

        /// <summary>
        /// 重置编码器内部状态（丢包恢复、分辨率变更）。
        /// Reset 后须重新 Initialize 才可编码。
        /// </summary>
        void Reset();
    }
}
