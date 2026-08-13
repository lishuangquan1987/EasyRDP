namespace EasyRDP.Core.Protocol
{
    /// <summary>协议常量。</summary>
    public static class Constants
    {
        /// <summary>Protocol version identifier used during handshake.</summary>
        public const byte ProtocolVersion = 0x03;
        /// <summary>Frame magic byte used for message framing.</summary>
        public const byte FrameMagic = 0xE5;
        /// <summary>Maximum allowed frame size (50 MB).</summary>
        public const int MaxFrameSize = 50 * 1024 * 1024;
        /// <summary>安全 payload 上限（超出拒绝，防 DoS 内存耗尽）。</summary>
        public const int MaxSafePayloadSize = 10 * 1024 * 1024;
    }
}
