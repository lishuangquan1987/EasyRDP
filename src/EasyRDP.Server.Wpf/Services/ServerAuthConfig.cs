#nullable disable
using EasyRDP.Server.Wpf.Models;

namespace EasyRDP.Server.Wpf.Services
{
    /// <summary>服务端连接认证配置（启动时由 UI 设置注入 TransportHost）。</summary>
    public class ServerAuthConfig
    {
        /// <summary>认证模式：口令认证 / 人工审批。</summary>
        public ServerAuthMode Mode { get; set; }
        /// <summary>允许连接的用户名（口令认证模式下校验）。</summary>
        public string Username { get; set; }
        /// <summary>口令（仅存在于内存，落盘由 ServerSettingsStore 做 DPAPI 加密）。</summary>
        public string Password { get; set; }
        /// <summary>PBKDF2 盐（16 字节）。空时每次挑战使用随机盐（仍安全，但不便于固定预计算防护）。</summary>
        public byte[] Salt { get; set; }

        public ServerAuthConfig()
        {
            Mode = ServerAuthMode.Password;
            Username = "";
            Password = "";
            Salt = new byte[0];
        }
    }
}
