namespace EasyRDP.Core.Protocol
{
    using System;

    /// <summary>
    /// 线格式工具。负责 Magic+Type+PayloadLen 外层的装/拆，与分片无关。
    /// 替代旧 MessageReassembler.BuildWireFragment 的 16 字节分片头构造。
    /// </summary>
    public static class Framing
    {
        /// <summary>framing 头字节数：Magic(1)+Type(1)+PayloadLen(4)。</summary>
        public const int HeaderSize = 6;

        /// <summary>把消息类型与 payload 组装为完整线格式消息（小端 PayloadLen）。</summary>
        public static byte[] BuildMessage(byte messageType, byte[] payload)
        {
            if (payload == null)
                payload = new byte[0];

            byte[] wire = new byte[HeaderSize + payload.Length];
            wire[0] = Constants.FrameMagic;
            wire[1] = messageType;

            uint len = (uint)payload.Length;
            wire[2] = (byte)(len & 0xFF);
            wire[3] = (byte)((len >> 8) & 0xFF);
            wire[4] = (byte)((len >> 16) & 0xFF);
            wire[5] = (byte)((len >> 24) & 0xFF);

            if (payload.Length > 0)
                Buffer.BlockCopy(payload, 0, wire, HeaderSize, payload.Length);
            return wire;
        }

        /// <summary>
        /// 从完整线格式消息解析出消息类型与 payload。
        /// 校验 Magic、Type 为已知类型、PayloadLen 不超上限、长度充足。
        /// </summary>
        public static bool TryParse(byte[] message, out byte messageType, out byte[] payload)
        {
            messageType = 0;
            payload = null;

            if (message == null || message.Length < HeaderSize)
                return false;
            if (message[0] != Constants.FrameMagic)
                return false;

            byte type = message[1];
            if (!IsKnownMessageType(type))
                return false;

            uint len = (uint)message[2]
                | ((uint)message[3] << 8)
                | ((uint)message[4] << 16)
                | ((uint)message[5] << 24);
            if (len > (uint)Constants.MaxSafePayloadSize)
                return false;
            if (message.Length < HeaderSize + (int)len)
                return false;

            messageType = type;
            payload = new byte[len];
            if (len > 0)
                Buffer.BlockCopy(message, HeaderSize, payload, 0, (int)len);
            return true;
        }

        /// <summary>
        /// 判断消息类型是否为已知类型。供 MessageFramingBuffer 失步重对齐时
        /// 与 Magic/PayloadLen 联合校验，避免 payload 内 0xE5 被误判为帧头。
        /// </summary>
        public static bool IsKnownMessageType(byte type)
        {
            return type == (byte)MessageType.HandshakeReq
                || type == (byte)MessageType.HandshakeRes
                || type == (byte)MessageType.Keepalive
                || type == (byte)MessageType.InputEvent
                || type == (byte)MessageType.CursorUpdate
                || type == (byte)MessageType.ClipboardSync
                || type == (byte)MessageType.ClipFormatList
                || type == (byte)MessageType.ClipFileContentsReq
                || type == (byte)MessageType.ClipFileContentsRes
                || type == (byte)MessageType.ImageClipboardStart
                || type == (byte)MessageType.ImageClipboardData
                || type == (byte)MessageType.ImageClipboardEnd
                || type == (byte)MessageType.VideoFrame
                || type == (byte)MessageType.FramebufferUpdateRequest
                || type == (byte)MessageType.VideoKeyframeRequest
                || type == (byte)MessageType.DiagnosticInfoRequest
                || type == (byte)MessageType.DiagnosticInfo;
        }
    }
}
