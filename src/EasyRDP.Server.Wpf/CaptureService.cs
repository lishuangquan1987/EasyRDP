#nullable disable
using System;
using System.Diagnostics;
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

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        // msvcrt.memcmp — Windows 内置 CRT 内存比较（返回 0 表示相等）。byte[] 自动 pin 后传首地址。
        [System.Runtime.InteropServices.DllImport("msvcrt.dll", EntryPoint = "memcmp", SetLastError = false)]
        private static extern int Memcmp(byte[] a, byte[] b, System.IntPtr count);

        private readonly IScreenCapturer _capturer;
        private Thread _captureThread;
        private volatile bool _running;
        private int _frameIntervalMs = 16; // ~60fps
        // 捕获/编码分辨率上限：0 = 不降分辨率，按屏幕原生分辨率捕获与编码。
        // >0 时用 GDI StretchBlt 一步完成“截屏 + 降采样”，把昂贵的托管逐像素
        // 缩放（原 DownscaleBgra，弱机单核上 1080p 实测 100~300ms/帧）卸载给
        // 显示驱动。D11 运行时自适应在编码持续超速时把此值从 0 降到 1920/1280
        // （1280 为清晰度底线，不再降 960），
        // 恢复后升回 0。由 ServerStreamSession 通过 SetCaptureMaxWidth 动态设置。
        private volatile int _captureMaxWidth;
        // 捕获宽度变化信号：编码线程设置后，捕获线程在下一轮迭代立即重算目标分辨率
        // （避免等待每 600 帧一次的 bounds 周期刷新，降档延迟最多约一个截屏周期 ~16ms）。
        private volatile bool _captureWidthDirty;
        // 生命周期锁：Start/Stop 在并发会话接入/断开下必须串行（防止检查-执行竞态产生双线程）
        private readonly object _lifecycleLock = new object();

        // ── 静止跳过截屏（D13）──
        // 弱机 BitBlt 全帧截屏实测 250~300ms/帧（Win7 虚拟机 + Hyper-V 虚拟显卡），
        // 是 FPS 上不去的主因。桌面静止时用"低成本缩略图"探测变化，静止则跳过
        // 昂贵的全帧 StretchBlt；只有检测到变化才做全帧截屏。这样静置时截屏开销
        // 从 ~300ms/帧 降到 ~几 ms/帧，编码线程不再空等，FPS 得以释放给实际变化。
        // 缩略图：全帧等比缩小 N 倍的 BGRA 像素，memcmp 批量对比。
        private const int ThumbDivisor = 8;                 // 缩略图边长 = 原尺寸/8
        private const int ThumbKeepaliveIntervalMs = 500;   // 静止时强制全帧截屏的间隔（保活）
        private int _thumbW, _thumbH;                       // 当前缩略图尺寸
        private byte[] _thumbBuffer;                        // 最新缩略图 BGRA 像素（复用缓冲）
        private byte[] _thumbPrev;                          // 上一参考缩略图 BGRA 像素（比较基准）
        private bool _thumbReady;                           // 缩略图是否已初始化（首帧需全截）
        private long _thumbLastKeepaliveTicks;              // 上次全帧截屏时刻（保活节流）
        private int _thumbCaptureCount;                     // 缩略图采样次数（诊断）

        /// <summary>Gets whether the capture loop is currently running.</summary>
        public bool IsRunning { get { return _running; } }

        /// <summary>Gets or sets the interval in milliseconds between screen captures.</summary>
        public int FrameIntervalMs
        {
            get { return _frameIntervalMs; }
            set { _frameIntervalMs = value; }
        }

        /// <summary>Gets the current capture/encode max width (0 = full native resolution).</summary>
        public int CaptureMaxWidth
        {
            get { return _captureMaxWidth; }
        }

        /// <summary>
        /// 动态设置捕获分辨率上限（D11 自适应调用）。0 = 全分辨率；
        /// >0 = 用 StretchBlt 直接按该宽度等比降采样捕获，编码线程不再做托管缩放。
        /// 线程安全：编码线程调用，捕获线程下一轮迭代读取。
        /// </summary>
        public void SetCaptureMaxWidth(int width)
        {
            if (_captureMaxWidth == width) return;
            _captureMaxWidth = width;
            _captureWidthDirty = true;
            Logger.Info("CaptureService: capture max width set to {0} (0=full resolution)", width);
        }

        public event Action<ScreenFrame> FrameCaptured;

        public CaptureService(IScreenCapturer capturer)
        {
            if (capturer == null)
                throw new ArgumentNullException("capturer");
            _capturer = capturer;
            // 采集方式由实际采集器类型判定（DXGI=1，BitBlt=0），供诊断信息下发
            _captureMethod = EasyRDP.Server.Wpf.Services.SystemInfoCollector.DetectCaptureMethod(capturer);
        }

        /// <summary>屏幕采集方式。0=BitBlt(GDI)，1=DXGI Desktop Duplication。供连接详情面板展示。</summary>
        public byte CaptureMethod { get { return _captureMethod; } }
        private readonly byte _captureMethod;

        /// <summary>Starts the capture thread if not already running.</summary>
        public void Start()
        {
            lock (_lifecycleLock)
            {
                if (_running) return;
                // 屏幕指标诊断：输入绝对坐标映射依赖显示布局与 DPI 感知状态。
                // 若 GetSystemMetrics(SM_CXSCREEN) 与捕获物理尺寸不一致，
                // 说明进程 DPI 未感知（逻辑像素 vs 物理像素），鼠标落点会整体偏移。
                try
                {
                    DesktopBounds primary = _capturer.GetPrimaryScreen();
                    Logger.Info("Screen primary: {0}x{1} at ({2},{3})",
                        primary.Width, primary.Height, primary.X, primary.Y);
                    DesktopBounds[] all = _capturer.GetAllScreens();
                    foreach (DesktopBounds s in all)
                    {
                        Logger.Info("Screen monitor: {0}x{1} at ({2},{3}) primary={4}",
                            s.Width, s.Height, s.X, s.Y, s.IsPrimary);
                    }
                    Logger.Info("SystemMetrics: primary={0}x{1} virtual=({2},{3}) {4}x{5}",
                        GetSystemMetrics(0), GetSystemMetrics(1),
                        GetSystemMetrics(76), GetSystemMetrics(77),
                        GetSystemMetrics(78), GetSystemMetrics(79));
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "Screen metrics log failed");
                }
                Logger.Info("CaptureService starting with interval={0}ms", _frameIntervalMs);
                _running = true;
                _captureThread = new Thread(CaptureLoop);
                _captureThread.IsBackground = true;
                // 降优先级：截屏是后台任务，不能让编码/输入处理等关键线程饿死
                // （Win7 弱机 CPU 饱和时，输入响应延迟主要来自线程调度竞争）。
                _captureThread.Priority = ThreadPriority.BelowNormal;
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

            // 计算目标捕获分辨率：屏幕尺寸超限时等比例缩小（宽高取偶数满足 I420）。
            // 直接按目标分辨率 StretchBlt，编码线程不再做托管软件缩放。
            int screenX = 0, screenY = 0, screenW = 0, screenH = 0;
            int targetW = 0, targetH = 0;
            Func<bool> refreshCaptureBounds = delegate
            {
                try
                {
                    DesktopBounds primary = _capturer.GetPrimaryScreen();
                    screenX = primary.X;
                    screenY = primary.Y;
                    screenW = primary.Width;
                    screenH = primary.Height;
                    int newW = screenW;
                    int newH = screenH;
                    if (CaptureMaxWidth > 0 && newW > CaptureMaxWidth)
                    {
                        newH = Math.Max(1, (int)((long)newH * CaptureMaxWidth / newW));
                        newW = CaptureMaxWidth;
                    }
                    newW = (newW + 1) & ~1;
                    newH = (newH + 1) & ~1;
                    if (newW != targetW || newH != targetH)
                        Logger.Info("CaptureLoop: target resolution {0}x{1} (screen {2}x{3})",
                            newW, newH, screenW, screenH);
                    targetW = newW;
                    targetH = newH;
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "CaptureLoop: failed to compute capture bounds, falling back to CaptureScreen");
                    targetW = 0;
                    targetH = 0;
                    return false;
                }
            };
            refreshCaptureBounds();
            // 约每 10 秒（60fps × 600 次）重新读取屏幕边界，应对运行中分辨率切换/显示器热插拔
            int boundsRefreshCounter = 0;

            while (_running)
            {
                try
                {
                    // D11 降/升档时立即重算目标分辨率（不等 600 帧周期刷新），
                    // 使 StretchBlt 降采样尽快生效，避免编码线程继续做托管缩放。
                    if (_captureWidthDirty)
                    {
                        _captureWidthDirty = false;
                        refreshCaptureBounds();
                        boundsRefreshCounter = 0;
                    }
                    if (++boundsRefreshCounter >= 600)
                    {
                        boundsRefreshCounter = 0;
                        refreshCaptureBounds();
                    }

                    // D13 静止跳过截屏：先用低成本缩略图探测桌面是否变化。
                    // 静止 → 跳过昂贵的全帧 StretchBlt（仅间隔保活一次全帧，维持连接活跃）；
                    // 有变化 → 走正常全帧截屏路径。
                    // 注意：仅当 bounds 计算成功（targetW>0）时缩略图探测才有意义；
                    // bounds 失败时应走下方 CaptureScreen 兜底分支，不能在此跳过。
                    bool thumbnailChanged = false;
                    if (targetW > 0 && targetH > 0)
                    {
                        thumbnailChanged = ProbeThumbnailChanged(
                            screenX, screenY, screenW, screenH, targetW, targetH);
                    }
                    if (targetW > 0 && targetH > 0
                        && !thumbnailChanged && !_thumbNeedFull())
                    {
                        // 桌面静止：跳过全帧截屏，等待下一个采集周期
                        Thread.Sleep(_frameIntervalMs);
                        continue;
                    }
                    _thumbLastKeepaliveTicks = Stopwatch.GetTimestamp();

                    ScreenFrame frame;
                    if (targetW > 0 && targetH > 0)
                    {
                        // StretchBlt 一步完成截屏 + 缩放：内容坐标空间不变，仅降低像素量
                        frame = _capturer.CaptureScaled(
                            screenX, screenY, screenW, screenH, targetW, targetH);
                    }
                    else
                    {
                        frame = _capturer.CaptureScreen(options);
                    }
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

        /// <summary>
        /// D13 静止跳过截屏——低成本缩略图变化探测。
        /// 用 CaptureScaled 缩略图（目标尺寸 = 全帧/ThumbDivisor）只抓少量像素做 memcmp 批量比较，
        /// 开销 ~几 ms/帧（远小于全帧 BitBlt 的 250~300ms）。返回 true 表示桌面有变化，需要全帧截屏。
        /// 尺寸变化（缩略图重建）时必然返回 true，保证新尺寸首次全截。
        /// 实现细节：抓取到的缩略图是独立 HGlobal 缓冲，比较后立即释放；失败时保守返回 true
        /// （宁可多截一次全帧，也不漏过变化）。双缓冲（_thumbPrev/_thumbBuffer）复用避免分配。
        /// </summary>
        private bool ProbeThumbnailChanged(int sx, int sy, int sw, int sh, int targetW, int targetH)
        {
            int tw = Math.Max(16, targetW / ThumbDivisor);
            int th = Math.Max(16, targetH / ThumbDivisor);
            long swStart = Stopwatch.GetTimestamp();
            try
            {
                ScreenFrame thumb = _capturer.CaptureScaled(sx, sy, sw, sh, tw, th);
                try
                {
                    int thumbBytes = tw * th * 4;
                    if (thumb.Scan0 == IntPtr.Zero || thumbBytes <= 0)
                        return true; // 抓取失败，保守全截

                    bool sizeChanged = !_thumbReady
                        || _thumbW != tw || _thumbH != th
                        || _thumbBuffer == null || _thumbBuffer.Length != thumbBytes;
                    if (sizeChanged)
                    {
                        _thumbW = tw;
                        _thumbH = th;
                        if (_thumbBuffer == null || _thumbBuffer.Length != thumbBytes)
                            _thumbBuffer = new byte[thumbBytes];
                        if (_thumbPrev == null || _thumbPrev.Length != thumbBytes)
                            _thumbPrev = new byte[thumbBytes];
                        // 尺寸变化/首次：无参考缩略图，返回有变化（触发全截）
                        System.Runtime.InteropServices.Marshal.Copy(
                            thumb.Scan0, _thumbPrev, 0, thumbBytes);
                        _thumbReady = true;
                        _thumbCaptureCount++;
                        return true;
                    }

                    // 拷贝最新缩略图到 _thumbBuffer，再用 memcmp 与上一参考 _thumbPrev 批量比较
                    // （82KB 级 memcmp 走 CRT 优化路径 ~微秒，远快于逐字节 ReadByte 的 interop 开销）。
                    System.Runtime.InteropServices.Marshal.Copy(
                        thumb.Scan0, _thumbBuffer, 0, thumbBytes);
                    bool changed;
                    try
                    {
                        changed = Memcmp(_thumbBuffer, _thumbPrev, (System.IntPtr)thumbBytes) != 0;
                    }
                    catch
                    {
                        // msvcrt.dll 缺失等异常情况退回逐字节比较
                        changed = false;
                        for (int i = 0; i < thumbBytes; i++)
                        {
                            if (_thumbBuffer[i] != _thumbPrev[i])
                            {
                                changed = true;
                                break;
                            }
                        }
                    }

                    _thumbCaptureCount++;
                    // 无论是否变化都更新参考（供下次比较）；仅变化时保留本次，静止时两 buffer 相同无影响
                    byte[] tmp = _thumbPrev;
                    _thumbPrev = _thumbBuffer;
                    _thumbBuffer = tmp;
                    return changed;
                }
                finally
                {
                    if (thumb.Scan0 != IntPtr.Zero)
                        System.Runtime.InteropServices.Marshal.FreeHGlobal(thumb.Scan0);
                }
            }
            catch (Exception ex)
            {
                if (_thumbCaptureCount <= 1 || _thumbCaptureCount % 600 == 0)
                    Logger.Warn(ex, "ProbeThumbnailChanged failed — conservative full capture (count={0})", _thumbCaptureCount);
                return true; // 探测失败保守全截
            }
            finally
            {
                // 缩略图探测耗时诊断（弱机 BitBlt 慢时用于验证缩略图是否真的"低成本"）
                if (_thumbCaptureCount == 1 || _thumbCaptureCount % 600 == 0)
                {
                    double swMs = (Stopwatch.GetTimestamp() - swStart) * 1000.0 / Stopwatch.Frequency;
                    Logger.Info("ProbeThumbnail: thumb={0}x{1} full={2}x{3} probeMs={4:F1} captures={5}",
                        _thumbW, _thumbH, targetW, targetH, swMs, _thumbCaptureCount);
                }
            }
        }

        /// <summary>
        /// 判断是否需要强制全帧截屏一次（保活）。桌面静止且未到 ThumbKeepaliveIntervalMs
        /// 时返回 false（可跳过）；达到保活间隔则返回 true（强制一次全帧，维持连接活跃，
        /// 与 ServerStreamSession 的保活帧语义一致，防止客户端误判断连）。
        /// </summary>
        private bool _thumbNeedFull()
        {
            if (!_thumbReady) return true; // 未初始化：首帧必须全截
            long now = Stopwatch.GetTimestamp();
            long elapsedMs = (now - _thumbLastKeepaliveTicks) * 1000 / Stopwatch.Frequency;
            return elapsedMs >= ThumbKeepaliveIntervalMs;
        }
    }
}
