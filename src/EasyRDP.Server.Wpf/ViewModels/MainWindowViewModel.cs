#nullable disable
using System;
using System.Collections.ObjectModel;
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
        // 连接认证：默认口令模式（与历史版本一致）；审批模式免密码
        private ServerAuthMode _authMode = ServerAuthMode.Password;
        // PBKDF2 盐（Base64）：首次运行生成后固定，保证相同口令派生出相同密钥
        private string _authSalt = "";
        // 运行中的认证配置对象（TransportHost 持有同一引用，UI 改动即时生效）
        private ServerAuthConfig _authConfig;

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

        // ====== 命令 ======

        public RelayCommand StartCommand { get; }
        public RelayCommand StopCommand { get; }
        public RelayCommand SaveSettingsCommand { get; }

        /// <summary>踢出指定会话（参数为 SessionItem.IdValue）。</summary>
        public RelayCommand<uint> KickCommand { get; }

        public MainWindowViewModel(Dispatcher dispatcher, IUserNotifier notifier = null)
        {
            _dispatcher = dispatcher;
            _notifier = notifier ?? new WpfUserNotifier();
            StartCommand = new RelayCommand(StartServer, () => !IsRunning);
            StopCommand = new RelayCommand(StopServer, () => IsRunning);
            SaveSettingsCommand = new RelayCommand(SaveSettings);
            KickCommand = new RelayCommand<uint>(KickSession);
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
            // 首次运行生成并立即落盘 PBKDF2 盐：盐固定后同一口令派生密钥稳定，
            // 也便于将来做口令预计算防护（盐不保密，明文存储即可）。
            if (string.IsNullOrEmpty(_authSalt))
            {
                _authSalt = Convert.ToBase64String(EasyRDP.Core.Security.AuthCrypto.RandomBytes(16));
                settings.AuthSalt = _authSalt;
                if (!_settingsStore.Save(settings))
                    Logger.Warn("Persist auth salt failed; using in-memory salt for this run");
            }
            // 自启状态以注册表为准：用户可能在任务管理器中已禁用启动项
            _autoStart = AutoStartManager.IsEnabled();

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
        /// 初始化 aly 更新客户端：启动后台循环（检查 → 下载 → 应用），
        /// 并将 SDK 线程上的状态事件转发到 UI 线程。
        /// </summary>
        private void InitializeUpdateClient()
        {
            _alyUpdateClient = new AlyUpdateClient();
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
                ResetUpdateConfirmation(ref _requestApplyUpdateCts);
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
                // 注入当前帧变化检测模式（运行时 UI 修改会通过属性 setter 同步更新）
                _transportHost.ChangeDetectionMode = _changeDetectionMode;
                _transportHost.SessionAttached += OnSessionAttached;
                _transportHost.SessionDetached += OnSessionDetached;
                _transportHost.Start(port.ToString());

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
                _transportHost.Stop();
                _transportHost.SessionAttached -= OnSessionAttached;
                _transportHost.SessionDetached -= OnSessionDetached;
            }
            _transportHost = null;
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
            AddLog("Server stopped");
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
                AuthSalt = _authSalt
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
