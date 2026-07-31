namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 握手编码协商器。取交集，按 H264Hardware > H264Software 优先级。
    /// </summary>
    public static class CodecNegotiator
    {
        /// <summary>
        /// 协商编码。返回 null 表示无共同编码。
        /// </summary>
        public static CodecId? Negotiate(CodecCapabilities clientCaps, CodecCapabilities serverCaps)
        {
            CodecCapabilities common = clientCaps & serverCaps;
            if ((common & CodecCapabilities.H264Hardware) != 0)
                return CodecId.H264Hardware;
            if ((common & CodecCapabilities.H264Software) != 0)
                return CodecId.H264Software;
            return null;
        }
    }
}
