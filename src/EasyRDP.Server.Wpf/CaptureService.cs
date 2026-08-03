#nullable disable
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
        // 生命周期锁：Start/Stop 在并发会话接入/断开下必须串行（防止检查-执行竞态产生双线程）
        private readonly object _lifecycleLock = new object();

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
            lock (_lifecycleLock)
            {
                if (_running) return;
                Logger.Info("CaptureService starting with interval={0}ms", _frameIntervalMs);
                _running = true;
                _captureThread = new Thread(CaptureLoop);
                _captureThread.IsBackground = true;
                _captureThread.Start();
            }
        }

        /// <summary>Stops the capture thread and waits for it to terminate.</summary>
        public void Stop()
        {
            lock (_lifecycleLock)
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
            int errorCount = 0;
            // 只捕获主屏：会话握手尺寸/编码器尺寸/鼠标坐标空间均以主屏为准，
            // 捕获整个虚拟桌面会导致帧尺寸超过会话预分配缓冲（全部丢帧=黑屏），
            // 且多显示器时鼠标坐标与画面内容错位。IncludeCursor=false：
            // DXGI/BitBlt 捕获均不含光标，光标由 CursorTracker 叠加层单独同步。
            var options = new CaptureOptions { IncludeCursor = false, TargetDisplay = 0 };
            while (_running)
            {
                try
                {
                    ScreenFrame frame = _capturer.CaptureScreen(options);
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
                    // 捕获成功后重置错误计数：每次故障突发都能记录首条警告
                    if (errorCount > 0)
                    {
                        Logger.Info("CaptureLoop: recovered after {0} errors", errorCount);
                        errorCount = 0;
                    }
                }
                catch (Exception ex)
                {
                    // 桌面不可用（锁屏/RDP 断开等）时每次捕获都会失败：
                    // 限频记录，避免 60fps 刷爆日志文件；捕获线程持续重试，桌面恢复后自动继续。
                    errorCount++;
                    if (errorCount == 1 || errorCount % 60 == 0)
                        Logger.Warn(ex, "Capture error — frame skipped (captureCount={0}, errors={1})",
                            captureCount, errorCount);
                }

                Thread.Sleep(_frameIntervalMs);
            }
            Logger.Info("CaptureLoop: exited, total captures={0}", captureCount);
        }
    }
}
