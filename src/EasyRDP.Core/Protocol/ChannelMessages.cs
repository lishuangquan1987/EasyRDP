namespace EasyRDP.Core.Protocol
{
    using System;
    using System.IO;

    /// <summary>辅助通道类型（握手完成后由客户端另开 TCP 连接打开）。</summary>
    public enum ChannelType : byte
    {
        /// <summary>文件传输通道：显式发送文件与剪贴板文件按需下载共用。</summary>
        FileTransfer = 1
    }

    /// <summary>辅助通道打开结果。</summary>
    public enum ChannelOpenResult : byte
    {
        /// <summary>通道已建立。</summary>
        Ok = 0,
        /// <summary>token 无效（会话已结束或伪造）。</summary>
        InvalidToken = 1,
        /// <summary>协议版本不一致。</summary>
        VersionMismatch = 2,
        /// <summary>不支持的通道类型。</summary>
        UnsupportedType = 3,
        /// <summary>服务端忙（同一会话已有通道时可能返回）。</summary>
        Busy = 4
    }

    /// <summary>
    /// 打开辅助通道请求（客户端 → 服务端）。客户端在握手成功后用独立 TCP 连接
    /// 连同一端口发送本消息，用 HandshakeRes 下发的 SessionToken 鉴权。
    /// 文件通道不占会话名额，也不参与心跳/流控。
    /// </summary>
    public class ChannelOpenReqMessage
    {
        /// <summary>SessionToken 固定长度（字节）。</summary>
        public const int TokenLength = 16;

        /// <summary>客户端协议版本（与握手一致）。</summary>
        public byte Version;

        /// <summary>请求打开的通道类型。</summary>
        public ChannelType ChannelType;

        /// <summary>握手响应下发的会话 token（16 字节）。</summary>
        public byte[] Token = new byte[TokenLength];

        /// <summary>序列化为 payload。</summary>
        public byte[] Pack()
        {
            using (var ms = new MemoryStream(18))
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(Version);
                bw.Write((byte)ChannelType);
                byte[] token = Token ?? new byte[TokenLength];
                bw.Write(token, 0, token.Length);
                return ms.ToArray();
            }
        }

        /// <summary>从 payload 反序列化；长度不足时抛异常（由调用方按协议错误断开）。</summary>
        public static ChannelOpenReqMessage Unpack(byte[] data)
        {
            if (data == null || data.Length < 2 + TokenLength)
                throw new ArgumentException("ChannelOpenReq payload too short");
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                var msg = new ChannelOpenReqMessage
                {
                    Version = br.ReadByte(),
                    ChannelType = (ChannelType)br.ReadByte()
                };
                msg.Token = br.ReadBytes(TokenLength);
                return msg;
            }
        }
    }

    /// <summary>打开辅助通道响应（服务端 → 客户端）。</summary>
    public class ChannelOpenResMessage
    {
        /// <summary>服务端协议版本。</summary>
        public byte Version;

        /// <summary>通道类型（回显请求值）。</summary>
        public ChannelType ChannelType;

        /// <summary>打开结果。</summary>
        public ChannelOpenResult Result;

        /// <summary>通道归属的会话 ID（便于客户端日志定位）。</summary>
        public uint SessionId;

        /// <summary>序列化为 payload。</summary>
        public byte[] Pack()
        {
            using (var ms = new MemoryStream(7))
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(Version);
                bw.Write((byte)ChannelType);
                bw.Write((byte)Result);
                bw.Write(SessionId);
                return ms.ToArray();
            }
        }

        /// <summary>从 payload 反序列化。</summary>
        public static ChannelOpenResMessage Unpack(byte[] data)
        {
            if (data == null || data.Length < 7)
                throw new ArgumentException("ChannelOpenRes payload too short");
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                return new ChannelOpenResMessage
                {
                    Version = br.ReadByte(),
                    ChannelType = (ChannelType)br.ReadByte(),
                    Result = (ChannelOpenResult)br.ReadByte(),
                    SessionId = br.ReadUInt32()
                };
            }
        }
    }
}
