namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 帧编码器标识。同时用于配置文件、握手协商、协议扩展字节。
    /// </summary>
    /// <remarks>
    /// <para>值域设计为可在握手响应中以 1 字节携带，向后兼容（老协议不含此字段时默认 <see cref="Bitmap"/>）。</para>
    /// <para>添加新编码后端时在此枚举追加值，并同步更新 <see cref="CodecCapabilities"/> 与 <see cref="CodecNegotiator"/>。</para>
    /// </remarks>
    public enum CodecId : byte
    {
        /// <summary>位图编码（脏矩形 + Zlib/JPEG）。net40 / net8.0 通用，XP 兼容。</summary>
        Bitmap = 0,

        /// <summary>OpenH264 软件编码（Cisco BSD，XP 兼容）。仅 net8.0 服务端可用。</summary>
        H264Software = 1,

        /// <summary>硬件编码（NVENC/QSV/Media Foundation）。Win8+，仅 net8.0 服务端可用（B-4 阶段）。</summary>
        H264Hardware = 2
    }
}
