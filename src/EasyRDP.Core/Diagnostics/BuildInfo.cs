namespace EasyRDP.Core.Diagnostics
{
    /// <summary>
    /// 构建诊断信息工具：打印程序集版本、exe 构建时间戳与关键修复特征标识。
    /// 用途：两端部署后从日志确认实际运行的二进制版本——此前多次"现象依旧"
    /// 的根因是部署的 exe 未包含工作区修复（git 提交缺失/进程未重启）。
    /// 日志中若看到 flowControlFix=v3、requestPayloadFix=v2 与 zrleFullFrameFix=v1
    /// 即确认含全部修复。
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
        /// ZRLE 全量帧修复版本标识（v1）：ZrleEncoder 尊重 forceKeyframe 强制全量编码。
        /// 修复 D14 H264→ZRLE 切回时客户端基线不重建导致的持久花屏
        /// （v2 曾忽略 forceKeyframe，使 ServerStreamSession._forceZrleKeyNext 形同虚设）。
        /// </summary>
        public const string ZrleFullFrameFixVersion = "v1-2026-09-09";

        /// <summary>
        /// ZRLE 基线漂移修复版本标识（v1）：发送队列丢帧/渲染丢帧后强制全量渲染，
        /// 并禁用 CopyRect 防止漂移被放大，修复 D14 切回后仍残留的 Ghost/拖影花屏。
        /// </summary>
        public const string ZrleDivergenceFixVersion = "v1-2026-09-10";

        /// <summary>
        /// TCP 帧同步修复版本标识（v1）：TcpTransport.Send 取消"大消息分块写+块间释放锁"
        /// 的队头阻塞优化，改为整条消息持锁一次性写入。旧实现会让控制消息（光标更新等）
        /// 在视频帧块间隙插队，TCP 字节流无消息边界 → 交错字节被接收端当作大帧 payload
        /// 消费 → ZRLE 数据污染 → 解码失败/丢帧 → 客户端基线漂移 → 花屏
        /// （日志实证：所有 ZRLE 解码失败帧均 >64KB，且失败前必有 "discarding 35 bytes"）。
        /// </summary>
        public const string TcpFramingFixVersion = "v1-2026-09-20";

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
