using System;
using System.Threading;
using EasyDesk.Core;
using EasyDesk.Core.Models;
using EasyRDP.Core.Services;
using NLog;

namespace EasyRDP.Server.Wpf
{
    /// <summary>
    /// 全局捕获服务。持有 IScreenCapturer（EasyDesk），独立截屏线程分发帧事件。
    /// D10：启动时探测镜像驱动，已装则用镜像驱动，未装回退 BitBlt。
    /// </summary>
    public class CaptureService : ICaptureService
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

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
            Logger.Info("CaptureService starting with interval={0}ms", _frameIntervalMs);
            _running = true;
            _captureThread = new Thread(CaptureLoop);
            _captureThread.IsBackground = true;
            _captureThread.Start();
        }

        /// <summary>Stops the capture thread and waits for it to terminate.</summary>
        public void Stop()
        {
            Logger.Info("CaptureService stopping");
            _running = false;
            if (_captureThread != null)
            {
                if (!_captureThread.Join(3000))
                {
                    Logger.Warn("Capture thread timeout (3s) — abandoned");
                    // Timeout — thread stuck, abandon
                }
                _captureThread = null;
            }
            Logger.Info("CaptureService stopped");
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
            int captureCount = 0;
            while (_running)
            {
                try
                {
                    ScreenFrame frame = _capturer.CaptureScreen();
                    captureCount++;
                    var handler = FrameCaptured;
                    if (handler != null)
                    {
                        if (captureCount == 1 || captureCount % 300 == 0)
                            Logger.Info("CaptureLoop: firing FrameCaptured #{0} res={1}x{2} scan0=0x{3:X}",
                                captureCount, frame.Width, frame.Height, frame.Scan0.ToInt64());
                        handler(frame);
                    }
                    if (frame.Scan0 != IntPtr.Zero)
                    {
                        System.Runtime.InteropServices.Marshal.FreeHGlobal(frame.Scan0);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Capture error — frame skipped (captureCount={0})", captureCount);
                    // Capture error — skip frame
                }

                Thread.Sleep(_frameIntervalMs);
            }
            Logger.Info("CaptureLoop: exited, total captures={0}", captureCount);
        }
    }
}
