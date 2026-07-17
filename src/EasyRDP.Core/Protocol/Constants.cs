using System;

namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// EasyRDP 协议常量定义
    /// </summary>
    public static class ProtocolConstants
    {
        /// <summary>魔数 "ERDP" (Little-Endian)</summary>
        public const uint Magic = 0x45524450;

        /// <summary>当前协议版本</summary>
        public const byte Version = 0x01;

        /// <summary>消息头字节数</summary>
        public const int HeaderSize = 14;

        /// <summary>消息最大负载字节数（100MB，支持 4K 屏幕全帧）</summary>
        public const int MaxPayload = 104857600;

        /// <summary>默认服务端口</summary>
        public const int DefaultPort = 8750;

        /// <summary>心跳发送间隔（毫秒）</summary>
        public const int KeepAliveIntervalMs = 5000;

        /// <summary>客户端心跳超时（毫秒）</summary>
        public const int KeepAliveTimeoutMs = 15000;

        /// <summary>服务端心跳超时（毫秒）</summary>
        public const int ServerTimeoutMs = 20000;

        /// <summary>剪贴板同步静默期（毫秒），防止死循环</summary>
        public const int ClipboardCooldownMs = 500;

        /// <summary>文件传输默认分块大小</summary>
        public const int DefaultBlockSize = 8192;
    }
}
