using EasyRDP.Core.Protocol;
using Xunit;

namespace EasyRDP.Core.Tests.Protocol
{
    public class FramingTests
    {
        [Fact]
        public void BuildMessage_RoundTrip_ShouldPreservePayload()
        {
            byte[] payload = new byte[] { 1, 2, 3, 4, 5 };
            byte[] wire = Framing.BuildMessage((byte)MessageType.InputEvent, payload);

            byte type;
            byte[] parsed;
            Assert.True(Framing.TryParse(wire, out type, out parsed));
            Assert.Equal((byte)MessageType.InputEvent, type);
            Assert.Equal(payload, parsed);
        }

        [Fact]
        public void BuildMessage_EmptyPayload_ShouldProduceZeroLength()
        {
            byte[] wire = Framing.BuildMessage((byte)MessageType.Keepalive, new byte[0]);
            Assert.Equal(Framing.HeaderSize, wire.Length);
            Assert.Equal(Constants.FrameMagic, wire[0]);
            Assert.Equal((byte)MessageType.Keepalive, wire[1]);
            // PayloadLen = 0（4 字节小端）
            Assert.Equal(0, wire[2]);
            Assert.Equal(0, wire[3]);
            Assert.Equal(0, wire[4]);
            Assert.Equal(0, wire[5]);

            byte type;
            byte[] parsed;
            Assert.True(Framing.TryParse(wire, out type, out parsed));
            Assert.Empty(parsed);
        }

        [Fact]
        public void BuildMessage_NullPayload_ShouldProduceEmptyMessage()
        {
            byte[] wire = Framing.BuildMessage((byte)MessageType.Keepalive, null);
            Assert.Equal(Framing.HeaderSize, wire.Length);
            byte type;
            byte[] parsed;
            Assert.True(Framing.TryParse(wire, out type, out parsed));
            Assert.Empty(parsed);
        }

        [Fact]
        public void TryParse_UnknownType_ShouldFail()
        {
            byte[] wire = Framing.BuildMessage(0x7F, new byte[] { 1 }); // 0x7F 不是已知类型
            byte type;
            byte[] parsed;
            Assert.False(Framing.TryParse(wire, out type, out parsed));
        }

        [Fact]
        public void TryParse_BadMagic_ShouldFail()
        {
            byte[] wire = Framing.BuildMessage((byte)MessageType.Keepalive, new byte[0]);
            wire[0] = 0x00;
            byte type;
            byte[] parsed;
            Assert.False(Framing.TryParse(wire, out type, out parsed));
        }

        [Fact]
        public void TryParse_Null_ShouldFail()
        {
            byte type;
            byte[] parsed;
            Assert.False(Framing.TryParse(null, out type, out parsed));
        }

        [Fact]
        public void BuildMessage_PayloadLen_IsLittleEndian()
        {
            byte[] payload = new byte[300]; // 300 = 0x12C
            byte[] wire = Framing.BuildMessage((byte)MessageType.VideoFrame, payload);
            Assert.Equal(0x2C, wire[2]);
            Assert.Equal(0x01, wire[3]);
            Assert.Equal(0, wire[4]);
            Assert.Equal(0, wire[5]);
        }

        [Fact]
        public void TryParse_TruncatedMessage_ShouldFail()
        {
            byte[] wire = Framing.BuildMessage((byte)MessageType.InputEvent, new byte[] { 1, 2, 3, 4 });
            byte[] truncated = new byte[wire.Length - 2];
            System.Buffer.BlockCopy(wire, 0, truncated, 0, truncated.Length);

            byte type;
            byte[] parsed;
            Assert.False(Framing.TryParse(truncated, out type, out parsed));
        }
    }
}
