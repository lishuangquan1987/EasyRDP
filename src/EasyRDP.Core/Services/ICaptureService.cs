namespace EasyRDP.Core.Services
{
    using System;
    using EasyDesk.Core;
    using EasyDesk.Core.Models;
    /// <summary>
    /// 全局捕获服务。单例，拥有独立截屏线程，通过 FrameCaptured 事件分发给所有 Session。
    /// </summary>
    public interface ICaptureService : IDisposable
    {
        void Start();
        void Stop();
        bool IsRunning { get; }
        int FrameIntervalMs { get; set; }
        event Action<ScreenFrame> FrameCaptured;
        DesktopBounds GetPrimaryScreen();
    }
}
