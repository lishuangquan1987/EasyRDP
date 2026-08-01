#nullable disable
using System.Collections.Generic;
using EasyRDP.Core.Protocol;
using EasyRDP.Core.Transport;
using Xunit;

namespace EasyRDP.Core.Tests.Transport
{
    /// <summary>
    /// FramingBuffer 帧解析测试，重点覆盖"单分片消息 payload 超过 FragmentSize"的场景。
    /// 服务端光标位图消息（如 32x32 光标约 4.3KB）以单分片发送，历史上会被截断导致流失步。
    /// </summary>
    public class FramingBufferTests
    {
        [Fact]
        public void SingleFragmentOverFragmentSize_ShouldDeliverCompleteFrame()
        {
            // 模拟光标位图：单分片但 payload 3000 字节 > FragmentSize(1400)
            byte[] payload = new byte[3000];
            for (int i = 0; i < payload.Length; i++)
                payload[i] = (byte)(i % 251);

            byte[] wire = BuildSingleFragmentWire((byte)MessageType.CursorUpdate, payload);

            var framing = new FramingBuffer();
            var received = new List<byte[]>();
            framing.FragmentReady += d => received.Add(d);

            framing.Feed(wire, 0, wire.Length);

            Assert.Single(received);
            Assert.Equal(16 + payload.Length, received[0].Length);
            Assert.Equal(payload, Slice(received[0], 16, payload.Length));
        }

        [Fact]
        public void LargeSingleFragment_ThenNextFrame_ShouldNotDesyncStream()
        {
            // 复现真实故障：大光标帧之后紧跟一个普通帧，若大帧被截断，后续帧会因垃圾字节失步。
            byte[] cursorPayload = new byte[3000];
            for (int i = 0; i < cursorPayload.Length; i++)
                cursorPayload[i] = (byte)((i * 7) % 256);
            byte[] cursorWire = BuildSingleFragmentWire((byte)MessageType.CursorUpdate, cursorPayload);

            byte[] nextPayload = new byte[] { 0xAA, 0xBB, 0xCC };
            byte[] nextWire = BuildSingleFragmentWire((byte)MessageType.Keepalive, nextPayload);

            var framing = new FramingBuffer();
            var received = new List<byte[]>();
            framing.FragmentReady += d => received.Add(d);

            // 模拟 TCP 分片到达：大帧后半段与下一帧混在一次 Feed 中
            byte[] blob = new byte[cursorWire.Length + nextWire.Length];
            System.Buffer.BlockCopy(cursorWire, 0, blob, 0, cursorWire.Length);
            System.Buffer.BlockCopy(nextWire, 0, blob, cursorWire.Length, nextWire.Length);
            framing.Feed(blob, 0, blob.Length);

            Assert.Equal(2, received.Count);
            Assert.Equal(16 + cursorPayload.Length, received[0].Length);
            Assert.Equal(cursorPayload, Slice(received[0], 16, cursorPayload.Length));
            Assert.Equal(16 + nextPayload.Length, received[1].Length);
            Assert.Equal(nextPayload, Slice(received[1], 16, nextPayload.Length));
        }

        [Fact]
        public void MultiFragment_ShouldDeliverAllFragments_Unchanged()
        {
            // 回归保护：标准多分片消息（FragAndSend 切分）解析行为不变。
            byte[] payload = new byte[3000];
            for (int i = 0; i < payload.Length; i++)
                payload[i] = (byte)((i * 13) % 256);

            var sent = new List<byte[]>();
            MessageReassembler.FragAndSend(7, (byte)MessageType.VideoFrame, payload,
                (sid, data) => sent.Add(data), 1);
            Assert.True(sent.Count > 1);

            var framing = new FramingBuffer();
            var received = new List<byte[]>();
            framing.FragmentReady += d => received.Add(d);

            foreach (var frag in sent)
                framing.Feed(frag, 0, frag.Length);

            Assert.Equal(sent.Count, received.Count);
            for (int i = 0; i < sent.Count; i++)
                Assert.Equal(sent[i], received[i]);
        }

        /// <summary>按服务端光标线格式构建单分片帧：Magic+Type+PayloadLen+FrameId+FragIdx+FragCount+CRC16+Data。</summary>
        private static byte[] BuildSingleFragmentWire(byte messageType, byte[] payload)
        {
            byte[] wire = new byte[16 + payload.Length];
            int pos = 0;
            wire[pos++] = Constants.FrameMagic;
            wire[pos++] = messageType;
            uint totalLen = (uint)payload.Length;
            wire[pos++] = (byte)(totalLen & 0xFF);
            wire[pos++] = (byte)((totalLen >> 8) & 0xFF);
            wire[pos++] = (byte)((totalLen >> 16) & 0xFF);
            wire[pos++] = (byte)((totalLen >> 24) & 0xFF);
            wire[pos++] = 0; wire[pos++] = 0; wire[pos++] = 0; wire[pos++] = 0; // FrameId=0
            wire[pos++] = 0; wire[pos++] = 0; // FragIdx=0
            wire[pos++] = 1; wire[pos++] = 0; // FragCount=1
            if (payload.Length > 0)
                System.Buffer.BlockCopy(payload, 0, wire, pos + 2, payload.Length);
            ushort crc = MessageReassembler.ComputeCrc16(payload, 0, payload.Length);
            wire[pos++] = (byte)(crc & 0xFF);
            wire[pos++] = (byte)((crc >> 8) & 0xFF);
            return wire;
        }

        private static byte[] Slice(byte[] data, int offset, int length)
        {
            byte[] result = new byte[length];
            System.Buffer.BlockCopy(data, offset, result, 0, length);
            return result;
        }
    }
}
