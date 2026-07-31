namespace EasyRDP.Core.Transport
{
    using System;
    using EasyRDP.Core.Protocol;
    using NLog;
    /// <summary>
    /// Framing 缓冲区。把 TCP 字节流按 Magic+Type+PayloadLen 切分为完整的线格式分片。
    /// </summary>
    public class FramingBuffer
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private byte[] _buffer = new byte[65536];
        private int _bufferPos;

        /// <summary>完整分片就绪事件。</summary>
        public event Action<byte[]> FragmentReady;

        /// <summary>喂入收到的字节。可能触发零到多个 FragmentReady。</summary>
        public void Feed(byte[] data, int offset, int length)
        {
            int needed = _bufferPos + length;
            if (needed > _buffer.Length)
            {
                int newSize = _buffer.Length;
                while (newSize < needed) newSize *= 2;
                byte[] newBuf = new byte[newSize];
                Buffer.BlockCopy(_buffer, 0, newBuf, 0, _bufferPos);
                _buffer = newBuf;
            }
            Buffer.BlockCopy(data, offset, _buffer, _bufferPos, length);
            _bufferPos += length;

            while (TryExtractFragment()) { }
        }

        private bool TryExtractFragment()
        {
            if (_bufferPos < 16) return false; // Need at least 16B header

            // Find Magic byte
            int start = -1;
            for (int i = 0; i < _bufferPos - 1; i++)
            {
                if (_buffer[i] == Constants.FrameMagic)
                {
                    // Verify it looks like a valid frame: Type at pos 1 is a known MessageType
                    byte type = _buffer[i + 1];
                    if (IsKnownMessageType(type))
                    {
                        start = i;
                        break;
                    }
                }
            }

            if (start < 0)
            {
                if (_bufferPos > 0)
                {
                    // 流失步：缓冲区内无有效帧头。附加首 16 字节 hex 帮助诊断协议错位。
                    int dumpLen = _bufferPos < 16 ? _bufferPos : 16;
                    string hex = BitConverter.ToString(_buffer, 0, dumpLen);
                    Logger.Warn("FramingBuffer: discarding {0} bytes — no valid frame magic found, first {1} bytes: {2}",
                        _bufferPos, dumpLen, hex);
                }
                _bufferPos = 0; // No valid frame start found, discard all
                return false;
            }

            // Discard bytes before magic
            if (start > 0)
            {
                Buffer.BlockCopy(_buffer, start, _buffer, 0, _bufferPos - start);
                _bufferPos -= start;
            }

            if (_bufferPos < 16) return false;

            // Parse header
            // byte magic = _buffer[0]; // already verified
            // byte type = _buffer[1];
            uint totalPayloadLen = (uint)_buffer[2]
                | ((uint)_buffer[3] << 8)
                | ((uint)_buffer[4] << 16)
                | ((uint)_buffer[5] << 24);
            // uint frameId (4 bytes at offset 6)
            ushort fragIdx = (ushort)(_buffer[10] | (_buffer[11] << 8));
            ushort fragCount = (ushort)(_buffer[12] | (_buffer[13] << 8));
            // ushort crc16 (2 bytes at offset 14)

            if (totalPayloadLen > Constants.MaxSafePayloadSize)
            {
                // Payload too large — possible DoS, discard
                Logger.Warn("FramingBuffer: oversized payload rejected ({0} bytes, max {1})",
                    totalPayloadLen, Constants.MaxSafePayloadSize);
                Buffer.BlockCopy(_buffer, 1, _buffer, 0, _bufferPos - 1);
                _bufferPos--;
                return true;
            }

            // Calculate fragDataLen
            int fragDataLen = Constants.FragmentSize;
            int lastIdx = fragCount - 1;
            if (fragIdx == lastIdx)
            {
                int totalFull = fragCount > 1 ? (fragCount - 1) * Constants.FragmentSize : 0;
                fragDataLen = (int)totalPayloadLen - totalFull;
                if (fragDataLen > Constants.FragmentSize) fragDataLen = Constants.FragmentSize;
            }
            if (fragDataLen < 0) fragDataLen = 0;

            int totalFragSize = 16 + fragDataLen;
            if (_bufferPos < totalFragSize)
                return false; // Need more data

            // Extract fragment
            byte[] frag = new byte[totalFragSize];
            Buffer.BlockCopy(_buffer, 0, frag, 0, totalFragSize);

            // Shift remaining
            int remaining = _bufferPos - totalFragSize;
            if (remaining > 0)
                Buffer.BlockCopy(_buffer, totalFragSize, _buffer, 0, remaining);
            _bufferPos = remaining;

            var handler = FragmentReady;
            if (handler != null)
                handler(frag);
            return true;
        }

        private static bool IsKnownMessageType(byte type)
        {
            return type == (byte)Protocol.MessageType.HandshakeReq
                || type == (byte)Protocol.MessageType.HandshakeRes
                || type == (byte)Protocol.MessageType.Keepalive
                || type == (byte)Protocol.MessageType.InputEvent
                || type == (byte)Protocol.MessageType.CursorUpdate
                || type == (byte)Protocol.MessageType.ClipboardSync
                || type == (byte)Protocol.MessageType.ClipFormatList
                || type == (byte)Protocol.MessageType.ClipFileContentsReq
                || type == (byte)Protocol.MessageType.ClipFileContentsRes
                || type == (byte)Protocol.MessageType.ImageClipboardStart
                || type == (byte)Protocol.MessageType.ImageClipboardData
                || type == (byte)Protocol.MessageType.ImageClipboardEnd
                || type == (byte)Protocol.MessageType.VideoFrame;
        }
    }
}
