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

        /// <summary>
        /// 本帧变化瓦片数（D14 混合编码 + D11 动态帧统计的数据源）。
        /// ZRLE 编码器填充实际变化瓦片数（0=静止帧）；H264 编码器模式下由
        /// 编排层用 ZrleEncoder.MeasureChangedTiles 测量后填入；未统计时为 -1。
        /// </summary>
        public int ChangedTileCount;
    }
}
