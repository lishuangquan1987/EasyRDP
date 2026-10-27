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

        /// <summary>
        /// 编码器主动跳帧（非失败）：H264 开启 bEnableFrameSkip 后，VBV 溢出时
        /// ret=0 但无层输出。语义为"本帧无输出"——编码器内部参考链未推进，
        /// 客户端未收到本帧无任何影响，下一帧照常编码即可。
        /// 调用方据此与真正的编码失败区分：跳帧不切换编码器、不计连续失败、
        /// 不触发全量重建尖峰（旧逻辑误判导致 ZRLE↔H264 频繁抖动切换）。
        /// </summary>
        public bool Skipped;

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
