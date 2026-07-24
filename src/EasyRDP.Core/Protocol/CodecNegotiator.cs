namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 握手编码协商器。服务端调用 Negotiate 取客户端解码能力与服务端编码能力的交集，
    /// 按优先级选出唯一 CodecId。协商逻辑集中在此类，不散落在编排层。
    /// </summary>
    public static class CodecNegotiator
    {
        /// <summary>
        /// 协商编码。返回 null 表示无共同编码（应回 HandshakeRes.Result=NoCommonCodec）。
        /// 优先级：H264Hardware > H264Software
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
