using System;

namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 编解码能力位。握手请求中由客户端携带，表示本地可解哪些编码。
    /// </summary>
    /// <remarks>
    /// <para>使用 [Flags] 让客户端一次声明多种能力（如同时支持 Bitmap 和 H264Software）。</para>
    /// <para>值 0 (<see cref="Legacy"/>) 专门表示"老客户端未携带扩展字节"，由 <see cref="Normalize"/> 规范化为 <see cref="Bitmap"/>。</para>
    /// <para>各能力位与 <see cref="CodecId"/> 一一对应：<see cref="CodecId.Bitmap"/>→1, <see cref="CodecId.H264Software"/>→2, <see cref="CodecId.H264Hardware"/>→4。</para>
    /// </remarks>
    [Flags]
    public enum CodecCapabilities : byte
    {
        /// <summary>老协议（无扩展字节）。仅用于握手兼容性检测，不应由新客户端显式声明。</summary>
        Legacy = 0,

        /// <summary>支持位图编码（ScreenFrame 消息）。</summary>
        Bitmap = 1,

        /// <summary>支持 OpenH264 软件解码（VideoFrame 消息）。</summary>
        H264Software = 2,

        /// <summary>支持硬件编码解码（VideoFrame 消息，B-4 阶段）。</summary>
        H264Hardware = 4,

        /// <summary>所有能力（Bitmap + H264Software + H264Hardware）。</summary>
        All = Bitmap | H264Software | H264Hardware
    }

    /// <summary>
    /// <see cref="CodecCapabilities"/> 扩展方法。
    /// </summary>
    public static class CodecCapabilitiesExtensions
    {
        /// <summary>
        /// 规范化能力位：<see cref="CodecCapabilities.Legacy"/> 视为 <see cref="CodecCapabilities.Bitmap"/>（老客户端兜底）。
        /// </summary>
        public static CodecCapabilities Normalize(this CodecCapabilities caps)
        {
            return caps == CodecCapabilities.Legacy ? CodecCapabilities.Bitmap : caps;
        }

        /// <summary>
        /// 判断是否具备指定编码能力。
        /// </summary>
        public static bool Has(this CodecCapabilities caps, CodecId codec)
        {
            CodecCapabilities flag;
            switch (codec)
            {
                case CodecId.Bitmap: flag = CodecCapabilities.Bitmap; break;
                case CodecId.H264Software: flag = CodecCapabilities.H264Software; break;
                case CodecId.H264Hardware: flag = CodecCapabilities.H264Hardware; break;
                default: return false;
            }
            return (caps & flag) != 0;
        }
    }
}
