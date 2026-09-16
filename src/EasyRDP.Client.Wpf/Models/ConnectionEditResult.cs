namespace EasyRDP.Client.Wpf.Models
{
    /// <summary>
    /// Edit 对话框的返回结果（由 IDialogService 填充，ViewModel 据此落盘）。
    /// </summary>
    public sealed class ConnectionEditResult
    {
        /// <summary>显示名称（可空，空则回退到 Host）。</summary>
        public string? DisplayName { get; set; }

        /// <summary>连接地址（IP 或主机名）。</summary>
        public string Host { get; set; } = string.Empty;

        /// <summary>连接端口。</summary>
        public string Port { get; set; } = "2000";

        /// <summary>登录用户名（可空）。</summary>
        public string? Username { get; set; }

        /// <summary>登录密码（可空）。</summary>
        public string? Password { get; set; }
    }
}
