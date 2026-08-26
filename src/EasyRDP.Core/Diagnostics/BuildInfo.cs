namespace EasyRDP.Core.Diagnostics
{
    /// <summary>
    /// 构建诊断信息工具：打印程序集版本、exe 构建时间戳与关键修复特征标识。
    /// 用途：两端部署后从日志确认实际运行的二进制版本——此前多次"现象依旧"
    /// 的根因是部署的 exe 未包含工作区修复（git 提交缺失/进程未重启）。
    /// 日志中若看到 flowControlFix=v3 与 requestPayloadFix=v2 即确认含全部修复。
    /// </summary>
    public static class BuildInfo
    {
        /// <summary>
        /// 服务端流控修复版本标识（v3）：EncodeLoop 死锁修复
        /// （超时继续取帧保底 1 FPS、请求消费移后 1:1 编码、流控模式取尽队列保留最新帧）。
        /// </summary>
        public const string FlowControlFixVersion = "v3-2026-08-09";

        /// <summary>
        /// 客户端帧请求修复版本标识（v2）：FramebufferUpdateRequest 改为 1 字节占位 payload
        /// （绕过服务端 MessageReassembler 的空分片保护丢弃），并启用 250ms 心跳请求。
        /// </summary>
        public const string RequestPayloadFixVersion = "v2-1byte-payload";

        /// <summary>
        /// 解码脱同步恢复修复版本标识（v1）：客户端连续解码失败时请求关键帧（IDR），
        /// 服务端收到后强制生成 IDR 快速恢复画面（避免低帧率下等周期性 IDR 的 10~15s 黑屏）。
        /// </summary>
        public const string KeyframeRequestFixVersion = "v1-2026-08-26";

        /// <summary>
        /// 构建描述：程序集版本 + exe 文件写入时间（UTC，即构建时间）+ 可执行文件路径。
        /// 用于与部署侧的 exe 时间戳直接对比。
        /// </summary>
        public static string Describe()
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var ver = asm.GetName().Version;
            string loc = "";
            try { loc = asm.Location; }
            catch { /* 无文件位置（如内存程序集）时保持空 */ }
            string buildUtc = "";
            try
            {
                buildUtc = System.IO.File.GetLastWriteTimeUtc(loc).ToString("yyyy-MM-dd HH:mm:ss");
            }
            catch { /* 位置为空时无法取时间 */ }
            return "asmVer=" + ver + " buildUtc=" + buildUtc + " loc=" + loc;
        }
    }
}
