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

        [Fact]
        public void SendSingleFragment_RoundTrip_ShouldDeliverCompleteMessage()
        {
            // ClipFileContentsRes 走单完整帧发送（1MB 数据块），验证接收端完整还原。
            byte[] payload = new byte[1024 * 1024];
            for (int i = 0; i < payload.Length; i++)
                payload[i] = (byte)((i * 31) % 256);

            var sent = new List<byte[]>();
            MessageReassembler.SendSingleFragment(0, (byte)MessageType.ClipFileContentsRes, payload,
                (sid, data) => sent.Add(data), 9);

            Assert.Single(sent);
            Assert.Equal(16 + payload.Length, sent[0].Length);

            var framing = new FramingBuffer();
            byte[] delivered = null;
            framing.FragmentReady += d => delivered = d;
            framing.Feed(sent[0], 0, sent[0].Length);

            Assert.NotNull(delivered);
            Assert.Equal(sent[0], delivered);

            // 重组器按单分片完整组装
            var reassembler = new MessageReassembler();
            MessageReceivedEventArgs received = null;
            reassembler.MessageReceived += (s, e) => received = e;
            reassembler.OnFragment(new FragmentReceivedEventArgs(9, delivered));

            Assert.NotNull(received);
            Assert.Equal((byte)MessageType.ClipFileContentsRes, received.MessageType);
            Assert.Equal(payload, received.Data);
        }

        [Fact]
        public void ConcurrentSingleFragmentResponses_ShouldBothReassemble()
        {
            // 真实故障场景：FileClipboardConsumer 并发请求多个 1MB 数据块，服务端并发响应。
            // 传输层每次 Write 在 session 锁内原子完成，整帧连续到达（帧之间按写入粒度切换），
            // 因此两个单完整帧响应互不干扰，接收端都能完整还原。
            byte[] payloadA = new byte[1024 * 1024];
            byte[] payloadB = new byte[1024 * 1024];
            for (int i = 0; i < payloadA.Length; i++)
            {
                payloadA[i] = (byte)(i & 0xFF);
                payloadB[i] = (byte)(~i & 0xFF);
            }

            var wires = new List<byte[]>();
            MessageReassembler.SendSingleFragment(0, (byte)MessageType.ClipFileContentsRes, payloadA,
                (sid, d) => wires.Add(d), 1);
            MessageReassembler.SendSingleFragment(0, (byte)MessageType.ClipFileContentsRes, payloadB,
                (sid, d) => wires.Add(d), 1);

            // 两个并发写入的先后顺序不保证，任意顺序都须完整解析
            foreach (var order in new[] { new[] { 0, 1 }, new[] { 1, 0 } })
            {
                var framing = new FramingBuffer();
                var delivered = new List<byte[]>();
                framing.FragmentReady += d => delivered.Add(d);
                framing.Feed(wires[order[0]], 0, wires[order[0]].Length);
                framing.Feed(wires[order[1]], 0, wires[order[1]].Length);
                Assert.Equal(2, delivered.Count);

                var reassembler = new MessageReassembler();
                var received = new List<MessageReceivedEventArgs>();
                reassembler.MessageReceived += (s, e) => received.Add(e);
                foreach (var d in delivered)
                    reassembler.OnFragment(new FragmentReceivedEventArgs(1, d));
                Assert.Equal(2, received.Count);

                var payloads = new[] { received[0].Data, received[1].Data };
                bool hasA = payloads[0].SequenceEqual(payloadA) || payloads[1].SequenceEqual(payloadA);
                bool hasB = payloads[0].SequenceEqual(payloadB) || payloads[1].SequenceEqual(payloadB);
                Assert.True(hasA && hasB, "both concurrent responses must reassemble intact");
            }
        }

        [Fact]
        public void InterleavedMultiFragmentResponses_LoseOneFrame()
        {
            // 对照测试：旧实现把每个响应切成 1400 字节分片且共用 frameId=0，
            // 两个并发响应的分片交错到达时，重组器会把后到的同槽分片丢弃，导致其中一个响应永远组装不齐
            // （下载块超时失败 → 剪贴板不被设置 → 无粘贴菜单）。本测试固化该缺陷以说明修复动机。
            byte[] payloadA = new byte[3000];
            byte[] payloadB = new byte[3000];
            for (int i = 0; i < payloadA.Length; i++)
            {
                payloadA[i] = (byte)(i & 0xFF);
                payloadB[i] = (byte)(~i & 0xFF);
            }

            var fragsA = new List<byte[]>();
            var fragsB = new List<byte[]>();
            MessageReassembler.FragAndSend(0, (byte)MessageType.ClipFileContentsRes, payloadA,
                (sid, d) => fragsA.Add(d), 1);
            MessageReassembler.FragAndSend(0, (byte)MessageType.ClipFileContentsRes, payloadB,
                (sid, d) => fragsB.Add(d), 1);

            var reassembler = new MessageReassembler();
            var received = new List<MessageReceivedEventArgs>();
            reassembler.MessageReceived += (s, e) => received.Add(e);

            // 分片级交错到达（TCP 上并发写时的真实形态）
            for (int i = 0; i < fragsA.Count; i++)
            {
                reassembler.OnFragment(new FragmentReceivedEventArgs(1, fragsA[i]));
                reassembler.OnFragment(new FragmentReceivedEventArgs(1, fragsB[i]));
            }

            // 交错后无法同时完整还原两个响应：至多组装出一个，且内容可能是混拼的
            bool bothIntact = false;
            if (received.Count >= 2)
            {
                bool hasA = received[0].Data.SequenceEqual(payloadA) || received[1].Data.SequenceEqual(payloadA);
                bool hasB = received[0].Data.SequenceEqual(payloadB) || received[1].Data.SequenceEqual(payloadB);
                bothIntact = hasA && hasB;
            }
            Assert.False(bothIntact, "interleaved multi-fragment responses must NOT both reassemble intact");
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
