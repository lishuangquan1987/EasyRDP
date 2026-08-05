using System;
using EasyRDP.Core.Protocol;
using Xunit;

namespace EasyRDP.Core.Tests.Protocol
{
    /// <summary>
    /// FullFrameChangeDetector 单元测试。
    /// 验证原始 memcmp 检测逻辑通过 IFrameChangeDetector 抽象后的行为正确性，
    /// 特别是 Detect/Commit/Reset 生命周期约定。
    /// </summary>
    public class FullFrameChangeDetectorTests
    {
        // 64×64 BGRA 帧：4×4=16 个 32×32 块，足够测试全帧比较语义
        private const int W = 64;
        private const int H = 64;
        private const int BgraLen = W * H * 4;

        /// <summary>首次调用无参考帧时必须返回 ShouldEncode=true。</summary>
        [Fact]
        public void FirstDetect_NoReference_ShouldEncode()
        {
            var detector = new FullFrameChangeDetector();
            var pixels = CreateSolidFrame(0x11);

            var result = detector.Detect(pixels, W, H);

            Assert.True(result.ShouldEncode);
        }

        /// <summary>Commit 后再次 Detect 完全相同的帧应返回 ShouldEncode=false（静态帧跳过）。</summary>
        [Fact]
        public void DetectAfterCommit_IdenticalFrame_ShouldSkip()
        {
            var detector = new FullFrameChangeDetector();
            var pixels = CreateSolidFrame(0x22);

            detector.Detect(pixels, W, H);
            detector.Commit();

            var result = detector.Detect(pixels, W, H);
            Assert.False(result.ShouldEncode);
        }

        /// <summary>Commit 后检测到不同帧应返回 ShouldEncode=true。</summary>
        [Fact]
        public void DetectAfterCommit_DifferentFrame_ShouldEncode()
        {
            var detector = new FullFrameChangeDetector();

            detector.Detect(CreateSolidFrame(0x33), W, H);
            detector.Commit();

            var result = detector.Detect(CreateSolidFrame(0x44), W, H);
            Assert.True(result.ShouldEncode);
        }

        /// <summary>未调用 Commit 时参考帧不更新：第二帧与第一帧不同但因参考帧为空仍应编码。
        /// 实际场景中编码失败不 Commit，下次比对基准仍是上次成功编码的帧。</summary>
        [Fact]
        public void DetectWithoutCommit_ReferenceUnchanged()
        {
            var detector = new FullFrameChangeDetector();
            var frame1 = CreateSolidFrame(0x55);

            // 首次 Detect + Commit 建立参考帧
            detector.Detect(frame1, W, H);
            detector.Commit();

            // 第二次 Detect（不 Commit）—— 与参考帧不同
            var frame2 = CreateSolidFrame(0x66);
            var result2 = detector.Detect(frame2, W, H);
            Assert.True(result2.ShouldEncode);

            // 第三次 Detect —— 未 Commit，参考帧仍为 frame1，frame1 与 frame1 相同 → skip
            var result3 = detector.Detect(frame1, W, H);
            Assert.False(result3.ShouldEncode);
        }

        /// <summary>Reset 后下次 Detect 必须返回 ShouldEncode=true（参考帧已清空）。</summary>
        [Fact]
        public void Reset_NextDetectShouldEncode()
        {
            var detector = new FullFrameChangeDetector();

            detector.Detect(CreateSolidFrame(0x77), W, H);
            detector.Commit();
            detector.Reset();

            var result = detector.Detect(CreateSolidFrame(0x77), W, H);
            Assert.True(result.ShouldEncode);
        }

        /// <summary>分辨率变化（尺寸不同）后应返回 ShouldEncode=true。</summary>
        [Fact]
        public void ResolutionChange_ShouldEncode()
        {
            var detector = new FullFrameChangeDetector();

            detector.Detect(CreateSolidFrame(0x88), W, H);
            detector.Commit();

            // 不同尺寸帧
            int w2 = 32, h2 = 32;
            var result = detector.Detect(CreateSolidFrame(0x88), w2, h2);
            Assert.True(result.ShouldEncode);
        }

        /// <summary>Commit 前从未 Detect 过时 Commit 是空操作，不应抛异常。</summary>
        [Fact]
        public void Commit_WithoutDetect_IsNoop()
        {
            var detector = new FullFrameChangeDetector();
            // 不应抛异常
            detector.Commit();

            var result = detector.Detect(CreateSolidFrame(0x99), W, H);
            Assert.True(result.ShouldEncode);
        }

        /// <summary>null 像素缓冲应抛 ArgumentNullException。</summary>
        [Fact]
        public void Detect_NullPixels_Throws()
        {
            var detector = new FullFrameChangeDetector();
            Assert.Throws<ArgumentNullException>(() => detector.Detect(null, W, H));
        }

        /// <summary>创建纯色 BGRA 帧（所有像素同色）。</summary>
        /// <param name="value">每通道的字节值（B=G=R=A=value）。</param>
        private static byte[] CreateSolidFrame(byte value)
        {
            var buf = new byte[BgraLen];
            for (int i = 0; i < BgraLen; i++)
                buf[i] = value;
            return buf;
        }
    }
}
