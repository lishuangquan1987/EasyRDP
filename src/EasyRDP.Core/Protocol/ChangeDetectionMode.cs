namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 帧变化检测模式。控制 ServerStreamSession 在编码前如何判断"画面是否变化"。
    /// 该枚举由 ServerSettings 持久化，UI 可切换；切换在下次会话建立时生效。
    /// </summary>
    public enum ChangeDetectionMode
    {
        /// <summary>
        /// 原始方式：全帧 memcmp（msvcrt.dll）逐字节比较。
        /// 静态帧 ~1ms 跳过编码；任意字节不同即整帧编码。
        /// 适合：完全静态桌面占主导、CPU 紧张的 XP 弱机。
        /// </summary>
        FullFrameMemcmp = 0,

        /// <summary>
        /// 改进方式（路径 1）：32×32 块级 CRC32 哈希对比。
        /// 静态帧跳过编码；变化块数低于阈值（如光标残影/局部闪烁）时也跳过编码，
        /// 避免少量像素差异触发整帧 H.264 重编码（150-250ms）。
        /// 适合：大部分时间静态、偶有局部变化的桌面（代码编辑、文档阅读）。
        /// </summary>
        BlockHashDirtyRect = 1
    }
}
