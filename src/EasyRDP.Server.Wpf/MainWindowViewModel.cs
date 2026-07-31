#nullable disable
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using EasyDesk.Core;
using EasyDesk.Windows;

namespace EasyRDP.Server.Wpf
{
    /// <summary>
    /// 服务端主窗口 ViewModel。管理服务生命周期、会话列表、日志。
    /// </summary>
    public class MainWindowViewModel : INotifyPropertyChanged
    {
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
        // 默认凭据 admin/admin — UI 可修改
        private string _username = "admin";
        private string _password = "admin";

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

        public MainWindowViewModel(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            StartCommand = new RelayCommand(StartServer, () => !IsRunning);
            StopCommand = new RelayCommand(StopServer, () => IsRunning);
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

            try
            {
                var factory = new WindowsDesktopFactory();
                var capturer = factory.CreateScreenCapturer();
                var inputSim = factory.CreateInputSimulator();
                var cursorCapturer = factory.CreateCursorCapturer();
                var clipboard = factory.CreateClipboardService();

                _captureService = new CaptureService(capturer);
                _captureService.Start();

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
