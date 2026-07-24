namespace EasyRDP.Core.Transport
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using EasyRDP.Core.Protocol;
    /// <summary>
    /// 完整消息事件参数。MessageReassembler 组装完毕后抛出。
    /// </summary>
    public class MessageReceivedEventArgs : EventArgs
    {
        public uint SessionId;
        public byte MessageType;
        public byte[] Data;

        public MessageReceivedEventArgs(uint sessionId, byte messageType, byte[] data)
        {
            SessionId = sessionId;
            MessageType = messageType;
            Data = data;
        }
    }

    /// <summary>
    /// 消息分片重组器。每个 Session 独立一个实例。
    /// 接收侧：订阅传输层 DataReceived → 按 FrameId 重组 → CRC16 校验 → 收齐后抛 MessageReceived。
    /// 发送侧：FragAndSend 静态方法切分+发送。
    /// </summary>
    public class MessageReassembler
    {
        // CRC-16/XMODEM lookup table
        private static readonly ushort[] Crc16Table = BuildCrc16Table();

        // Current reassembly state
        private uint _currentFrameId;
        private bool _initialized;
        private int _expectedFragCount;
        private int _receivedFragCount;
        private byte[][] _fragBuffers;
        private int _totalPayloadLen;
        private byte _messageType;
        private Stopwatch _reassemblyTimer = new Stopwatch();

        /// <summary>完整消息组装完成事件。</summary>
        public event EventHandler<MessageReceivedEventArgs> MessageReceived;

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
                return;
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
            if (fragCount > 4096 || fragCount == 0)
                return;
            // Validate fragIdx
            if (fragIdx >= fragCount)
                return;

            // Extract fragment data
            int fragDataLen = data.Length - pos;
            if (fragDataLen <= 0)
                return;

            // Verify CRC16
            ushort actualCrc = ComputeCrc16(data, pos, fragDataLen);
            if (actualCrc != expectedCrc)
                return; // Corrupted fragment — discard

            // FrameId logic
            if (frameId > _currentFrameId || !_initialized)
            {
                // Newer frame or first frame — discard old partial, start new
                StartNewFrame(frameId, messageType, totalPayloadLen, fragCount);
            }
            else if (frameId < _currentFrameId)
            {
                return; // Old frame fragment — discard
            }

            // Check timeout
            if (_reassemblyTimer.ElapsedMilliseconds > Constants.FragmentReassembleTimeoutMs)
            {
                StartNewFrame(frameId, messageType, totalPayloadLen, fragCount);
            }

            // Store fragment
            if (_fragBuffers != null && fragIdx < _fragBuffers.Length && _fragBuffers[fragIdx] == null)
            {
                _fragBuffers[fragIdx] = new byte[fragDataLen];
                Buffer.BlockCopy(data, pos, _fragBuffers[fragIdx], 0, fragDataLen);
                _receivedFragCount++;
            }

            // Check if complete
            if (_receivedFragCount >= _expectedFragCount)
            {
                AssembleAndDeliver(frag.SessionId);
            }
        }

        private void StartNewFrame(uint frameId, byte messageType, int totalPayloadLen, int fragCount)
        {
            _currentFrameId = frameId;
            _initialized = true;
            _messageType = messageType;
            _totalPayloadLen = totalPayloadLen;
            _expectedFragCount = fragCount;
            _receivedFragCount = 0;
            _fragBuffers = new byte[fragCount][];
            _reassemblyTimer.Restart();
        }

        private void AssembleAndDeliver(uint sessionId)
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

            // Reset state (keep _initialized=true to reject old frames)
            _expectedFragCount = 0;
            _receivedFragCount = 0;
            _fragBuffers = null;
            _reassemblyTimer.Reset();

            // Deliver
            var handler = MessageReceived;
            if (handler != null)
            {
                handler(this, new MessageReceivedEventArgs(sessionId, _messageType, fullPayload));
            }
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
    }
}
