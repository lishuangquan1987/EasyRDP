namespace EasyRDP.Core.Session
{
    using System;
    /// <summary>致命错误事件参数。</summary>
    public class ErrorEventArgs : EventArgs
    {
        public string Message;
        public Exception Exception;

        public ErrorEventArgs(string message, Exception exception = null)
        {
            Message = message;
            Exception = exception;
        }
    }

    /// <summary>
    /// 截屏线程入队的捕获帧（两级队列第一级元素）。
    /// 截屏回调中从 ScreenFrame.Scan0 拷贝像素到此缓冲（Scan0 回调返回后即被释放）。
    /// Pixels 由 Session 内双缓冲交替提供，非每帧 new。
    /// </summary>
    public struct CapturedFrame
    {
        public byte[] Pixels;
        public int Width;
        public int Height;
        public long CaptureTimestamp;
    }

    /// <summary>
    /// 发送队列元素（两级队列第二级）。编码线程产出，发送线程消费。
    /// </summary>
    public struct FrameToSend
    {
        public byte[] Data;
        public bool IsKeyframe;
        public long SequenceNumber;
        public long CaptureTimestamp;
    }
}
