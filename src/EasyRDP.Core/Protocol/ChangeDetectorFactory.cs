namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 帧变化检测器工厂。按 ChangeDetectionMode 创建 IFrameChangeDetector 实例。
    /// 与 EncoderFactory 对称：EncoderFactory 选择编码器，ChangeDetectorFactory 选择检测策略。
    /// 由 TransportHost 在会话建立时调用，注入到 ServerStreamSession。
    /// </summary>
    public static class ChangeDetectorFactory
    {
        /// <summary>
        /// 创建指定模式的检测器。返回 null 表示模式无效（不应发生，枚举已约束）。
        /// </summary>
        public static IFrameChangeDetector Create(ChangeDetectionMode mode)
        {
            switch (mode)
            {
                case ChangeDetectionMode.FullFrameMemcmp:
                    return new FullFrameChangeDetector();
                case ChangeDetectionMode.BlockHashDirtyRect:
                    return new BlockHashDirtyRectDetector();
                default:
                    // 未知枚举值回退到原始方式（保守：宁可慢不能错）
                    return new FullFrameChangeDetector();
            }
        }
    }
}
