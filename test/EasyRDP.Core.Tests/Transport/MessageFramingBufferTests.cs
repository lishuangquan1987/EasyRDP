using System.Collections.Generic;
using EasyRDP.Core.Protocol;
using EasyRDP.Core.Transport;
using Xunit;

namespace EasyRDP.Core.Tests.Transport
{
    public class MessageFramingBufferTests
    {
        [Fact]
        public void SingleMessage_ShouldBeExtracted()
        {
            var buf = new MessageFramingBuffer();
            var received = new List<byte[]>();
            buf.MessageReady += m => received.Add(m);

            byte[] msg = Framing.BuildMessage((byte)MessageType.Keepalive, new byte[] { 1, 2, 3 });
            buf.Feed(msg, 0, msg.Length);

            Assert.Single(received);
            Assert.Equal(msg, received[0]);
        }

        [Fact]
        public void FragmentedAcrossFeeds_ShouldBeReassembled()
        {
            var buf = new MessageFramingBuffer();
            var received = new List<byte[]>();
            buf.MessageReady += m => received.Add(m);

            byte[] msg = Framing.BuildMessage((byte)MessageType.InputEvent, new byte[] { 9, 8, 7, 6, 5 });
            for (int i = 0; i < msg.Length; i++)
                buf.Feed(msg, i, 1); // 逐字节喂入

            Assert.Single(received);
            Assert.Equal(msg, received[0]);
        }

        [Fact]
        public void MultipleMessagesInOneFeed_ShouldAllBeExtracted()
        {
            var buf = new MessageFramingBuffer();
            var received = new List<byte[]>();
            buf.MessageReady += m => received.Add(m);

            byte[] m1 = Framing.BuildMessage((byte)MessageType.Keepalive, new byte[] { 1 });
            byte[] m2 = Framing.BuildMessage((byte)MessageType.InputEvent, new byte[] { 2, 3 });
            byte[] combined = new byte[m1.Length + m2.Length];
            System.Buffer.BlockCopy(m1, 0, combined, 0, m1.Length);
            System.Buffer.BlockCopy(m2, 0, combined, m1.Length, m2.Length);

            buf.Feed(combined, 0, combined.Length);

            Assert.Equal(2, received.Count);
            Assert.Equal(m1, received[0]);
            Assert.Equal(m2, received[1]);
        }

        [Fact]
        public void LeadingGarbage_ShouldResync()
        {
            var buf = new MessageFramingBuffer();
            var received = new List<byte[]>();
            buf.MessageReady += m => received.Add(m);

            byte[] msg = Framing.BuildMessage((byte)MessageType.CursorUpdate, new byte[] { 0xAA });
            byte[] withGarbage = new byte[3 + msg.Length];
            withGarbage[0] = 0x11;
            withGarbage[1] = 0x22;
            withGarbage[2] = 0x33;
            System.Buffer.BlockCopy(msg, 0, withGarbage, 3, msg.Length);

            buf.Feed(withGarbage, 0, withGarbage.Length);

            Assert.Single(received);
            Assert.Equal(msg, received[0]);
        }

        [Fact]
        public void TrailingMagicByte_ShouldBeRetainedAcrossFeed()
        {
            var buf = new MessageFramingBuffer();
            var received = new List<byte[]>();
            buf.MessageReady += m => received.Add(m);

            byte[] msg = Framing.BuildMessage((byte)MessageType.Keepalive, new byte[] { 7 });
            // 先喂入垃圾 + 末尾 0xE5（可能是下一帧 magic 起始被 TCP 切分）
            byte[] junk = new byte[] { 0x00, 0x01, Constants.FrameMagic };
            buf.Feed(junk, 0, junk.Length);
            Assert.Empty(received);

            // 再喂入真正的消息（以 0xE5 开头），应能拼出完整消息
            buf.Feed(msg, 0, msg.Length);
            Assert.Single(received);
            Assert.Equal(msg, received[0]);
        }

        [Fact]
        public void OversizedPayload_ShouldBeRejectedAndResync()
        {
            var buf = new MessageFramingBuffer();
            var received = new List<byte[]>();
            buf.MessageReady += m => received.Add(m);

            // 手工构造一个 PayloadLen 超限的假帧头
            byte[] bogus = new byte[Framing.HeaderSize];
            bogus[0] = Constants.FrameMagic;
            bogus[1] = (byte)MessageType.VideoFrame;
            uint huge = (uint)Constants.MaxSafePayloadSize + 1;
            bogus[2] = (byte)(huge & 0xFF);
            bogus[3] = (byte)((huge >> 8) & 0xFF);
            bogus[4] = (byte)((huge >> 16) & 0xFF);
            bogus[5] = (byte)((huge >> 24) & 0xFF);

            // 超限帧头后紧跟一条合法消息
            byte[] good = Framing.BuildMessage((byte)MessageType.Keepalive, new byte[] { 5 });
            byte[] combined = new byte[bogus.Length + good.Length];
            System.Buffer.BlockCopy(bogus, 0, combined, 0, bogus.Length);
            System.Buffer.BlockCopy(good, 0, combined, bogus.Length, good.Length);

            buf.Feed(combined, 0, combined.Length);

            // 超限帧被拒绝，合法消息仍能被提取
            Assert.Single(received);
            Assert.Equal(good, received[0]);
        }
    }
}
