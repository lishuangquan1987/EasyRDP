using System;

namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 改进型帧变化检测器（路径 1）：32×32 块级哈希对比。
    ///
    /// 工作原理：
    /// 1. 将帧划分为 32×32 像素的网格（每块 4096 字节 BGRA）
    /// 2. 对每块计算 FNV-1a 哈希（4 字节一组，4 字节）
    /// 3. 与参考帧对应块的哈希对比
    /// 4. 0 块变化 → ShouldEncode=false（静态帧，同原始方式）
    /// 5. 变化块数 ≤ SkipThreshold → ShouldEncode=false（小变化跳过，节省 H.264 编码 150-250ms）
    /// 6. 变化块数 &gt; SkipThreshold → ShouldEncode=true（真正的内容更新）
    ///
    /// 性能特征：
    /// - 哈希用 FNV-1a 按 4 字节字处理（原逐字节 CRC32 查表在单核 XP 上 ~15-50ms/帧，
    ///   是跳过链和每帧热路径的主要开销），优化后 ~3-8ms/帧（960×582）。
    /// - 哈希仅用于变化检测（非校验用途），FNV 碰撞概率对屏幕数据可忽略。
    ///
    /// 内存占用：仅缓存哈希数组（~8KB / 1080p），不缓存整帧像素（~8.3MB）。
    /// </summary>
    /// <remarks>
    /// 线程安全：非线程安全，仅由 ServerStreamSession.EncodeLoop 单线程调用。
    /// </remarks>
    public sealed class BlockHashDirtyRectDetector : IFrameChangeDetector
    {
        /// <summary>
        /// 块边长（像素）。32×32 是 H.264 宏块(16×16)的整数倍，
        /// 平衡检测粒度与哈希计算开销。
        /// </summary>
        public const int BlockSize = 32;

        /// <summary>
        /// 默认小变化跳过阈值：变化块数 ≤ 此值时跳过编码。
        /// 设为 0 = 任何变化都编码（仅完全无变化才跳过）。
        ///
        /// 历史值 4 会把任务栏时钟（1-2 块）、托盘图标闪烁等"真实小变化"误判为噪声，
        /// 导致桌面看似静止但时钟在走时 FPS=0。RDP 场景下用户可见的任何变化都应编码，
        /// 因此默认改为 0。完全静止时由 ServerStreamSession.KeepaliveFrameInterval 保活。
        /// </summary>
        public const int DefaultSkipThreshold = 0;

        private readonly int _skipThreshold;
        // 参考帧的块哈希（行优先：blocksY × blocksX）。null 表示首次调用或 Reset 后
        private uint[] _prevHashes;
        private int _prevBlocksX;
        private int _prevBlocksY;
        // 临时缓存：Detect 时计算当前帧哈希，Commit 时提升为 _prevHashes
        private uint[] _pendingHashes;
        private int _pendingBlocksX;
        private int _pendingBlocksY;
        // FNV-1a 常量
        private const uint FnvOffsetBasis = 2166136261u;
        private const uint FnvPrime = 16777619u;

        /// <summary>构造指定跳过阈值的检测器。</summary>
        /// <param name="skipThreshold">变化块数 ≤ 此值时跳过编码。默认 4。</param>
        public BlockHashDirtyRectDetector(int skipThreshold)
        {
            if (skipThreshold < 0) skipThreshold = 0;
            _skipThreshold = skipThreshold;
        }

        /// <summary>使用默认阈值（DefaultSkipThreshold=4）构造。</summary>
        public BlockHashDirtyRectDetector() : this(DefaultSkipThreshold)
        {
        }

        /// <summary>
        /// 对比当前帧与参考帧的块哈希。返回是否应编码及变化块数。
        /// 副作用：计算当前帧的块哈希并缓存到 _pendingHashes，等待 Commit() 提升。
        /// </summary>
        public FrameChangeResult Detect(byte[] pixels, int width, int height)
        {
            if (pixels == null)
                throw new ArgumentNullException("pixels");
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException("width/height");

            int blocksX = (width + BlockSize - 1) / BlockSize;
            int blocksY = (height + BlockSize - 1) / BlockSize;
            int totalBlocks = blocksX * blocksY;

            // 计算当前帧的块哈希（缓存到 pending，供 Commit 提升）
            if (_pendingHashes == null || _pendingHashes.Length != totalBlocks)
                _pendingHashes = new uint[totalBlocks];
            ComputeBlockHashes(pixels, width, height, blocksX, blocksY, _pendingHashes);
            _pendingBlocksX = blocksX;
            _pendingBlocksY = blocksY;

            // 首次调用或 Reset 后无基准 → 必须编码
            if (_prevHashes == null
                || _prevBlocksX != blocksX
                || _prevBlocksY != blocksY)
            {
                return FrameChangeResult.Changed(totalBlocks, totalBlocks);
            }

            int changedBlocks = 0;
            for (int i = 0; i < totalBlocks; i++)
            {
                if (_pendingHashes[i] != _prevHashes[i])
                    changedBlocks++;
            }

            // 决策：变化块数 ≤ 阈值时跳过编码
            if (changedBlocks == 0)
                return FrameChangeResult.Unchanged(totalBlocks);

            if (changedBlocks <= _skipThreshold)
            {
                // 小变化跳过编码（节省 H.264 150-250ms），但仍记录实际变化块数供日志诊断和阈值调优
                return new FrameChangeResult
                {
                    ShouldEncode = false,
                    ChangedBlockCount = changedBlocks,
                    TotalBlockCount = totalBlocks
                };
            }

            return FrameChangeResult.Changed(changedBlocks, totalBlocks);
        }

        /// <summary>
        /// 将 _pendingHashes 提升为 _prevHashes。
        /// 编码成功后调用；编码失败时禁止调用（保持参考帧为上次成功帧）。
        /// </summary>
        public void Commit()
        {
            if (_pendingHashes == null)
                return; // Detect 从未被调用，空操作

            // 交换 pending 和 prev：避免数组分配
            var tmp = _prevHashes;
            _prevHashes = _pendingHashes;
            _prevBlocksX = _pendingBlocksX;
            _prevBlocksY = _pendingBlocksY;
            _pendingHashes = tmp;
        }

        /// <summary>清空内部缓存，下次 Detect 必然返回 ShouldEncode=true。</summary>
        public void Reset()
        {
            _prevHashes = null;
            _prevBlocksX = 0;
            _prevBlocksY = 0;
            _pendingHashes = null;
            _pendingBlocksX = 0;
            _pendingBlocksY = 0;
        }

        /// <summary>
        /// 计算帧的每块 FNV-1a 哈希（4 字节一组处理，弱机单核比逐字节 CRC32 查表快 4~8 倍）。
        /// 块大小为 BlockSize×BlockSize，按行优先填充 curHashes[blocksY × blocksX]。
        /// 边缘块不足 BlockSize 时按实际像素计算。
        /// </summary>
        private void ComputeBlockHashes(byte[] pixels, int width, int height,
            int blocksX, int blocksY, uint[] curHashes)
        {
            int stride = width * 4;

            int hashIdx = 0;
            for (int by = 0; by < blocksY; by++)
            {
                int y0 = by * BlockSize;
                int yEnd = y0 + BlockSize;
                if (yEnd > height) yEnd = height;

                for (int bx = 0; bx < blocksX; bx++)
                {
                    int x0 = bx * BlockSize;
                    int xEnd = x0 + BlockSize;
                    if (xEnd > width) xEnd = width;

                    uint h = FnvOffsetBasis;
                    for (int y = y0; y < yEnd; y++)
                    {
                        int off = y * stride + x0 * 4;
                        int end = y * stride + xEnd * 4;
                        // 4 字节一组（哈希用途，字节序无关）
                        while (off + 4 <= end)
                        {
                            uint word = (uint)(pixels[off]
                                | (pixels[off + 1] << 8)
                                | (pixels[off + 2] << 16)
                                | (pixels[off + 3] << 24));
                            h = (h ^ word) * FnvPrime;
                            off += 4;
                        }
                        while (off < end)
                        {
                            h = (h ^ pixels[off]) * FnvPrime;
                            off++;
                        }
                    }
                    curHashes[hashIdx++] = h;
                }
            }
        }
    }
}
