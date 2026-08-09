using EasyRDP.Core.Protocol;
using Xunit;

namespace EasyRDP.Core.Tests.Protocol
{
    public class DiagnosticInfoMessageTests
    {
        [Fact]
        public void PackUnpack_RoundTrip_ShouldPreserveAllFields()
        {
            var msg = new DiagnosticInfoMessage
            {
                CpuName = "Intel(R) Core(TM) i7-1360P",
                CpuCores = 16,
                GpuName = "Intel(R) Iris(R) Xe Graphics",
                TotalMemoryMb = 32512,
                OsVersion = "Microsoft Windows 10 Pro",
                CaptureMethod = 1,       // DXGI
                ScaleFactorX100 = 150,   // 150%
                ScreenWidth = 2240,
                ScreenHeight = 1400,
                H264Available = 1,
                ZrleAvailable = 1,
                Vp8Available = 0
            };

            byte[] payload = msg.Pack();
            var back = DiagnosticInfoMessage.Unpack(payload);

            Assert.Equal(msg.CpuName, back.CpuName);
            Assert.Equal(msg.CpuCores, back.CpuCores);
            Assert.Equal(msg.GpuName, back.GpuName);
            Assert.Equal(msg.TotalMemoryMb, back.TotalMemoryMb);
            Assert.Equal(msg.OsVersion, back.OsVersion);
            Assert.Equal(msg.CaptureMethod, back.CaptureMethod);
            Assert.Equal(msg.ScaleFactorX100, back.ScaleFactorX100);
            Assert.Equal(msg.ScreenWidth, back.ScreenWidth);
            Assert.Equal(msg.ScreenHeight, back.ScreenHeight);
            Assert.Equal(msg.H264Available, back.H264Available);
            Assert.Equal(msg.ZrleAvailable, back.ZrleAvailable);
            Assert.Equal(msg.Vp8Available, back.Vp8Available);
        }

        [Fact]
        public void PackUnpack_EmptyStrings_ShouldRoundTrip()
        {
            // 空字符串/未知字段不应破坏序列化
            var msg = new DiagnosticInfoMessage
            {
                CpuName = "",
                CpuCores = 0,
                GpuName = null,
                TotalMemoryMb = 0,
                OsVersion = "",
                CaptureMethod = 0,
                ScaleFactorX100 = 100,
                ScreenWidth = 1920,
                ScreenHeight = 1080,
                H264Available = 0,
                ZrleAvailable = 1,
                Vp8Available = 0
            };

            byte[] payload = msg.Pack();
            var back = DiagnosticInfoMessage.Unpack(payload);

            Assert.Equal("", back.CpuName);
            Assert.Equal("", back.GpuName);
            Assert.Equal("", back.OsVersion);
            Assert.Equal(100, back.ScaleFactorX100);
            Assert.Equal(1, back.ZrleAvailable);
        }

        [Fact]
        public void Unpack_TooShortPayload_ShouldThrow()
        {
            Assert.Throws<System.ArgumentException>(
                () => DiagnosticInfoMessage.Unpack(new byte[] { 0x01, 0x02 }));
        }
    }
}
