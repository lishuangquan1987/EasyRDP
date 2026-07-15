using System;

namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 握手响应消息 S→C
    /// </summary>
    public class HandshakeResMessage
    {
        /// <summary>握手结果</summary>
        public HandshakeResult Result;

        /// <summary>会话 ID（仅成功时有效）</summary>
        public uint SessionId;

        /// <summary>实际屏幕宽度</summary>
        public ushort ScreenWidth;

        /// <summary>实际屏幕高度</summary>
        public ushort ScreenHeight;

        /// <summary>协商后的压缩类型</summary>
        public CompressType CompressType;

        /// <summary>UDP 屏幕流端口</summary>
        public ushort UdpPort;

        public byte[] Encode()
        {
            // Result(1) + SessionId(4) + ScreenWidth(2) + ScreenHeight(2) + CompressType(1) + UdpPort(2)
            int size = 1 + 4 + 2 + 2 + 1 + 2;
            byte[] buffer = new byte[size];
            int offset = 0;

            buffer[offset] = (byte)Result;
            offset += 1;
            BinaryPacker.WriteUInt32LE(buffer, offset, SessionId);
            offset += 4;
            BinaryPacker.WriteUInt16LE(buffer, offset, ScreenWidth);
            offset += 2;
            BinaryPacker.WriteUInt16LE(buffer, offset, ScreenHeight);
            offset += 2;
            buffer[offset] = (byte)CompressType;
            offset += 1;
            BinaryPacker.WriteUInt16LE(buffer, offset, UdpPort);

            return buffer;
        }

        public void Decode(byte[] payload)
        {
            int offset = 0;
            Result = (HandshakeResult)BinaryPacker.ReadByte(payload, offset);
            offset += 1;
            SessionId = BinaryPacker.ReadUInt32LE(payload, offset);
            offset += 4;
            ScreenWidth = BinaryPacker.ReadUInt16LE(payload, offset);
            offset += 2;
            ScreenHeight = BinaryPacker.ReadUInt16LE(payload, offset);
            offset += 2;
            CompressType = (CompressType)BinaryPacker.ReadByte(payload, offset);
            offset += 1;
            UdpPort = BinaryPacker.ReadUInt16LE(payload, offset);
        }
    }
}
