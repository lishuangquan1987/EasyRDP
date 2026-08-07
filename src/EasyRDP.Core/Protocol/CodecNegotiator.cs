namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 握手编码协商器。取交集，按 H264Hardware > ZRLE > H264Software 优先级。
    /// </summary>
    public static class CodecNegotiator
    {
        /// <summary>
        /// 协商编码。返回 null 表示无共同编码。
        /// </summary>
        public static CodecId? Negotiate(CodecCapabilities clientCaps, CodecCapabilities serverCaps)
        {
            CodecCapabilities common = clientCaps & serverCaps;
            // 优先级：硬件 H264（画质+压缩率最优）> ZRLE（单核 CPU 性能最优）> 软件 H264（兼容性兜底）
            if ((common & CodecCapabilities.H264Hardware) != 0)
                return CodecId.H264Hardware;
            if ((common & CodecCapabilities.Zrle) != 0)
                return CodecId.Zrle;
            if ((common & CodecCapabilities.H264Software) != 0)
                return CodecId.H264Software;
            return null;
        }
    }
}
