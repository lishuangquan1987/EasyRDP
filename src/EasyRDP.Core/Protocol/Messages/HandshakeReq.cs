using System;

namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 握手请求消息 C→S
    /// </summary>
    public class HandshakeReqMessage
    {
        /// <summary>认证令牌</summary>
        public string AuthToken;

        /// <summary>期望屏幕宽度</summary>
        public ushort ScreenWidth;

        /// <summary>期望屏幕高度</summary>
        public ushort ScreenHeight;

        /// <summary>支持的压缩类型</summary>
        public CompressType CompressType;

        public HandshakeReqMessage()
        {
            AuthToken = string.Empty;
        }

        public byte[] Encode()
        {
            int tokenLen = string.IsNullOrEmpty(AuthToken) ? 0 : BinaryPacker.MeasureStringUTF8(AuthToken);
            // payload: AuthLen(2) + AuthToken + ScreenWidth(2) + ScreenHeight(2) + CompressType(1)
            int size = 2 + tokenLen + 2 + 2 + 1;
            byte[] buffer = new byte[size];
            int offset = 0;

            // 直接写入 token，MeasureStringUTF8 已经包含了 2 字节长度头
            byte[] tokenBuf = new byte[tokenLen];
            BinaryPacker.WriteStringUTF8(tokenBuf, 0, AuthToken);
            Array.Copy(tokenBuf, 0, buffer, offset, tokenLen);
            offset += tokenLen;

            BinaryPacker.WriteUInt16LE(buffer, offset, ScreenWidth);
            offset += 2;
            BinaryPacker.WriteUInt16LE(buffer, offset, ScreenHeight);
            offset += 2;
            buffer[offset] = (byte)CompressType;

            return buffer;
        }

        public void Decode(byte[] payload)
        {
            int offset = 0;
            int bytesRead;
            AuthToken = BinaryPacker.ReadStringUTF8(payload, offset, out bytesRead);
            offset += bytesRead;
            ScreenWidth = BinaryPacker.ReadUInt16LE(payload, offset);
            offset += 2;
            ScreenHeight = BinaryPacker.ReadUInt16LE(payload, offset);
            offset += 2;
            CompressType = (CompressType)BinaryPacker.ReadByte(payload, offset);
        }
    }
}
