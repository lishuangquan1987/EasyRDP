using System;

namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 心跳请求 C→S（负载为空）
    /// </summary>
    public class KeepAliveMessage
    {
        public byte[] Encode()
        {
            return new byte[0];
        }

        public void Decode(byte[] payload)
        {
            // 无负载
        }
    }

    /// <summary>
    /// 心跳应答 S→C（负载为空）
    /// </summary>
    public class KeepAliveAckMessage
    {
        public byte[] Encode()
        {
            return new byte[0];
        }

        public void Decode(byte[] payload)
        {
            // 无负载
        }
    }

    /// <summary>
    /// 断开连接消息 (双向)
    /// </summary>
    public class DisconnectMessage
    {
        /// <summary>断开原因</summary>
        public DisconnectReason Reason;

        /// <summary>附加消息（UTF-8，用于日志/UI 显示）</summary>
        public string Message;

        public DisconnectMessage()
        {
            Message = string.Empty;
        }

        public byte[] Encode()
        {
            byte[] msgBytes = System.Text.Encoding.UTF8.GetBytes(Message ?? string.Empty);
            if (msgBytes.Length > 255)
            {
                Array.Resize(ref msgBytes, 255);
            }
            // Reason(1) + MsgLen(1) + Message
            int size = 1 + 1 + msgBytes.Length;
            byte[] buffer = new byte[size];
            int offset = 0;

            buffer[offset] = (byte)Reason;
            offset += 1;
            buffer[offset] = (byte)msgBytes.Length;
            offset += 1;

            if (msgBytes.Length > 0)
            {
                Buffer.BlockCopy(msgBytes, 0, buffer, offset, msgBytes.Length);
            }

            return buffer;
        }

        public void Decode(byte[] payload)
        {
            int offset = 0;
            Reason = (DisconnectReason)BinaryPacker.ReadByte(payload, offset);
            offset += 1;
            byte msgLen = BinaryPacker.ReadByte(payload, offset);
            offset += 1;

            if (msgLen > 0)
            {
                Message = System.Text.Encoding.UTF8.GetString(payload, offset, msgLen);
            }
            else
            {
                Message = string.Empty;
            }
        }
    }
}
