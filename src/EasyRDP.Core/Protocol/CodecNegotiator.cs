namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 握手编码协商器。取交集，按 H264Hardware > ZRLE > VP8 > H264Software 优先级。
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

            // 优先级：硬件 H264（画质+压缩率最优）> ZRLE（静态/低变化场景带宽最优）
            //         > VP8（动态场景实时低延时，弱 CPU 友好）> 软件 H264（兼容性兜底）
            if ((common & CodecCapabilities.H264Hardware) != 0)
            {
                Logger.Info("Codec negotiated: H264Hardware (client=0x{0:X} server=0x{1:X})",
                    (byte)clientCaps, (byte)serverCaps);
                return CodecId.H264Hardware;
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
            if ((common & CodecCapabilities.H264Software) != 0)
            {
                Logger.Info("Codec negotiated: H264Software (client=0x{0:X} server=0x{1:X})",
                    (byte)clientCaps, (byte)serverCaps);
                return CodecId.H264Software;
            }
            Logger.Warn("Codec negotiation failed: no common codec. client=0x{0:X} server=0x{1:X}",
                (byte)clientCaps, (byte)serverCaps);
            return null;
        }
    }
}
