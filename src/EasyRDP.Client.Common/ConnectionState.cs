namespace EasyRDP.Client.Common
{
    /// <summary>
    /// 客户端连接状态。
    /// </summary>
    public enum ConnectionState
    {
        /// <summary>未连接</summary>
        Disconnected,

        /// <summary>正在建立 TCP 连接 + 握手中</summary>
        Connecting,

        /// <summary>握手成功，可收发数据</summary>
        Connected,

        /// <summary>正在断开</summary>
        Disconnecting
    }
}
