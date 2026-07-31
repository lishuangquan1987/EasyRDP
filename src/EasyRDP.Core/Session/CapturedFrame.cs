namespace EasyRDP.Core.Session
{
    /// <summary>
    /// 截屏线程入队的捕获帧（两级队列第一级元素）。
    /// 截屏回调中从 ScreenFrame.Scan0 拷贝像素到此缓冲（Scan0 回调返回后即被释放）。
    /// Pixels 由 Session 内双缓冲交替提供，非每帧 new。
    /// </summary>
    public struct CapturedFrame
    {
        public byte[] Pixels;
        /// <summary>像素缓冲在会话捕获缓冲数组中的下标（编码完成后用于归还缓冲所有权）。</summary>
        public int BufferIndex;
        public int Width;
        public int Height;
        public long CaptureTimestamp;
    }
}
