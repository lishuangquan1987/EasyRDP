#nullable disable
using System;

namespace EasyRDP.Server.Wpf.Services
{
    internal class SessionInfo
    {
        public ServerStreamSession Stream;
        public ServerInputSession Input;

        // ── 审计信息 ──
        /// <summary>通过认证的客户端用户名（审批模式下为客户自报）。</summary>
        public string Username = "";
        /// <summary>客户端远端地址。</summary>
        public string RemoteEndPoint = "";
        /// <summary>会话建立时间（UTC），用于计算连接时长。</summary>
        public DateTime ConnectedAtUtc = DateTime.UtcNow;
        /// <summary>协商编码。</summary>
        public string Codec = "";
        /// <summary>会话建立时的分辨率描述（如 1920x1080）。</summary>
        public string Resolution = "";
        /// <summary>文件通道鉴权 token（v5 新增，握手成功时生成）。</summary>
        public byte[] SessionToken = new byte[0];
    }
}
