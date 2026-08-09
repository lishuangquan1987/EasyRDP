#nullable disable
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
        public void FragAndSend_EmptyPayload_ShouldDeliver()
        {
            // 回归（流控死锁+心跳断连根因）：空 payload 的 FragAndSend 生成 0 字节分片，
            // 服务端 OnFragment 必须允许送达（fragDataLen<0 才拒绝）。
            // 此前 `<=0` 把空分片静默丢弃 → Keepalive 心跳/FramebufferUpdateRequest
            // 帧请求全部丢失 → 服务端 45s 心跳超时断连 + ZRLE 流控死锁。
            var sentFragments = new System.Collections.Generic.List<byte[]>();
            MessageReassembler.FragAndSend(0, (byte)MessageType.Keepalive,
                new byte[0], (sid, data) => sentFragments.Add(data), 42);
            Assert.Single(sentFragments);

            var reassembler = new MessageReassembler();
            MessageReceivedEventArgs received = null;
            reassembler.MessageReceived += (s, e) => received = e;
            reassembler.OnFragment(new FragmentReceivedEventArgs(42, sentFragments[0]));

            // 空 payload 消息必须送达（Keepalive 依赖此行为更新服务端心跳）
            Assert.NotNull(received);
            Assert.Equal((byte)MessageType.Keepalive, received.MessageType);
            Assert.NotNull(received.Data);
            Assert.Equal(0, received.Data.Length);
        }

        [Fact]
        public void FragAndSend_OneBytePayload_ShouldDeliver()
        {
            // 修复验证：FramebufferUpdateRequest 改用 1 字节占位 payload 后必须正常送达
            var payload = new byte[] { 0 };
            var sentFragments = new System.Collections.Generic.List<byte[]>();
            MessageReassembler.FragAndSend(0, (byte)MessageType.FramebufferUpdateRequest,
                payload, (sid, data) => sentFragments.Add(data), 42);
            Assert.Single(sentFragments);

            var reassembler = new MessageReassembler();
            MessageReceivedEventArgs received = null;
            reassembler.MessageReceived += (s, e) => received = e;
            reassembler.OnFragment(new FragmentReceivedEventArgs(42, sentFragments[0]));

            Assert.NotNull(received);
            Assert.Equal((byte)MessageType.FramebufferUpdateRequest, received.MessageType);
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

        /// <summary>
        /// 回归测试：控制消息（ClipboardSync, frameId=0）在实时帧（VideoFrame, frameId 单调递增）
        /// 到达后不应被丢弃。修复前，两类消息共用单一 _currentFrameId，控制帧 frameId=0 始终被判为
        /// stale（&lt; 当前实时 frameId）而静默丢弃，导致剪贴板同步失效。
        /// </summary>
        [Fact]
        public void OnFragment_ControlMessage_NotDiscarded_AfterRealtimeFramesAdvance()
        {
            // 1) 先发送大量实时帧（VideoFrame），让实时流的 _currentFrameId 推进到很大值
            int realtimeFrameCount = 100;
            var sentRealtimeFrags = new System.Collections.Generic.List<byte[]>[realtimeFrameCount];
            for (int i = 0; i < realtimeFrameCount; i++)
            {
                var rtPayload = new byte[] { (byte)i, (byte)(0xA0 + i) };
                sentRealtimeFrags[i] = new System.Collections.Generic.List<byte[]>();
                MessageReassembler.FragAndSend((uint)(i + 1), (byte)MessageType.VideoFrame, rtPayload,
                    (sid, data) => sentRealtimeFrags[i].Add(data), 0);
            }

            // 2) 发送控制消息（ClipboardSync, frameId=0）
            var controlPayload = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };
            var sentControlFrags = new System.Collections.Generic.List<byte[]>();
            MessageReassembler.FragAndSend(0, (byte)MessageType.ClipboardSync, controlPayload,
                (sid, data) => sentControlFrags.Add(data), 0);

            // 3) 喂入重组器：先全部实时帧，再控制消息
            var reassembler = new MessageReassembler();
            System.Collections.Generic.List<MessageReceivedEventArgs> received = new System.Collections.Generic.List<MessageReceivedEventArgs>();
            reassembler.MessageReceived += (s, e) => received.Add(e);

            for (int i = 0; i < realtimeFrameCount; i++)
            {
                foreach (var frag in sentRealtimeFrags[i])
                    reassembler.OnFragment(new FragmentReceivedEventArgs(0, frag));
            }
            foreach (var frag in sentControlFrags)
                reassembler.OnFragment(new FragmentReceivedEventArgs(0, frag));

            // 4) 断言：控制消息必须被完整送达（修复前会被判 stale 丢弃）
            var controlMsg = received.Find(e => e.MessageType == (byte)MessageType.ClipboardSync);
            Assert.NotNull(controlMsg);
            Assert.Equal(controlPayload, controlMsg.Data);
        }

        /// <summary>
        /// 回归测试：实时帧与控制消息分片交错到达时，控制消息仍应完整重组。
        /// 模拟 47 分片的文件剪贴板传输期间夹杂数个 VideoFrame 分片。
        /// 修复前共用状态会被实时帧 StartNewFrame 冲刷，导致控制帧永远无法凑齐。
        /// </summary>
        [Fact]
        public void OnFragment_ControlMessage_Assembled_WithInterleavedRealtimeFrames()
        {
            // 大 payload 触发多分片（控制流）
            int controlPayloadSize = Constants.FragmentSize * 3 + 50;
            var controlPayload = new byte[controlPayloadSize];
            for (int i = 0; i < controlPayloadSize; i++)
                controlPayload[i] = (byte)(i % 256);

            var sentControlFrags = new System.Collections.Generic.List<byte[]>();
            MessageReassembler.FragAndSend(0, (byte)MessageType.ClipFormatList, controlPayload,
                (sid, data) => sentControlFrags.Add(data), 0);
            int controlFragCount = sentControlFrags.Count;
            Assert.True(controlFragCount >= 4);

            // 几个实时帧，用于在控制分片之间插入
            var sentRealtimeFrags = new System.Collections.Generic.List<byte[]>();
            for (int i = 0; i < 5; i++)
            {
                var rtPayload = new byte[] { (byte)i, 0x99 };
                MessageReassembler.FragAndSend((uint)(100 + i), (byte)MessageType.VideoFrame, rtPayload,
                    (sid, data) => sentRealtimeFrags.Add(data), 0);
            }

            var reassembler = new MessageReassembler();
            System.Collections.Generic.List<MessageReceivedEventArgs> received = new System.Collections.Generic.List<MessageReceivedEventArgs>();
            reassembler.MessageReceived += (s, e) => received.Add(e);

            // 交错喂入：每 2 个控制分片后插 1 个实时帧
            int rtIdx = 0;
            for (int i = 0; i < controlFragCount; i++)
            {
                reassembler.OnFragment(new FragmentReceivedEventArgs(0, sentControlFrags[i]));
                if (i % 2 == 1 && rtIdx < sentRealtimeFrags.Count)
                {
                    reassembler.OnFragment(new FragmentReceivedEventArgs(0, sentRealtimeFrags[rtIdx]));
                    rtIdx++;
                }
            }

            // 控制消息必须完整送达
            var controlMsg = received.Find(e => e.MessageType == (byte)MessageType.ClipFormatList);
            Assert.NotNull(controlMsg);
            Assert.Equal(controlPayload.Length, controlMsg.Data.Length);
            Assert.Equal(controlPayload, controlMsg.Data);
        }

        /// <summary>
        /// 回归测试：同 frameId=0 的连续控制消息必须被独立组装，不能因 _initialized=true
        /// 而误判为同一帧。修复前的 BUG：第一个控制消息组装完成后 _fragBuffers=null _expectedFragCount=0，
        /// 第二个同 frameId=0 的消息分片到达时不触发 StartNewFrame，直接进入 AssembleAndDeliver，
        /// 用旧的 messageType/payloadLen 组装出空 payload，导致 ClipFormatList 被误判为 HandshakeReq。
        /// </summary>
        [Fact]
        public void ControlStream_SequentialSameFrameId_MustBeAssembledIndependently()
        {
            // 模拟实际场景：HandshakeReq（frameId=0）→ ClipFormatList（frameId=0）
            var reassembler = new MessageReassembler();
            var received = new System.Collections.Generic.List<MessageReceivedEventArgs>();
            reassembler.MessageReceived += (s, e) => received.Add(e);

            // 第一个控制消息：HandshakeReq，10 字节
            byte[] hsPayload = new byte[] { 2, 0, 0, 0, 1, 0, 0, 0, 0, 0 };
            var hsFrags = new System.Collections.Generic.List<byte[]>();
            MessageReassembler.FragAndSend(0, (byte)MessageType.HandshakeReq, hsPayload,
                (sid, data) => hsFrags.Add(data), 0);

            // 第二个控制消息：ClipFormatList，frameId=0，多分片
            byte[] clipPayload = new byte[500];
            for (int i = 0; i < clipPayload.Length; i++) clipPayload[i] = (byte)(i & 0xFF);
            var clipFrags = new System.Collections.Generic.List<byte[]>();
            MessageReassembler.FragAndSend(0, (byte)MessageType.ClipFormatList, clipPayload,
                (sid, data) => clipFrags.Add(data), 0);

            // 喂入 HandshakeReq 分片
            foreach (var frag in hsFrags)
                reassembler.OnFragment(new FragmentReceivedEventArgs(0, frag));

            // 喂入 ClipFormatList 分片
            foreach (var frag in clipFrags)
                reassembler.OnFragment(new FragmentReceivedEventArgs(0, frag));

            // 断言两条消息都被正确组装
            Assert.Equal(2, received.Count);

            // 第一条：HandshakeReq
            Assert.Equal((byte)MessageType.HandshakeReq, received[0].MessageType);
            Assert.Equal(hsPayload.Length, received[0].Data.Length);
            Assert.Equal(hsPayload, received[0].Data);

            // 第二条：ClipFormatList（关键：不能被误判为 HandshakeReq）
            Assert.Equal((byte)MessageType.ClipFormatList, received[1].MessageType);
            Assert.Equal(clipPayload.Length, received[1].Data.Length);
            Assert.Equal(clipPayload, received[1].Data);
        }

        /// <summary>
        /// 回归测试：三个连续同 frameId=0 的控制消息（HandshakeRes → ClipFormatList → ClipFileContentsReq）
        /// 都必须被独立正确组装。模拟客户端实际场景：握手响应后收到文件剪贴板广播，再发文件内容请求。
        /// </summary>
        [Fact]
        public void ControlStream_ThreeSequentialSameFrameId_AllAssembledCorrectly()
        {
            var reassembler = new MessageReassembler();
            var received = new System.Collections.Generic.List<MessageReceivedEventArgs>();
            reassembler.MessageReceived += (s, e) => received.Add(e);

            byte[] resPayload = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 };
            byte[] listPayload = new byte[300];
            for (int i = 0; i < listPayload.Length; i++) listPayload[i] = (byte)(i + 1);
            byte[] reqPayload = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF };

            var resFrags = new System.Collections.Generic.List<byte[]>();
            var listFrags = new System.Collections.Generic.List<byte[]>();
            var reqFrags = new System.Collections.Generic.List<byte[]>();

            MessageReassembler.FragAndSend(0, (byte)MessageType.HandshakeRes, resPayload,
                (sid, data) => resFrags.Add(data), 0);
            MessageReassembler.FragAndSend(0, (byte)MessageType.ClipFormatList, listPayload,
                (sid, data) => listFrags.Add(data), 0);
            MessageReassembler.FragAndSend(0, (byte)MessageType.ClipFileContentsReq, reqPayload,
                (sid, data) => reqFrags.Add(data), 0);

            foreach (var frag in resFrags)
                reassembler.OnFragment(new FragmentReceivedEventArgs(0, frag));
            foreach (var frag in listFrags)
                reassembler.OnFragment(new FragmentReceivedEventArgs(0, frag));
            foreach (var frag in reqFrags)
                reassembler.OnFragment(new FragmentReceivedEventArgs(0, frag));

            Assert.Equal(3, received.Count);
            Assert.Equal((byte)MessageType.HandshakeRes, received[0].MessageType);
            Assert.Equal(resPayload, received[0].Data);
            Assert.Equal((byte)MessageType.ClipFormatList, received[1].MessageType);
            Assert.Equal(listPayload, received[1].Data);
            Assert.Equal((byte)MessageType.ClipFileContentsReq, received[2].MessageType);
            Assert.Equal(reqPayload, received[2].Data);
        }

        /// <summary>
        /// 回归测试：多分片的 ClipFileContentsRes（frameId=0）传输途中插入一条单分片的
        /// ClipboardSync（frameId=0），两条消息都必须完整送达。
        /// 旧实现所有控制消息共用一个重组状态，插入的控制消息会被当作文件响应的分片而静默丢弃。
        /// </summary>
        [Fact]
        public void ControlStream_InterleavedDifferentTypes_AllDeliveredIntact()
        {
            var reassembler = new MessageReassembler();
            var received = new System.Collections.Generic.List<MessageReceivedEventArgs>();
            reassembler.MessageReceived += (s, e) => received.Add(e);

            // 模拟 1MB 文件响应的前几片（多分片控制消息）
            byte[] resPayload = new byte[Constants.FragmentSize * 3 + 100];
            for (int i = 0; i < resPayload.Length; i++)
                resPayload[i] = (byte)(i % 251);
            // 插入的剪贴板文本同步（单分片）
            byte[] syncPayload = new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 4, 0, 0, 0, 0x48, 0x69 }; // Format=1, "Hi"

            var resFrags = new System.Collections.Generic.List<byte[]>();
            var syncFrags = new System.Collections.Generic.List<byte[]>();
            MessageReassembler.FragAndSend(0, (byte)MessageType.ClipFileContentsRes, resPayload,
                (sid, data) => resFrags.Add(data), 0);
            MessageReassembler.FragAndSend(0, (byte)MessageType.ClipboardSync, syncPayload,
                (sid, data) => syncFrags.Add(data), 0);

            Assert.True(resFrags.Count > 3);
            Assert.Single(syncFrags);

            // 交错投递：文件响应前 2 片 → 剪贴板同步 → 文件响应剩余全部
            for (int i = 0; i < 2 && i < resFrags.Count; i++)
                reassembler.OnFragment(new FragmentReceivedEventArgs(0, resFrags[i]));
            reassembler.OnFragment(new FragmentReceivedEventArgs(0, syncFrags[0]));
            for (int i = 2; i < resFrags.Count; i++)
                reassembler.OnFragment(new FragmentReceivedEventArgs(0, resFrags[i]));

            Assert.Equal(2, received.Count);

            // 剪贴板同步先完成组装，因此先送达
            Assert.Equal((byte)MessageType.ClipboardSync, received[0].MessageType);
            Assert.Equal(syncPayload, received[0].Data);

            Assert.Equal((byte)MessageType.ClipFileContentsRes, received[1].MessageType);
            Assert.Equal(resPayload.Length, received[1].Data.Length);
            for (int i = 0; i < resPayload.Length; i++)
                Assert.Equal(resPayload[i], received[1].Data[i]);
        }

        [Fact]
        public void CrcError_ShouldCountAndNotDeliver()
        {
            // 篡改 FragData 一字节 → CRC16 必然不匹配 → 分片被丢弃且计入 CrcErrorCount
            var sent = new System.Collections.Generic.List<byte[]>();
            MessageReassembler.FragAndSend(1, (byte)MessageType.Keepalive,
                new byte[] { 0x01, 0x02, 0x03 }, (sid, data) => sent.Add(data), 42);

            byte[] corrupted = (byte[])sent[0].Clone();
            corrupted[corrupted.Length - 1] ^= 0xFF; // 翻转 FragData 最后一个字节

            var reassembler = new MessageReassembler();
            MessageReceivedEventArgs received = null;
            reassembler.MessageReceived += (s, e) => received = e;

            reassembler.OnFragment(new FragmentReceivedEventArgs(42, corrupted));

            Assert.Null(received);                       // 损坏分片不送达
            Assert.Equal(1, reassembler.CrcErrorCount);  // 计入 CRC 错误
            Assert.Equal(0L, reassembler.TotalFramesCompleted);
        }

        [Fact]
        public void StaleFrameDrop_ShouldReflectInPacketLossRate()
        {
            // 先送 frameId=1 完整帧，再送 frameId=0 旧帧 → 旧帧被 stale 丢弃
            var reassembler = new MessageReassembler();
            MessageReceivedEventArgs received = null;
            reassembler.MessageReceived += (s, e) => received = e;

            var frame1 = new System.Collections.Generic.List<byte[]>();
            MessageReassembler.FragAndSend(1, (byte)MessageType.Keepalive,
                new byte[] { 0x10, 0x20 }, (sid, data) => frame1.Add(data), 42);
            reassembler.OnFragment(new FragmentReceivedEventArgs(42, frame1[0]));
            Assert.NotNull(received);

            var frame0 = new System.Collections.Generic.List<byte[]>();
            MessageReassembler.FragAndSend(0, (byte)MessageType.Keepalive,
                new byte[] { 0x30, 0x40 }, (sid, data) => frame0.Add(data), 42);
            reassembler.OnFragment(new FragmentReceivedEventArgs(42, frame0[0]));

            // 1 帧完成、至少 1 帧被丢，丢帧率 > 0
            Assert.Equal(1L, reassembler.TotalFramesCompleted);
            Assert.True(reassembler.TotalFramesDropped >= 1L);
            Assert.True(reassembler.PacketLossRate > 0.0);
            Assert.True(reassembler.PacketLossRate <= 1.0);
        }
    }
}
