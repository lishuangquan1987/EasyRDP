using EasyRDP.Core.Protocol;
using EasyRDP.Core.Transport;
using Xunit;

namespace EasyRDP.Core.Tests.Transport
{
    public class MessageReassemblerTests
    {
        [Fact]
        public void Crc16_Deterministic_SameInput_ShouldReturnSameOutput()
        {
            byte[] data = new byte[] { 0x01, 0x02, 0x03, 0x04, 0xFF };
            ushort crc1 = MessageReassembler.ComputeCrc16(data, 0, data.Length);
            ushort crc2 = MessageReassembler.ComputeCrc16(data, 0, data.Length);
            Assert.Equal(crc1, crc2);
        }

        [Fact]
        public void Crc16_DifferentInput_ShouldReturnDifferentOutput()
        {
            byte[] d1 = new byte[] { 0x01, 0x02, 0x03 };
            byte[] d2 = new byte[] { 0x01, 0x02, 0x04 };
            ushort crc1 = MessageReassembler.ComputeCrc16(d1, 0, d1.Length);
            ushort crc2 = MessageReassembler.ComputeCrc16(d2, 0, d2.Length);
            Assert.NotEqual(crc1, crc2);
        }

        [Fact]
        public void Crc16_EmptyInput_ShouldBeZero()
        {
            ushort crc = MessageReassembler.ComputeCrc16(new byte[0], 0, 0);
            Assert.Equal((ushort)0, crc);
        }

        [Fact]
        public void FragAndSend_SingleFragment_RoundTrip_ShouldDeliverCompleteMessage()
        {
            var payload = new byte[] { 0x10, 0x20, 0x30, 0x40 };
            var sentFragments = new System.Collections.Generic.List<byte[]>();

            MessageReassembler.FragAndSend(1, (byte)MessageType.Keepalive, payload,
                (sid, data) => sentFragments.Add(data), 42);

            Assert.Single(sentFragments);

            // Feed into reassembler
            var reassembler = new MessageReassembler();
            MessageReceivedEventArgs received = null;
            reassembler.MessageReceived += (s, e) => received = e;

            reassembler.OnFragment(new FragmentReceivedEventArgs(42, sentFragments[0]));

            Assert.NotNull(received);
            Assert.Equal((uint)42, received.SessionId);
            Assert.Equal((byte)MessageType.Keepalive, received.MessageType);
            Assert.Equal(payload, received.Data);
        }

        [Fact]
        public void FragAndSend_MultiFragment_RoundTrip()
        {
            // Create a payload larger than FragmentSize
            int payloadSize = Constants.FragmentSize * 3 + 100;
            var payload = new byte[payloadSize];
            for (int i = 0; i < payloadSize; i++)
                payload[i] = (byte)(i % 256);

            var sentFragments = new System.Collections.Generic.List<byte[]>();
            MessageReassembler.FragAndSend(7, (byte)MessageType.VideoFrame, payload,
                (sid, data) => sentFragments.Add(data), 1);

            Assert.True(sentFragments.Count >= 4); // 3 full + 1 partial

            // Feed all fragments into reassembler
            var reassembler = new MessageReassembler();
            MessageReceivedEventArgs received = null;
            reassembler.MessageReceived += (s, e) => received = e;

            foreach (var frag in sentFragments)
            {
                reassembler.OnFragment(new FragmentReceivedEventArgs(1, frag));
            }

            Assert.NotNull(received);
            Assert.Equal((byte)MessageType.VideoFrame, received.MessageType);
            Assert.Equal(payload.Length, received.Data.Length);
            Assert.Equal(payload, received.Data);
        }

        [Fact]
        public void OnFragment_NewerFrame_DiscardsOldFrame()
        {
            var payload1 = new byte[] { 0xAA, 0xBB };
            var payload2 = new byte[] { 0xCC, 0xDD, 0xEE };

            var sentFrags1 = new System.Collections.Generic.List<byte[]>();
            MessageReassembler.FragAndSend(1, (byte)MessageType.InputEvent, payload1,
                (sid, data) => sentFrags1.Add(data), 0);

            var sentFrags2 = new System.Collections.Generic.List<byte[]>();
            MessageReassembler.FragAndSend(2, (byte)MessageType.InputEvent, payload2,
                (sid, data) => sentFrags2.Add(data), 0);

            var reassembler = new MessageReassembler();
            MessageReceivedEventArgs received = null;
            reassembler.MessageReceived += (s, e) => received = e;

            // Feed first fragment of frame 1, then all of frame 2
            reassembler.OnFragment(new FragmentReceivedEventArgs(0, sentFrags1[0]));
            // Feed frame 2 — should discard incomplete frame 1
            foreach (var frag in sentFrags2)
            {
                reassembler.OnFragment(new FragmentReceivedEventArgs(0, frag));
            }

            Assert.NotNull(received);
            Assert.Equal(payload2, received.Data);
        }

        [Fact]
        public void OnFragment_OldFrameId_Discarded()
        {
            var payload1 = new byte[] { 0x01 };
            var payload2 = new byte[] { 0x02 };

            var f1 = new System.Collections.Generic.List<byte[]>();
            MessageReassembler.FragAndSend(5, (byte)MessageType.Keepalive, payload1,
                (sid, data) => f1.Add(data), 0);
            var f2 = new System.Collections.Generic.List<byte[]>();
            MessageReassembler.FragAndSend(3, (byte)MessageType.Keepalive, payload2,
                (sid, data) => f2.Add(data), 0);

            var reassembler = new MessageReassembler();
            MessageReceivedEventArgs received = null;
            reassembler.MessageReceived += (s, e) => received = e;

            // Feed frame 5 first
            foreach (var frag in f1) reassembler.OnFragment(new FragmentReceivedEventArgs(0, frag));
            // Feed frame 3 — should be discarded (older)
            foreach (var frag in f2) reassembler.OnFragment(new FragmentReceivedEventArgs(0, frag));

            Assert.NotNull(received);
            Assert.Equal(payload1, received.Data);
        }

        [Fact]
        public void OnFragment_BadMagic_Discarded()
        {
            var reassembler = new MessageReassembler();
            bool delivered = false;
            reassembler.MessageReceived += (s, e) => delivered = true;

            byte[] badData = new byte[20];
            badData[0] = 0xFF; // Not FrameMagic
            reassembler.OnFragment(new FragmentReceivedEventArgs(0, badData));

            Assert.False(delivered);
        }
    }
}
