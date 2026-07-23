using System;

namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 编码后端协商器。根据客户端声明的能力与服务端配置的编码器，选出双方都支持的编码。
    /// </summary>
    /// <remarks>
    /// <para>协商优先级：<see cref="CodecId.H264Hardware"/> &gt; <see cref="CodecId.H264Software"/> &gt; <see cref="CodecId.Bitmap"/>。</para>
    /// <para>设计为静态无状态方法，便于在 net40 / net8.0 双目标下共用。</para>
    /// </remarks>
    public static class CodecNegotiator
    {
        /// <summary>
        /// 根据服务端配置的编码器返回服务端能力位。
        /// Bitmap 编码器仅声明 Bitmap；H264Software 声明 Bitmap|H264Software（编码 H264 时客户端也可降级到 Bitmap）。
        /// </summary>
        public static CodecCapabilities GetServerCapabilities(CodecId serverCodec)
        {
            switch (serverCodec)
            {
                case CodecId.Bitmap:
                    return CodecCapabilities.Bitmap;
                case CodecId.H264Software:
                    // 服务端配 H264Software 时仍兼容 Bitmap 客户端（自动降级）
                    return CodecCapabilities.Bitmap | CodecCapabilities.H264Software;
                case CodecId.H264Hardware:
                    // 同理，硬件编码不可用时也可降级到软件/位图
                    return CodecCapabilities.All;
                default:
                    return CodecCapabilities.Bitmap;
            }
        }

        /// <summary>
        /// 协商编码后端。返回双方都支持的最高优先级编码。
        /// </summary>
        /// <param name="clientCaps">客户端声明的能力位（<see cref="CodecCapabilities.Legacy"/> 会被规范化为 <see cref="CodecCapabilities.Bitmap"/>）</param>
        /// <param name="serverCaps">服务端能力位（由 <see cref="GetServerCapabilities"/> 计算）</param>
        /// <returns>协商结果。交集为空时回退到 <see cref="CodecId.Bitmap"/>（保证连接可用）</returns>
        public static CodecId Negotiate(CodecCapabilities clientCaps, CodecCapabilities serverCaps)
        {
            CodecCapabilities c = clientCaps.Normalize();
            CodecCapabilities s = serverCaps;

            // 优先级从高到低：H264Hardware > H264Software > Bitmap
            if ((c & s & CodecCapabilities.H264Hardware) != 0)
                return CodecId.H264Hardware;
            if ((c & s & CodecCapabilities.H264Software) != 0)
                return CodecId.H264Software;
            // Bitmap 是兜底，所有客户端都应支持
            return CodecId.Bitmap;
        }
    }
}
