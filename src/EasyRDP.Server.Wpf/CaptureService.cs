using System;
using System.Threading;
using EasyDesk.Core;
using EasyDesk.Core.Models;
using EasyRDP.Core.Services;

namespace EasyRDP.Server.Wpf
{
    /// <summary>
    /// 全局捕获服务。持有 IScreenCapturer（EasyDesk），独立截屏线程分发帧事件。
    /// D10：启动时探测镜像驱动，已装则用镜像驱动，未装回退 BitBlt。
    /// </summary>
    public class CaptureService : ICaptureService
    {
        private readonly IScreenCapturer _capturer;
        private Thread _captureThread;
        private volatile bool _running;
        private int _frameIntervalMs = 16; // ~60fps

        /// <summary>Gets whether the capture loop is currently running.</summary>
        public bool IsRunning { get { return _running; } }

        /// <summary>Gets or sets the interval in milliseconds between screen captures.</summary>
        public int FrameIntervalMs
        {
            get { return _frameIntervalMs; }
            set { _frameIntervalMs = value; }
        }

        public event Action<ScreenFrame> FrameCaptured;

        public CaptureService(IScreenCapturer capturer)
        {
            if (capturer == null)
                throw new ArgumentNullException("capturer");
            _capturer = capturer;
        }

        /// <summary>Starts the capture thread if not already running.</summary>
        public void Start()
        {
            if (_running) return;
            _running = true;
            _captureThread = new Thread(CaptureLoop);
            _captureThread.IsBackground = true;
            _captureThread.Start();
        }

        /// <summary>Stops the capture thread and waits for it to terminate.</summary>
        public void Stop()
        {
            _running = false;
            if (_captureThread != null)
            {
                if (!_captureThread.Join(3000))
                {
                    // Timeout — thread stuck, abandon
                }
                _captureThread = null;
            }
        }

        public DesktopBounds GetPrimaryScreen()
        {
            return _capturer.GetPrimaryScreen();
        }

        /// <summary>Disposes the service by stopping the capture thread.</summary>
        public void Dispose()
        {
            Stop();
        }

        private void CaptureLoop()
        {
            while (_running)
            {
                try
                {
                    ScreenFrame frame = _capturer.CaptureScreen();
                    var handler = FrameCaptured;
                    if (handler != null)
                    {
                        handler(frame);
                    }
                    if (frame.Scan0 != IntPtr.Zero)
                    {
                        System.Runtime.InteropServices.Marshal.FreeHGlobal(frame.Scan0);
                    }
                }
                catch (Exception)
                {
                    // Capture error — skip frame
                }

                Thread.Sleep(_frameIntervalMs);
            }
        }
    }
}
