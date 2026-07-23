using System;

namespace EasyRDP.Core.Protocol
{
    public class VideoFrameMessage
    {
        public FrameType FrameType;
        public CodecId Codec;
        public ushort Width;
        public ushort Height;
        public uint FrameIndex;
        public byte[] Pixels;

        public VideoFrameMessage()
        {
            Pixels = new byte[0];
        }

        public byte[] Encode()
        {
            int size = 1 + 1 + 2 + 2 + 4 + Pixels.Length;
            byte[] buffer = new byte[size];
            int offset = 0;

            buffer[offset] = (byte)FrameType;
            offset += 1;
            buffer[offset] = (byte)Codec;
            offset += 1;
            BinaryPacker.WriteUInt16LE(buffer, offset, Width);
            offset += 2;
            BinaryPacker.WriteUInt16LE(buffer, offset, Height);
            offset += 2;
            BinaryPacker.WriteUInt32LE(buffer, offset, FrameIndex);
            offset += 4;

            if (Pixels.Length > 0)
            {
                Buffer.BlockCopy(Pixels, 0, buffer, offset, Pixels.Length);
            }

            return buffer;
        }

        public void Decode(byte[] payload)
        {
            int offset = 0;
            FrameType = (FrameType)BinaryPacker.ReadByte(payload, offset);
            offset += 1;
            Codec = (CodecId)BinaryPacker.ReadByte(payload, offset);
            offset += 1;
            Width = BinaryPacker.ReadUInt16LE(payload, offset);
            offset += 2;
            Height = BinaryPacker.ReadUInt16LE(payload, offset);
            offset += 2;
            FrameIndex = BinaryPacker.ReadUInt32LE(payload, offset);
            offset += 4;

            int pixelsLen = payload.Length - offset;
            Pixels = new byte[pixelsLen];
            if (pixelsLen > 0)
            {
                Buffer.BlockCopy(payload, offset, Pixels, 0, pixelsLen);
            }
        }
    }
}