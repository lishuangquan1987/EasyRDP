#nullable disable
using System;
using System.Diagnostics;
using System.Threading;
using EasyDesk.Core;
using EasyDesk.Core.Models;
using EasyRDP.Core.Services;
using NLog;

namespace EasyRDP.Server.Wpf.Services
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
        // volatile：ServerStreamSession 按编码模式跨线程设置（流控 50ms / 推送 16ms），
        // 捕获线程循环读取；非 volatile 存在 JIT 缓存旧值风险，导致探测/全截频率失真
        // （D20：曾观察到 16ms 初始值被持续使用，虚拟显卡上高频探测占满 CPU）。
        private volatile int _frameIntervalMs = 16; // ~60fps
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
        // 采集显示器：0=主屏，1..n=按枚举顺序的副屏（与 EasyDesk CaptureOptions.TargetDisplay 一致）
        private volatile int _captureDisplay;
        // 显示器切换信号：捕获线程下一轮立即重算边界并通知会话（重建编码器 + 关键帧）
        private volatile bool _captureDisplayDirty;
        // 选中的显示器边界变化（切换/热插拔/分辨率变化），参数为新的边界
        public event Action<DesktopBounds> DisplayBoundsChanged;
        // 生命周期锁：Start/Stop 在并发会话接入/断开下必须串行（防止检查-执行竞态产生双线程）
        private readonly object _lifecycleLock = new object();

        // ── 静止跳过截屏（D13 + D20 虚拟显卡 CPU 优化）──
        // 弱机/虚拟显卡 BitBlt 全帧截屏实测 250~300ms/帧（Win7 虚拟机 + Hyper-V 虚拟显卡），
        // 桌面静止时用"低成本缩略图"探测变化，静止则跳过昂贵的全帧 StretchBlt；
        // 只有检测到变化才做全帧截屏。
        // D20 实证：Hyper-V 虚拟显卡上缩略图探测一次也要 76ms，若按 50ms 间隔持续探测，
        // 捕获线程被探测占满 1 核（服务端 CPU 12% 主因，远程时整台服务器卡顿）。
        // 修正：静止时探测降频到 250ms/次（省 4 倍探测 CPU），检测到变化立即恢复
        // 短间隔快速响应；保活全截从 500ms 拉长到 2000ms（虚拟显卡全截 ~200ms 很贵，
        // 且 ServerStreamSession 自带 keepalive 微型帧维持连接活跃，捕获保活无需高频）。
        private const int ThumbDivisor = 8;                 // 缩略图边长 = 原尺寸/8
        private const int ThumbKeepaliveIntervalMs = 2000;  // 静止时强制全帧截屏的间隔（保活）
        private const int ThumbIdleProbeIntervalMs = 250;   // 静止时缩略图探测间隔（省 CPU）

        // ── 输入驱动的强制全截（脏画面修复）──
        // 缩略图探测的两类漏检（1/8 降采样平均效应 / 变化帧被丢弃后探测参考已前移）
        // 会让"静止"误判持续到保活全截或下一次真实变化，表现为客户端画面残留。
        // 等待期间监听 GetLastInputInfo（远端 SendInput 注入与本物理键鼠都会推进
        // 系统空闲计时），检测到新输入立即强制一次全帧截屏，由编码器逐瓦片比较
        // 原生像素兜底捕捉全部漏检变化。
        private const int InputPollSliceMs = 50;            // 静止等待期间的输入轮询分片
        // 强制全截的最小间隔（限频）：120Hz 鼠标移动流下防止捕获线程被打满。
        // 50ms 分片轮询 + 200ms 强制限频 = 持续输入时最多 5 次额外全截/秒。
        private const int InputForcedCaptureMinIntervalMs = 200;
        private uint _lastSeenInputTick;                     // 上次轮询到的系统空闲计时（GetTickCount 域）
        private long _lastInputForcedCaptureTicks;           // 上次输入强制全截时刻（限频）
        private long _inputForcedCaptureCount;               // 输入强制全截总次数（诊断限频日志用）

        // GetLastInputInfo（user32，Windows 2000+ 可用）：查询系统最后键鼠输入时刻。
        // dwTime 与 GetTickCount 同域（毫秒，约 49.7 天回绕一次——对输入活动检测无影响）。
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetLastInputInfo(ref NativeLastInputInfo plii);

        /// <summary>GetLastInputInfo 的原生结构（cbSize 必须先初始化）。</summary>
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct NativeLastInputInfo
        {
            /// <summary>结构体大小（字节），调用前必须赋值。</summary>
            public uint cbSize;
            /// <summary>最后一次输入事件时刻（GetTickCount 毫秒域）。</summary>
            public uint dwTime;
        }
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
        /// 动态设置捕获分辨率上限。0 = 全分辨率；
        /// >0 = 用 StretchBlt 直接按该宽度等比降采样捕获。
        /// 注意：清晰度绝对优先（用户明确要求不降清晰度）——会话层
        /// （ServerStreamSession）已不再调用本方法做负载降档，保留能力仅供
        /// 未来的显式 UI 开关使用。线程安全：编码线程调用，捕获线程下一轮迭代读取。
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

        /// <summary>当前采集的显示器编号（0=主屏，1..n=副屏）。</summary>
        public int CaptureDisplay { get { return _captureDisplay; } }

        /// <summary>
        /// 切换采集显示器。运行中切换由捕获线程在下一轮迭代生效：
        /// 重新计算边界 → 触发 DisplayBoundsChanged → 会话重建编码器并发关键帧。
        /// </summary>
        public void SetCaptureDisplay(int displayIndex)
        {
            if (displayIndex < 0) displayIndex = 0;
            if (_captureDisplay == displayIndex) return;
            _captureDisplay = displayIndex;
            _captureDisplayDirty = true;
            Logger.Info("CaptureService: capture display set to {0}", displayIndex);
        }

        /// <summary>
        /// 解析当前选中显示器的边界（虚拟桌面坐标）。
        /// 索引语义与 EasyDesk 一致：0=主屏（按 IsPrimary 查找），1..n=副屏（按枚举顺序）。
        /// </summary>
        public DesktopBounds GetCaptureBounds()
        {
            DesktopBounds[] screens = _capturer.GetAllScreens();
            int wanted = _captureDisplay;
            if (wanted <= 0)
            {
                foreach (DesktopBounds s in screens)
                {
                    if (s.IsPrimary) return s;
                }
                if (screens.Length > 0) return screens[0];
                throw new InvalidOperationException("No display found");
            }

            int secondarySeen = 0;
            foreach (DesktopBounds s in screens)
            {
                if (s.IsPrimary) continue;
                if (secondarySeen == wanted - 1) return s;
                secondarySeen++;
            }
            throw new ArgumentOutOfRangeException("displayIndex",
                string.Format("Display index {0} out of range ({1} secondary displays)", wanted, secondarySeen));
        }

        /// <summary>
        /// 解析当前采集区域（供持有 ICaptureService 的调用方使用）：
        /// CaptureService 实现返回选中显示器，其他实现回退主屏。
        /// </summary>
        public static DesktopBounds ResolveBounds(ICaptureService captureService)
        {
            CaptureService concrete = captureService as CaptureService;
            if (concrete != null) return concrete.GetCaptureBounds();
            return captureService.GetPrimaryScreen();
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
            // 采集选中的显示器（默认主屏）：会话握手尺寸/编码器尺寸/鼠标坐标空间均以该屏为准。
            // IncludeCursor=false：DXGI/BitBlt 捕获均不含光标，光标由 CursorTracker 叠加层单独同步。
            var options = new CaptureOptions { IncludeCursor = false, TargetDisplay = _captureDisplay };

            // 计算目标捕获分辨率：屏幕尺寸超限时等比例缩小（宽高取偶数满足 I420）。
            // 直接按目标分辨率 StretchBlt，编码线程不再做托管软件缩放。
            int screenX = 0, screenY = 0, screenW = 0, screenH = 0;
            int targetW = 0, targetH = 0;
            Func<bool> refreshCaptureBounds = delegate
            {
                try
                {
                    DesktopBounds display = GetCaptureBounds();
                    bool boundsChanged = display.X != screenX || display.Y != screenY
                        || display.Width != screenW || display.Height != screenH;
                    screenX = display.X;
                    screenY = display.Y;
                    screenW = display.Width;
                    screenH = display.Height;
                    // 下发路径的 TargetDisplay 与当前选择保持一致（CaptureScreen 兜底分支使用）
                    options.TargetDisplay = _captureDisplay;

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
                        Logger.Info("CaptureLoop: target resolution {0}x{1} (display {2} {3}x{4} at {5},{6})",
                            newW, newH, _captureDisplay, screenW, screenH, screenX, screenY);
                    targetW = newW;
                    targetH = newH;

                    if (boundsChanged)
                    {
                        Logger.Info("CaptureLoop: display bounds changed → display={0} {1}x{2} at ({3},{4})",
                            _captureDisplay, screenW, screenH, screenX, screenY);
                        var changedHandler = DisplayBoundsChanged;
                        if (changedHandler != null)
                        {
                            try { changedHandler(display); }
                            catch (Exception ex) { Logger.Warn(ex, "DisplayBoundsChanged handler failed"); }
                        }
                    }
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
            // 首次刷新不广播（初始边界由会话自己读取），避免启动瞬间重复触发重建
            try
            {
                DesktopBounds initial = GetCaptureBounds();
                screenX = initial.X;
                screenY = initial.Y;
                screenW = initial.Width;
                screenH = initial.Height;
                int nw = screenW;
                int nh = screenH;
                if (CaptureMaxWidth > 0 && nw > CaptureMaxWidth)
                {
                    nh = Math.Max(1, (int)((long)nh * CaptureMaxWidth / nw));
                    nw = CaptureMaxWidth;
                }
                targetW = (nw + 1) & ~1;
                targetH = (nh + 1) & ~1;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "CaptureLoop: initial capture bounds failed");
            }
            // 约每 10 秒（60fps × 600 次）重新读取屏幕边界，应对运行中分辨率切换/显示器热插拔
            int boundsRefreshCounter = 0;

            while (_running)
            {
                try
                {
                    // D11 降/升档或显示器切换时立即重算目标分辨率（不等 600 帧周期刷新），
                    // 使 StretchBlt 降采样/换屏尽快生效。
                    if (_captureWidthDirty)
                    {
                        _captureWidthDirty = false;
                        refreshCaptureBounds();
                        boundsRefreshCounter = 0;
                    }
                    if (_captureDisplayDirty)
                    {
                        _captureDisplayDirty = false;
                        // 换屏后旧缩略图参考帧无效（分辨率可能相同，但内容完全不同）
                        _thumbReady = false;
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
                        // 桌面静止候选：等待期间监听系统输入活动（GetLastInputInfo），
                        // 覆盖远端 SendInput 注入与本物理键鼠（二者都推进系统空闲计时）。
                        // 有新输入时立即结束等待并强制一次全帧截屏——缩略图探测存在
                        // 两类漏检（1/8 降采样平均掉小面积变化、变化帧在队列满处被丢弃后
                        // 探测参考已前移），输入后强制全截由编码器逐瓦片比较原生像素兜底，
                        // 修复"窗口最小化后客户端残留旧画面、晃动鼠标才刷新"的脏画面问题。
                        bool inputForced = TryBeginInputForcedCapture();
                        if (!inputForced)
                        {
                            long idleDeadline = Stopwatch.GetTimestamp()
                                + (long)ThumbIdleProbeIntervalMs * Stopwatch.Frequency / 1000;
                            while (_running && !inputForced && Stopwatch.GetTimestamp() < idleDeadline)
                            {
                                Thread.Sleep(InputPollSliceMs);
                                inputForced = TryBeginInputForcedCapture();
                            }
                        }
                        if (!inputForced)
                        {
                            // 整个静止间隔内无输入活动：跳过全帧截屏，低频探测
                            //（D20：虚拟显卡探测 ~76ms/次，高频探测会把捕获线程占满 1 核）
                            continue;
                        }
                        _inputForcedCaptureCount++;
                        if (_inputForcedCaptureCount == 1 || _inputForcedCaptureCount % 200 == 0)
                            Logger.Info("CaptureLoop: input-driven forced capture (total={0})", _inputForcedCaptureCount);
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

        /// <summary>
        /// 检测系统输入活动并在满足限频时请求强制全帧截屏（脏画面修复核心）。
        /// GetLastInputInfo 覆盖两类输入：客户端远端注入（EasyDesk SendInput）与
        /// 服务端本地物理键鼠——二者都会推进系统空闲计时。
        /// 返回 true 表示应立即做一次全帧截屏（输入驱动 + 200ms 限频双条件满足）；
        /// 返回 false 表示无新输入或距上次强制全截不足 200ms（输入风暴下由
        /// 调用方的分片循环继续重试，不丢失"有输入"这一事实）。
        /// </summary>
        private bool TryBeginInputForcedCapture()
        {
            // 读取系统空闲计时，与本地上次记录比较判断"是否有新输入"
            var info = new NativeLastInputInfo();
            info.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeLastInputInfo));
            bool newInput;
            try
            {
                if (!GetLastInputInfo(ref info))
                    return false;
                newInput = info.dwTime != _lastSeenInputTick;
                _lastSeenInputTick = info.dwTime;
            }
            catch
            {
                // XP/受限环境理论上均可用；防御性兜底：视为无输入
                return false;
            }
            if (!newInput)
                return false;

            long now = Stopwatch.GetTimestamp();
            long sinceLastMs = (now - _lastInputForcedCaptureTicks) * 1000 / Stopwatch.Frequency;
            if (sinceLastMs < InputForcedCaptureMinIntervalMs)
                return false; // 输入风暴限频：不足 200ms 不再强制全截
            _lastInputForcedCaptureTicks = now;
            return true;
        }
    }
}
