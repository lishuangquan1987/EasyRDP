using System;

namespace EasyRDP.Core.Protocol
{
    public class HandshakeReqMessage
    {
        public string AuthToken;
        public ushort ScreenWidth;
        public ushort ScreenHeight;
        public CompressType CompressType;
        public CodecCapabilities Capabilities;

        public HandshakeReqMessage()
        {
            AuthToken = string.Empty;
            Capabilities = CodecCapabilities.Legacy;
        }

        public byte[] Encode()
        {
            int tokenLen = BinaryPacker.MeasureStringUTF8(AuthToken);
            bool hasCaps = Capabilities != CodecCapabilities.Legacy;
            int size = tokenLen + 2 + 2 + 1 + (hasCaps ? 1 : 0);
            byte[] buffer = new byte[size];
            int offset = 0;

            BinaryPacker.WriteStringUTF8(buffer, offset, AuthToken);
            offset += tokenLen;

            BinaryPacker.WriteUInt16LE(buffer, offset, ScreenWidth);
            offset += 2;
            BinaryPacker.WriteUInt16LE(buffer, offset, ScreenHeight);
            offset += 2;
            buffer[offset] = (byte)CompressType;
            offset += 1;

            if (hasCaps)
            {
                buffer[offset] = (byte)Capabilities;
            }

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
            offset += 1;

            bool hasCaps = offset < payload.Length;
            Capabilities = hasCaps ? (CodecCapabilities)payload[offset] : CodecCapabilities.Legacy;
        }
    }
}