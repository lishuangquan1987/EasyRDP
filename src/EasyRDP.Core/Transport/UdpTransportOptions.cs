using System;

namespace EasyRDP.Core.Transport
{
    /// <summary>
    /// UDP 传输可配置参数。
    /// 所有字段均有合理默认值，可直接 new 使用或按需覆盖。
    /// </summary>
    public class UdpTransportOptions
    {
        // ── 客户端 / 服务端共用 ──────────────────────────

        /// <summary>发送超时（毫秒）。默认 5000。</summary>
        public int SendTimeoutMs { get; set; }

        /// <summary>接收超时（毫秒），用于接收循环的轮询间隔。默认 1000。</summary>
        public int ReceiveTimeoutMs { get; set; }

        /// <summary>Socket 接收缓冲区大小（字节）。默认 65536。</summary>
        public int ReceiveBufferSize { get; set; }

        // ── 客户端专用 ────────────────────────────────────

        /// <summary>注册探测重试次数（连接时发送注册数据报）。默认 3。</summary>
        public int ProbeRetries { get; set; }

        // ── 服务端专用 ────────────────────────────────────

        /// <summary>僵尸会话超时（秒）。客户端断网后超过此时间自动清理。默认 30。</summary>
        public int SessionTimeoutSeconds { get; set; }

        // ── 工厂 ──────────────────────────────────────────

        /// <summary>默认配置实例。</summary>
        public static readonly UdpTransportOptions Default;

        static UdpTransportOptions()
        {
            Default = new UdpTransportOptions();
        }

        /// <summary>
        /// 使用默认值创建配置。
        /// </summary>
        public UdpTransportOptions()
        {
            SendTimeoutMs = 5000;
            ReceiveTimeoutMs = 1000;
            ReceiveBufferSize = 65536;
            ProbeRetries = 3;
            SessionTimeoutSeconds = 30;
        }
    }
}
