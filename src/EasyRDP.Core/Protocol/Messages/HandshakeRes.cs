using System;

namespace EasyRDP.Core.Protocol
{
    public class HandshakeResMessage
    {
        public HandshakeResult Result;
        public uint SessionId;
        public ushort ScreenWidth;
        public ushort ScreenHeight;
        public CompressType CompressType;
        public CodecId NegotiatedCodec;

        public HandshakeResMessage()
        {
            NegotiatedCodec = CodecId.Bitmap;
        }

        public byte[] Encode()
        {
            bool hasCodec = NegotiatedCodec != CodecId.Bitmap;
            int size = 1 + 4 + 2 + 2 + 1 + (hasCodec ? 1 : 0);
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

            if (hasCodec)
            {
                buffer[offset] = (byte)NegotiatedCodec;
            }

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

            bool hasCodec = offset < payload.Length;
            NegotiatedCodec = hasCodec ? (CodecId)payload[offset] : CodecId.Bitmap;
        }
    }
}