namespace EasyRDP.Core.Transport
{
    using System;
    using EasyRDP.Core.Protocol;
    using NLog;

    /// <summary>
    /// 消息级 framing 缓冲。把字节流按 Magic+Type+PayloadLen 切为完整消息。
    /// 替代旧 FramingBuffer 的分片切分（旧按 16 字节分片头切分片）。
    /// </summary>
    public class MessageFramingBuffer
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private byte[] _buffer = new byte[65536];
        private int _bufferPos;

        /// <summary>切出完整消息（Magic+Type+PayloadLen+Payload）时触发。</summary>
        public event Action<byte[]> MessageReady;

        /// <summary>喂入收到的字节，可能触发零到多个 MessageReady。</summary>
        public void Feed(byte[] data, int offset, int length)
        {
            if (length <= 0)
                return;

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

            while (TryExtractMessage()) { }
        }

        private bool TryExtractMessage()
        {
            if (_bufferPos < Framing.HeaderSize)
                return false;

            // 找帧头：Magic + Type 为已知类型（联合校验避免 payload 内 0xE5 误判）
            int start = FindFrameStart();
            if (start < 0)
            {
                // 无有效帧头：保留末位 magic 字节——TCP 可能在帧边界切分，
                // 末位 0xE5 可能是下一帧的起始，整段丢弃会把帧头永久丢失。
                if (_bufferPos > 0 && _buffer[_bufferPos - 1] == Constants.FrameMagic)
                {
                    _buffer[0] = _buffer[_bufferPos - 1];
                    _bufferPos = 1;
                }
                else
                {
                    if (_bufferPos > 0)
                    {
                        // 失步：缓冲内无有效帧头，丢弃字节（限频记录防刷屏）
                        Logger.Debug("MessageFramingBuffer: discarding {0} bytes (no valid frame head)", _bufferPos);
                    }
                    _bufferPos = 0;
                }
                return false;
            }

            // 丢弃帧头之前的字节
            if (start > 0)
            {
                Buffer.BlockCopy(_buffer, start, _buffer, 0, _bufferPos - start);
                _bufferPos -= start;
            }

            if (_bufferPos < Framing.HeaderSize)
                return false;

            // 读 PayloadLen（4 字节小端，offset 2..6）
            uint payloadLen = (uint)_buffer[2]
                | ((uint)_buffer[3] << 8)
                | ((uint)_buffer[4] << 16)
                | ((uint)_buffer[5] << 24);

            if (payloadLen > (uint)Constants.MaxSafePayloadSize)
            {
                Logger.Warn("MessageFramingBuffer: oversized payload rejected ({0} bytes, max {1})",
                    payloadLen, Constants.MaxSafePayloadSize);
                // 丢弃 Magic 字节，继续向后扫描
                Buffer.BlockCopy(_buffer, 1, _buffer, 0, _bufferPos - 1);
                _bufferPos--;
                return true;
            }

            int totalSize = Framing.HeaderSize + (int)payloadLen;
            if (_bufferPos < totalSize)
                return false; // 数据未到齐

            // 切出完整消息
            byte[] msg = new byte[totalSize];
            Buffer.BlockCopy(_buffer, 0, msg, 0, totalSize);

            int remaining = _bufferPos - totalSize;
            if (remaining > 0)
                Buffer.BlockCopy(_buffer, totalSize, _buffer, 0, remaining);
            _bufferPos = remaining;

            var handler = MessageReady;
            if (handler != null)
                handler(msg);
            return true;
        }

        /// <summary>在缓冲中定位有效帧头（Magic + 已知 Type）。返回 -1 表示未找到。</summary>
        private int FindFrameStart()
        {
            int maxStart = _bufferPos - 1; // 至少留 1 字节给 Type
            for (int i = 0; i < maxStart; i++)
            {
                if (_buffer[i] == Constants.FrameMagic && Framing.IsKnownMessageType(_buffer[i + 1]))
                    return i;
            }
            return -1;
        }
    }
}
