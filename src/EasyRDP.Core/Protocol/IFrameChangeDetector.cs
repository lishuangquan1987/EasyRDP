namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 帧变化检测器抽象。在 ServerStreamSession.EncodeLoop 中调用，
    /// 决定当前 BGRA 帧是否需要送入 H.264 编码器。
    /// 实现类：FullFrameChangeDetector（原始 memcmp）/ BlockHashDirtyRectDetector（块哈希）。
    /// 通过 ChangeDetectorFactory 按 ChangeDetectionMode 创建，会话启动时注入。
    /// </summary>
    /// <remarks>
    /// 生命周期约定（与 ServerStreamSession 原始 _prevBgra 逻辑保持一致）：
    /// 1. Detect(pixels) — 比较当前帧与内部参考帧，返回是否应编码（不修改参考帧）
    /// 2. 若 ShouldEncode=true，调用方尝试编码
    /// 3. 编码成功后调用 Commit() — 将当前帧提升为新的参考帧
    /// 4. 编码失败时不调用 Commit — 参考帧保持为上次成功编码的帧
    /// 这样保证：下次比对基准永远是"客户端已收到的最后一帧"。
    /// </remarks>
    public interface IFrameChangeDetector
    {
        /// <summary>
        /// 对比当前帧与上一帧（参考帧），返回变化信息。不修改内部参考帧状态。
        /// 调用方负责保证传入像素缓冲的尺寸与 width/height 一致（BGRA32 = width*height*4 字节）。
        /// 内部会缓存本次 Detect 的计算结果（哈希或像素拷贝），供 Commit() 提升为参考帧。
        /// </summary>
        /// <param name="pixels">BGRA32 像素缓冲。调用方保证在 Detect 返回前不会被外部修改。</param>
        /// <param name="width">帧宽（像素）。</param>
        /// <param name="height">帧高（像素）。</param>
        FrameChangeResult Detect(byte[] pixels, int width, int height);

        /// <summary>
        /// 将最近一次 Detect 的结果提升为新的参考帧。
        /// 调用时机：编码成功后。编码失败时禁止调用（保持参考帧为上次成功帧）。
        /// 若 Detect 从未被调用过，调用 Commit 是空操作。
        /// </summary>
        void Commit();

        /// <summary>
        /// 重置内部状态（清空参考帧和临时缓存）。
        /// 调用时机：会话 Stop、分辨率变更、编码器 Reset 之后。
        /// 重置后下一次 Detect 必然返回 ShouldEncode=true（无基准帧可比）。
        /// </summary>
        void Reset();
    }
}
