namespace EasyRDP.Client.Wpf.Models
{
    /// <summary>一条已保存的服务器连接配置（Model 层，纯数据）。</summary>
    public class ServerProfile
    {
        /// <summary>配置名称（唯一标识，用于替换/删除）。</summary>
        public string Name { get; set; } = "";

        /// <summary>服务器地址（IP 或主机名）。</summary>
        public string Host { get; set; } = "";

        /// <summary>端口号。</summary>
        public string Port { get; set; } = "2000";

        /// <summary>登录用户名。</summary>
        public string Username { get; set; } = "";

        /// <summary>登录密码（写入磁盘前经 Windows DPAPI 加密，仅当前用户可解密）。</summary>
        public string Password { get; set; } = "";

        /// <summary>复制一份（避免列表项被外部修改）。</summary>
        public ServerProfile Clone()
        {
            return new ServerProfile
            {
                Name = Name,
                Host = Host,
                Port = Port,
                Username = Username,
                Password = Password
            };
        }
    }
}
