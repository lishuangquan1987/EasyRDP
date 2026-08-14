#nullable disable
using System;
using System.Collections.Generic;
using System.Threading;
using EasyDesk.Core;
using EasyDesk.Core.Models;
using EasyRDP.Core.Protocol;
using EasyRDP.Core.Services;
using EasyRDP.Core.Session;
using EasyRDP.Core.Transport;
using NLog;

namespace EasyRDP.Server.Wpf
{
    /// <summary>
    /// 服务端传输主机。管理所有 Session 生命周期、握手、心跳、并发控制。
    /// </summary>
    public class TransportHost : IDisposable
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        /// <summary>会话 attached 事件（UI 绑定用），参数为 (sessionId, remoteEndPoint, codec, resolution)。</summary>
        public event Action<uint, string, string, string> SessionAttached;

        /// <summary>会话 detached 事件（UI 绑定用）。</summary>
        public event Action<uint> SessionDetached;

        private readonly ICaptureService _captureService;
        private readonly ITransportAcceptor _transportAcceptor;
        private readonly IInputSimulator _inputSimulator; // Shared for all input sessions

        // Session tracking
        private readonly Dictionary<uint, SessionInfo> _sessions = new Dictionary<uint, SessionInfo>();
        // 客户端远端地址（连接时记录，握手完成后用于 UI 展示）
        private readonly Dictionary<uint, string> _remoteEndpoints = new Dictionary<uint, string>();
        private readonly object _lock = new object();
        private int _maxSessions = 2; // D12 default for XP dual-core
        private int _activeCount;

        // Per-session transports (sessionId → ITransport)，替代旧的 MessageReassembler 字典。
        // 路由由 TransportHost 维护：MessageReceived 订阅用闭包捕获 sessionId。
        private readonly Dictionary<uint, ITransport> _transports = new Dictionary<uint, ITransport>();
        // sessionId 自增计数器（原来在 TcpTransportServer.AcceptLoop 分配，迁移到 TransportHost）
        private uint _nextSessionId = 1;

        // Cursor tracking
        private readonly ICursorTracker _cursorTracker;

        // Clipboard (双向同步)：必须通过 STA 线程访问。
        // 客户端→服务端：客户端复制 → ClipboardSync 消息 → 入队 → STA 线程 IClipboardService.SetText
        // 服务端→客户端：STA 线程轮询本地剪贴板变化 → 检测到变化 → 发送 ClipboardSync 到所有客户端
        private readonly IClipboardService _clipboardService;
        private readonly Thread _clipboardThread;
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _clipboardQueue = new System.Collections.Concurrent.ConcurrentQueue<string>();
        private readonly AutoResetEvent _clipboardSignal = new AutoResetEvent(false);
        // 剪贴板变化通知信号：由 AddClipboardFormatListener（WM_CLIPBOARDUPDATE）触发，
        // 替代每 800ms 轮询 —— 只有复制/剪切发生时才会读取本机剪贴板。
        private readonly AutoResetEvent _clipboardChangeSignal = new AutoResetEvent(false);
        private IntPtr _clipboardListenerHwnd;
        private IntPtr _clipboardPrevWndProc;
        private ClipboardWndProcDelegate _clipboardWndProcDelegate;
        private System.Runtime.InteropServices.GCHandle _clipboardDelegateHandle;

        // ── 剪贴板变化通知（替代轮询）：AddClipboardFormatListener / WM_CLIPBOARDUPDATE ──
        private const int GWL_WNDPROC = -4;
        private const uint WM_CLIPBOARDUPDATE = 0x031D;
        private const uint QS_ALLINPUT = 0x04FF;
        private const uint PM_REMOVE = 0x0001;
        private const uint MWMO_INPUTAVAILABLE = 0x0004;
        private static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

        private delegate IntPtr ClipboardWndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern IntPtr SetWindowLong32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName,
            uint dwStyle, int x, int y, int w, int h, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool AddClipboardFormatListener(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool RemoveClipboardFormatListener(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint MsgWaitForMultipleObjectsEx(uint nCount, IntPtr[] pHandles,
            uint dwMilliseconds, uint dwWakeMask, uint dwFlags);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct NativeMsg
        {
            public IntPtr Hwnd;
            public uint Message;
            public IntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public NativePoint Pt;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool PeekMessage(out NativeMsg lpMsg, IntPtr hWnd,
            uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref NativeMsg lpMsg);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref NativeMsg lpMsg);

        private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        {
            if (IntPtr.Size == 8)
                return SetWindowLongPtr64(hWnd, nIndex, dwNewLong);
            return SetWindowLong32(hWnd, nIndex, dwNewLong);
        }
        // 服务端本地剪贴板上次文本，用于检测变化 + 避免回环（客户端发来的文本设置后不再发回）
        private string _lastServerClipboardText = "";
        // 服务端本地剪贴板上次文件列表签名（拼接路径），用于检测变化 + 避免回环
        private string _lastServerClipboardFilesSig = "";
        // 服务端本地剪贴板上次图片签名（CF_DIB 字节数 + 前 32 字节哈希），用于检测变化 + 避免回环
        private string _lastServerClipboardImageSig = "";
        // ClipboardLoop 循环计数，用于周期性记录状态日志
        private int _clipboardCheckCount = 0;
        // 文件/图片传输 ID 自增（每次剪贴板同步递增）
        private uint _fileTransferIdSeq = 0;
        // 图片块大小（64KB）：平衡内存占用和分片数量
        private const int ImageChunkSize = 64 * 1024;
        // 图片传输并发控制：同时只允许一个图片剪贴板传输会话（避免大图片并发占满带宽）
        private static readonly SemaphoreSlim _imageTransferLock = new SemaphoreSlim(1, 1);

        // ── 文件剪贴板延迟渲染（RustDesk 风格）──
        // 服务端是发送方（服务端用户复制文件）：per-session Provider，响应客户端的 FileContentsReq
        // key = sessionId，value = 该 session 对应的 Provider（每次服务端用户复制文件时替换）
        private readonly Dictionary<uint, FileClipboardProvider> _serverClipProviders = new Dictionary<uint, FileClipboardProvider>();
        private readonly object _clipProviderLock = new object();
        // 服务端是接收方（客户端用户复制文件）：per-session Consumer，按需下载客户端文件
        // key = "sessionId_transferId"，value = 该传输对应的 Consumer
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, FileClipboardConsumer> _serverClipConsumers
            = new System.Collections.Concurrent.ConcurrentDictionary<string, FileClipboardConsumer>();
        // 客户端发来的文件路径队列：由 ClipboardLoop 在 STA 线程消费，调用 SetFiles
        private readonly System.Collections.Concurrent.ConcurrentQueue<string[]> _serverFileSetQueue
            = new System.Collections.Concurrent.ConcurrentQueue<string[]>();
        // 服务端接收客户端发来的图片剪贴板：key = "sessionId_transferId"
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ImageClipboardReceiver> _serverImageReceivers
            = new System.Collections.Concurrent.ConcurrentDictionary<string, ImageClipboardReceiver>();
        // 客户端发来的 CF_DIB 字节队列：由 ClipboardLoop 在 STA 线程消费，调用 SetImageDibBytes
        private readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> _serverImageSetQueue
            = new System.Collections.Concurrent.ConcurrentQueue<byte[]>();

        // Authentication credentials: username → password (plaintext, sufficient for v1)
        private readonly Dictionary<string, string> _credentials;

        // 帧变化检测模式：由 MainWindowViewModel 在 Start 前从 ServerSettings 注入，
        // 运行时可更新（volatile 保证新会话建立时读到最新值，已有会话不受影响）。
        // 默认 FullFrameMemcmp 保持与历史版本一致的行为。
        private volatile ChangeDetectionMode _changeDetectionMode = ChangeDetectionMode.FullFrameMemcmp;

        // Heartbeat
        private Thread _heartbeatThread;
        private volatile bool _running;
        private readonly Dictionary<uint, DateTime> _lastActivity = new Dictionary<uint, DateTime>();

        // 诊断信息采集器（系统静态信息缓存）
        private readonly EasyRDP.Server.Wpf.Services.SystemInfoCollector _systemInfoCollector
            = new EasyRDP.Server.Wpf.Services.SystemInfoCollector();

        // D12 全局负载：当前负载级（0=正常，1/2=过载）与升降级连续判定计数
        private volatile int _globalLoadLevel;
        private int _globalLoadHighStreak;
        private int _globalLoadLowStreak;

        /// <summary>
        /// 获取或设置帧变化检测模式。新会话建立时按此值通过 ChangeDetectorFactory 创建
        /// IFrameChangeDetector 注入 ServerStreamSession。已建立的会话不受影响。
        /// 切换在下次会话接入时生效（UI 修改后无需重启服务）。
        /// </summary>
        public ChangeDetectionMode ChangeDetectionMode
        {
            get { return _changeDetectionMode; }
            set { _changeDetectionMode = value; }
        }

        public TransportHost(
            ICaptureService captureService,
            ITransportAcceptor transportAcceptor,
            IInputSimulator inputSimulator,
            ICursorCapturer cursorCapturer,
            IClipboardService clipboardService,
            Dictionary<string, string> credentials)
        {
            _captureService = captureService;
            _transportAcceptor = transportAcceptor;
            _inputSimulator = inputSimulator;
            _cursorTracker = new CursorTracker(cursorCapturer);
            _clipboardService = clipboardService;
            _credentials = credentials ?? new Dictionary<string, string>();

            _transportAcceptor.ClientConnected += OnClientConnected;

            // 剪贴板 STA 线程：IClipboardService 必须在 STA 线程调用
            if (_clipboardService != null)
            {
                _clipboardThread = new Thread(ClipboardLoop);
                _clipboardThread.IsBackground = true;
                _clipboardThread.SetApartmentState(ApartmentState.STA);
            }
        }

        public void Start(string endpoint)
        {
            Logger.Info("TransportHost starting on endpoint {0}", endpoint);
            _running = true;
            _transportAcceptor.Start(endpoint);

            _heartbeatThread = new Thread(HeartbeatLoop);
            _heartbeatThread.IsBackground = true;
            _heartbeatThread.Start();

            // 启动剪贴板 STA 线程
            if (_clipboardThread != null && !_clipboardThread.IsAlive)
                _clipboardThread.Start();
        }

        /// <summary>
        /// 首个会话接入时启动屏幕捕获与光标追踪（空闲时不捕获）：
        /// 避免无客户端时 60fps 截屏/光标轮询浪费 CPU/GPU，
        /// 也避免 DXGI Desktop Duplication 常驻导致本机光标渲染异常。
        /// </summary>
        private void EnsureCaptureRunning()
        {
            try { _cursorTracker.Start(); }
            catch (Exception ex) { LogCaptureStartFailure("CursorTracker start failed", ex); }
            if (_captureService != null && !_captureService.IsRunning)
            {
                try { _captureService.Start(); }
                catch (Exception ex) { LogCaptureStartFailure("CaptureService start failed", ex); }
            }
        }

        private bool _captureStartFailureLogged;

        /// <summary>启动失败只记一次日志，避免无 GPU 等降级环境下每次接入都刷警告。</summary>
        private void LogCaptureStartFailure(string message, Exception ex)
        {
            if (_captureStartFailureLogged) return;
            _captureStartFailureLogged = true;
            Logger.Warn(ex, message);
        }

        /// <summary>
        /// 最后一个会话断开后停止屏幕捕获与光标追踪。
        /// </summary>
        private void StopCaptureIfIdle()
        {
            lock (_lock)
            {
                if (_activeCount > 0)
                    return;
            }
            try { _cursorTracker.StopAll(); }
            catch (Exception ex) { Logger.Warn(ex, "CursorTracker stop failed"); }
            if (_captureService != null)
            {
                try { _captureService.Stop(); }
                catch (Exception ex) { Logger.Warn(ex, "CaptureService stop failed"); }
            }
            // 停止期间可能已有新会话接入（TOCTOU）：重新检查并恢复捕获
            lock (_lock)
            {
                if (_activeCount > 0)
                    EnsureCaptureRunning();
            }
        }

        public void Stop()
        {
            Logger.Info("TransportHost stopping, active sessions: {0}", _activeCount);
            _running = false;

            // 唤醒剪贴板线程使其退出
            _clipboardSignal.Set();
            _clipboardChangeSignal.Set();

            // Stop all sessions
            lock (_lock)
            {
                foreach (var kv in _sessions)
                {
                    try
                    {
                        kv.Value.Stream?.Stop();
                        kv.Value.Stream?.Dispose();
                        kv.Value.Input?.Dispose();
                    }
                    catch { }
                }
                _sessions.Clear();
                _transports.Clear();
                _lastActivity.Clear();
                _remoteEndpoints.Clear();
            }

            // 清理所有 per-session 延迟渲染状态
            lock (_clipProviderLock)
            {
                foreach (var kv in _serverClipProviders)
                {
                    try { kv.Value.Dispose(); } catch { }
                }
                _serverClipProviders.Clear();
            }
            foreach (var kv in _serverClipConsumers)
            {
                try { kv.Value.Cancel(); } catch { }
            }
            _serverClipConsumers.Clear();

            Logger.Info("TransportHost stopped");

            _cursorTracker.StopAll();
            if (_captureService != null)
            {
                try { _captureService.Stop(); }
                catch (Exception ex) { Logger.Warn(ex, "CaptureService stop failed"); }
            }
            _transportAcceptor.Stop();
            _heartbeatThread?.Join(2000);
            _clipboardThread?.Join(2000);
        }

        /// <summary>
        /// 剪贴板 STA 线程主循环。双向同步：
        /// 1) 处理来自客户端的剪贴板设置请求（客户端→服务端）
        /// 2) 轮询本地剪贴板变化，变化时发送到所有客户端（服务端→客户端）
        /// IClipboardService 内部用 OpenClipboard/SetClipboardData，必须在 STA 线程调用。
        /// </summary>
        private void ClipboardLoop()
        {
            Logger.Info("ClipboardLoop started (STA thread, service={0})",
                _clipboardService != null ? "yes" : "null");

            // 创建剪贴板变化监听（WM_CLIPBOARDUPDATE），替代轮询：
            // 复制/剪切发生时系统通知本窗口，不再周期性 OpenClipboard。
            CreateClipboardListener();

            IntPtr[] waitHandles = new IntPtr[]
            {
                _clipboardSignal.SafeWaitHandle.DangerousGetHandle(),
                _clipboardChangeSignal.SafeWaitHandle.DangerousGetHandle()
            };
            // 会话接入瞬间补发一次既有剪贴板内容（事件驱动不会为已存在内容触发通知）
            bool prevHasSession = false;

            while (_running)
            {
                try
                {
                    // 1) 处理来自客户端的文本剪贴板设置请求
                    string text;
                    while (_clipboardQueue.TryDequeue(out text))
                    {
                        if (!_running) break;
                        try
                        {
                            _clipboardService?.SetText(text);
                            // 更新 last，避免监听线程检测到"变化"又发回客户端（避免回环）
                            _lastServerClipboardText = text;
                            Logger.Info("Clipboard set from client: len={0}", text != null ? text.Length : 0);
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn(ex, "Clipboard SetText failed");
                        }
                    }

                    // 2) 处理来自客户端的文件剪贴板设置请求（防回环：更新 _lastServerClipboardFilesSig + OwnerFlag）
                    string[] filePaths;
                    while (_serverFileSetQueue.TryDequeue(out filePaths))
                    {
                        if (!_running) break;
                        try
                        {
                            _clipboardService?.SetFiles(filePaths);
                            // Owner Flag 防回环：标记为 SideHost（表示"由服务端从客户端同步过来"），
                            // CheckFileClipboardChange 看到此标记即跳过，避免回发到客户端
                            EasyRDP.Core.ClipboardOwnerHelper.SetOwnerFlag(EasyRDP.Core.ClipboardOwnerHelper.SideHost);
                            // 防回环：更新签名，避免 CheckFileClipboardChange 又发回客户端
                            _lastServerClipboardFilesSig = string.Join("|", filePaths);
                            Logger.Info("File clipboard set from client: count={0}", filePaths.Length);
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn(ex, "Clipboard SetFiles failed");
                        }
                    }

                    // 3) 处理来自客户端的图片剪贴板设置请求（防回环：更新 _lastServerClipboardImageSig）
                    byte[] dibBytes;
                    while (_serverImageSetQueue.TryDequeue(out dibBytes))
                    {
                        if (!_running) break;
                        try
                        {
                            _clipboardService?.SetImageDibBytes(dibBytes);
                            // 防回环：更新签名，避免 CheckImageClipboardChange 又发回客户端
                            _lastServerClipboardImageSig = dibBytes.Length + ":" + ComputeSimpleHash(dibBytes, 32);
                            Logger.Info("Image clipboard set from client: dibSize={0}", dibBytes.Length);
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn(ex, "Clipboard SetImageDibBytes failed");
                        }
                    }

                    // 4) 本地剪贴板变化由 WM_CLIPBOARDUPDATE 通知驱动：
                    //    仅在有会话且确实发生复制/剪切时才读取（延迟 150ms，
                    //    避免复制方仍持有剪贴板导致 OpenClipboard 失败），
                    //    不再每 800ms 轮询干扰本机剪贴板。
                    bool hasSession;
                    lock (_lock)
                    {
                        hasSession = _sessions.Count > 0;
                    }
                    bool sessionJustAttached = hasSession && !prevHasSession;
                    prevHasSession = hasSession;
                    if (hasSession && (sessionJustAttached || _clipboardChangeSignal.WaitOne(0)))
                    {
                        // 延迟 150ms 让复制方释放剪贴板，期间继续泵消息（不阻塞 WM_QUIT 等）
                        MsgWaitForMultipleObjectsEx(0, null, 150, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
                        CheckServerClipboardChange();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "ClipboardLoop error");
                }

                // 等待队列入队信号 / 剪贴板变化通知 / 窗口消息（最长 2s 兜底）。
                // 必须泵消息，WM_CLIPBOARDUPDATE 才会投递到监听窗口。
                uint waitResult = MsgWaitForMultipleObjectsEx(
                    2, waitHandles, 2000, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
                if (waitResult == 0xFFFFFFFF) // WAIT_FAILED
                {
                    Logger.Warn("MsgWaitForMultipleObjectsEx failed (err={0})",
                        System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                    Thread.Sleep(200);
                }
                else if (waitResult == 2) // WAIT_OBJECT_0 + nCount：有窗口消息待处理
                {
                    NativeMsg msg;
                    while (PeekMessage(out msg, IntPtr.Zero, 0, 0, PM_REMOVE))
                    {
                        if (msg.Message == 0x0012) break; // WM_QUIT
                        TranslateMessage(ref msg);
                        DispatchMessage(ref msg);
                    }
                }
            }
            DestroyClipboardListener();
            Logger.Info("ClipboardLoop exited");
        }

        /// <summary>
        /// 创建隐藏消息窗口并注册剪贴板变化监听（Vista+，Win7 可用）。
        /// 必须在 STA 线程（ClipboardLoop）创建；必须由同一线程泵消息。
        /// </summary>
        private void CreateClipboardListener()
        {
            try
            {
                IntPtr hWnd = CreateWindowEx(0, "STATIC", "EasyRDPClipboardListener", 0,
                    0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
                if (hWnd == IntPtr.Zero)
                {
                    Logger.Warn("CreateClipboardListener: CreateWindowEx failed (err={0})",
                        System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                    return;
                }
                // 立即记录句柄，确保后续步骤抛异常时 DestroyClipboardListener 能清理
                _clipboardListenerHwnd = hWnd;
                _clipboardWndProcDelegate = delegate(IntPtr h, uint msg, IntPtr w, IntPtr l)
                {
                    if (msg == WM_CLIPBOARDUPDATE)
                    {
                        try { _clipboardChangeSignal.Set(); } catch { }
                        return IntPtr.Zero;
                    }
                    return CallWindowProc(_clipboardPrevWndProc, h, msg, w, l);
                };
                _clipboardDelegateHandle = System.Runtime.InteropServices.GCHandle.Alloc(_clipboardWndProcDelegate);
                IntPtr proc = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_clipboardWndProcDelegate);
                _clipboardPrevWndProc = SetWindowLongPtr(hWnd, GWL_WNDPROC, proc);
                // STATIC 窗口必有默认 WndProc，返回零说明子类化失败
                if (_clipboardPrevWndProc == IntPtr.Zero)
                {
                    Logger.Warn("SetWindowLongPtr failed (err={0}) — 剪贴板监听不可用",
                        System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                    DestroyClipboardListener();
                    return;
                }
                if (!AddClipboardFormatListener(hWnd))
                {
                    Logger.Warn("AddClipboardFormatListener failed (err={0}) — 剪贴板变化将无法通知",
                        System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                }
                Logger.Info("Clipboard change listener created (WM_CLIPBOARDUPDATE)");
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "CreateClipboardListener failed");
            }
        }

        /// <summary>释放剪贴板监听窗口与委托句柄。</summary>
        private void DestroyClipboardListener()
        {
            if (_clipboardListenerHwnd != IntPtr.Zero)
            {
                try { RemoveClipboardFormatListener(_clipboardListenerHwnd); } catch { }
                try { DestroyWindow(_clipboardListenerHwnd); } catch { }
                _clipboardListenerHwnd = IntPtr.Zero;
            }
            if (_clipboardDelegateHandle.IsAllocated)
                _clipboardDelegateHandle.Free();
            _clipboardPrevWndProc = IntPtr.Zero;
            _clipboardWndProcDelegate = null;
        }

        /// <summary>
        /// 检查服务端本地剪贴板文本是否变化，变化时发送 ClipboardSync 到所有 attached 客户端。
        /// 必须在 STA 线程调用（ClipboardLoop 内）。
        /// </summary>
        private void CheckServerClipboardChange()
        {
            if (_clipboardService == null) return;
            _clipboardCheckCount++;
            try
            {
                bool hasText = _clipboardService.ContainsText();
                bool hasFiles = _clipboardService.ContainsFiles();
                bool hasImage = _clipboardService.ContainsImage();
                // 每 10 次循环（约 8 秒）记录一次状态，确认 ClipboardLoop 在工作
                if (_clipboardCheckCount % 10 == 0)
                    Logger.Debug("Clipboard check #{0}: hasText={1} hasFiles={2} hasImage={3} lastTextLen={4}",
                        _clipboardCheckCount, hasText, hasFiles, hasImage, _lastServerClipboardText.Length);

                // 优先处理文件剪贴板（CF_HDROP）：用户右键复制文件时触发
                if (hasFiles)
                {
                    CheckFileClipboardChange();
                    // 文件覆盖了图片：清空图片签名，避免下次复制相同图片时误判为"没变化"
                    _lastServerClipboardImageSig = "";
                    return; // 文件和文本/图片不会同时在剪贴板上
                }

                // 图片剪贴板（CF_DIB）：用户截图/复制图片时触发
                if (hasImage)
                {
                    CheckImageClipboardChange();
                    // 图片覆盖了文件：清空文件签名，避免下次复制相同文件时误判为"没变化"
                    _lastServerClipboardFilesSig = "";
                    return; // 图片和文本不会同时在剪贴板上
                }

                // 没有文件/图片时清空对应签名
                _lastServerClipboardFilesSig = "";
                _lastServerClipboardImageSig = "";

                if (!hasText) return;
                string current = _clipboardService.GetText() ?? "";
                if (current != _lastServerClipboardText)
                {
                    _lastServerClipboardText = current;
                    SendClipboardToClients(current);
                    Logger.Info("Clipboard sync to clients: len={0}", current.Length);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "CheckServerClipboardChange failed");
            }
        }

        /// <summary>
        /// 检查服务端本地文件剪贴板是否变化，变化时启动后台线程异步发送到所有客户端。
        /// 必须在 STA 线程调用（ClipboardLoop 内）— 只在 STA 线程读剪贴板，文件读取和网络发送在后台线程。
        /// Owner Flag 防回环：剪贴板若是服务端从客户端同步过来并打上 SideHost 标记的，跳过不回传。
        /// </summary>
        private void CheckFileClipboardChange()
        {
            try
            {
                // Owner Flag 防回环：剪贴板若是服务端从客户端同步过来并打上 SideHost 标记的，跳过不回传
                byte owner = EasyRDP.Core.ClipboardOwnerHelper.GetOwnerFlag();
                if (owner == EasyRDP.Core.ClipboardOwnerHelper.SideHost)
                {
                    return; // 远程同步过来的，不回传
                }

                string[] files = _clipboardService.GetFileList();
                if (files == null || files.Length == 0)
                {
                    _lastServerClipboardFilesSig = "";
                    return;
                }

                // 构造签名用于检测变化（拼接所有路径）
                string sig = string.Join("|", files);
                if (sig == _lastServerClipboardFilesSig)
                    return; // 没变化

                _lastServerClipboardFilesSig = sig;
                Logger.Info("File clipboard changed: count={0}", files.Length);

                // 复制文件路径数组（避免后台线程访问时数组被修改）
                string[] pathsCopy = (string[])files.Clone();

                // 后台线程异步发送：不阻塞 STA 线程，ClipboardLoop 可继续检测剪贴板变化
                // net40 没有 Task.Run，用 Task.Factory.StartNew
                System.Threading.Tasks.Task.Factory.StartNew(() =>
                {
                    try { SendFileClipboardToClients(pathsCopy); }
                    catch (Exception ex) { Logger.Warn(ex, "SendFileClipboardToClients background task failed"); }
                });
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "CheckFileClipboardChange failed");
            }
        }

        /// <summary>
        /// 发送文件剪贴板到所有已 attached 的客户端（延迟渲染：仅发元信息，文件内容由客户端按需拉取）。
        /// 流程：为每个 session 创建 FileClipboardProvider → 发送 ClipFormatList（仅元信息）。
        /// 客户端收到后通过 ClipFileContentsReq 按需请求文件内容，Provider 响应 ClipFileContentsRes。
        /// 接收方控制下载速率，避免灌满 TCP 连接。
        /// </summary>
        private void SendFileClipboardToClients(string[] filePaths)
        {
            uint transferId = ++_fileTransferIdSeq;

            // 1) 构造文件元信息列表（仅文件名+大小，不含文件内容）
            var metaList = new List<ClipFormatListMessage.FileMeta>(filePaths.Length);
            foreach (var path in filePaths)
            {
                try
                {
                    var fi = new System.IO.FileInfo(path);
                    metaList.Add(new ClipFormatListMessage.FileMeta
                    {
                        FileName = System.IO.Path.GetFileName(path),
                        FileSize = fi.Exists ? fi.Length : 0
                    });
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "GetFileInfo failed for {0}", path);
                    metaList.Add(new ClipFormatListMessage.FileMeta
                    {
                        FileName = System.IO.Path.GetFileName(path),
                        FileSize = 0
                    });
                }
            }

            // 2) 收集目标 session，并为每个 session 创建 FileClipboardProvider
            List<uint> sessionIds;
            lock (_lock)
            {
                sessionIds = new List<uint>(_sessions.Keys);
            }
            if (sessionIds.Count == 0)
            {
                Logger.Info("No attached clients — skipping file clipboard transfer");
                return;
            }

            Logger.Info("ClipFormatList transfer started: transferId={0} fileCount={1} sessions={2}",
                transferId, metaList.Count, sessionIds.Count);

            // 3) 为每个 session 创建 Provider（响应客户端的 FileContentsReq）
            //    替换旧 Provider（Dispose 旧的，避免资源泄漏）
            foreach (var sid in sessionIds)
            {
                // 捕获循环变量到局部变量，避免闭包捕获 foreach 变量
                // 关键：FileClipboardProvider 内部调用 _sendAction(0, payload) 传 0（Provider 不知道 sessionId），
                // 所以这里必须用 targetSid 而非 sidArg，否则响应会发到 sessionId=0（不存在）导致客户端永远收不到
                uint targetSid = sid;
                var provider = new FileClipboardProvider(transferId, filePaths,
                    (sidArg, payload) =>
                    {
                        // sidArg 被忽略（Provider 内部传 0）；用 targetSid 作为实际目标 session
                        // FragAndSend 签名：(frameId, messageType, payload, sendAction, sessionId)
                        // 最后一个参数 sessionId 才是传给 sendAction 的实参。
                        // 控制流 frameId=0；sessionId=targetSid（必须真实，否则 SendTo 静默失败）
                        // 单完整帧发送：并发响应的分片若交错且共用 frameId=0，
                        // 接收端重组器会把不同响应的分片混在一起导致 payload 损坏（下载失败 → 无粘贴菜单）。
                        // 每个响应作为完整帧发送，线上交错时互不干扰。
                        SendMessage(targetSid, (byte)MessageType.ClipFileContentsRes, payload);
                    });
                lock (_clipProviderLock)
                {
                    FileClipboardProvider old;
                    if (_serverClipProviders.TryGetValue(sid, out old))
                    {
                        try { old.Dispose(); } catch { }
                    }
                    _serverClipProviders[sid] = provider;
                }
            }

            // 4) 发送 ClipFormatList（仅元信息，几百字节）到每个 session
            var listMsg = new ClipFormatListMessage
            {
                TransferId = transferId,
                Files = metaList
            };
            byte[] listPayload = listMsg.Pack();
            foreach (var sid in sessionIds)
            {
                try
                {
                    // FragAndSend 签名：(frameId, messageType, payload, sendAction, sessionId)
                    // 控制流 frameId=0；sessionId=sid（传给 sendAction → SendTo）
                    SendMessage(sid, (byte)MessageType.ClipFormatList, listPayload);
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "SendClipFormatList failed for session {0}", sid);
                }
            }
            Logger.Info("ClipFormatList sent: transferId={0} fileCount={1} sessions={2}",
                transferId, metaList.Count, sessionIds.Count);
        }

        // ── 图片剪贴板同步（CF_DIB）──

        /// <summary>
        /// 检查服务端本地图片剪贴板是否变化，变化时启动后台线程异步发送到所有客户端。
        /// 必须在 STA 线程调用（ClipboardLoop 内）— 只在 STA 线程读剪贴板，数据发送在后台线程。
        /// </summary>
        private void CheckImageClipboardChange()
        {
            try
            {
                byte[] dibBytes = _clipboardService.GetImageDibBytes();
                if (dibBytes == null || dibBytes.Length == 0)
                {
                    _lastServerClipboardImageSig = "";
                    return;
                }

                // 构造签名：长度 + 前 32 字节哈希（用简单的字节拼接，无需加密库）
                string sig = dibBytes.Length + ":" + ComputeSimpleHash(dibBytes, 32);
                if (sig == _lastServerClipboardImageSig)
                    return; // 没变化

                _lastServerClipboardImageSig = sig;
                Logger.Info("Image clipboard changed: dibSize={0}", dibBytes.Length);

                // 后台线程异步发送：不阻塞 STA 线程
                // 注意：dibBytes 已是独立数组（GetImageDibBytes 返回新数组），可直接传给后台线程
                System.Threading.Tasks.Task.Factory.StartNew(() =>
                {
                    try { SendImageClipboardToClients(dibBytes); }
                    catch (Exception ex) { Logger.Warn(ex, "SendImageClipboardToClients background task failed"); }
                });
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "CheckImageClipboardChange failed");
            }
        }

        /// <summary>
        /// 发送图片剪贴板到所有已 attached 的客户端（后台线程调用）。
        /// 流程：ImageClipboardStart → 多个 ImageClipboardData（64KB 分块）→ ImageClipboardEnd
        /// 用 SemaphoreSlim 限制并发：避免大图片并发占满带宽。
        /// </summary>
        private void SendImageClipboardToClients(byte[] dibBytes)
        {
            _imageTransferLock.Wait();
            try
            {
                uint transferId = ++_fileTransferIdSeq;

                // 收集目标 session
                List<uint> sessionIds;
                lock (_lock)
                {
                    sessionIds = new List<uint>(_sessions.Keys);
                }
                if (sessionIds.Count == 0)
                {
                    Logger.Info("No attached clients — skipping image clipboard transfer");
                    return;
                }

                Logger.Info("ImageClipboard transfer started: transferId={0} dibSize={1} sessions={2}",
                    transferId, dibBytes.Length, sessionIds.Count);

                // 1) 发送 Start
                var startMsg = new ImageClipboardStartMessage
                {
                    TransferId = transferId,
                    TotalSize = dibBytes.Length
                };
                byte[] startPayload = startMsg.Pack();
                foreach (var sid in sessionIds)
                {
                    try
                    {
                        // FragAndSend 签名：(frameId, messageType, payload, sendAction, sessionId)
                        // 控制流 frameId=0；sessionId=sid（传给 sendAction → SendTo）
                        SendMessage(sid, (byte)MessageType.ImageClipboardStart, startPayload);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, "SendImageClipboardStart failed for session {0}", sid);
                    }
                }
                Logger.Info("ImageClipboardStart sent: transferId={0} dibSize={1}", transferId, dibBytes.Length);

                // 2) 分块发送 Data
                int offset = 0;
                while (offset < dibBytes.Length)
                {
                    int chunkLen = Math.Min(ImageChunkSize, dibBytes.Length - offset);
                    byte[] chunk;
                    if (chunkLen == ImageChunkSize && offset == 0)
                    {
                        // 第一块可以直接用原数组引用（只读）
                        chunk = dibBytes;
                    }
                    else
                    {
                        chunk = new byte[chunkLen];
                        Buffer.BlockCopy(dibBytes, offset, chunk, 0, chunkLen);
                    }

                    var dataMsg = new ImageClipboardDataMessage
                    {
                        TransferId = transferId,
                        Offset = offset,
                        DataLen = chunkLen,
                        Data = chunk
                    };
                    byte[] dataPayload = dataMsg.Pack();
                    foreach (var sid in sessionIds)
                    {
                        try
                        {
                            // FragAndSend 签名：(frameId, messageType, payload, sendAction, sessionId)
                            // 控制流 frameId=0；sessionId=sid（传给 sendAction → SendTo）
                            SendMessage(sid, (byte)MessageType.ImageClipboardData, dataPayload);
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn(ex, "SendImageClipboardData failed for session {0}", sid);
                        }
                    }
                    offset += chunkLen;
                }
                Logger.Info("ImageClipboardData sent: transferId={0} chunks={1} totalBytes={2}",
                    transferId, (dibBytes.Length + ImageChunkSize - 1) / ImageChunkSize, dibBytes.Length);

                // 3) 发送 End
                var endMsg = new ImageClipboardEndMessage { TransferId = transferId };
                byte[] endPayload = endMsg.Pack();
                foreach (var sid in sessionIds)
                {
                    try
                    {
                        // FragAndSend 签名：(frameId, messageType, payload, sendAction, sessionId)
                        // 控制流 frameId=0；sessionId=sid（传给 sendAction → SendTo）
                        SendMessage(sid, (byte)MessageType.ImageClipboardEnd, endPayload);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, "SendImageClipboardEnd failed for session {0}", sid);
                    }
                }
                Logger.Info("ImageClipboardEnd sent: transferId={0}", transferId);
            }
            finally
            {
                _imageTransferLock.Release();
            }
        }

        /// <summary>计算字节数组的前 N 字节的简单哈希（用于图片签名，非加密用途）。</summary>
        private static string ComputeSimpleHash(byte[] data, int sampleLen)
        {
            int len = Math.Min(sampleLen, data.Length);
            long hash = 0;
            for (int i = 0; i < len; i++)
            {
                hash = (hash << 3) ^ data[i];
            }
            return hash.ToString("X");
        }

        /// <summary>
        /// 发送剪贴板文本到所有已 attached 的客户端。
        /// </summary>
        private void SendClipboardToClients(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                var msg = new ClipboardSyncMessage
                {
                    Format = ClipboardSyncMessage.FormatText,
                    Text = text
                };
                byte[] payload = msg.Pack();

                List<uint> sessionIds;
                lock (_lock)
                {
                    sessionIds = new List<uint>(_sessions.Keys);
                }

                foreach (var sid in sessionIds)
                {
                    try
                    {
                        // FragAndSend 签名：(frameId, messageType, payload, sendAction, sessionId)
                        // 控制流 frameId=0；sessionId=sid（传给 sendAction → SendTo）
                        SendMessage(sid, (byte)MessageType.ClipboardSync, payload);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, "SendClipboardToClients failed for session {0}", sid);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "SendClipboardToClients pack failed");
            }
        }

        /// <summary>
        /// 把客户端发来的剪贴板文本入队，由 STA 线程异步设置到系统剪贴板。
        /// 线程安全：可从任意线程调用。
        /// </summary>
        public void EnqueueClipboardText(string text)
        {
            if (_clipboardService == null || string.IsNullOrEmpty(text)) return;
            _clipboardQueue.Enqueue(text);
            _clipboardSignal.Set();
        }

        public void Dispose()
        {
            Stop();
        }

        /// <summary>获取指定会话已发送帧数（-1 表示会话不存在），供 UI 定期刷新。</summary>
        public long GetSessionFrames(uint sessionId)
        {
            lock (_lock)
            {
                SessionInfo info;
                if (_sessions.TryGetValue(sessionId, out info) && info.Stream != null)
                    return info.Stream.FramesSent;
                return -1;
            }
        }

        /// <summary>强制断开指定会话（UI 踢出按钮）。</summary>
        public void KickSession(uint sessionId)
        {
            Logger.Info("Session {0} kicked by UI", sessionId);
            DisconnectSession(sessionId);
        }

        private void OnClientConnected(object sender, TransportAcceptedEventArgs e)
        {
            uint sessionId;
            lock (_lock)
            {
                sessionId = _nextSessionId++;
            }

            Logger.Info("Client connected: sessionId={0} remote={1}", sessionId, e.RemoteEndPoint);

            var transport = e.Transport;

            lock (_lock)
            {
                _transports[sessionId] = transport;
                _lastActivity[sessionId] = DateTime.UtcNow;
                _remoteEndpoints[sessionId] = e.RemoteEndPoint ?? "";
            }

            // 订阅该连接的 MessageReceived/Disconnected（闭包捕获 sessionId 完成路由），
            // 订阅完成后才 Start() 启动接收，避免首包在订阅前到达而丢失（首包竞态）。
            transport.MessageReceived += (s, args) =>
            {
                // 闭包捕获 sessionId 完成多会话路由（SessionId 不再由事件参数携带）。
                lock (_lock)
                {
                    _lastActivity[sessionId] = DateTime.UtcNow;
                }
                OnMessageReceived(sessionId, args);
            };
            transport.Disconnected += (s, args) => OnTransportDisconnected(sessionId);

            transport.Start();
        }

        private void OnMessageReceived(uint sessionId, MessageReceivedEventArgs e)
        {
            if (e.MessageType == (byte)MessageType.HandshakeReq)
            {
                HandleHandshake(sessionId, e);
            }
            else
            {
                // Route to appropriate session
                SessionInfo info;
                lock (_lock)
                {
                    if (!_sessions.TryGetValue(sessionId, out info))
                    {
                        // 诊断：会话不存在时记录，定位 InputEvent 丢失是否因会话查找失败
                        if (e.MessageType == (byte)MessageType.InputEvent)
                            Logger.Warn("InputEvent dropped: sessionId={0} not found in _sessions", sessionId);
                        return;
                    }
                }

                if (e.MessageType == (byte)MessageType.InputEvent)
                {
                    if (info.Input == null)
                    {
                        Logger.Warn("InputEvent dropped: sessionId={0} info.Input is null", sessionId);
                    }
                    else
                    {
                        var inputMsg = InputEventMessage.Unpack(e.Data);
                        // 诊断：记录 InputEvent 的 InputEventType（payload 第 1 字节），
                        // 与客户端 SendInput 日志对照可定位消息丢失环节。
                        // MouseDown=4 MouseUp=5 KeyDown=1 KeyUp=2 MouseMove=3 MouseWheel=6
                        Logger.Debug("InputEvent dispatch: sessionId={0} inputType={1} keyCode={2}",
                            sessionId, inputMsg.Type, inputMsg.KeyCode);
                        info.Input.HandleInput(inputMsg);
                        // 阶段二：鼠标按下/抬起时通知流会话（ZRLE CopyRect 触发条件），
                        // 仅在 ZRLE 模式下编码器会响应此状态，H264 路径无副作用。
                        if (inputMsg.Type == InputEventType.MouseDown)
                            info.Stream.SetMouseButtonDown(true);
                        else if (inputMsg.Type == InputEventType.MouseUp)
                            info.Stream.SetMouseButtonDown(false);
                    }
                }
                else if (e.MessageType == (byte)MessageType.ClipboardSync)
                {
                    HandleClipboardSync(sessionId, e);
                }
                else if (e.MessageType == (byte)MessageType.ClipFormatList)
                {
                    HandleClipFormatListFromClient(sessionId, e);
                }
                else if (e.MessageType == (byte)MessageType.ClipFileContentsReq)
                {
                    HandleClipFileContentsReqFromClient(sessionId, e);
                }
                else if (e.MessageType == (byte)MessageType.ClipFileContentsRes)
                {
                    HandleClipFileContentsResFromClient(sessionId, e);
                }
                else if (e.MessageType == (byte)MessageType.ImageClipboardStart
                         || e.MessageType == (byte)MessageType.ImageClipboardData
                         || e.MessageType == (byte)MessageType.ImageClipboardEnd)
                {
                    HandleImageClipboardFromClient(sessionId, e);
                }
                else if (e.MessageType == (byte)MessageType.FramebufferUpdateRequest)
                {
                    // 阶段三：客户端请求下一帧（ZRLE 流控）。
                    // 无 payload，仅通知流会话有新的消费能力；流控仅在 ZRLE 会话启用，
                    // H264 会话的 Stream.OnFramebufferUpdateRequest 置标志但不产生副作用
                    // （H264 未启用 _flowControlEnabled，EncodeLoop 不等待）。
                    info.Stream.OnFramebufferUpdateRequest();
                }
                else if (e.MessageType == (byte)MessageType.Keepalive)
                {
                    // RTT 测量：客户端 Keepalive payload 携带发送时刻时间戳（8 字节 UtcNow.Ticks），
                    // 原样回显给该客户端，客户端收到后计算往返时延。空 payload（服务端自身
                    // 心跳探测的回包路径不经过此处）直接忽略。TCP 下该消息极轻量（≤24 字节线格式）。
                    if (e.Data != null && e.Data.Length >= 8)
                    {
                        SendMessage(sessionId, (byte)MessageType.Keepalive, e.Data);
                    }
                }
                else if (e.MessageType == (byte)MessageType.DiagnosticInfoRequest)
                {
                    // 连接详情面板：客户端请求服务端系统信息，回发 DiagnosticInfo。
                    // 采集一次缓存，不阻塞；失败静默（面板对应项显示未知）。
                    try
                    {
                        SendDiagnosticInfo(sessionId);
                    }
                    catch (Exception diagEx)
                    {
                        Logger.Warn(diagEx, "SendDiagnosticInfo failed for session {0}", sessionId);
                    }
                }
            }
        }

        /// <summary>
        /// 处理客户端发来的 ClipFormatList（延迟渲染）：客户端用户复制文件，仅发元信息。
        /// 创建 per-session FileClipboardConsumer，启动后台下载线程按需拉取文件内容。
        /// 下载完成后入队，由 ClipboardLoop 在 STA 线程调用 SetFiles 设置 CF_HDROP。
        /// </summary>
        private void HandleClipFormatListFromClient(uint sessionId, MessageReceivedEventArgs e)
        {
            try
            {
                var msg = ClipFormatListMessage.Unpack(e.Data);
                string sessionTag = "server_" + sessionId;
                string key = sessionId + "_" + msg.TransferId;

                // 创建 Consumer：通过 transport 向客户端发送 ClipFileContentsReq
                var consumer = new FileClipboardConsumer(msg.TransferId, msg.Files, sessionTag,
                    (sidArg, payload) =>
                    {
                        // sidArg 被忽略（Consumer 不区分 session，由本回调封装）；用外层 sessionId 发送
                        SendMessage(sessionId, (byte)MessageType.ClipFileContentsReq, payload);
                    },
                    localPaths =>
                    {
                        // 下载完成（无论成功失败）后从字典移除，避免长期运行内存累积
                        FileClipboardConsumer removed;
                        _serverClipConsumers.TryRemove(key, out removed);

                        Logger.Info("Server file clipboard download complete: session={0} transferId={1} files={2}",
                            sessionId, msg.TransferId, localPaths != null ? localPaths.Length : 0);
                        if (localPaths != null && localPaths.Length > 0)
                            EnqueueServerClipboardFiles(localPaths);
                    });

                _serverClipConsumers[key] = consumer;

                // 里程碑式进度日志：每 10% 记录一次，避免日志过多
                int lastMilestone = -1;
                consumer.ProgressChanged += (downloaded, total) =>
                {
                    if (total <= 0) return;
                    int percent = (int)((downloaded * 100) / total);
                    int milestone = percent / 10 * 10; // 0, 10, 20, ..., 100
                    if (milestone > lastMilestone)
                    {
                        lastMilestone = milestone;
                        Logger.Info("Server clipboard download progress: session={0} transferId={1} {2}% ({3} / {4} bytes)",
                            sessionId, msg.TransferId, milestone, downloaded, total);
                    }
                };

                consumer.StartDownload();
                Logger.Info("Server received ClipFormatList: session={0} transferId={1} fileCount={2}",
                    sessionId, msg.TransferId, msg.Files.Count);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "HandleClipFormatListFromClient failed");
            }
        }

        /// <summary>
        /// 处理客户端发来的 ClipFileContentsReq（延迟渲染）：客户端请求服务端的文件内容。
        /// 路由到 per-session FileClipboardProvider，由 Provider 读取文件并响应 ClipFileContentsRes。
        /// </summary>
        private void HandleClipFileContentsReqFromClient(uint sessionId, MessageReceivedEventArgs e)
        {
            try
            {
                var msg = ClipFileContentsReqMessage.Unpack(e.Data);
                FileClipboardProvider provider;
                lock (_clipProviderLock)
                {
                    if (!_serverClipProviders.TryGetValue(sessionId, out provider))
                    {
                        Logger.Warn("ClipFileContentsReq from session {0} but no provider: transferId={1}",
                            sessionId, msg.TransferId);
                        return;
                    }
                }
                // 文件读取与响应发送放到线程池：接收线程不应被磁盘 IO 阻塞（影响输入事件延迟）
                System.Threading.ThreadPool.QueueUserWorkItem(state =>
                {
                    try { provider.HandleFileContentsReq(msg); }
                    catch (Exception ex) { Logger.Warn(ex, "HandleFileContentsReqFromClient failed on worker"); }
                });
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "HandleClipFileContentsReqFromClient failed");
            }
        }

        /// <summary>
        /// 处理客户端发来的 ClipFileContentsRes（延迟渲染）：客户端返回的文件内容块。
        /// 路由到 per-session FileClipboardConsumer，由 Consumer 写入临时文件。
        /// </summary>
        private void HandleClipFileContentsResFromClient(uint sessionId, MessageReceivedEventArgs e)
        {
            try
            {
                var msg = ClipFileContentsResMessage.Unpack(e.Data);
                string key = sessionId + "_" + msg.TransferId;
                FileClipboardConsumer consumer;
                if (_serverClipConsumers.TryGetValue(key, out consumer))
                {
                    consumer.HandleFileContentsRes(msg);
                }
                else
                {
                    Logger.Warn("ClipFileContentsRes for unknown transfer: session={0} transferId={1}",
                        sessionId, msg.TransferId);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "HandleClipFileContentsResFromClient failed");
            }
        }

        /// <summary>
        /// 由 FileClipboardConsumer 的 onFinish 回调调用：文件接收完毕，把文件路径设置到服务端本地剪贴板。
        /// 必须转发到 STA 线程（ClipboardLoop）调用 IClipboardService.SetFiles。
        /// </summary>
        public void EnqueueServerClipboardFiles(string[] filePaths)
        {
            if (_clipboardService == null || filePaths == null || filePaths.Length == 0) return;
            // 入队，由 ClipboardLoop 在 STA 线程处理
            _serverFileSetQueue.Enqueue(filePaths);
            _clipboardSignal.Set();
        }

        /// <summary>
        /// 处理客户端发来的图片剪贴板消息（ImageClipboardStart/Data/End）。
        /// 用 per-session 的 ImageClipboardReceiver 管理接收状态。
        /// 收到 End 时，把 CF_DIB 字节入队，由 ClipboardLoop 在 STA 线程调用 SetImageDibBytes。
        /// </summary>
        private void HandleImageClipboardFromClient(uint sessionId, MessageReceivedEventArgs e)
        {
            try
            {
                if (e.MessageType == (byte)MessageType.ImageClipboardStart)
                {
                    var msg = ImageClipboardStartMessage.Unpack(e.Data);
                    string key = sessionId + "_" + msg.TransferId;
                    var receiver = new ImageClipboardReceiver(msg.TransferId, msg.TotalSize);
                    _serverImageReceivers[key] = receiver;
                    Logger.Info("Server received ImageClipboardStart: session={0} transferId={1} totalSize={2}",
                        sessionId, msg.TransferId, msg.TotalSize);
                }
                else if (e.MessageType == (byte)MessageType.ImageClipboardData)
                {
                    var msg = ImageClipboardDataMessage.Unpack(e.Data);
                    string key = sessionId + "_" + msg.TransferId;
                    ImageClipboardReceiver receiver;
                    if (!_serverImageReceivers.TryGetValue(key, out receiver))
                    {
                        Logger.Warn("Server ImageClipboardData for unknown transfer: {0}", key);
                        return;
                    }
                    receiver.WriteChunk(msg.Offset, msg.Data, msg.DataLen);
                }
                else if (e.MessageType == (byte)MessageType.ImageClipboardEnd)
                {
                    var msg = ImageClipboardEndMessage.Unpack(e.Data);
                    string key = sessionId + "_" + msg.TransferId;
                    ImageClipboardReceiver receiver;
                    if (!_serverImageReceivers.TryRemove(key, out receiver))
                    {
                        Logger.Warn("Server ImageClipboardEnd for unknown transfer: {0}", key);
                        return;
                    }
                    byte[] dibBytes = receiver.Finish();
                    Logger.Info("Server ImageClipboardEnd: session={0} transferId={1} dibSize={2}",
                        sessionId, msg.TransferId, dibBytes != null ? dibBytes.Length : 0);
                    if (dibBytes != null && dibBytes.Length > 0)
                        EnqueueServerClipboardImage(dibBytes);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "HandleImageClipboardFromClient failed");
            }
        }

        /// <summary>
        /// 图片剪贴板接收完毕入队：由 ClipboardLoop 在 STA 线程调用 SetImageDibBytes 设置 CF_DIB。
        /// </summary>
        public void EnqueueServerClipboardImage(byte[] dibBytes)
        {
            if (_clipboardService == null || dibBytes == null || dibBytes.Length == 0) return;
            _serverImageSetQueue.Enqueue(dibBytes);
            _clipboardSignal.Set();
        }

        /// <summary>
        /// 处理客户端发来的剪贴板同步消息。把文本入队，由 STA 线程设置到系统剪贴板。
        /// </summary>
        private void HandleClipboardSync(uint sessionId, MessageReceivedEventArgs e)
        {
            try
            {
                var msg = ClipboardSyncMessage.Unpack(e.Data);
                if (msg.Format == ClipboardSyncMessage.FormatText)
                {
                    string text = msg.Text;
                    if (!string.IsNullOrEmpty(text))
                    {
                        EnqueueClipboardText(text);
                        Logger.Info("Clipboard sync from session {0}: len={1}", sessionId, text.Length);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ClipboardSync unpack failed from session {0}", sessionId);
            }
        }

        private void HandleHandshake(uint sessionId, MessageReceivedEventArgs e)
        {
            var req = HandshakeReq.Unpack(e.Data);
            Logger.Info("Handshake request from sessionId={0}: version={1} username={2}",
                sessionId, req.Version, req.Username);

            HandshakeRes res;
            if (req.Version != Constants.ProtocolVersion)
            {
                Logger.Warn("Version mismatch: client={0} server={1}", req.Version, Constants.ProtocolVersion);
                res = new HandshakeRes { Result = HandshakeResult.VersionMismatch };
                SendResponse(sessionId, res);
                DisconnectSession(sessionId);
                return;
            }

            // Check concurrency limit
            lock (_lock)
            {
                if (_activeCount >= _maxSessions)
                {
                    Logger.Warn("Server busy: activeCount={0} maxSessions={1}", _activeCount, _maxSessions);
                    res = new HandshakeRes { Result = HandshakeResult.ServerBusy };
                    SendResponse(sessionId, res);
                    DisconnectSession(sessionId);
                    return;
                }
            }

            // 简单认证：硬编码凭据表（后续应改为配置文件或外部凭据存储）
            if (!ValidateCredentials(req.Username, req.Password))
            {
                Logger.Warn("Auth failed for username='{0}'", req.Username);
                res = new HandshakeRes { Result = HandshakeResult.AuthFailed };
                SendResponse(sessionId, res);
                DisconnectSession(sessionId);
                return;
            }

            // Negotiate codec
            var serverCaps = EncoderFactory.GetAvailableCodecs();
            var negotiated = CodecNegotiator.Negotiate(req.Capabilities, serverCaps);
            if (!negotiated.HasValue)
            {
                // H.264 是唯一编码方式（设计文档 D1 禁止回退原始像素）。
                // 无公共编码器（含服务端无编码器）直接拒绝，避免客户端拿到 Success 后黑屏。
                Logger.Warn("No common codec: clientCaps={0} serverCaps={1}", req.Capabilities, serverCaps);
                res = new HandshakeRes { Result = HandshakeResult.NoCommonCodec };
                SendResponse(sessionId, res);
                DisconnectSession(sessionId);
                return;
            }

            try
            {
                var bounds = _captureService.GetPrimaryScreen();

                // Create sessions first (don't send Success until Start() passes)
                // 按 ServerSettings.ChangeDetectionMode 创建帧变化检测器注入会话。
                // 切换在下次会话接入时生效，已有会话保持原检测器不变。
                var changeDetector = ChangeDetectorFactory.Create(_changeDetectionMode);
                Logger.Info("Session {0}: change detector created, mode={1}", sessionId, _changeDetectionMode);
                var streamSession = new ServerStreamSession(_captureService, (sid, data) =>
                {
                    SendRaw(sid, data);
                }, _cursorTracker, changeDetector);
                // 流会话不可恢复故障（编码器反复失败等）→ 记录日志并异步断开该会话。
                // 事件可能在编码线程触发，不能直接调用 DisconnectSession（Stop 会 Join 自身线程），
                // 因此通过线程池调度。
                streamSession.FatalError += (s, args) =>
                {
                    string message = args != null ? args.Message : "Unknown";
                    Logger.Error("Session {0}: stream fatal error: {1}", sessionId, message);
                    // 线程池调度，避免与编码线程自我 Join 死锁
                    System.Threading.ThreadPool.QueueUserWorkItem(state =>
                    {
                        try { DisconnectSession(sessionId); }
                        catch (Exception ex) { Logger.Warn(ex, "Fatal-error disconnect failed"); }
                    });
                };

                var inputSession = new ServerInputSession(_inputSimulator);

                lock (_lock)
                {
                    _sessions[sessionId] = new SessionInfo
                    {
                        Stream = streamSession,
                        Input = inputSession
                    };
                    _activeCount++;
                }

                // 首个会话时启动捕获/光标（幂等；必须在 streamSession.Start 之前，
                // 否则编码线程收不到帧、光标会话无轮询线程）
                EnsureCaptureRunning();

                // Start — may throw if encoder init fails
                streamSession.Start(sessionId, negotiated.Value);

                // Only send Success after session fully starts
                // 注意：握手分辨率必须用编码实际分辨率（_lastW/_lastH，向上取偶后），
                // 而非 bounds.Width/Height（原始屏幕尺寸）。
                // 客户端用此值计算 aspect ratio 和坐标映射，必须与视频实际分辨率一致，
                // 否则即使 1px 偏差也会导致 letterbox 计算和鼠标映射不精确。
                res = new HandshakeRes
                {
                    Result = HandshakeResult.Success,
                    Codec = negotiated.Value,
                    ScreenWidth = streamSession.EncodeWidth,
                    ScreenHeight = streamSession.EncodeHeight
                };
                SendResponse(sessionId, res);
                Logger.Info("Handshake success: sessionId={0} codec={1} resolution={2}x{3}",
                    sessionId, negotiated.Value, bounds.Width, bounds.Height);
                Logger.Info("Session {0} stream started with codec {1}", sessionId, negotiated.Value);

                // Fire session attached event
                var handler = SessionAttached;
                if (handler != null)
                {
                    string remote;
                    lock (_lock)
                    {
                        if (!_remoteEndpoints.TryGetValue(sessionId, out remote))
                            remote = "?";
                    }
                    string codec = negotiated.Value.ToString();
                    string resolution = bounds.Width + "x" + bounds.Height;
                    handler(sessionId, remote, codec, resolution);
                }
            }
            catch (Exception ex)
            {
                // Session startup failed — send error response and clean up
                Logger.Error(ex, "Handshake session startup failed for sessionId={0}", sessionId);
                res = new HandshakeRes { Result = HandshakeResult.InternalError };
                SendResponse(sessionId, res);
                DisconnectSession(sessionId);
            }
        }

        private void SendResponse(uint sessionId, HandshakeRes res)
        {
            byte[] payload = res.Pack();
            SendMessage(sessionId, (byte)MessageType.HandshakeRes, payload);
        }

        /// <summary>线程安全地向指定会话发送一条完整消息（framing 外层由本方法拼装）。</summary>
        private void SendMessage(uint sessionId, byte messageType, byte[] payload)
        {
            ITransport transport;
            lock (_lock)
            {
                if (!_transports.TryGetValue(sessionId, out transport))
                    return;
            }
            transport.Send(Framing.BuildMessage(messageType, payload));
        }

        /// <summary>线程安全地向指定会话发送已 framed 的完整消息字节（如 ServerStreamSession 已拼装好的帧）。</summary>
        private void SendRaw(uint sessionId, byte[] wire)
        {
            ITransport transport;
            lock (_lock)
            {
                if (!_transports.TryGetValue(sessionId, out transport))
                    return;
            }
            transport.Send(wire);
        }

        private void OnTransportDisconnected(uint sessionId)
        {
            DisconnectSession(sessionId);

            var handler = SessionDetached;
            if (handler != null) handler(sessionId);
        }

        private void DisconnectSession(uint sessionId)
        {
            Logger.Info("Disconnecting session {0}", sessionId);
            SessionInfo info = null;
            ITransport transport = null;
            bool hasSession;
            lock (_lock)
            {
                hasSession = _sessions.TryGetValue(sessionId, out info);
                if (hasSession)
                {
                    _sessions.Remove(sessionId);
                    _activeCount--;
                }
                // 无论握手是否完成，都清理 per-session 字典，防止「连上但握手前断连」时泄漏
                _transports.TryGetValue(sessionId, out transport);
                _transports.Remove(sessionId);
                _lastActivity.Remove(sessionId);
                _remoteEndpoints.Remove(sessionId);
            }

            if (!hasSession)
            {
                // 握手前断连：无 Session 可清理，仅断开 transport
                if (transport != null)
                {
                    try { transport.Disconnect(); } catch { }
                }
                return;
            }

            // 清理 per-session 延迟渲染状态：Dispose Provider（停止响应 FileContentsReq）
            lock (_clipProviderLock)
            {
                FileClipboardProvider provider;
                if (_serverClipProviders.TryGetValue(sessionId, out provider))
                {
                    _serverClipProviders.Remove(sessionId);
                    try { provider.Dispose(); } catch { }
                }
            }
            // 清理 per-session Consumer：Cancel 正在进行的下载
            string sessionPrefix = sessionId + "_";
            foreach (var kv in _serverClipConsumers)
            {
                if (kv.Key.StartsWith(sessionPrefix))
                {
                    FileClipboardConsumer removed;
                    if (_serverClipConsumers.TryRemove(kv.Key, out removed))
                    {
                        try { removed.Cancel(); } catch { }
                    }
                }
            }
            // 清理 per-session 图片剪贴板接收器：未收到 End 就断连的会话会残留大 DIB 缓冲
            foreach (var kv in _serverImageReceivers)
            {
                if (kv.Key.StartsWith(sessionPrefix))
                {
                    ImageClipboardReceiver removed;
                    _serverImageReceivers.TryRemove(kv.Key, out removed);
                }
            }

            try { info.Stream?.Stop(); } catch { }
            try { info.Stream?.Dispose(); } catch { }
            try { info.Input?.Dispose(); } catch { }

            if (transport != null)
            {
                try { transport.Disconnect(); } catch { }
            }
            Logger.Info("Session {0} disconnected", sessionId);

            // 无会话后停止捕获与光标追踪（服务端保持监听，等待下一客户端）
            StopCaptureIfIdle();
        }

        /// <summary>
        /// 组装并回发服务端诊断信息（响应 DiagnosticInfoRequest）。
        /// 屏幕尺寸/采集方式取当前实际值；编码器可用性通过 EncoderFactory 动态探测；
        /// 系统静态信息（CPU/GPU/OS/内存）由 SystemInfoCollector 缓存。
        /// </summary>
        private void SendDiagnosticInfo(uint sessionId)
        {
            // 采集方式：仅当具体实现为 CaptureService 时才能取到（ICaptureService 接口无此属性）
            byte captureMethod = 0;
            CaptureService capture = _captureService as CaptureService;
            if (capture != null)
                captureMethod = capture.CaptureMethod;

            int screenW = 0, screenH = 0;
            try
            {
                EasyDesk.Core.Models.DesktopBounds primary = _captureService.GetPrimaryScreen();
                screenW = primary.Width;
                screenH = primary.Height;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "GetPrimaryScreen failed for diagnostics");
            }

            CodecCapabilities caps = EncoderFactory.GetAvailableCodecs();
            bool h264 = (caps & CodecCapabilities.H264Software) != 0;
            bool zrle = (caps & CodecCapabilities.Zrle) != 0;
            bool vp8 = (caps & CodecCapabilities.Vp8Software) != 0;

            DiagnosticInfoMessage msg = _systemInfoCollector.Collect(
                captureMethod, screenW, screenH,
                EasyRDP.Server.Wpf.Services.SystemInfoCollector.GetScaleFactorX100(),
                h264, zrle, vp8);

            byte[] payload = msg.Pack();
            // 控制流单分片发送（消息很小，不切分）
            SendMessage(sessionId, (byte)MessageType.DiagnosticInfo, payload);
            Logger.Info("DiagnosticInfo sent to session {0}: cpu={1} gpu={2} memMB={3} capture={4} scale={5}",
                sessionId, msg.CpuName, msg.GpuName, msg.TotalMemoryMb, msg.CaptureMethod, msg.ScaleFactorX100);
        }

        private void HeartbeatLoop()
        {
            while (_running)
            {
                Thread.Sleep(10000); // 10s interval

                List<uint> timedOut = new List<uint>();
                List<uint> keepaliveTargets = new List<uint>();
                lock (_lock)
                {
                    var now = DateTime.UtcNow;
                    foreach (var kv in _lastActivity)
                    {
                        if ((now - kv.Value).TotalSeconds > 45) // 30s + 15s grace
                        {
                            timedOut.Add(kv.Key);
                        }
                        else if ((now - kv.Value).TotalSeconds > 30)
                        {
                            keepaliveTargets.Add(kv.Key);
                        }
                    }
                }

                // D12 全局负载自适应：统计所有活跃会话的平均编码耗时，
                // 过载时对所有会话同步降级（ApplyGlobalLoadLevel 加帧间隔），
                // 恢复后逐级回升。与 per-Session 的 D11 自适应叠加，取更保守设置。
                UpdateGlobalLoadLevel();

                // 锁外发送 keepalive：SendTo 是阻塞网络 I/O，慢客户端会冻结锁保护的所有会话管理。
                foreach (var sid in keepaliveTargets)
                {
                    try
                    {
                        var empty = new byte[0];
                        SendMessage(sid, (byte)MessageType.Keepalive, empty);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, "Keepalive send failed for session {0}", sid);
                    }
                }

                foreach (var sid in timedOut)
                {
                    Logger.Warn("Session {0} heartbeat timeout — disconnecting", sid);
                    DisconnectSession(sid);
                }
            }
        }

        /// <summary>
        /// D12 全局负载感知：统计所有会话的平均编码耗时，决定全局负载级并下发。
        /// 负载级 0=正常；1/2=过载（每级帧间隔 +10ms，由 ServerStreamSession 生效）。
        /// 判定带滞后：连续 3 次超标才升一级、连续 3 次充裕才降一级，避免抖动。
        /// </summary>
        private void UpdateGlobalLoadLevel()
        {
            double sum = 0;
            int count = 0;
            lock (_lock)
            {
                foreach (var kv in _sessions)
                {
                    if (kv.Value.Stream != null && kv.Value.Stream.AvgEncodeMs > 0)
                    {
                        sum += kv.Value.Stream.AvgEncodeMs;
                        count++;
                    }
                }
            }
            if (count == 0)
            {
                if (_globalLoadLevel != 0)
                {
                    _globalLoadLevel = 0;
                    ApplyGlobalLoadLevelToSessions();
                }
                return;
            }

            double avgMs = sum / count;
            // 过载：平均编码耗时超过目标帧周期（33ms≈30fps）
            if (avgMs > 33)
            {
                _globalLoadHighStreak++;
                _globalLoadLowStreak = 0;
                if (_globalLoadHighStreak >= 3 && _globalLoadLevel < 2)
                {
                    _globalLoadHighStreak = 0;
                    _globalLoadLevel++;
                    Logger.Warn("D12: global encode load high ({0:F1}ms avg, {1} sessions) — load level {2}",
                        avgMs, count, _globalLoadLevel);
                    ApplyGlobalLoadLevelToSessions();
                }
            }
            else if (avgMs < 20)
            {
                _globalLoadLowStreak++;
                _globalLoadHighStreak = 0;
                if (_globalLoadLowStreak >= 3 && _globalLoadLevel > 0)
                {
                    _globalLoadLowStreak = 0;
                    _globalLoadLevel--;
                    Logger.Info("D12: global encode load recovered ({0:F1}ms avg) — load level {1}",
                        avgMs, _globalLoadLevel);
                    ApplyGlobalLoadLevelToSessions();
                }
            }
            else
            {
                _globalLoadHighStreak = 0;
                _globalLoadLowStreak = 0;
            }
        }

        private void ApplyGlobalLoadLevelToSessions()
        {
            lock (_lock)
            {
                foreach (var kv in _sessions)
                {
                    try
                    {
                        if (kv.Value.Stream != null)
                            kv.Value.Stream.ApplyGlobalLoadLevel(_globalLoadLevel);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, "ApplyGlobalLoadLevel failed for session {0}", kv.Key);
                    }
                }
            }
        }

        /// <summary>
        /// 验证凭据。读取 UI 配置的凭据表（_credentials）。
        /// 若 _credentials 为空，默认拒绝所有连接。
        /// </summary>
        private bool ValidateCredentials(string username, string password)
        {
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
                return false;
            string stored;
            if (_credentials.TryGetValue(username, out stored))
                return stored == password;
            return false;
        }


    }
}
