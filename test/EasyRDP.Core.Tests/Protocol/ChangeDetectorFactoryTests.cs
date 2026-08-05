using EasyRDP.Core.Protocol;
using Xunit;

namespace EasyRDP.Core.Tests.Protocol
{
    /// <summary>
    /// ChangeDetectorFactory 单元测试。
    /// 验证按 ChangeDetectionMode 创建正确的 IFrameChangeDetector 实现类型，
    /// 以及未知枚举值回退到 FullFrameChangeDetector 的保守行为。
    /// </summary>
    public class ChangeDetectorFactoryTests
    {
        /// <summary>FullFrameMemcmp 模式应创建 FullFrameChangeDetector 实例。</summary>
        [Fact]
        public void Create_FullFrameMemcmp_ReturnsFullFrameChangeDetector()
        {
            var detector = ChangeDetectorFactory.Create(ChangeDetectionMode.FullFrameMemcmp);
            Assert.IsType<FullFrameChangeDetector>(detector);
        }

        /// <summary>BlockHashDirtyRect 模式应创建 BlockHashDirtyRectDetector 实例。</summary>
        [Fact]
        public void Create_BlockHashDirtyRect_ReturnsBlockHashDirtyRectDetector()
        {
            var detector = ChangeDetectorFactory.Create(ChangeDetectionMode.BlockHashDirtyRect);
            Assert.IsType<BlockHashDirtyRectDetector>(detector);
        }

        /// <summary>未知枚举值应回退到 FullFrameChangeDetector（保守策略：宁可慢不能错）。</summary>
        [Fact]
        public void Create_UnknownMode_FallsBackToFullFrame()
        {
            var detector = ChangeDetectorFactory.Create((ChangeDetectionMode)999);
            Assert.IsType<FullFrameChangeDetector>(detector);
        }

        /// <summary>工厂创建的实例不应为 null。</summary>
        [Fact]
        public void Create_NeverReturnsNull()
        {
            Assert.NotNull(ChangeDetectorFactory.Create(ChangeDetectionMode.FullFrameMemcmp));
            Assert.NotNull(ChangeDetectorFactory.Create(ChangeDetectionMode.BlockHashDirtyRect));
        }
    }
}
