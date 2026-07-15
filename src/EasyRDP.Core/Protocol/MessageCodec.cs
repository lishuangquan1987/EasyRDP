using System;

namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 完整消息（头 + 负载 + 类型化消息对象）
    /// </summary>
    public class Message
    {
        /// <summary>消息头</summary>
        public MessageHeader Header;

        /// <summary>原始负载字节</summary>
        public byte[] Payload;

        /// <summary>解析后的消息对象（未知类型时为 null）</summary>
        public object Body;

        public Message()
        {
            Payload = new byte[0];
            // Body intentionally left null for unknown types
            Body = null;
        }

        /// <summary>
        /// 将消息序列化为完整字节数组（header + payload）。
        /// </summary>
        public byte[] ToBytes()
        {
            byte[] result = new byte[MessageHeader.Size + Payload.Length];
            Header.WriteTo(result, 0);
            if (Payload.Length > 0)
            {
                Buffer.BlockCopy(Payload, 0, result, MessageHeader.Size, Payload.Length);
            }
            return result;
        }
    }

    /// <summary>
    /// 消息编解码器——将字节流与类型化消息对象互转。
    /// </summary>
    public static class MessageCodec
    {
        /// <summary>
        /// 从字节数组解码为 Message。返回 null 表示魔数或版本无效。
        /// </summary>
        public static Message Decode(byte[] rawData)
        {
            if (rawData == null || rawData.Length < MessageHeader.Size)
                return null;

            MessageHeader header = MessageHeader.FromBytes(rawData);
            if (!header.IsValid())
                return null;

            // Reject oversized payloads to prevent memory exhaustion / overflow (DoS)
            if (header.Length > ProtocolConstants.MaxTcpPayload)
                return null;

            byte[] payload = null;
            if (header.Length > 0 && rawData.Length >= MessageHeader.Size + (int)header.Length)
            {
                payload = new byte[header.Length];
                Buffer.BlockCopy(rawData, MessageHeader.Size, payload, 0, (int)header.Length);
            }
            else if (header.Length == 0)
            {
                payload = new byte[0];
            }
            else
            {
                // 负载长度超出数据范围，数据不完整
                return null;
            }

            object body = DecodePayload(header.Type, payload);

            return new Message
            {
                Header = header,
                Payload = payload,
                Body = body
            };
        }

        /// <summary>
        /// 将消息对象编码为完整字节数组（header + payload）。
        /// </summary>
        public static byte[] Encode(MessageType type, uint sequence, object body)
        {
            byte[] payload = EncodePayload(type, body);

            MessageHeader header = new MessageHeader
            {
                Magic = ProtocolConstants.Magic,
                Version = ProtocolConstants.Version,
                Type = type,
                Sequence = sequence,
                Length = (uint)payload.Length
            };

            byte[] result = new byte[MessageHeader.Size + payload.Length];
            header.WriteTo(result, 0);
            if (payload.Length > 0)
            {
                Buffer.BlockCopy(payload, 0, result, MessageHeader.Size, payload.Length);
            }
            return result;
        }

        /// <summary>
        /// 将负载字节解码为类型化消息对象。
        /// </summary>
        public static object DecodePayload(MessageType type, byte[] payload)
        {
            switch (type)
            {
                case MessageType.HandshakeReq:
                {
                    var msg = new HandshakeReqMessage();
                    msg.Decode(payload);
                    return msg;
                }
                case MessageType.HandshakeRes:
                {
                    var msg = new HandshakeResMessage();
                    msg.Decode(payload);
                    return msg;
                }
                case MessageType.ScreenFrame:
                {
                    var msg = new ScreenFrameMessage();
                    msg.Decode(payload);
                    return msg;
                }
                case MessageType.CursorUpdate:
                {
                    var msg = new CursorUpdateMessage();
                    msg.Decode(payload);
                    return msg;
                }
                case MessageType.InputEvent:
                {
                    var msg = new InputEventMessage();
                    msg.Decode(payload);
                    return msg;
                }
                case MessageType.ClipboardData:
                {
                    var msg = new ClipboardDataMessage();
                    msg.Decode(payload);
                    return msg;
                }
                case MessageType.KeepAlive:
                {
                    var msg = new KeepAliveMessage();
                    msg.Decode(payload);
                    return msg;
                }
                case MessageType.KeepAliveAck:
                {
                    var msg = new KeepAliveAckMessage();
                    msg.Decode(payload);
                    return msg;
                }
                case MessageType.FileTransferReq:
                {
                    var msg = new FileTransferReqMessage();
                    msg.Decode(payload);
                    return msg;
                }
                case MessageType.FileTransferData:
                {
                    var msg = new FileTransferDataMessage();
                    msg.Decode(payload);
                    return msg;
                }
                case MessageType.Disconnect:
                {
                    var msg = new DisconnectMessage();
                    msg.Decode(payload);
                    return msg;
                }
                default:
                    return null;
            }
        }

        /// <summary>
        /// 将类型化消息对象编码为负载字节数组。
        /// </summary>
        public static byte[] EncodePayload(MessageType type, object body)
        {
            if (body == null)
                throw new ArgumentNullException("body");

            switch (type)
            {
                case MessageType.HandshakeReq:
                    return ((HandshakeReqMessage)body).Encode();
                case MessageType.HandshakeRes:
                    return ((HandshakeResMessage)body).Encode();
                case MessageType.ScreenFrame:
                    return ((ScreenFrameMessage)body).Encode();
                case MessageType.CursorUpdate:
                    return ((CursorUpdateMessage)body).Encode();
                case MessageType.InputEvent:
                    return ((InputEventMessage)body).Encode();
                case MessageType.ClipboardData:
                    return ((ClipboardDataMessage)body).Encode();
                case MessageType.KeepAlive:
                    return ((KeepAliveMessage)body).Encode();
                case MessageType.KeepAliveAck:
                    return ((KeepAliveAckMessage)body).Encode();
                case MessageType.FileTransferReq:
                    return ((FileTransferReqMessage)body).Encode();
                case MessageType.FileTransferData:
                    return ((FileTransferDataMessage)body).Encode();
                case MessageType.Disconnect:
                    return ((DisconnectMessage)body).Encode();
                default:
                    return new byte[0];
            }
        }
    }
}
