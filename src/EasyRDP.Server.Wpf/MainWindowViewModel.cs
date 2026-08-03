#nullable disable
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using AlyClient.CSharpSDK;
using EasyDesk.Core;
using EasyDesk.Windows;
using EasyRDP.Shared;
using NLog;

namespace EasyRDP.Server.Wpf
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
        private TcpTransportServer _transportServer;
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

        // 设置持久化：%AppData%\EasyRDP\server\settings.json
        private readonly ServerSettingsStore _settingsStore = new ServerSettingsStore();

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

        /// <summary>服务端认证用户名。启动时读取，运行中修改不影响已建立会话。</summary>
        public string Username
        {
            get { return _username; }
            set { _username = value ?? ""; OnPropertyChanged(nameof(Username)); }
        }

        /// <summary>服务端认证密码。明文存储，v1 足够；后续可改为加密。</summary>
        public string Password
        {
            get { return _password; }
            set { _password = value ?? ""; OnPropertyChanged(nameof(Password)); }
        }

        // ====== 命令 ======

        public RelayCommand StartCommand { get; }
        public RelayCommand StopCommand { get; }
        public RelayCommand SaveSettingsCommand { get; }

        public MainWindowViewModel(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            StartCommand = new RelayCommand(StartServer, () => !IsRunning);
            StopCommand = new RelayCommand(StopServer, () => IsRunning);
            SaveSettingsCommand = new RelayCommand(SaveSettings);
            CheckUpdateCommand = new RelayCommand(CheckUpdate,
                () => AlyClientStatus == AlyClientStatus.DiscoveredUpdate
                    || AlyClientStatus == AlyClientStatus.DownloadedUpdate);

            // 启动时恢复上次保存的服务端设置
            ServerSettings settings = _settingsStore.Load();
            if (!string.IsNullOrEmpty(settings.Port))
                PortText = settings.Port;
            Username = settings.Username ?? "";
            Password = settings.Password ?? "";

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
                if (!string.IsNullOrEmpty(msg))
                    Logger.Warn("aly update error: {0}", msg);
            };
            _alyUpdateClient.ErrorStatusChanged += _onUpdateErrorChanged;
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

        private void StartServer()
        {
            if (_transportHost != null) return;

            if (!int.TryParse(PortText, out int port))
                port = 2000;

            // 校验凭据非空
            string username = Username ?? "";
            string password = Password ?? "";
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                MessageBox.Show("Username and password must not be empty.", "Invalid credentials",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
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

                _transportServer = new TcpTransportServer();
                _transportServer.OnLog = (msg) => _dispatcher.Invoke(() => AddLog(msg));

                // 构造凭据表：UI 配置的用户名/密码
                var credentials = new System.Collections.Generic.Dictionary<string, string>
                {
                    { username, password }
                };

                _transportHost = new TransportHost(_captureService, _transportServer, inputSim, cursorCapturer, clipboard, credentials);
                _transportHost.SessionAttached += OnSessionAttached;
                _transportHost.SessionDetached += OnSessionDetached;
                _transportHost.Start(port);

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
                MessageBox.Show("Failed to start: " + ex.Message, "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ====== Stop 逻辑 ======

        private void StopServer()
        {
            _uptimeTimer?.Stop();

            if (_transportHost != null)
            {
                _transportHost.Stop();
                _transportHost.SessionAttached -= OnSessionAttached;
                _transportHost.SessionDetached -= OnSessionDetached;
            }
            _transportHost = null;
            _transportServer?.Dispose();
            _transportServer = null;
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

        /// <summary>把当前端口/用户名/密码保存到本地配置文件。</summary>
        public void SaveSettings()
        {
            bool ok = _settingsStore.Save(new ServerSettings
            {
                Port = PortText ?? "2000",
                Username = Username ?? "",
                Password = Password ?? ""
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
