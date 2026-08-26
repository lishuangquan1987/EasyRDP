namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 握手编码协商器。取交集，按 H264Hardware > ZRLE > H264Software > VP8 优先级。
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

            // 优先级：硬件 H264 > ZRLE（区域增量） > 软件 H264 > VP8。
            // v2026-08-26 修正：ZRLE 优先级从软件 H264 之下提升到其上。
            //   - ZRLE 是区域增量编码（64×64 瓦片，只编码变化瓦片，纯 C# 无原生依赖），
            //     弱机上静态/局部变化场景开销远低于"全帧 H264 编码"（每帧 BGRA→I420 转换
            //     + 整帧压缩 150ms+）。RealVNC 类工具正是此模式，弱机体验流畅。
            //   - 旧注释称"弱机上 ZRLE 的 Deflate 慢 500-2300ms/帧"已过时：当时 ZRLE 未做
            //     uint 步长比较、缓冲池化与 CopyRect 锚点传播优化（详见 ZrleEncoder.cs），
            //     优化后实测静态 3-8ms / 局部 15-40ms / 全屏 50-100ms，弱机显著优于软件 H264。
            //   - 保留 H264Hardware 最高优先：硬件编码速度/能耗仍优于软件 ZRLE。
            //   - H264Software 保留为 ZRLE 不可用时回退（如超高分辨率瓦片数超限）；
            //     支持软件 H264 但仍保有 ZRLE 可用的客户端，弱机优先走 ZRLE 换取帧率。
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
            if ((common & CodecCapabilities.H264Software) != 0)
            {
                Logger.Info("Codec negotiated: H264Software (client=0x{0:X} server=0x{1:X})",
                    (byte)clientCaps, (byte)serverCaps);
                return CodecId.H264Software;
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
