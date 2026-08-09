namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 解码器工厂。按 CodecId 创建 IVideoDecoder 实例。
    /// </summary>
    public static class DecoderFactory
    {
        /// <summary>
        /// 创建指定解码器。返回 null 表示当前平台不支持。
        /// </summary>
        public static IVideoDecoder Create(CodecId codec)
        {
            switch (codec)
            {
                case CodecId.H264Software:
                {
                    var decoder = new H264DecoderNative();
                    return decoder.IsAvailable ? decoder : null;
                }
                case CodecId.H264Hardware:
                    return null; // 未来实现
                case CodecId.Zrle:
                    return new ZrleDecoder();
                case CodecId.Vp8Software:
                {
                    var vp8 = new Vp8DecoderNative();
                    return vp8.IsAvailable ? vp8 : null;
                }
                default:
                    return null;
            }
        }

        /// <summary>探测单个解码器是否可用。</summary>
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
        /// </summary>
        public static CodecCapabilities GetAvailableCodecs()
        {
            var caps = CodecCapabilities.None;
            // ZRLE 无需探测（纯 C# 始终可用），直接置位
            caps |= CodecCapabilities.Zrle;
            foreach (CodecId c in new[] { CodecId.H264Software, CodecId.H264Hardware, CodecId.Vp8Software })
            {
                if (!GetAvailableCodec(c).HasValue)
                    continue;
                switch (c)
                {
                    case CodecId.H264Software: caps |= CodecCapabilities.H264Software; break;
                    case CodecId.H264Hardware: caps |= CodecCapabilities.H264Hardware; break;
                    case CodecId.Vp8Software: caps |= CodecCapabilities.Vp8Software; break;
                }
            }
            return caps;
        }
    }
}
