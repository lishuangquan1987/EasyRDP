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

        public ServerSettings()
        {
            Port = "2000";
            Username = "";
            Password = "";
            // 默认原始方式：保持与历史版本完全一致的行为，避免引入潜在回归
            ChangeDetectionMode = ChangeDetectionMode.FullFrameMemcmp;
        }
    }
}
