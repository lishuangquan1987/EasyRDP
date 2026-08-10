namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 握手编码协商器。取交集，按 H264Hardware > H264Software > ZRLE > VP8 优先级。
    /// 协商结果通过日志输出（Debug 记录输入能力位，Info 记录结论，Warn 记录无共同编码），
    /// 排查"客户端/服务端各自宣称了什么、为何选了某编码"时可直接看日志。
    /// </summary>
    public static class CodecNegotiator
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>
        /// 协商编码。返回 null 表示无共同编码。
        /// </summary>
        public static CodecId? Negotiate(CodecCapabilities clientCaps, CodecCapabilities serverCaps)
        {
            CodecCapabilities common = clientCaps & serverCaps;
            if (Logger.IsDebugEnabled)
            {
                Logger.Debug("Codec negotiate: client=0x{0:X} server=0x{1:X} common=0x{2:X}",
                    (byte)clientCaps, (byte)serverCaps, (byte)common);
            }

            // 优先级：硬件 H264 > 软件 H264 > ZRLE > VP8。
            // 原设计 ZRLE 优先（静态低带宽）；实测弱机（Win7 32 位单核）上 ZRLE 的
            // .NET Deflate 压缩 500-2300ms/帧，而 OpenH264 软编（优化汇编）快 3-5 倍。
            // 服务端 CPU 是瓶颈时优先 H264 软编换取帧率；ZRLE 保留为 H264 不可用回退。
            if ((common & CodecCapabilities.H264Hardware) != 0)
            {
                Logger.Info("Codec negotiated: H264Hardware (client=0x{0:X} server=0x{1:X})",
                    (byte)clientCaps, (byte)serverCaps);
                return CodecId.H264Hardware;
            }
            if ((common & CodecCapabilities.H264Software) != 0)
            {
                Logger.Info("Codec negotiated: H264Software (client=0x{0:X} server=0x{1:X})",
                    (byte)clientCaps, (byte)serverCaps);
                return CodecId.H264Software;
            }
            if ((common & CodecCapabilities.Zrle) != 0)
            {
                Logger.Info("Codec negotiated: Zrle (client=0x{0:X} server=0x{1:X})",
                    (byte)clientCaps, (byte)serverCaps);
                return CodecId.Zrle;
            }
            if ((common & CodecCapabilities.Vp8Software) != 0)
            {
                Logger.Info("Codec negotiated: Vp8Software (client=0x{0:X} server=0x{1:X})",
                    (byte)clientCaps, (byte)serverCaps);
                return CodecId.Vp8Software;
            }
            Logger.Warn("Codec negotiation failed: no common codec. client=0x{0:X} server=0x{1:X}",
                (byte)clientCaps, (byte)serverCaps);
            return null;
        }
    }
}
