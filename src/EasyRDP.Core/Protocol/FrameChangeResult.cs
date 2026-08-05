namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 帧变化检测结果。由 IFrameChangeDetector.Detect 返回，
    /// 指导 ServerStreamSession 是否编码、是否强制关键帧。
    /// </summary>
    public struct FrameChangeResult
    {
        /// <summary>
        /// True 表示当前帧相对上一帧发生了变化，应该送入编码器。
        /// False 表示完全无变化，跳过编码并累加 _framesSkipped。
        /// </summary>
        public bool ShouldEncode;

        /// <summary>
        /// 变化的 32×32 块数量（仅 BlockHashDirtyRect 模式有意义；FullFrameMemcmp 模式恒为 0 或 TotalBlockCount）。
        /// 用于日志诊断和未来 ROI/QP 调整策略。
        /// </summary>
        public int ChangedBlockCount;

        /// <summary>
        /// 总块数（= ceil(width/32) × ceil(height/32)）。
        /// 与 ChangedBlockCount 配合可计算变化比例。
        /// </summary>
        public int TotalBlockCount;

        /// <summary>静态帧结果工厂：ShouldEncode=false，块计数为 0。</summary>
        public static FrameChangeResult Unchanged(int totalBlockCount)
        {
            return new FrameChangeResult
            {
                ShouldEncode = false,
                ChangedBlockCount = 0,
                TotalBlockCount = totalBlockCount
            };
        }

        /// <summary>变化帧结果工厂：ShouldEncode=true，块计数由调用方提供。</summary>
        public static FrameChangeResult Changed(int changedBlockCount, int totalBlockCount)
        {
            return new FrameChangeResult
            {
                ShouldEncode = true,
                ChangedBlockCount = changedBlockCount,
                TotalBlockCount = totalBlockCount
            };
        }
    }
}
