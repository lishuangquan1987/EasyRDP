namespace EasyRDP.Server.Wpf.Models
{
    /// <summary>
    /// 服务端连接认证模式。
    /// </summary>
    public enum ServerAuthMode
    {
        /// <summary>口令认证：客户端输入 USER/PASS，服务端校验挑战响应。</summary>
        Password = 0,
        /// <summary>人工审批：客户端无需密码，连接时由服务端操作员 Allow/Deny。</summary>
        Approve = 1
    }
}
