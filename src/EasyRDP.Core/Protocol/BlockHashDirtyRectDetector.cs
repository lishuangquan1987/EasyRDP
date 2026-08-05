using System;

namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 改进型帧变化检测器（路径 1）：32×32 块级 CRC32 哈希对比。
    ///
    /// 工作原理：
    /// 1. 将帧划分为 32×32 像素的网格（每块 4096 字节 BGRA）
    /// 2. 对每块计算 CRC32 哈希（4 字节）
    /// 3. 与参考帧对应块的哈希对比
    /// 4. 0 块变化 → ShouldEncode=false（静态帧，同原始方式）
    /// 5. 变化块数 ≤ SkipThreshold → ShouldEncode=false（小变化跳过，节省 H.264 编码 150-250ms）
    /// 6. 变化块数 &gt; SkipThreshold → ShouldEncode=true（真正的内容更新）
    ///
    /// 性能特征：
    /// - 静态帧：CRC32 全帧哈希 ~3-5ms（1080p），比 memcmp ~1ms 慢但可接受
    /// - 完全变化：CRC32 全帧哈希 ~3-5ms，比 memcmp 即时返回慢
    /// - 小局部变化（&lt; 阈值）：跳过 150-250ms H.264 编码，净节省显著
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
        // CRC32 查找表（懒初始化，所有实例共享）
        private static uint[] _crcTable;

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
        /// 计算帧的每块 CRC32 哈希。
        /// 块大小为 BlockSize×BlockSize，按行优先填充 curHashes[blocksY × blocksX]。
        /// 边缘块不足 BlockSize 时按实际像素计算。
        /// </summary>
        private void ComputeBlockHashes(byte[] pixels, int width, int height,
            int blocksX, int blocksY, uint[] curHashes)
        {
            int stride = width * 4;
            EnsureCrcTable();
            uint[] table = _crcTable;

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

                    uint crc = 0xFFFFFFFF;
                    for (int y = y0; y < yEnd; y++)
                    {
                        int rowOffset = y * stride + x0 * 4;
                        int rowBytes = (xEnd - x0) * 4;
                        // 行内逐字节 CRC32：BGRA 数据按字节流处理
                        for (int i = 0; i < rowBytes; i++)
                        {
                            byte b = pixels[rowOffset + i];
                            crc = (crc >> 8) ^ table[(crc ^ b) & 0xFF];
                        }
                    }
                    curHashes[hashIdx++] = crc ^ 0xFFFFFFFF;
                }
            }
        }

        /// <summary>初始化 CRC32 查找表（多项式 0xEDB88320）。所有实例共享。</summary>
        private static void EnsureCrcTable()
        {
            if (_crcTable != null) return;
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                {
                    if ((c & 1) != 0)
                        c = (c >> 1) ^ 0xEDB88320u;
                    else
                        c = c >> 1;
                }
                table[i] = c;
            }
            _crcTable = table;
        }
    }
}
