namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 编码结果。中性数据结构，不含协议字段，由编排层包装为 VideoFrameMessage。
    /// </summary>
    public struct EncodedFrame
    {
        /// <summary>H.264 压缩字节。</summary>
        public byte[] Data;

        /// <summary>是否 IDR 关键帧。</summary>
        public bool IsKeyframe;

        /// <summary>编码时的宽度。</summary>
        public int Width;

        /// <summary>编码时的高度。</summary>
        public int Height;
    }
}
