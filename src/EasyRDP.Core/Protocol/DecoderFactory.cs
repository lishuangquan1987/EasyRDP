namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 解码器工厂。按 CodecId 创建 IVideoDecoder 实例。
    /// 与 EncoderFactory 对称。
    /// </summary>
    public static class DecoderFactory
    {
        /// <summary>
        /// 创建指定解码器。返回 null 表示当前平台不支持（如原生 DLL 缺失）。
        /// </summary>
        public static IVideoDecoder Create(CodecId codec)
        {
            switch (codec)
            {
                case CodecId.H264Software:
#if NET8_0_OR_GREATER
                    return null; // TODO: H264Decoder (Phase 6)
#else
                    return null; // TODO: H264DecoderNative (Phase 6)
#endif
                case CodecId.H264Hardware:
                    return null; // 未来实现
                default:
                    return null;
            }
        }

        /// <summary>探测单个解码器是否可用（创建后立即 Dispose）。</summary>
        public static CodecId? GetAvailableCodec(CodecId preferred)
        {
            var d = Create(preferred);
            if (d != null)
            {
                d.Dispose();
                return preferred;
            }
            return null;
        }

        /// <summary>
        /// 枚举本机所有可用解码器，返回能力位掩码。
        /// 握手时客户端调用此方法广告解码能力。
        /// </summary>
        public static CodecCapabilities GetAvailableCodecs()
        {
            var caps = CodecCapabilities.None;
            foreach (CodecId c in new[] { CodecId.H264Software, CodecId.H264Hardware })
            {
                if (!GetAvailableCodec(c).HasValue)
                    continue;
                switch (c)
                {
                    case CodecId.H264Software: caps |= CodecCapabilities.H264Software; break;
                    case CodecId.H264Hardware: caps |= CodecCapabilities.H264Hardware; break;
                }
            }
            return caps;
        }
    }
}
