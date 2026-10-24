namespace EasyRDP.Core.Protocol
{
    /// <summary>协议常量。</summary>
    public static class Constants
    {
        /// <summary>Protocol version identifier used during handshake.</summary>
        /// <remarks>
        /// v3 → v4：VideoFrameMessage 头新增 ContentWidth/ContentHeight 字段
        /// （内容坐标空间与编码分辨率分离，修复 D11 降采样后鼠标偏移）。
        /// v4 → v5：握手改为挑战-响应认证（HandshakeReq 携带 ClientNonce，
        /// 服务端下发 AuthChallenge，客户端回 HandshakeProof），HandshakeRes 末尾
        /// 追加 16 字节 SessionToken（文件通道鉴权）；新增 ChannelOpen / FileTransfer 消息。
        /// 新旧版本不兼容（握手布局不同），握手版本检查会明确拒绝混用。
        /// </remarks>
        public const byte ProtocolVersion = 0x05;
        /// <summary>Frame magic byte used for message framing.</summary>
        public const byte FrameMagic = 0xE5;
        /// <summary>Maximum allowed frame size (50 MB).</summary>
        public const int MaxFrameSize = 50 * 1024 * 1024;
        /// <summary>安全 payload 上限（超出拒绝，防 DoS 内存耗尽）。</summary>
        public const int MaxSafePayloadSize = 10 * 1024 * 1024;
    }
}
