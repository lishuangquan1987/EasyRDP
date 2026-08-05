using System;
using EasyRDP.Core.Protocol;
using Xunit;

namespace EasyRDP.Core.Tests.Protocol
{
    /// <summary>
    /// BlockHashDirtyRectDetector 单元测试。
    /// 验证 32×32 块级 CRC32 哈希检测的核心行为：
    /// 静态帧跳过、小变化跳过（≤阈值）、大变化编码、Commit/Reset 生命周期。
    /// </summary>
    public class BlockHashDirtyRectDetectorTests
    {
        // 128×128 帧：4×4=16 个 32×32 块，可测试阈值边界（0/1/4/5/16 块变化）
        private const int W = 128;
        private const int H = 128;
        private const int BlocksPerRow = W / BlockHashDirtyRectDetector.BlockSize; // 4
        private const int TotalBlocks = BlocksPerRow * (H / BlockHashDirtyRectDetector.BlockSize); // 16

        /// <summary>首次调用无参考帧时必须返回 ShouldEncode=true。</summary>
        [Fact]
        public void FirstDetect_NoReference_ShouldEncode()
        {
            var detector = new BlockHashDirtyRectDetector();
            var pixels = CreateSolidFrame(0x10);

            var result = detector.Detect(pixels, W, H);

            Assert.True(result.ShouldEncode);
        }

        /// <summary>Commit 后再次 Detect 完全相同的帧应返回 ShouldEncode=false（静态帧跳过）。</summary>
        [Fact]
        public void DetectAfterCommit_IdenticalFrame_ShouldSkip()
        {
            var detector = new BlockHashDirtyRectDetector();
            var pixels = CreateSolidFrame(0x20);

            detector.Detect(pixels, W, H);
            detector.Commit();

            var result = detector.Detect(pixels, W, H);
            Assert.False(result.ShouldEncode);
        }

        /// <summary>
        /// 默认阈值=0 时，1 块变化也应编码（任务栏时钟/托盘图标等真实小变化不能过滤）。
        /// 验证修复"桌面时钟在走但 FPS=0"的回归。
        /// </summary>
        [Fact]
        public void DefaultThresholdZero_OneBlockChanged_ShouldEncode()
        {
            var detector = new BlockHashDirtyRectDetector(); // 默认 skipThreshold=0
            var baseFrame = CreateSolidFrame(0x30);

            detector.Detect(baseFrame, W, H);
            detector.Commit();

            var changed = (byte[])baseFrame.Clone();
            ModifyBlock(changed, 0, 0); // 仅 1 块变化

            var result = detector.Detect(changed, W, H);
            Assert.True(result.ShouldEncode);
            Assert.Equal(1, result.ChangedBlockCount);
        }

        /// <summary>显式阈值=4 时，变化块数 = 阈值应跳过（验证阈值机制仍可用）。</summary>
        [Fact]
        public void ChangedBlocks_EqualsExplicitThreshold_ShouldSkip()
        {
            var detector = new BlockHashDirtyRectDetector(4);
            var baseFrame = CreateSolidFrame(0x30);

            detector.Detect(baseFrame, W, H);
            detector.Commit();

            var changed = (byte[])baseFrame.Clone();
            ModifyBlock(changed, 0, 0);
            ModifyBlock(changed, 1, 0);
            ModifyBlock(changed, 2, 0);
            ModifyBlock(changed, 3, 0);

            var result = detector.Detect(changed, W, H);
            Assert.False(result.ShouldEncode);
            Assert.Equal(4, result.ChangedBlockCount);
        }

        /// <summary>显式阈值=4 时，变化块数 = 阈值+1（5）应编码。</summary>
        [Fact]
        public void ChangedBlocks_ExceedsExplicitThreshold_ShouldEncode()
        {
            var detector = new BlockHashDirtyRectDetector(4);
            var baseFrame = CreateSolidFrame(0x40);

            detector.Detect(baseFrame, W, H);
            detector.Commit();

            var changed = (byte[])baseFrame.Clone();
            ModifyBlock(changed, 0, 0);
            ModifyBlock(changed, 1, 0);
            ModifyBlock(changed, 2, 0);
            ModifyBlock(changed, 3, 0);
            ModifyBlock(changed, 0, 1);

            var result = detector.Detect(changed, W, H);
            Assert.True(result.ShouldEncode);
            Assert.Equal(5, result.ChangedBlockCount);
        }

        /// <summary>显式阈值=4 时，变化块数 &lt; 阈值（1 块）应跳过。</summary>
        [Fact]
        public void ChangedBlocks_BelowExplicitThreshold_ShouldSkip()
        {
            var detector = new BlockHashDirtyRectDetector(4);
            var baseFrame = CreateSolidFrame(0x50);

            detector.Detect(baseFrame, W, H);
            detector.Commit();

            var changed = (byte[])baseFrame.Clone();
            ModifyBlock(changed, 0, 0); // 仅 1 块

            var result = detector.Detect(changed, W, H);
            Assert.False(result.ShouldEncode);
            Assert.Equal(1, result.ChangedBlockCount);
        }

        /// <summary>阈值为 0 时：仅完全无变化才跳过，1 块变化即编码。
        /// 验证 BlockHashDirtyRect 在 threshold=0 时行为等同 FullFrameMemcmp。</summary>
        [Fact]
        public void ThresholdZero_OneBlockChanged_ShouldEncode()
        {
            var detector = new BlockHashDirtyRectDetector(0);
            var baseFrame = CreateSolidFrame(0x60);

            detector.Detect(baseFrame, W, H);
            detector.Commit();

            var changed = (byte[])baseFrame.Clone();
            ModifyBlock(changed, 0, 0);

            var result = detector.Detect(changed, W, H);
            Assert.True(result.ShouldEncode);
        }

        /// <summary>未调用 Commit 时参考帧不更新（编码失败路径）。</summary>
        [Fact]
        public void DetectWithoutCommit_ReferenceUnchanged()
        {
            var detector = new BlockHashDirtyRectDetector();
            var frame1 = CreateSolidFrame(0x70);

            detector.Detect(frame1, W, H);
            detector.Commit();

            // Detect frame2（不 Commit）—— 模拟编码失败
            var frame2 = CreateSolidFrame(0x80);
            detector.Detect(frame2, W, H);

            // 第三次 Detect frame1 —— 参考帧仍为 frame1，完全相同 → skip
            var result = detector.Detect(frame1, W, H);
            Assert.False(result.ShouldEncode);
        }

        /// <summary>Reset 后下次 Detect 必须返回 ShouldEncode=true。</summary>
        [Fact]
        public void Reset_NextDetectShouldEncode()
        {
            var detector = new BlockHashDirtyRectDetector();

            detector.Detect(CreateSolidFrame(0x90), W, H);
            detector.Commit();
            detector.Reset();

            var result = detector.Detect(CreateSolidFrame(0x90), W, H);
            Assert.True(result.ShouldEncode);
        }

        /// <summary>分辨率变化后应返回 ShouldEncode=true（块网格不同）。</summary>
        [Fact]
        public void ResolutionChange_ShouldEncode()
        {
            var detector = new BlockHashDirtyRectDetector();

            detector.Detect(CreateSolidFrame(0xA0), W, H);
            detector.Commit();

            // 不同尺寸帧（块网格不同 → 必须编码）
            int w2 = 64, h2 = 64;
            var result = detector.Detect(CreateSolidFrame(0xA0), w2, h2);
            Assert.True(result.ShouldEncode);
        }

        /// <summary>TotalBlockCount 应正确反映帧的块网格大小。</summary>
        [Fact]
        public void TotalBlockCount_MatchesFrameGrid()
        {
            var detector = new BlockHashDirtyRectDetector();

            var result = detector.Detect(CreateSolidFrame(0xB0), W, H);

            Assert.Equal(TotalBlocks, result.TotalBlockCount);
        }

        /// <summary>Commit 前从未 Detect 过时 Commit 是空操作，不应抛异常。</summary>
        [Fact]
        public void Commit_WithoutDetect_IsNoop()
        {
            var detector = new BlockHashDirtyRectDetector();
            detector.Commit(); // 不应抛异常

            var result = detector.Detect(CreateSolidFrame(0xC0), W, H);
            Assert.True(result.ShouldEncode);
        }

        /// <summary>null 像素缓冲应抛 ArgumentNullException。</summary>
        [Fact]
        public void Detect_NullPixels_Throws()
        {
            var detector = new BlockHashDirtyRectDetector();
            Assert.Throws<ArgumentNullException>(() => detector.Detect(null, W, H));
        }

        // ====== 辅助方法 ======

        /// <summary>创建纯色 BGRA 帧（所有像素同色）。</summary>
        private static byte[] CreateSolidFrame(byte value)
        {
            var buf = new byte[W * H * 4];
            for (int i = 0; i < buf.Length; i++)
                buf[i] = value;
            return buf;
        }

        /// <summary>
        /// 修改指定块（bx,by）的第一个像素，使其与其他像素不同。
        /// 块大小为 BlockSize×BlockSize，块内第一个像素位于 (bx*32, by*32)。
        /// 修改首像素的 B 通道即可使该块的 CRC32 哈希变化。
        /// </summary>
        private static void ModifyBlock(byte[] pixels, int bx, int by)
        {
            int px = bx * BlockHashDirtyRectDetector.BlockSize;
            int py = by * BlockHashDirtyRectDetector.BlockSize;
            int offset = (py * W + px) * 4;
            // 翻转第一个字节（B 通道），保证哈希变化
            pixels[offset] = (byte)(pixels[offset] ^ 0xFF);
        }
    }
}
