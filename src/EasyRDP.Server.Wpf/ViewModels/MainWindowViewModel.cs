#nullable disable
using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using AlyClient.CSharpSDK;
using EasyRDP.Server.Wpf.Models;
using EasyRDP.Server.Wpf.Services;
using EasyDesk.Core;
using EasyDesk.Windows;
using EasyRDP.Core.Protocol;
using EasyRDP.Core.Transport;
using EasyRDP.Shared;
using NLog;

namespace EasyRDP.Server.Wpf.ViewModels
{
    /// <summary>
    /// 服务端主窗口 ViewModel。管理服务生命周期、会话列表、日志。
    /// </summary>
    public class MainWindowViewModel : INotifyPropertyChanged
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly Dispatcher _dispatcher;

        // 业务对象
        private CaptureService _captureService;
        private ITransportAcceptor _transportAcceptor;
        private TransportHost _transportHost;
        private DateTime _startTime;
        private DispatcherTimer _uptimeTimer;

        // 绑定属性字段
        private string _statusText = "Stopped";
        private string _portText = "2000";
        private string _displayPort = "—";
        private string _sessionCount = "0";
        private string _uptime = "—";
        private string _logLine = "Ready";
        private bool _isRunning;
        // 默认不预设凭据：启动服务端时必须显式输入用户名/密码（StartServer 会校验非空）
        private string _username = "";
        private string _password = "";
        // 帧变化检测模式：默认原始方式（FullFrameMemcmp），保持与历史版本一致的行为。
        // UI 可切换到 BlockHashDirtyRect（32×32 块哈希，小变化跳过编码）。
        private ChangeDetectionMode _changeDetectionMode = ChangeDetectionMode.FullFrameMemcmp;
        // 开机自启：以注册表实际状态为准（构造函数中读取）
        private bool _autoStart;
        // 启动即监听：默认开启，打开程序即开始接受连接
        private bool _autoListen = true;
        // 允许局域网 UDP 发现（只暴露计算机名与 TCP 端口）
        private bool _allowDiscovery = true;
        private DiscoveryService _discoveryService;
        // 局域网发现显示名（空=计算机名）
        private string _serverName = "";
        private string _serverNameText = "";
        // 连接认证：默认口令模式（与历史版本一致）；审批模式免密码
        private ServerAuthMode _authMode = ServerAuthMode.Password;
        // PBKDF2 盐（Base64）：首次运行生成后固定，保证相同口令派生出相同密钥
        private string _authSalt = "";
        // 运行中的认证配置对象（TransportHost 持有同一引用，UI 改动即时生效）
        private ServerAuthConfig _authConfig;
        // 采集显示器索引（0=主屏，1..n=副屏）
        private int _captureDisplayIndex;
        private DisplayItem _selectedDisplay;

        // 设置持久化：%AppData%\EasyRDP\server\settings.json
        private readonly ServerSettingsStore _settingsStore = new ServerSettingsStore();

        // 用户提示抽象（默认 MessageBox，由 View 注入），避免 ViewModel 直接依赖 System.Windows.MessageBox
        private readonly IUserNotifier _notifier;

        /// <summary>日志条目集合（最新在前）。</summary>
        public ObservableCollection<string> LogEntries { get; } = new ObservableCollection<string>();

        /// <summary>会话列表集合。</summary>
        public ObservableCollection<SessionItem> Sessions { get; } = new ObservableCollection<SessionItem>();

        // ====== 绑定属性 ======

        public string StatusText
        {
            get { return _statusText; }
            set { _statusText = value; OnPropertyChanged(nameof(StatusText)); }
        }

        public string PortText
        {
            get { return _portText; }
            set { _portText = value; OnPropertyChanged(nameof(PortText)); }
        }

        public string DisplayPort
        {
            get { return _displayPort; }
            set { _displayPort = value; OnPropertyChanged(nameof(DisplayPort)); }
        }

        public string SessionCount
        {
            get { return _sessionCount; }
            set { _sessionCount = value; OnPropertyChanged(nameof(SessionCount)); }
        }

        public string Uptime
        {
            get { return _uptime; }
            set { _uptime = value; OnPropertyChanged(nameof(Uptime)); }
        }

        public string LogLine
        {
            get { return _logLine; }
            set { _logLine = value; OnPropertyChanged(nameof(LogLine)); }
        }

        public bool IsRunning
        {
            get { return _isRunning; }
            set
            {
                _isRunning = value;
                OnPropertyChanged(nameof(IsRunning));
                // 联动按钮状态
                StartCommand.RaiseCanExecuteChanged();
                StopCommand.RaiseCanExecuteChanged();
            }
        }

        /// <summary>服务端认证用户名。</summary>
        public string Username
        {
            get { return _username; }
            set
            {
                _username = value ?? "";
                OnPropertyChanged(nameof(Username));
                if (_authConfig != null) _authConfig.Username = _username;
            }
        }

        /// <summary>服务端认证密码（内存明文，落盘时由 ServerSettingsStore 做 DPAPI 加密）。</summary>
        public string Password
        {
            get { return _password; }
            set
            {
                _password = value ?? "";
                OnPropertyChanged(nameof(Password));
                if (_authConfig != null) _authConfig.Password = _password;
            }
        }

        /// <summary>
        /// 连接认证模式：
        /// - Password：客户端输入 USER/PASS，服务端校验挑战响应（默认）
        /// - Approve：客户端免密码，连接时由本机操作员 Allow/Deny
        /// 运行中切换立即生效（仅影响之后的握手）。
        /// </summary>
        public ServerAuthMode AuthMode
        {
            get { return _authMode; }
            set
            {
                if (_authMode == value) return;
                _authMode = value;
                OnPropertyChanged(nameof(AuthMode));
                OnPropertyChanged(nameof(IsPasswordMode));
                if (_authConfig != null) _authConfig.Mode = value;
                AddLog(value == ServerAuthMode.Approve
                    ? "Auth mode: approval required (no password)"
                    : "Auth mode: password");
            }
        }

        /// <summary>是否口令认证模式（XAML 用于禁用 PASS 输入框）。</summary>
        public bool IsPasswordMode
        {
            get { return _authMode == ServerAuthMode.Password; }
        }

        /// <summary>可采集的显示器列表（索引 0=主屏，1..n=副屏）。</summary>
        public ObservableCollection<DisplayItem> Displays { get; } =
            new ObservableCollection<DisplayItem>();

        /// <summary>
        /// 当前采集目标显示器。运行中切换立即生效：捕获线程换边界 →
        /// 会话重建编码器并发关键帧 → 输入坐标原点同步偏移。
        /// </summary>
        public DisplayItem SelectedDisplay
        {
            get { return _selectedDisplay; }
            set
            {
                if (value == null || ReferenceEquals(_selectedDisplay, value)) return;
                _selectedDisplay = value;
                _captureDisplayIndex = value.Index;
                OnPropertyChanged(nameof(SelectedDisplay));
                // 运行中：由 TransportHost 统一驱动（内部同时刷新鼠标偏移）
                if (_transportHost != null) _transportHost.SetCaptureDisplay(value.Index);
                AddLog("Capture display: " + value.DisplayText);
            }
        }

        /// <summary>
        /// 枚举本机显示器刷新下拉列表，并尽量保留用户此前的选择。
        /// 主屏固定排在索引 0，副屏按 EnumDisplayMonitors 顺序排 1..n，
        /// 语义与 CaptureService.GetCaptureBounds 完全一致。
        /// </summary>
        private void RefreshDisplays()
        {
            try
            {
                EasyDesk.Core.IScreenCapturer capturer = new WindowsDesktopFactory().CreateScreenCapturer();
                EasyDesk.Core.Models.DesktopBounds[] screens = capturer.GetAllScreens();
                if (screens == null || screens.Length == 0) return;

                int desired = _captureDisplayIndex;
                Displays.Clear();

                // 先主屏，再副屏；单显示器机器上只有一项，界面仍可用不出错
                EasyDesk.Core.Models.DesktopBounds primary = null;
                int secondaryCount = 0;
                foreach (EasyDesk.Core.Models.DesktopBounds s in screens)
                {
                    if (s.IsPrimary) { primary = s; break; }
                }
                if (primary == null) primary = screens[0];
                Displays.Add(NewDisplayItem(0, primary, true));

                int nextIndex = 1;
                foreach (EasyDesk.Core.Models.DesktopBounds s in screens)
                {
                    if (ReferenceEquals(s, primary)) continue;
                    Displays.Add(NewDisplayItem(nextIndex++, s, false));
                    secondaryCount++;
                }

                if (desired > secondaryCount) desired = 0;
                if (desired < 0) desired = 0;
                _captureDisplayIndex = desired;

                DisplayItem match = null;
                foreach (DisplayItem d in Displays)
                {
                    if (d.Index == desired) { match = d; break; }
                }
                // 直接赋字段：刷新列表不算"用户切换"，不应下发给运行中的会话
                _selectedDisplay = match;
                OnPropertyChanged(nameof(SelectedDisplay));
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "RefreshDisplays failed");
            }
        }

        /// <summary>构造一个显示器下拉项。</summary>
        private static DisplayItem NewDisplayItem(int index, EasyDesk.Core.Models.DesktopBounds bounds, bool isPrimary)
        {
            string text = "Display " + (index + 1) + " · " + bounds.Width + "×" + bounds.Height
                + (isPrimary ? " (Primary)" : " (" + bounds.X + "," + bounds.Y + ")");
            DisplayItem item = new DisplayItem();
            item.Index = index;
            item.DisplayText = text;
            item.Width = bounds.Width;
            item.Height = bounds.Height;
            item.IsPrimary = isPrimary;
            return item;
        }

        /// <summary>
        /// 帧变化检测模式。UI 可切换：
        /// - FullFrameMemcmp（原始方式，全帧 memcmp）
        /// - BlockHashDirtyRect（改进方式，32×32 块哈希，小变化跳过编码）
        /// 服务运行中修改时，新值在下次会话接入时生效（已建立会话不受影响）。
        /// </summary>
        public ChangeDetectionMode ChangeDetectionMode
        {
            get { return _changeDetectionMode; }
            set
            {
                if (_changeDetectionMode == value) return;
                _changeDetectionMode = value;
                OnPropertyChanged(nameof(ChangeDetectionMode));
                // 运行中切换：立即更新 TransportHost，下次会话接入时生效
                if (_transportHost != null)
                    _transportHost.ChangeDetectionMode = value;
            }
        }

        /// <summary>
        /// 是否随 Windows 登录自动启动。赋值立即写注册表（HKCU Run），
        /// 写入失败时保持原值并刷新界面，避免出现"勾上了但实际没生效"的假象。
        /// </summary>
        public bool AutoStart
        {
            get { return _autoStart; }
            set
            {
                if (_autoStart == value) return;
                bool actual = AutoStartManager.SetEnabled(value);
                if (actual != value)
                {
                    _notifier.ShowWarning(
                        "Failed to update the Windows startup entry. Please check registry permissions.",
                        "Auto Start");
                    // 字段未变，仍需通知界面回滚复选框
                    OnPropertyChanged(nameof(AutoStart));
                    return;
                }
                _autoStart = actual;
                OnPropertyChanged(nameof(AutoStart));
                AddLog(actual ? "Auto start enabled" : "Auto start disabled");
            }
        }

        /// <summary>
        /// 程序启动后是否立即开始监听。关闭后需手动点击 Start。
        /// 开启但凭据为空时静默跳过（只写日志），避免开机时弹框打断用户。
        /// </summary>
        public bool AutoListen
        {
            get { return _autoListen; }
            set
            {
                if (_autoListen == value) return;
                _autoListen = value;
                OnPropertyChanged(nameof(AutoListen));
            }
        }

        /// <summary>局域网发现显示名（空=计算机名）；运行中修改会重建发现服务。</summary>
        public string ServerNameText
        {
            get { return _serverNameText; }
            set
            {
                string text = value ?? "";
                if (_serverNameText == text) return;
                _serverNameText = text;
                _serverName = text;
                OnPropertyChanged(nameof(ServerNameText));
                if (IsRunning && _allowDiscovery)
                    StartDiscoveryService(CurrentPort());
            }
        }

        /// <summary>
        /// 是否允许局域网 UDP 发现。运行中切换立即生效（启停 UDP 监听）。
        /// 发现应答只包含计算机名、TCP 端口、认证模式与是否已满，不含任何凭据。
        /// </summary>
        public bool AllowDiscovery
        {
            get { return _allowDiscovery; }
            set
            {
                if (_allowDiscovery == value) return;
                _allowDiscovery = value;
                OnPropertyChanged(nameof(AllowDiscovery));
                AddLog(value ? "LAN discovery enabled" : "LAN discovery disabled");
                if (IsRunning)
                {
                    if (value)
                        StartDiscoveryService(CurrentPort());
                    else
                        StopDiscoveryService();
                }
            }
        }

        // ====== 命令 ======

        public RelayCommand StartCommand { get; }
        public RelayCommand StopCommand { get; }
        public RelayCommand SaveSettingsCommand { get; }

        /// <summary>踢出指定会话（参数为 SessionItem.IdValue）。</summary>
        public RelayCommand<uint> KickCommand { get; }

        /// <summary>向指定会话发送文件（参数为 SessionItem.IdValue）。</summary>
        public RelayCommand<uint> SendFileCommand { get; }

        // ====== 文件传输进度（服务端收发共用一条进度条） ======

        private bool _isFileTransferVisible;
        private double _fileTransferPercent;
        private string _fileTransferText = "";

        /// <summary>是否显示文件传输进度面板。</summary>
        public bool IsFileTransferVisible
        {
            get { return _isFileTransferVisible; }
            private set { _isFileTransferVisible = value; OnPropertyChanged(nameof(IsFileTransferVisible)); }
        }

        /// <summary>文件传输进度百分比（0~100）。</summary>
        public double FileTransferPercent
        {
            get { return _fileTransferPercent; }
            private set { _fileTransferPercent = value; OnPropertyChanged(nameof(FileTransferPercent)); }
        }

        /// <summary>文件传输说明文字（方向/文件名/速率）。</summary>
        public string FileTransferText
        {
            get { return _fileTransferText; }
            private set { _fileTransferText = value; OnPropertyChanged(nameof(FileTransferText)); }
        }

        public MainWindowViewModel(Dispatcher dispatcher, IUserNotifier notifier = null)
        {
            _dispatcher = dispatcher;
            _notifier = notifier ?? new WpfUserNotifier();
            StartCommand = new RelayCommand(StartServer, () => !IsRunning);
            StopCommand = new RelayCommand(StopServer, () => IsRunning);
            SaveSettingsCommand = new RelayCommand(SaveSettings);
            KickCommand = new RelayCommand<uint>(KickSession);
            SendFileCommand = new RelayCommand<uint>(SendFilesToSession);
            CheckUpdateCommand = new RelayCommand(CheckUpdate,
                () => AlyClientStatus == AlyClientStatus.DiscoveredUpdate
                    || AlyClientStatus == AlyClientStatus.DownloadedUpdate);

            // 启动时恢复上次保存的服务端设置
            ServerSettings settings = _settingsStore.Load();
            if (!string.IsNullOrEmpty(settings.Port))
                PortText = settings.Port;
            Username = settings.Username ?? "";
            Password = settings.Password ?? "";
            ChangeDetectionMode = settings.ChangeDetectionMode;
            AutoListen = settings.AutoListen;
            _authMode = settings.AuthMode;
            _authSalt = settings.AuthSalt ?? "";
            _allowDiscovery = settings.AllowDiscovery;
            _serverName = settings.ServerName ?? "";
            _serverNameText = _serverName;
            _captureDisplayIndex = settings.CaptureDisplay;
            RefreshDisplays();
            // 首次运行生成并立即落盘 PBKDF2 盐：盐固定后同一口令派生密钥稳定，
            // 也便于将来做口令预计算防护（盐不保密，明文存储即可）。
            if (string.IsNullOrEmpty(_authSalt))
            {
                _authSalt = Convert.ToBase64String(EasyRDP.Core.Security.AuthCrypto.RandomBytes(16));
                settings.AuthSalt = _authSalt;
                if (!_settingsStore.Save(settings))
                    Logger.Warn("Persist auth salt failed; using in-memory salt for this run");
            }
            // 自启状态以系统实际生效状态为准（Run 值 + StartupApproved 禁用标记）。
            // 启动自愈（修复"卸载/重装后自启悄悄失效"）：
            // settings.json 记录"曾开启自启"（AutoStart=true）而当前失效时——
            //   Run 值丢失（安装包卸载/重装时 uninsdeletevalue 静默删除、或注册表被
            //   清理）→ 按用户既往意图用当前 exe 路径重写注册表；
            //   Run 值存在但被任务管理器/系统启动设置禁用 → 尊重系统开关不强行恢复，
            //   在日志与界面明确提示用户去系统设置里重新启用（区分两种失效场景）。
            bool autoStartInRegistry = AutoStartManager.IsEnabled();
            if (settings.AutoStart && !autoStartInRegistry)
            {
                if (AutoStartManager.IsDisabledByTaskManager())
                {
                    Logger.Warn("Auto start is enabled in settings but disabled by Windows (Task Manager / Startup settings)");
                    AddLog("Auto start is OFF in Windows startup settings — re-enable it there");
                    _autoStart = false;
                }
                else
                {
                    bool healed = AutoStartManager.SetEnabled(true);
                    _autoStart = healed;
                    if (healed)
                    {
                        Logger.Info("Auto start entry was missing (uninstall/update cleaned it) — restored from saved settings");
                        AddLog("Auto start entry restored from saved settings");
                    }
                }
            }
            else
            {
                _autoStart = autoStartInRegistry;
            }

            // 启动 aly 自动更新后台检查（检查 → 下载 → 应用）
            InitializeUpdateClient();
        }

        // ====== 自动更新（aly） ======

        private AlyUpdateClient _alyUpdateClient;
        private System.Threading.CancellationTokenSource _requestDownloadUpdateCts =
            new System.Threading.CancellationTokenSource();
        private System.Threading.CancellationTokenSource _requestApplyUpdateCts =
            new System.Threading.CancellationTokenSource();
        private readonly System.Threading.CancellationTokenSource _updateShutdownCts =
            new System.Threading.CancellationTokenSource();
        private Action<AlyClientStatus, string> _onUpdateStatusChanged;
        private Action<string> _onRequestDownloadUpdate;
        private Action<string> _onRequestApplyUpdate;
        private Action<string> _onUpdateErrorChanged;
        // aly-client.exe 崩溃时错误消息包含整段 Go 堆栈，且每 ~5 秒重试一次：
        // 限频 + 截断，避免日志文件被堆栈刷爆。用 long ticks + Interlocked 保证 x86 下原子。
        private long _lastAlyErrorLoggedTicks;
        // aly-client.exe 连续崩溃计数：达到阈值后停止更新轮询（避免进程崩溃风暴）
        private int _alyConsecutiveErrors;

        /// <summary>当前更新状态（None=无更新）。</summary>
        private AlyClientStatus _alyClientStatus = AlyClientStatus.None;
        public AlyClientStatus AlyClientStatus
        {
            get { return _alyClientStatus; }
            private set
            {
                if (_alyClientStatus == value) return;
                _alyClientStatus = value;
                OnPropertyChanged(nameof(AlyClientStatus));
                IsUpdatePanelVisible = value != AlyClientStatus.None;
                CheckUpdateCommand.RaiseCanExecuteChanged();
            }
        }

        /// <summary>更新状态提示文本（如 "Found new version: v1.0.1"）。</summary>
        private string _alyClientUpdateStr = string.Empty;
        public string AlyClientUpdateStr
        {
            get { return _alyClientUpdateStr; }
            private set
            {
                if (_alyClientUpdateStr == value) return;
                _alyClientUpdateStr = value;
                OnPropertyChanged(nameof(AlyClientUpdateStr));
            }
        }

        /// <summary>是否有更新提示需要展示（status != None）。</summary>
        private bool _isUpdatePanelVisible;
        public bool IsUpdatePanelVisible
        {
            get { return _isUpdatePanelVisible; }
            private set
            {
                if (_isUpdatePanelVisible == value) return;
                _isUpdatePanelVisible = value;
                OnPropertyChanged(nameof(IsUpdatePanelVisible));
            }
        }

        /// <summary>确认下载/应用更新的命令（仅在等待用户确认时可用）。</summary>
        public RelayCommand CheckUpdateCommand { get; private set; }

        /// <summary>
        /// 用户已确认应用更新。此后更新器会向本进程发送 WM_CLOSE 以优雅关闭：
        /// 主窗口收到关闭请求时必须真正退出（停止监听、保存设置、释放托盘），
        /// 而不是隐藏到托盘——否则 2 秒后被更新器强杀，端口无法干净释放。
        /// </summary>
        public bool ExitRequestedByUpdate { get; private set; }

        /// <summary>
        /// 判断本进程此刻是否应配合更新器真正退出（而非隐藏到托盘）。
        /// 两个信号任一成立即应退出：
        /// 1) 用户在程序内确认应用更新（ExitRequestedByUpdate，更新器随后发 WM_CLOSE）；
        /// 2) aly 更新锁处于 apply_update / rollback 且持锁进程存活——更新并非经程序内
        ///    确认路径发起时（旧版自动应用、aly-client-ui 手动应用、发布 CLI），
        ///    ExitRequestedByUpdate 不会置位，WM_CLOSE 被当成普通关闭隐藏到托盘，
        ///    旧实例带着监听 socket 存活 → 新实例绑定端口失败（生产日志实证）。
        ///    更新锁是更新器侧的客观事实信号，覆盖全部发起路径。
        /// </summary>
        public bool ShouldExitForUpdate()
        {
            return ExitRequestedByUpdate || AlyUpdateLockDetector.IsUpdateInProgress();
        }

        /// <summary>
        /// 初始化 aly 更新客户端：启动后台循环（检查 → 下载 → 应用），
        /// 并将 SDK 线程上的状态事件转发到 UI 线程。
        /// </summary>
        private void InitializeUpdateClient()
        {
            _alyUpdateClient = new AlyUpdateClient();
            // 应用更新前让更新器优雅关闭本进程（WM_CLOSE → 窗口真正退出并停止监听），
            // 而不是在替换文件时探测到占用后强杀——强杀会导致新进程启动后端口被
            // TIME_WAIT 连接短暂占用（详见 StartServerInternal 的端口预检说明）。
            try
            {
                _alyUpdateClient.MustCloseProcessNames =
                    System.Diagnostics.Process.GetCurrentProcess().ProcessName;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Set aly must-close process name failed");
            }
            // 事件处理委托保存为字段，便于 CleanupUpdateClient 退订
            _onUpdateStatusChanged = (status, tips) =>
            {
                try
                {
                    _dispatcher.BeginInvoke(() =>
                    {
                        AlyClientStatus = status;
                        AlyClientUpdateStr = tips ?? string.Empty;
                    });
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "aly update status marshal failed");
                }
            };
            _alyUpdateClient.StatusChanged += _onUpdateStatusChanged;

            // 非强制更新：阻塞更新循环，直到用户在 UI 上点击确认（CheckUpdate 取消对应的 CTS）
            _onRequestDownloadUpdate = newVersion =>
            {
                // 捕获令牌（struct）：源 CTS 被取消/替换/Dispose 后仍可安全读取状态
                var confirmationToken = _requestDownloadUpdateCts.Token;
                var shutdownToken = _updateShutdownCts.Token;
                while (!confirmationToken.IsCancellationRequested && !shutdownToken.IsCancellationRequested)
                    System.Threading.Thread.Sleep(500);
            };
            _alyUpdateClient.RequestDownloadUpdate += _onRequestDownloadUpdate;

            _onRequestApplyUpdate = newVersion =>
            {
                var confirmationToken = _requestApplyUpdateCts.Token;
                var shutdownToken = _updateShutdownCts.Token;
                while (!confirmationToken.IsCancellationRequested && !shutdownToken.IsCancellationRequested)
                    System.Threading.Thread.Sleep(500);
            };
            _alyUpdateClient.RequestApplyUpdate += _onRequestApplyUpdate;

            _onUpdateErrorChanged = msg =>
            {
                if (string.IsNullOrEmpty(msg))
                {
                    System.Threading.Interlocked.Exchange(ref _alyConsecutiveErrors, 0);
                    return;
                }
                int current = System.Threading.Interlocked.Increment(ref _alyConsecutiveErrors);
                if (current >= 3)
                {
                    // aly-client.exe 连续崩溃（本机 Go 1.10/386 二进制访问违例）：
                    // 停止更新轮询，避免每 ~5 秒一次的进程崩溃风暴。
                    DisableAlyUpdateClient();
                    return;
                }
                long nowTicks = DateTime.UtcNow.Ticks;
                long lastTicks = System.Threading.Interlocked.Read(ref _lastAlyErrorLoggedTicks);
                if (lastTicks != 0
                    && (nowTicks - lastTicks) / TimeSpan.TicksPerSecond < 60)
                    return;
                if (System.Threading.Interlocked.CompareExchange(
                    ref _lastAlyErrorLoggedTicks, nowTicks, lastTicks) != lastTicks)
                    return;
                string firstLine = msg;
                int nl = msg.IndexOf('\n');
                if (nl > 0)
                    firstLine = msg.Substring(0, nl);
                Logger.Debug("aly updater not available: {0}", firstLine);
            };
            _alyUpdateClient.ErrorStatusChanged += _onUpdateErrorChanged;
        }

        /// <summary>
        /// 线程安全地停用 aly 更新客户端（SDK 后台线程触发，可并发调用）：
        /// 原子取出引用、退订全部事件、解除确认等待、释放资源并隐藏更新状态。
        /// </summary>
        private void DisableAlyUpdateClient()
        {
            AlyUpdateClient client = System.Threading.Interlocked.Exchange(ref _alyUpdateClient, null);
            if (client == null)
                return;
            if (_onUpdateStatusChanged != null) client.StatusChanged -= _onUpdateStatusChanged;
            if (_onRequestDownloadUpdate != null) client.RequestDownloadUpdate -= _onRequestDownloadUpdate;
            if (_onRequestApplyUpdate != null) client.RequestApplyUpdate -= _onRequestApplyUpdate;
            if (_onUpdateErrorChanged != null) client.ErrorStatusChanged -= _onUpdateErrorChanged;
            try { _updateShutdownCts.Cancel(); } catch (ObjectDisposedException) { }
            try { client.Dispose(); }
            catch (Exception ex) { Logger.Warn(ex, "aly update client dispose failed"); }
            System.Threading.Interlocked.Exchange(ref _alyConsecutiveErrors, 0);
            Logger.Warn("aly update disabled after consecutive crashes");
            try
            {
                _dispatcher.BeginInvoke(new Action(() =>
                {
                    AlyClientStatus = AlyClientStatus.None;
                    AlyClientUpdateStr = string.Empty;
                }));
            }
            catch (Exception ex) { Logger.Warn(ex, "aly update status reset failed"); }
        }

        /// <summary>根据当前状态确认"下载"或"应用"更新。</summary>
        private void CheckUpdate()
        {
            if (AlyClientStatus == AlyClientStatus.DiscoveredUpdate)
                ResetUpdateConfirmation(ref _requestDownloadUpdateCts);
            else if (AlyClientStatus == AlyClientStatus.DownloadedUpdate)
            {
                // 用户确认应用更新：置位退出标记，让随后更新器发来的 WM_CLOSE
                // 触发真正的优雅退出（见 ExitRequestedByUpdate 注释）。
                ExitRequestedByUpdate = true;
                ResetUpdateConfirmation(ref _requestApplyUpdateCts);
            }
            else
                Logger.Warn("CheckUpdate called in unexpected state: {0}", AlyClientStatus);
        }

        /// <summary>取消当前确认等待，并为下一个更新周期准备新的确认令牌。</summary>
        private static void ResetUpdateConfirmation(ref System.Threading.CancellationTokenSource cts)
        {
            // 等待循环持有的是 Token（struct），此处可安全 Cancel + Dispose 旧实例
            var old = cts;
            cts = new System.Threading.CancellationTokenSource();
            try { old.Cancel(); } catch (ObjectDisposedException) { }
            try { old.Dispose(); } catch (ObjectDisposedException) { }
        }

        /// <summary>停止自动更新后台检查并释放资源（窗口关闭时调用）。</summary>
        public void CleanupUpdateClient()
        {
            // 先解除可能阻塞的确认等待，再停止后台循环
            try { _updateShutdownCts.Cancel(); } catch (ObjectDisposedException) { }

            var client = _alyUpdateClient;
            _alyUpdateClient = null;
            if (client != null)
            {
                if (_onUpdateStatusChanged != null) client.StatusChanged -= _onUpdateStatusChanged;
                if (_onRequestDownloadUpdate != null) client.RequestDownloadUpdate -= _onRequestDownloadUpdate;
                if (_onRequestApplyUpdate != null) client.RequestApplyUpdate -= _onRequestApplyUpdate;
                if (_onUpdateErrorChanged != null) client.ErrorStatusChanged -= _onUpdateErrorChanged;
                client.Dispose();
            }

            try { _updateShutdownCts.Dispose(); } catch (ObjectDisposedException) { }
            try { _requestDownloadUpdateCts.Dispose(); } catch (ObjectDisposedException) { }
            try { _requestApplyUpdateCts.Dispose(); } catch (ObjectDisposedException) { }
        }

        // ====== Start 逻辑 ======

        /// <summary>启动按钮入口：失败时通过弹框反馈。</summary>
        private void StartServer()
        {
            StartServerInternal(false);
        }

        /// <summary>
        /// 启动服务并开始监听。
        /// </summary>
        /// <param name="silent">
        /// true=自动监听场景：凭据缺失或启动失败只写日志，不弹框（开机自启时不应打断用户）。
        /// </param>
        private void StartServerInternal(bool silent)
        {
            if (_transportHost != null) return;

            if (!int.TryParse(PortText, out int port))
                port = 2000;

            // 端口占用预检 + 等待：
            // - 常规场景（用户手动 Start）：重试 5×2s=10s——覆盖端口释放的毫秒级延迟；
            // - 更新重启场景（本进程由更新器拉起，aly 更新锁仍持锁）：旧实例此刻可能
            //   正在退出流程中（停止监听/释放 socket 需数秒），重试加长到 45×2s=90s，
            //   覆盖旧实例退出全程（旧构建可能隐藏托盘不退出——用户从托盘退出即恢复）。
            bool launchedByUpdater = AlyUpdateLockDetector.IsUpdateInProgress();
            int maxAttempts = launchedByUpdater ? 45 : 5;
            for (int attempt = 1; ; attempt++)
            {
                if (!IsPortInUse(port)) break;
                if (attempt >= maxAttempts)
                {
                    // 枚举同进程名的其他服务端实例：区分"双开旧实例未退出"与"端口被其他程序占用"
                    string otherInstanceHint = FindOtherInstanceHint();
                    string msg = "Port " + port + " is already in use. " +
                        (otherInstanceHint ?? "Another program may be using this port.");
                    Logger.Warn(msg);
                    if (silent) AddLog(msg);
                    else _notifier.ShowError(msg, "Port In Use");
                    return;
                }
                if (attempt == 1 && launchedByUpdater)
                    Logger.Info("Port {0} still in use — waiting for old instance to exit (update restart, up to 90s)", port);
                Thread.Sleep(2000);
                if (!IsRunning) continue;
            }


            // 校验凭据：审批模式无需密码（由操作员确认）；口令模式必须配置非空凭据
            string username = Username ?? "";
            string password = Password ?? "";
            if (_authMode == ServerAuthMode.Password
                && (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)))
            {
                if (silent)
                {
                    Logger.Info("Auto listen skipped: username/password not configured");
                    AddLog("Auto listen skipped: configure USER / PASS first");
                }
                else
                {
                    _notifier.ShowWarning("Username and password must not be empty in password mode.", "Invalid credentials");
                }
                return;
            }

            // 启动即持久化当前设置
            SaveSettings();

            try
            {
                var factory = new WindowsDesktopFactory();
                var capturer = factory.CreateScreenCapturer();
                var inputSim = factory.CreateInputSimulator();
                var cursorCapturer = factory.CreateCursorCapturer();
                var clipboard = factory.CreateClipboardService();

                _captureService = new CaptureService(capturer);
                // 应用已选采集显示器（停机期间的选择、或停机期间插拔显示器后的索引回退，在启动时生效）
                RefreshDisplays();
                _captureService.SetCaptureDisplay(_captureDisplayIndex);
                // 捕获与光标追踪由 TransportHost 在首个客户端会话接入时惰性启动、
                // 最后一个会话断开时停止：无客户端时不截屏，避免资源浪费与本机光标异常。

                _transportAcceptor = new TcpTransportAcceptor();
                _transportAcceptor.OnLog = (msg) => _dispatcher.Invoke(() => AddLog(msg));

                // v5 认证配置：挑战-响应口令模式 / 免密码审批模式。
                // 保留对象引用，运行中修改 USER/PASS/AUTH 立即对后续握手生效。
                _authConfig = new ServerAuthConfig
                {
                    Mode = _authMode,
                    Username = username,
                    Password = password,
                    Salt = DecodeAuthSalt()
                };

                _transportHost = new TransportHost(_captureService, _transportAcceptor, inputSim, cursorCapturer, clipboard, _authConfig);
                _transportHost.ApprovalRequested += OnApprovalRequested;
                _transportHost.FileTransferProgress += OnFileTransferProgress;
                _transportHost.FilesReceived += OnFilesReceived;
                _transportHost.FilesSent += OnFilesSent;
                _transportHost.FileTransferFailed += OnFileTransferFailed;
                // 注入当前帧变化检测模式（运行时 UI 修改会通过属性 setter 同步更新）
                _transportHost.ChangeDetectionMode = _changeDetectionMode;
                _transportHost.SessionAttached += OnSessionAttached;
                _transportHost.SessionDetached += OnSessionDetached;
                _transportHost.Start(port.ToString());
                StartDiscoveryService(port);

                _startTime = DateTime.Now;
                _uptimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _uptimeTimer.Tick += (s, ev) => _dispatcher.Invoke(UpdateUptime);
                _uptimeTimer.Start();

                IsRunning = true;
                StatusText = "Running";
                DisplayPort = port.ToString();
                AddLog("Server started on port " + port + " (user: " + username + ")");
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Start server failed");
                if (silent)
                {
                    AddLog("Auto listen failed: " + ex.Message);
                }
                else
                {
                    _notifier.ShowError("Failed to start: " + ex.Message, "Error");
                }
            }
        }

        /// <summary>启动即监听（窗口 Loaded 后调用一次）。</summary>
        public void TryAutoListen()
        {
            if (!_autoListen || IsRunning) return;
            // 口令模式必须有凭据才能监听；审批模式可无凭据直接监听
            if (_authMode == ServerAuthMode.Password
                && (string.IsNullOrEmpty(Username) || string.IsNullOrEmpty(Password)))
            {
                Logger.Info("Auto listen skipped: username/password not configured");
                AddLog("Auto listen skipped: configure USER / PASS first");
                return;
            }
            StartServerInternal(true);
        }

        /// <summary>把 Base64 盐解码为字节；配置损坏时回退为空（服务端将使用随机盐）。</summary>
        private byte[] DecodeAuthSalt()
        {
            try
            {
                if (!string.IsNullOrEmpty(_authSalt))
                    return Convert.FromBase64String(_authSalt);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Decode auth salt failed; using random salt");
            }
            return new byte[0];
        }

        /// <summary>
        /// 免密码审批：由传输线程同步调用。调度到 UI 线程弹窗，用户选择后返回；
        /// 弹窗内部 60s 倒计时超时按拒绝处理。
        /// </summary>
        private bool OnApprovalRequested(ConnectionApprovalRequest request)
        {
            try
            {
                return (bool)_dispatcher.Invoke(new Func<bool>(() => ShowApprovalDialog(request)));
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Approval dialog failed for session {0}", request != null ? request.SessionId : 0);
                return false;
            }
        }

        /// <summary>显示连接审批窗口（窗口隐藏/最小化时先恢复，保证操作员能看到）。</summary>
        private bool ShowApprovalDialog(ConnectionApprovalRequest request)
        {
            Window owner = Application.Current != null ? Application.Current.MainWindow : null;
            if (owner != null)
            {
                if (!owner.IsVisible) owner.Show();
                if (owner.WindowState == WindowState.Minimized) owner.WindowState = WindowState.Normal;
                owner.Activate();
            }

            var dialog = new EasyRDP.Server.Wpf.Views.ConnectionApprovalWindow(request);
            if (owner != null && !ReferenceEquals(owner, dialog)) dialog.Owner = owner;
            bool approved = dialog.ShowDialog() == true;
            AddLog(approved
                ? "Connection approved: " + (request != null ? request.RemoteEndPoint : "")
                : "Connection denied: " + (request != null ? request.RemoteEndPoint : ""));
            return approved;
        }

        // ====== Stop 逻辑 ======

        private void StopServer()
        {
            _uptimeTimer?.Stop();

            if (_transportHost != null)
            {
                _transportHost.ApprovalRequested -= OnApprovalRequested;
                _transportHost.FileTransferProgress -= OnFileTransferProgress;
                _transportHost.FilesReceived -= OnFilesReceived;
                _transportHost.FilesSent -= OnFilesSent;
                _transportHost.FileTransferFailed -= OnFileTransferFailed;
                _transportHost.Stop();
                _transportHost.SessionAttached -= OnSessionAttached;
                _transportHost.SessionDetached -= OnSessionDetached;
            }
            _transportHost = null;
            StopDiscoveryService();
            _authConfig = null;
            _transportAcceptor?.Dispose();
            _transportAcceptor = null;
            _captureService?.Stop();
            _captureService = null;

            _dispatcher.Invoke(() => Sessions.Clear());
            IsRunning = false;
            StatusText = "Stopped";
            DisplayPort = "—";
            SessionCount = "0";
            Uptime = "—";
            HideFileTransfer();
            AddLog("Server stopped");
        }

        /// <summary>
        /// 探测端口是否真的不可用。分两步，避免把 TIME_WAIT 残留误判为占用：
        /// 1) 向 127.0.0.1:port 发起 TCP 连接——能连上说明有活跃监听者，属于真占用；
        /// 2) 用带 SO_REUSEADDR 的 socket 试绑定——与 TcpTransportAcceptor 的真实
        ///    监听行为一致：旧实例被更新器强杀后残留的 TIME_WAIT 连接（最长 240 秒）
        ///    不会阻塞带此选项的绑定，因此不应报"端口被占用"。
        /// </summary>
        private static bool IsPortInUse(int port)
        {
            // 1) 活跃监听探测：连接成功 = 有进程正在监听该端口
            try
            {
                var client = new System.Net.Sockets.TcpClient();
                try
                {
                    IAsyncResult ar = client.BeginConnect(
                        System.Net.IPAddress.Loopback, port, null, null);
                    if (ar.AsyncWaitHandle.WaitOne(300, false))
                    {
                        try
                        {
                            client.EndConnect(ar);
                            return true; // 连接成功：存在活跃监听者
                        }
                        catch
                        {
                            // 连接被拒绝：无活跃监听者，继续第二步
                        }
                    }
                }
                finally
                {
                    try { client.Close(); } catch { }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Port active probe failed for {0}", port);
            }

            // 2) 带 SO_REUSEADDR 的绑定探测（与真实监听器一致）
            System.Net.Sockets.Socket probe = null;
            try
            {
                probe = new System.Net.Sockets.Socket(
                    System.Net.Sockets.AddressFamily.InterNetwork,
                    System.Net.Sockets.SocketType.Stream,
                    System.Net.Sockets.ProtocolType.Tcp);
                probe.SetSocketOption(
                    System.Net.Sockets.SocketOptionLevel.Socket,
                    System.Net.Sockets.SocketOptionName.ReuseAddress, true);
                probe.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Any, port));
                return false;
            }
            catch (System.Net.Sockets.SocketException)
            {
                return true;
            }
            finally
            {
                try { if (probe != null) probe.Close(); } catch { }
            }
        }

        /// <summary>当前监听端口（解析失败回退 2000）。</summary>
        private int CurrentPort()
        {
            int port;
            return int.TryParse(PortText, out port) && port > 0 && port < 65536 ? port : 2000;
        }

        /// <summary>
        /// 查找同进程名的其他服务端实例（排除本进程）。
        /// 端口占用报错时区分两种场景：
        /// - 找到 → 旧实例/双开实例仍存活（通常隐藏在托盘继续监听），
        ///   提示用户从托盘图标退出后重试；
        /// - 未找到 → 端口被其他程序占用，提示更换端口。
        /// 返回 null 表示无同进程名实例。
        /// </summary>
        private static string FindOtherInstanceHint()
        {
            try
            {
                int currentId = System.Diagnostics.Process.GetCurrentProcess().Id;
                string processName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
                var others = System.Diagnostics.Process.GetProcessesByName(processName);
                try
                {
                    foreach (var p in others)
                    {
                        if (p != null && p.Id != currentId)
                        {
                            return "Another EasyRDP Server instance (PID " + p.Id +
                                ") is still running — exit it from the tray icon and try again.";
                        }
                    }
                }
                finally
                {
                    // 统一在 finally 释放全部进程句柄（Process.Dispose 幂等，
                    // return 路径与正常路径都覆盖，无需在取值时单独释放）
                    foreach (var p in others)
                        if (p != null) p.Dispose();
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Find other server instances failed");
            }
            return null;
        }

        /// <summary>启动局域网发现（幂等；已启动则先停止再重建，保证端口/模式最新）。</summary>
        private void StartDiscoveryService(int tcpPort)
        {
            StopDiscoveryService();
            if (!_allowDiscovery) return;
            if (_transportHost == null) return;

            var host = _transportHost;
            var service = new DiscoveryService(
                () => tcpPort,
                () => (byte)(_authMode == ServerAuthMode.Approve
                    ? EasyRDP.Core.Discovery.DiscoveryConstants.AuthApprove
                    : EasyRDP.Core.Discovery.DiscoveryConstants.AuthPassword),
                () => host.ActiveSessionCount >= host.MaxSessions,
                string.IsNullOrWhiteSpace(_serverName) ? Environment.MachineName : _serverName.Trim());
            service.Start();
            _discoveryService = service;
            AddLog("LAN discovery: UDP " + EasyRDP.Core.Discovery.DiscoveryConstants.UdpPort
                + " (" + Environment.MachineName + ")");
        }

        /// <summary>停止局域网发现。</summary>
        private void StopDiscoveryService()
        {
            var service = _discoveryService;
            _discoveryService = null;
            if (service == null) return;
            try { service.Dispose(); } catch (Exception ex) { Logger.Warn(ex, "Stop discovery failed"); }
        }

        /// <summary>把当前端口/凭据/检测模式/认证设置保存到本地配置文件。</summary>
        public void SaveSettings()
        {
            bool ok = _settingsStore.Save(new ServerSettings
            {
                Port = PortText ?? "2000",
                Username = Username ?? "",
                Password = Password ?? "",
                ChangeDetectionMode = _changeDetectionMode,
                AutoStart = _autoStart,
                AutoListen = _autoListen,
                AuthMode = _authMode,
                AuthSalt = _authSalt,
                AllowDiscovery = _allowDiscovery,
                ServerName = _serverName,
                CaptureDisplay = _captureDisplayIndex
            });
            AddLog(ok ? "Settings saved" : "Settings save failed");
        }

        // ====== 会话事件 ======

        private void OnSessionAttached(uint sessionId, string remote, string codec, string resolution)
        {
            _dispatcher.Invoke(() =>
            {
                Sessions.Add(new SessionItem
                {
                    IdValue = sessionId,
                    Id = sessionId.ToString(),
                    Remote = remote,
                    Codec = codec,
                    Resolution = resolution,
                    Frames = 0
                });
                SessionCount = Sessions.Count.ToString();
                AddLog("Session " + sessionId + " connected — " + codec + " " + resolution);
            });
        }

        private void OnSessionDetached(uint sessionId)
        {
            _dispatcher.Invoke(() =>
            {
                for (int i = Sessions.Count - 1; i >= 0; i--)
                {
                    if (Sessions[i].IdValue == sessionId)
                    {
                        Sessions.RemoveAt(i);
                        break;
                    }
                }
                SessionCount = Sessions.Count.ToString();
                AddLog("Session " + sessionId + " disconnected");
            });
        }

        // ====== 内部辅助 ======

        private void UpdateUptime()
        {
            var elapsed = DateTime.Now - _startTime;
            Uptime = string.Format("{0:D2}:{1:D2}:{2:D2}",
                elapsed.Hours, elapsed.Minutes, elapsed.Seconds);

            // 每秒刷新会话已发送帧数（原实现 Frames 列永远为 0）
            if (_transportHost != null)
            {
                foreach (var item in Sessions)
                {
                    long frames = _transportHost.GetSessionFrames(item.IdValue);
                    if (frames >= 0 && frames != item.Frames)
                        item.Frames = (int)frames;
                }
            }
        }

        /// <summary>踢出指定会话（异步执行，避免 UI 线程等待 Stop 的线程 Join）。</summary>
        public void KickSession(uint sessionId)
        {
            var host = _transportHost;
            if (host == null) return;
            // 显式指定 TaskScheduler.Default，避免捕获调用方当前调度器
            System.Threading.Tasks.Task.Factory.StartNew(() =>
            {
                try
                {
                    host.KickSession(sessionId);
                }
                catch (Exception ex)
                {
                    string message = ex.Message;
                    _dispatcher.Invoke(() => AddLog("Kick failed: " + message));
                }
            }, System.Threading.CancellationToken.None, System.Threading.Tasks.TaskCreationOptions.None,
                System.Threading.Tasks.TaskScheduler.Default);
        }

        /// <summary>选择文件并发送给指定会话（服务端 → 客户端）。</summary>
        private void SendFilesToSession(uint sessionId)
        {
            var host = _transportHost;
            if (host == null) return;

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select files to send to the remote computer",
                Multiselect = true,
                CheckFileExists = true
            };
            if (dialog.ShowDialog() != true || dialog.FileNames.Length == 0) return;

            uint transferId = host.SendFiles(sessionId, dialog.FileNames);
            if (transferId == 0)
            {
                _notifier.ShowError("Failed to start the file transfer.", "Send File");
                AddLog("Send file failed for session " + sessionId);
                return;
            }
            AddLog("Sending " + dialog.FileNames.Length + " file(s) to session " + sessionId);
            ShowFileTransfer("Preparing file transfer…", 0);
        }

        /// <summary>文件传输进度（后台线程回调）：切回 UI 线程更新进度条。</summary>
        private void OnFileTransferProgress(uint sessionId, EasyRDP.Core.Transfer.FileTransferProgress progress)
        {
            if (progress == null) return;
            _dispatcher.Invoke(() =>
            {
                string direction = progress.Outgoing ? "Sending" : "Receiving";
                string text = string.Format("{0} {1} ({2}/{3}) — {4:F1} MB / {5:F1} MB · {6}",
                    direction, progress.FileName, progress.FileIndex + 1, progress.FileCount,
                    progress.BytesTransferred / 1048576.0, progress.TotalBytes / 1048576.0,
                    FormatRate(progress.BytesPerSecond));
                double percent = progress.TotalBytes > 0
                    ? progress.BytesTransferred * 100.0 / progress.TotalBytes
                    : 0;
                ShowFileTransfer(text, percent);
            });
        }

        /// <summary>本端接收文件完成：记录保存路径。</summary>
        private void OnFilesReceived(uint sessionId, string[] paths)
        {
            _dispatcher.Invoke(() =>
            {
                AddLog("Received " + (paths != null ? paths.Length : 0) + " file(s) from session " + sessionId);
                if (paths != null)
                {
                    foreach (string path in paths)
                        AddLog("  saved: " + path);
                }
                HideFileTransfer();
            });
        }

        /// <summary>本端发送文件完成。</summary>
        private void OnFilesSent(uint sessionId)
        {
            _dispatcher.Invoke(() =>
            {
                AddLog("File transfer to session " + sessionId + " completed");
                HideFileTransfer();
            });
        }

        /// <summary>文件传输失败（任一方向）。</summary>
        private void OnFileTransferFailed(uint sessionId, string reason, bool outgoing)
        {
            _dispatcher.Invoke(() =>
            {
                AddLog(string.Format("File transfer {0} failed: {1}",
                    outgoing ? "to" : "from", reason));
                HideFileTransfer();
            });
        }

        private void ShowFileTransfer(string text, double percent)
        {
            FileTransferText = text;
            FileTransferPercent = percent < 0 ? 0 : (percent > 100 ? 100 : percent);
            IsFileTransferVisible = true;
        }

        private void HideFileTransfer()
        {
            IsFileTransferVisible = false;
            FileTransferPercent = 0;
        }

        /// <summary>速率格式化（MB/s 或 KB/s）。</summary>
        private static string FormatRate(double bytesPerSecond)
        {
            if (bytesPerSecond >= 1048576)
                return (bytesPerSecond / 1048576.0).ToString("F1") + " MB/s";
            return (bytesPerSecond / 1024.0).ToString("F0") + " KB/s";
        }

        private void AddLog(string message)
        {
            var entry = string.Format("[{0:HH:mm:ss}] {1}", DateTime.Now, message);
            LogEntries.Insert(0, entry);
            if (LogEntries.Count > 200)
                LogEntries.RemoveAt(LogEntries.Count - 1);
            LogLine = message;
        }

        // ====== INotifyPropertyChanged ======

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
