namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 编码器工厂。按 CodecId 创建 IVideoEncoder 实例。
    /// </summary>
    public static class EncoderFactory
    {
        /// <summary>
        /// 创建指定编码器。返回 null 表示当前平台不支持。
        /// </summary>
        public static IVideoEncoder Create(CodecId codec)
        {
            switch (codec)
            {
                case CodecId.H264Software:
                {
                    var encoder = new H264EncoderNative();
                    return encoder.IsAvailable ? encoder : null;
                }
                case CodecId.H264Hardware:
                    return null; // 未来实现
                default:
                    return null;
            }
        }

        /// <summary>探测单个编码器是否可用。</summary>
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
