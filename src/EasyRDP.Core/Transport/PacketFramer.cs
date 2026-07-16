using System;
using System.Collections.Generic;

namespace EasyRDP.Core.Transport
{
    /// <summary>
    /// 流式数据分包器——处理粘包/半包问题。
    /// 从连续的字节流中提取完整的 EasyRDP 消息帧。
    /// 传输无关：TCP/UDP/任意流式传输均可使用。
    /// </summary>
    public class PacketFramer
    {
        private byte[] _buffer;
        private int _bufferPos;
        private int _bufferLen;

        private const int InitialBufferSize = 65536; // 64 KB
        private const int MaxBufferSize = 1048576;   // 1 MB

        /// <summary>
        /// 创建分包器实例。
        /// </summary>
        public PacketFramer()
        {
            _buffer = new byte[InitialBufferSize];
            _bufferPos = 0;
            _bufferLen = 0;
        }

        /// <summary>
        /// 输入新收到的字节数据。返回解析出的完整消息列表。
        /// </summary>
        /// <param name="data">收到的数据</param>
        /// <param name="offset">数据起始偏移</param>
        /// <param name="count">数据长度</param>
        /// <returns>解析出的完整消息帧列表（每条为完整的 header + payload 字节数组）</returns>
        public List<byte[]> Feed(byte[] data, int offset, int count)
        {
            List<byte[]> messages = new List<byte[]>();

            // 确保 buffer 够大
            EnsureCapacity(_bufferLen + count);

            // 追加到内部 buffer
            Buffer.BlockCopy(data, offset, _buffer, _bufferLen, count);
            _bufferLen += count;

            // 尝试提取完整消息
            while (TryExtractMessage(messages)) { }

            return messages;
        }

        /// <summary>
        /// 重置缓冲区（连接断开时调用）。
        /// </summary>
        public void Reset()
        {
            _bufferPos = 0;
            _bufferLen = 0;
        }

        private bool TryExtractMessage(List<byte[]> messages)
        {
            int available = _bufferLen - _bufferPos;

            // 至少需要头的大小
            if (available < Protocol.ProtocolConstants.HeaderSize)
                return false;

            // 读取长度字段（偏移 10，4 字节 LE）
            uint payloadLen = Protocol.BinaryPacker.ReadUInt32LE(_buffer, _bufferPos + 10);

            // 检查负载长度是否合法
            if (payloadLen > Protocol.ProtocolConstants.MaxPayload)
            {
                // 协议错误——丢弃缓冲区并重置
                Reset();
                return false;
            }

            int totalSize = Protocol.ProtocolConstants.HeaderSize + (int)payloadLen;

            // 数据不够完整一帧
            if (available < totalSize)
                return false;

            // 提取完整消息
            byte[] message = new byte[totalSize];
            Buffer.BlockCopy(_buffer, _bufferPos, message, 0, totalSize);
            messages.Add(message);

            _bufferPos += totalSize;

            // 如果 buffer 已全部消费，重置指针
            if (_bufferPos == _bufferLen)
            {
                _bufferPos = 0;
                _bufferLen = 0;
            }
            else if (_bufferPos > InitialBufferSize / 2)
            {
                Compact();
            }

            return true;
        }

        private void EnsureCapacity(int required)
        {
            if (required <= _buffer.Length)
                return;

            int newSize = _buffer.Length * 2;
            while (newSize < required)
                newSize *= 2;

            if (newSize > MaxBufferSize)
                throw new InvalidOperationException("PacketFramer buffer exceeded maximum size");

            byte[] newBuffer = new byte[newSize];
            if (_bufferLen > 0)
            {
                Buffer.BlockCopy(_buffer, _bufferPos, newBuffer, 0, _bufferLen - _bufferPos);
            }
            _bufferLen = _bufferLen - _bufferPos;
            _bufferPos = 0;
            _buffer = newBuffer;
        }

        private void Compact()
        {
            int remaining = _bufferLen - _bufferPos;
            if (remaining > 0)
            {
                Buffer.BlockCopy(_buffer, _bufferPos, _buffer, 0, remaining);
            }
            _bufferLen = remaining;
            _bufferPos = 0;
        }
    }
}
