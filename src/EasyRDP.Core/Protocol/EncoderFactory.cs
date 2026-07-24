namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 编码器工厂。按 CodecId 创建 IVideoEncoder 实例。
    /// net40 与 net8.0 下 H264Software 都必须有可用实现——服务端常跑在 XP/.NET4。
    /// </summary>
    public static class EncoderFactory
    {
        /// <summary>
        /// 创建指定编码器。返回 null 表示当前平台不支持（如原生 DLL 缺失）。
        /// </summary>
        public static IVideoEncoder Create(CodecId codec)
        {
            switch (codec)
            {
                case CodecId.H264Software:
#if NET8_0_OR_GREATER
                    return null; // TODO: H264Encoder (Phase 6)
#else
                    return null; // TODO: H264EncoderNative (Phase 6, libx264/OpenH264 P/Invoke)
#endif
                case CodecId.H264Hardware:
                    return null; // 未来实现
                default:
                    return null;
            }
        }

        /// <summary>探测单个编码器是否可用（创建后立即 Dispose）。</summary>
        public static CodecId? GetAvailableCodec(CodecId preferred)
        {
            var e = Create(preferred);
            if (e != null)
            {
                e.Dispose();
                return preferred;
            }
            return null;
        }

        /// <summary>
        /// 枚举本机所有可用编码器，返回能力位掩码。
        /// 握手时服务端调用此方法广告实际能力（动态探测——仅含能实际创建的编码器）。
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
