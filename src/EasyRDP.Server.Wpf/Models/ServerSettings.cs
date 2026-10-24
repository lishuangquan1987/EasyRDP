#nullable disable
using EasyRDP.Core.Protocol;

namespace EasyRDP.Server.Wpf.Models
{
    /// <summary>服务端可持久化设置（Model 层，纯数据）。</summary>
    public class ServerSettings
    {
        /// <summary>监听端口。</summary>
        public string Port { get; set; }

        /// <summary>认证用户名。</summary>
        public string Username { get; set; }

        /// <summary>认证密码（写入磁盘前经 Windows DPAPI 加密，仅当前用户可解密）。</summary>
        public string Password { get; set; }

        /// <summary>
        /// 帧变化检测模式。控制 ServerStreamSession 在编码前如何判断画面是否变化。
        /// FullFrameMemcmp=原始方式（全帧 memcmp），BlockHashDirtyRect=改进方式（32×32 块哈希）。
        /// 切换在下次会话建立时生效。
        /// </summary>
        public ChangeDetectionMode ChangeDetectionMode { get; set; }

        /// <summary>是否随 Windows 登录自动启动（HKCU Run 项，注册表为准）。</summary>
        public bool AutoStart { get; set; }

        /// <summary>程序启动后是否立即开始监听（Approve 模式下无需凭据；Password 模式凭据为空时静默跳过）。</summary>
        public bool AutoListen { get; set; }

        /// <summary>允许局域网 UDP 发现（只暴露名称与 TCP 端口，不含凭据）。</summary>
        public bool AllowDiscovery { get; set; }

        /// <summary>局域网发现时显示的名称（空=使用计算机名）。</summary>
        public string ServerName { get; set; }

        /// <summary>连接认证模式（口令认证 / 人工审批）。</summary>
        public ServerAuthMode AuthMode { get; set; }

        /// <summary>PBKDF2 盐（Base64，16 字节）。首次运行生成后固定，用于挑战-响应派生。</summary>
        public string AuthSalt { get; set; }

        /// <summary>采集显示器索引（0=主屏，1..n=副屏，与 EasyDesk TargetDisplay 一致）。</summary>
        public int CaptureDisplay { get; set; }

        public ServerSettings()
        {
            Port = "2000";
            Username = "";
            Password = "";
            // 默认原始方式：保持与历史版本完全一致的行为，避免引入潜在回归
            ChangeDetectionMode = ChangeDetectionMode.FullFrameMemcmp;
            // 自启需要显式开启（会写注册表）；监听默认开启，打开程序即可接受连接
            AutoStart = false;
            AutoListen = true;
            AllowDiscovery = true;
            ServerName = "";
            // 默认口令认证：与历史版本行为一致；审批模式需显式切换
            AuthMode = ServerAuthMode.Password;
            AuthSalt = "";
            // 默认采集主屏（与历史行为一致）
            CaptureDisplay = 0;
        }
    }
}
