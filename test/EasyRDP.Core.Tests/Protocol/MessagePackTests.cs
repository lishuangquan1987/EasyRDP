using EasyRDP.Core.Protocol;
using Xunit;

namespace EasyRDP.Core.Tests.Protocol
{
    public class MessagePackTests
    {
        [Fact]
        public void HandshakeReq_PackUnpack_ShouldBeEqual()
        {
            var original = new HandshakeReq
            {
                Version = 0x02,
                Capabilities = CodecCapabilities.H264Software | CodecCapabilities.H264Hardware,
                Username = "admin",
                Password = "secret123"
            };
            byte[] packed = original.Pack();
            var restored = HandshakeReq.Unpack(packed);

            Assert.Equal(original.Version, restored.Version);
            Assert.Equal(original.Capabilities, restored.Capabilities);
            Assert.Equal(original.Username, restored.Username);
            Assert.Equal(original.Password, restored.Password);
        }

        [Fact]
        public void HandshakeRes_PackUnpack_ShouldBeEqual()
        {
            var original = new HandshakeRes
            {
                Result = HandshakeResult.Success,
                Codec = CodecId.H264Software,
                ScreenWidth = 1920,
                ScreenHeight = 1080,
                ContentWidth = 1913,
                ContentHeight = 1160
            };
            byte[] packed = original.Pack();
            var restored = HandshakeRes.Unpack(packed);

            Assert.Equal(original.Result, restored.Result);
            Assert.Equal(original.Codec, restored.Codec);
            Assert.Equal(original.ScreenWidth, restored.ScreenWidth);
            Assert.Equal(original.ScreenHeight, restored.ScreenHeight);
            Assert.Equal(original.ContentWidth, restored.ContentWidth);
            Assert.Equal(original.ContentHeight, restored.ContentHeight);
        }

        [Fact]
        public void HandshakeRes_PayloadLength_ShouldBe18Bytes()
        {
            var res = new HandshakeRes
            {
                Result = HandshakeResult.AuthFailed,
                ScreenWidth = 0,
                ScreenHeight = 0
            };
            byte[] packed = res.Pack();
            Assert.Equal(18, packed.Length);
        }

        [Fact]
        public void InputEventMessage_PackUnpack_ShouldBeEqual()
        {
            var original = new InputEventMessage
            {
                Type = InputEventType.MouseMove,
                KeyCode = 0,
                X = 800,
                Y = 600,
                WheelDelta = 0
            };
            byte[] packed = original.Pack();
            var restored = InputEventMessage.Unpack(packed);

            Assert.Equal(original.Type, restored.Type);
            Assert.Equal(original.KeyCode, restored.KeyCode);
            Assert.Equal(original.X, restored.X);
            Assert.Equal(original.Y, restored.Y);
            Assert.Equal(original.WheelDelta, restored.WheelDelta);
        }

        [Fact]
        public void InputEventMessage_PayloadLength_ShouldBe17Bytes()
        {
            var msg = new InputEventMessage();
            byte[] packed = msg.Pack();
            Assert.Equal(17, packed.Length);
        }

        [Fact]
        public void CursorUpdateMessage_PackUnpack_ShouldBeEqual()
        {
            var original = new CursorUpdateMessage
            {
                Visible = true,
                X = 100,
                Y = 200,
                Width = 32,
                Height = 32,
                HotX = 5,
                HotY = 10,
                RgbaPixels = new byte[] { 0xFF, 0x00, 0x00, 0xFF }
            };
            byte[] packed = original.Pack();
            var restored = CursorUpdateMessage.Unpack(packed);

            Assert.Equal(original.Visible, restored.Visible);
            Assert.Equal(original.X, restored.X);
            Assert.Equal(original.Y, restored.Y);
            Assert.Equal(original.Width, restored.Width);
            Assert.Equal(original.Height, restored.Height);
            Assert.Equal(original.HotX, restored.HotX);
            Assert.Equal(original.HotY, restored.HotY);
            Assert.Equal(original.RgbaPixels, restored.RgbaPixels);
        }

        [Fact]
        public void CursorUpdateMessage_NullPixels_ShouldBeEqual()
        {
            var original = new CursorUpdateMessage
            {
                Visible = false,
                X = 50,
                Y = 60,
                RgbaPixels = null
            };
            byte[] packed = original.Pack();
            var restored = CursorUpdateMessage.Unpack(packed);

            Assert.False(restored.Visible);
            Assert.Equal(50, restored.X);
            Assert.Equal(60, restored.Y);
            Assert.Null(restored.RgbaPixels);
        }

        [Fact]
        public void VideoFrameMessage_PackUnpack_ShouldBeEqual()
        {
            var original = new VideoFrameMessage
            {
                Width = 1920,
                Height = 1080,
                IsKeyframe = true,
                SequenceNumber = 1234567890L,
                ContentWidth = 1913,
                ContentHeight = 1160,
                Data = new byte[] { 0x00, 0x00, 0x00, 0x01, 0x65 }
            };
            byte[] packed = original.Pack();
            var restored = VideoFrameMessage.Unpack(packed);

            Assert.Equal(original.Width, restored.Width);
            Assert.Equal(original.Height, restored.Height);
            Assert.Equal(original.IsKeyframe, restored.IsKeyframe);
            Assert.Equal(original.SequenceNumber, restored.SequenceNumber);
            Assert.Equal(original.ContentWidth, restored.ContentWidth);
            Assert.Equal(original.ContentHeight, restored.ContentHeight);
            Assert.Equal(original.Data, restored.Data);
        }
    }
}
