using System;

namespace EasyRDP.Core.Transport
{
    /// <summary>
    /// TCP 传输可配置参数。
    /// 所有字段均有合理默认值，可直接 new 使用或按需覆盖。
    /// </summary>
    public class TcpTransportOptions
    {
        // ── 客户端 / 服务端共用 ──────────────────────────

        /// <summary>发送缓冲区大小（字节）。默认 8192。</summary>
        public int SendBufferSize { get; set; }

        /// <summary>接收缓冲区大小（字节）。默认 8192。</summary>
        public int ReceiveBufferSize { get; set; }

        /// <summary>禁用 Nagle 算法（NoDelay=true 降低延迟，适合实时场景）。默认 true。</summary>
        public bool NoDelay { get; set; }

        /// <summary>发送超时（毫秒），-1 表示无超时。默认 -1。</summary>
        public int SendTimeoutMs { get; set; }

        /// <summary>接收超时（毫秒），-1 表示无超时（阻塞）。默认 -1。</summary>
        public int ReceiveTimeoutMs { get; set; }

        /// <summary>连接超时（毫秒）。默认 5000。</summary>
        public int ConnectTimeoutMs { get; set; }

        // ── 服务端专用 ────────────────────────────────────

        /// <summary>监听队列长度。默认 100。</summary>
        public int Backlog { get; set; }

        /// <summary>最大客户端数，0 表示不限制。默认 0。</summary>
        public int MaxClients { get; set; }

        // ── 工厂 ──────────────────────────────────────────

        /// <summary>默认配置实例。</summary>
        public static readonly TcpTransportOptions Default;

        static TcpTransportOptions()
        {
            Default = new TcpTransportOptions();
        }

        /// <summary>
        /// 使用默认值创建配置。
        /// </summary>
        public TcpTransportOptions()
        {
            SendBufferSize = 8192;
            ReceiveBufferSize = 8192;
            NoDelay = true;
            SendTimeoutMs = -1;
            ReceiveTimeoutMs = -1;
            ConnectTimeoutMs = 5000;
            Backlog = 100;
            MaxClients = 0;
        }
    }
}
