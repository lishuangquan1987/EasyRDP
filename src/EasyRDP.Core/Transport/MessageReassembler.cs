namespace EasyRDP.Core.Transport
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using EasyRDP.Core.Protocol;
    using NLog;
    /// <summary>
    /// 消息分片重组器。每个 Session 独立一个实例。
    /// 接收侧：订阅传输层 DataReceived → 按 FrameId 重组 → CRC16 校验 → 收齐后抛 MessageReceived。
    /// 发送侧：FragAndSend 静态方法切分+发送。
    /// </summary>
    /// <remarks>
    /// 内部维护两路独立的重组状态，防止实时流与控制流互相冲刷：
    /// - 实时流（VideoFrame/InputEvent/CursorUpdate）：frameId 单调递增，旧帧可丢弃以降低延迟。
    /// - 控制流（Clipboard*/Handshake/Keepalive）：必须完整重组，不允许因实时帧到达而丢弃。
    /// 同一 socket 上两类分片会交错到达，若共用单一状态会导致：实时帧 StartNewFrame 冲刷控制帧，
    /// 或控制帧 frameId(=0) 被判为 stale（&lt; 当前实时 frameId）而静默丢弃。
    /// </remarks>
    public class MessageReassembler
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        // CRC-16/XMODEM lookup table
        private static readonly ushort[] Crc16Table = BuildCrc16Table();

        // 实时流重组状态（VideoFrame/InputEvent/CursorUpdate）
        private readonly FrameState _realtimeState = new FrameState("realtime");
        // 控制流重组状态（Clipboard*/Handshake/Keepalive）
        private readonly FrameState _controlState = new FrameState("control");

        // 协议级诊断计数器（与分流无关，统计所有分片）
        private int _fragCountRejectCount;
        private int _fragIdxRejectCount;

        /// <summary>完整消息组装完成事件。</summary>
        public event EventHandler<MessageReceivedEventArgs> MessageReceived;

        /// <summary>
        /// 判断消息类型是否属于实时流。实时流允许丢帧（旧帧无效），控制流必须完整送达。
        /// </summary>
        private static bool IsRealtimeType(byte messageType)
        {
            return messageType == (byte)MessageType.VideoFrame
                || messageType == (byte)MessageType.InputEvent
                || messageType == (byte)MessageType.CursorUpdate;
        }

        /// <summary>
        /// 收到一个线格式分片（来自传输层 DataReceived）。非线程安全，调用方须保证串行。
        /// 线格式：Magic(1) + Type(1) + PayloadLen(4) + FrameId(4) + FragIdx(2) + FragCount(2) + CRC16(2) + FragData
        /// </summary>
        public void OnFragment(FragmentReceivedEventArgs frag)
        {
            if (frag == null || frag.Data == null || frag.Data.Length < 16)
                return; // Too short for header

            byte[] data = frag.Data;
            int pos = 0;

            // Parse framing outer
            byte magic = data[pos++];
            if (magic != Constants.FrameMagic)
                return; // Not a valid frame
            byte messageType = data[pos++];
            uint rawPayloadLen = (uint)data[pos] |
                ((uint)data[pos + 1] << 8) |
                ((uint)data[pos + 2] << 16) |
                ((uint)data[pos + 3] << 24);
            pos += 4;

            // Reject oversized payloads (DoS protection)
            if (rawPayloadLen > (uint)Constants.MaxSafePayloadSize)
            {
                Logger.Warn("Oversized payload rejected: {0} bytes (max {1})",
                    rawPayloadLen, Constants.MaxSafePayloadSize);
                return;
            }
            int totalPayloadLen = (int)rawPayloadLen;

            // Parse fragment header
            uint frameId = (uint)(
                (uint)data[pos] |
                ((uint)data[pos + 1] << 8) |
                ((uint)data[pos + 2] << 16) |
                ((uint)data[pos + 3] << 24));
            pos += 4;
            ushort fragIdx = (ushort)(data[pos] | (data[pos + 1] << 8));
            pos += 2;
            ushort fragCount = (ushort)(data[pos] | (data[pos + 1] << 8));
            pos += 2;
            ushort expectedCrc = (ushort)(data[pos] | (data[pos + 1] << 8));
            pos += 2;

            // Reject excessive fragment counts (DoS protection)
            // 注意：H264 编码帧通常 < 100 分片。如果触发此限制，说明 payload 异常大。
            if (fragCount > 4096 || fragCount == 0)
            {
                _fragCountRejectCount++;
                if (_fragCountRejectCount <= 3 || _fragCountRejectCount % 100 == 0)
                    Logger.Warn("Fragment rejected: fragCount={0} fragIdx={1} frameId={2} type=0x{3:X2} payloadLen={4} (total rejects={5})",
                        fragCount, fragIdx, frameId, messageType, totalPayloadLen, _fragCountRejectCount);
                return;
            }
            // Validate fragIdx
            if (fragIdx >= fragCount)
            {
                _fragIdxRejectCount++;
                if (_fragIdxRejectCount <= 3 || _fragIdxRejectCount % 100 == 0)
                    Logger.Warn("Fragment rejected: fragIdx={0} >= fragCount={1} frameId={2} (total rejects={3})",
                        fragIdx, fragCount, frameId, _fragIdxRejectCount);
                return;
            }

            // Extract fragment data
            int fragDataLen = data.Length - pos;
            if (fragDataLen <= 0)
                return;

            // Verify CRC16
            ushort actualCrc = ComputeCrc16(data, pos, fragDataLen);
            if (actualCrc != expectedCrc)
            {
                Logger.Warn("CRC16 mismatch on frameId={0} fragIdx={1}/{2} — fragment discarded",
                    frameId, fragIdx, fragCount);
                return; // Corrupted fragment — discard
            }

            // 按消息类型分流：实时流走 stale 检测，控制流独立重组不受实时帧干扰
            var state = IsRealtimeType(messageType) ? _realtimeState : _controlState;
            state.ProcessFragment(frag.SessionId, messageType, frameId, fragIdx, fragCount,
                totalPayloadLen, data, pos, fragDataLen, MessageReceived);
        }

        /// <summary>
        /// 把完整消息 payload 切分为分片并逐片发送。
        /// </summary>
        public static void FragAndSend(uint frameId, byte messageType, byte[] payload,
            Action<uint, byte[]> sendAction, uint sessionId)
        {
            int totalLen = payload.Length;
            int fragCount = (totalLen + Constants.FragmentSize - 1) / Constants.FragmentSize;
            if (fragCount == 0)
                fragCount = 1;
            if (fragCount > ushort.MaxValue)
                fragCount = ushort.MaxValue; // Safety clamp

            for (int i = 0; i < fragCount; i++)
            {
                int offset = i * Constants.FragmentSize;
                int fragLen = Constants.FragmentSize;
                if (offset + fragLen > totalLen)
                    fragLen = totalLen - offset;
                if (fragLen < 0)
                    fragLen = 0;

                byte[] fragData = new byte[fragLen];
                if (fragLen > 0)
                    Buffer.BlockCopy(payload, offset, fragData, 0, fragLen);

                byte[] wire = BuildWireFragment(
                    frameId, (ushort)i, (ushort)fragCount,
                    messageType, (uint)totalLen, fragData);
                sendAction(sessionId, wire);
            }
        }

        private static byte[] BuildWireFragment(uint frameId, ushort fragIdx, ushort fragCount,
            byte messageType, uint totalPayloadLen, byte[] fragData)
        {
            // Magic(1)+Type(1)+PayloadLen(4)+FrameId(4)+FragIdx(2)+FragCount(2)+CRC16(2)+FragData
            int headerSize = 16;
            byte[] wire = new byte[headerSize + fragData.Length];
            int pos = 0;

            wire[pos++] = Constants.FrameMagic;
            wire[pos++] = messageType;

            // PayloadLen (4 bytes LE)
            wire[pos++] = (byte)(totalPayloadLen & 0xFF);
            wire[pos++] = (byte)((totalPayloadLen >> 8) & 0xFF);
            wire[pos++] = (byte)((totalPayloadLen >> 16) & 0xFF);
            wire[pos++] = (byte)((totalPayloadLen >> 24) & 0xFF);

            // FrameId (4 bytes LE)
            wire[pos++] = (byte)(frameId & 0xFF);
            wire[pos++] = (byte)((frameId >> 8) & 0xFF);
            wire[pos++] = (byte)((frameId >> 16) & 0xFF);
            wire[pos++] = (byte)((frameId >> 24) & 0xFF);

            // FragIdx (2 bytes LE)
            wire[pos++] = (byte)(fragIdx & 0xFF);
            wire[pos++] = (byte)((fragIdx >> 8) & 0xFF);

            // FragCount (2 bytes LE)
            wire[pos++] = (byte)(fragCount & 0xFF);
            wire[pos++] = (byte)((fragCount >> 8) & 0xFF);

            // CRC16 placeholder — compute after copying fragData
            // FragData
            if (fragData.Length > 0)
                Buffer.BlockCopy(fragData, 0, wire, pos + 2, fragData.Length);

            // Compute CRC16 of FragData
            ushort crc = ComputeCrc16(fragData, 0, fragData.Length);
            wire[pos++] = (byte)(crc & 0xFF);
            wire[pos++] = (byte)((crc >> 8) & 0xFF);

            return wire;
        }

        #region CRC16

        private static ushort[] BuildCrc16Table()
        {
            ushort[] table = new ushort[256];
            for (int i = 0; i < 256; i++)
            {
                ushort crc = (ushort)(i << 8);
                for (int j = 0; j < 8; j++)
                {
                    if ((crc & 0x8000) != 0)
                        crc = (ushort)((crc << 1) ^ 0x1021);
                    else
                        crc = (ushort)(crc << 1);
                }
                table[i] = crc;
            }
            return table;
        }

        public static ushort ComputeCrc16(byte[] data, int offset, int length)
        {
            ushort crc = 0;
            for (int i = 0; i < length; i++)
            {
                byte index = (byte)((crc >> 8) ^ data[offset + i]);
                crc = (ushort)((crc << 8) ^ Crc16Table[index]);
            }
            return crc;
        }

        #endregion

        /// <summary>
        /// 单路重组状态。实时流实例启用 stale 检测；控制流实例始终接受新帧。
        /// 两路状态独立，互不冲刷。
        /// </summary>
        private sealed class FrameState
        {
            private readonly string _tag;
            private uint _currentFrameId;
            private bool _initialized;
            private int _expectedFragCount;
            private int _receivedFragCount;
            private byte[][] _fragBuffers;
            private int _totalPayloadLen;
            private byte _messageType;
            private readonly Stopwatch _reassemblyTimer = new Stopwatch();

            // 当前帧是否已完成组装（AssembleAndDeliver 已触发）。
            // 关键作用：控制流所有消息都用 frameId=0，组装完成后若再来一个 frameId=0 的消息，
            // 必须强制 StartNewFrame，否则会因 frameId==_currentFrameId 且 _fragBuffers=null
            // 直接触发 AssembleAndDeliver，用旧的 messageType/payloadLen 组装出空 payload 误判。
            private bool _frameCompleted;

            // 诊断计数器：跟踪 stale 帧拒绝
            private int _staleFrameRejectCount;

            public FrameState(string tag)
            {
                _tag = tag;
            }

            public void ProcessFragment(uint sessionId, byte messageType, uint frameId,
                ushort fragIdx, ushort fragCount, int totalPayloadLen,
                byte[] data, int dataPos, int fragDataLen,
                EventHandler<MessageReceivedEventArgs> handler)
            {
                // FrameId ordering — cases:
                //   !_initialized: very first fragment (regardless of frameId), initialize state
                //   frameId > _currentFrameId: newer frame arrived, discard old partial (real-time semantics)
                //   frameId < _currentFrameId: stale frame fragment, discard
                //   frameId == _currentFrameId && _frameCompleted: 上一个同 frameId 的帧已组装完成，
                //       新分片属于新消息（如连续的控制消息都用 frameId=0），强制 StartNewFrame
                //   frameId == _currentFrameId && !_frameCompleted: 同一帧的后续分片，继续组装
                if (!_initialized || frameId > _currentFrameId)
                {
                    StartNewFrame(frameId, messageType, totalPayloadLen, fragCount);
                }
                else if (frameId < _currentFrameId)
                {
                    _staleFrameRejectCount++;
                    if (_staleFrameRejectCount <= 3 || _staleFrameRejectCount % 100 == 0)
                        Logger.Warn("[{0}] Stale fragment discarded: frameId={1} < current={2} fragIdx={3}/{4} (total stale={5})",
                            _tag, frameId, _currentFrameId, fragIdx, fragCount, _staleFrameRejectCount);
                    return; // Old frame fragment — discard
                }
                else if (_frameCompleted)
                {
                    // frameId == _currentFrameId 但上一帧已完成，说明这是新的同 frameId 消息
                    StartNewFrame(frameId, messageType, totalPayloadLen, fragCount);
                }

                // Timeout guard: if current frame takes too long to assemble (lost fragments),
                // restart to prevent dead state. Real-time protocol: old incomplete frames are worthless.
                if (_reassemblyTimer.ElapsedMilliseconds > Constants.FragmentReassembleTimeoutMs)
                {
                    Logger.Warn("[{0}] Reassembly timeout for frameId={1} after {2}ms (received {3}/{4} fragments)",
                        _tag, _currentFrameId, _reassemblyTimer.ElapsedMilliseconds,
                        _receivedFragCount, _expectedFragCount);
                    StartNewFrame(frameId, messageType, totalPayloadLen, fragCount);
                }

                // Store fragment
                if (_fragBuffers != null && fragIdx < _fragBuffers.Length && _fragBuffers[fragIdx] == null)
                {
                    _fragBuffers[fragIdx] = new byte[fragDataLen];
                    Buffer.BlockCopy(data, dataPos, _fragBuffers[fragIdx], 0, fragDataLen);
                    _receivedFragCount++;
                }

                // Check if complete
                if (_receivedFragCount >= _expectedFragCount)
                {
                    AssembleAndDeliver(sessionId, handler);
                }
            }

            private void StartNewFrame(uint frameId, byte messageType, int totalPayloadLen, int fragCount)
            {
                _currentFrameId = frameId;
                _initialized = true;
                _frameCompleted = false;
                _messageType = messageType;
                _totalPayloadLen = totalPayloadLen;
                _expectedFragCount = fragCount;
                _receivedFragCount = 0;
                _fragBuffers = new byte[fragCount][];
                _reassemblyTimer.Restart();
            }

            private void AssembleAndDeliver(uint sessionId, EventHandler<MessageReceivedEventArgs> handler)
            {
                // Assemble full payload from fragments
                byte[] fullPayload = new byte[_totalPayloadLen];
                int offset = 0;
                for (int i = 0; i < _expectedFragCount; i++)
                {
                    if (_fragBuffers[i] != null)
                    {
                        int copyLen = _fragBuffers[i].Length;
                        if (offset + copyLen > _totalPayloadLen)
                            copyLen = _totalPayloadLen - offset;
                        if (copyLen > 0)
                        {
                            Buffer.BlockCopy(_fragBuffers[i], 0, fullPayload, offset, copyLen);
                            offset += copyLen;
                        }
                    }
                }

                // Capture fragCount before reset for logging
                int fragCount = _expectedFragCount;
                byte messageType = _messageType;

                // Reset state (keep _initialized=true to reject old frames;
                // set _frameCompleted=true to force StartNewFrame on next fragment with same frameId)
                _expectedFragCount = 0;
                _receivedFragCount = 0;
                _fragBuffers = null;
                _reassemblyTimer.Reset();
                _frameCompleted = true;

                Logger.Debug("[{0}] Message assembled: sessionId={1} type=0x{2:X2} payloadLen={3} fragCount={4}",
                    _tag, sessionId, messageType, fullPayload.Length, fragCount);

                // Deliver
                if (handler != null)
                {
                    handler(this, new MessageReceivedEventArgs(sessionId, messageType, fullPayload));
                }
            }
        }
    }
}
