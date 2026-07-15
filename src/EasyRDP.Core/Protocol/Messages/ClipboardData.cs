using System;

namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 剪贴板同步消息 (双向)
    /// </summary>
    public class ClipboardDataMessage
    {
        /// <summary>剪贴板数据格式</summary>
        public ClipboardFormat Format;

        /// <summary>UTF-16LE 编码的文本数据</summary>
        public string Text;

        public ClipboardDataMessage()
        {
            Text = string.Empty;
        }

        public byte[] Encode()
        {
            byte[] textBytes = System.Text.Encoding.Unicode.GetBytes(Text ?? string.Empty);
            // Format(1) + DataLen(4) + Text
            int size = 1 + 4 + textBytes.Length;
            byte[] buffer = new byte[size];
            int offset = 0;

            buffer[offset] = (byte)Format;
            offset += 1;
            BinaryPacker.WriteUInt32LE(buffer, offset, (uint)textBytes.Length);
            offset += 4;

            if (textBytes.Length > 0)
            {
                Buffer.BlockCopy(textBytes, 0, buffer, offset, textBytes.Length);
            }

            return buffer;
        }

        public void Decode(byte[] payload)
        {
            if (payload == null || payload.Length < 5)
            {
                Text = string.Empty;
                return;
            }

            int offset = 0;
            Format = (ClipboardFormat)BinaryPacker.ReadByte(payload, offset);
            offset += 1;
            uint dataLen = BinaryPacker.ReadUInt32LE(payload, offset);
            offset += 4;

            if (dataLen > 0 && offset + (int)dataLen <= payload.Length)
            {
                Text = System.Text.Encoding.Unicode.GetString(payload, offset, (int)dataLen);
            }
            else
            {
                Text = string.Empty;
            }
        }
    }
}
