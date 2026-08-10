namespace EasyRDP.Core.Protocol
{
    /// <summary>协议常量。</summary>
    public static class Constants
    {
        /// <summary>Protocol version identifier used during handshake.</summary>
        public const byte ProtocolVersion = 0x02;
        /// <summary>Frame magic byte used for message framing.</summary>
        public const byte FrameMagic = 0xE5;
        /// <summary>Maximum allowed frame size (50 MB).</summary>
        public const int MaxFrameSize = 50 * 1024 * 1024;
        /// <summary>Maximum size of a single fragment (16384 bytes).
        /// 1400 → 16384：TCP 流协议下大分片显著降低每帧的分片数量与发送开销——
        /// 实测 4.8MB 首帧 3444 片 → 295 片（弱机发送 3.3s → ~300ms），
        /// 帧间延迟减半。接收端 FramingBuffer 无分片长度字段（非末片按此常量推断，
        /// 末片按 totalPayloadLen 计算），两端同版本 Core.dll 自动一致。
        /// 10MB 最大帧 / 16KB = 640 片，远低于 reassembler 的 4096 分片上限与 ushort 上限。</summary>
        public const int FragmentSize = 16384;
        /// <summary>Timeout in milliseconds for fragment reassembly (5s for large raw frames).</summary>
        public const int FragmentReassembleTimeoutMs = 5000;
        /// <summary>安全 payload 上限（超出拒绝，防 DoS 内存耗尽）。</summary>
        public const int MaxSafePayloadSize = 10 * 1024 * 1024;
    }
}
