using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Windows;
using AlyClient.CSharpSDK;
using EasyRDP.Core.Logging;
using EasyRDP.Core.Protocol;
using EasyRDP.Core.Transport;
using EasyRDP.Server.Wpf.Models;
using EasyRDP.Server.Wpf.Services;

namespace EasyRDP.Server.Wpf.ViewModels
{
    public class MainViewModel : ViewModelBase
    {
        private readonly ServerEngine _server = new ServerEngine();
        private readonly CaptureEngine _capture = new CaptureEngine();
        private readonly ClipboardSyncService _clipboard = new ClipboardSyncService();

        private ServerConfigModel _config = new ServerConfigModel();
        private bool _isRunning;
        private string _statusText = "未启动";
        private ClientSessionModel _selectedClient;
        private readonly ObservableCollection<ClientSessionModel> _clients = new ObservableCollection<ClientSessionModel>();
        private readonly ObservableCollection<LogEntry> _log = new ObservableCollection<LogEntry>();
        private const int MaxLog = 500;

        public ServerConfigModel Config { get { return _config; } set { Set(ref _config, value, "Config"); } }
        public bool IsRunning { get { return _isRunning; } set { Set(ref _isRunning, value, "IsRunning"); OnPropertyChanged("IsStopped"); } }
        public bool IsStopped { get { return !_isRunning; } }
        public string StatusText { get { return _statusText; } set { Set(ref _statusText, value, "StatusText"); } }
        public ClientSessionModel SelectedClient { get { return _selectedClient; } set { Set(ref _selectedClient, value, "SelectedClient"); } }
        public ObservableCollection<ClientSessionModel> Clients { get { return _clients; } }
        public ObservableCollection<LogEntry> LogEntries { get { return _log; } }

        public RelayCommand StartCommand { get; private set; }
        public RelayCommand StopCommand { get; private set; }
        public RelayCommand DisconnectClientCommand { get; private set; }
        public RelayCommand ClearLogCommand { get; private set; }
        public RelayCommand ExitCommand { get; private set; }
        public RelayCommand AboutCommand { get; private set; }
        public RelayCommand CheckUpdateCommand { get; private set; }

        private AlyUpdateClient _alyUpdateClient;
        private CancellationTokenSource _requestDownloadUpdateCts = new CancellationTokenSource();
        private CancellationTokenSource _requestApplyUpdateCts = new CancellationTokenSource();

        private AlyClientStatus _alyClientStatus;
        public AlyClientStatus AlyClientStatus
        {
            get { return _alyClientStatus; }
            set
            {
                if (_alyClientStatus != value)
                {
                    _alyClientStatus = value;
                    OnPropertyChanged("AlyClientStatus");
                    if (CheckUpdateCommand != null)
                        CheckUpdateCommand.RaiseCanExecuteChanged();
                }
            }
        }

        private string _alyClientUpdateStr = string.Empty;
        public string AlyClientUpdateStr
        {
            get { return _alyClientUpdateStr; }
            set { Set(ref _alyClientUpdateStr, value, "AlyClientUpdateStr"); }
        }

        public MainViewModel()
        {
            StartCommand = new RelayCommand(Start, () => !IsRunning);
            StopCommand = new RelayCommand(Stop, () => IsRunning);
            DisconnectClientCommand = new RelayCommand(() => { if (_selectedClient != null) { _server.Disconnect(_selectedClient.SessionId); _selectedClient = null; } }, () => _selectedClient != null);
            ClearLogCommand = new RelayCommand(() => _log.Clear());
            ExitCommand = new RelayCommand(() => Application.Current.Shutdown());
            AboutCommand = new RelayCommand(() => MessageBox.Show("EasyRDP Server WPF v1.0", "关于"));
            CheckUpdateCommand = new RelayCommand(CheckUpdate, () =>
                this.AlyClientStatus == AlyClientStatus.DiscoveredUpdate ||
                this.AlyClientStatus == AlyClientStatus.DownloadedUpdate);

            // 自动更新
            _alyUpdateClient = new AlyUpdateClient();
            _alyUpdateClient.StatusChanged += (status, tips) =>
            {
                LogHelper.Info(string.Format("[AlyUpdate] 状态变更: {0} — {1}", status, tips));
                Dispatch(() =>
                {
                    this.AlyClientStatus = status;
                    this.AlyClientUpdateStr = tips;
                });
            };
            _alyUpdateClient.ErrorStatusChanged += (msg) =>
            {
                if (msg != null)
                {
                    LogHelper.Error("[AlyUpdate] " + msg);
                    Log(LogLevel.Error, "[AlyUpdate] " + msg);
                }
            };
            _alyUpdateClient.RequestDownloadUpdate += (newVersion) =>
            {
                while (true)
                {
                    Thread.Sleep(1000);
                    if (_requestDownloadUpdateCts.IsCancellationRequested) break;
                }
            };
            _alyUpdateClient.RequestApplyUpdate += (newVersion) =>
            {
                while (true)
                {
                    Thread.Sleep(1000);
                    if (_requestApplyUpdateCts.IsCancellationRequested) break;
                }
            };
        }

        private void WireServices()
        {
            _capture.SendTo = (sid, data) => _server.SendTo(sid, data);
            _capture.OnLog = msg => Log(LogLevel.Debug, msg);

            _clipboard.BroadcastToAll = data =>
            {
                // 快照客户端列表，防止后台线程枚举时 UI 线程同时修改集合
                ClientSessionModel[] snapshot;
                lock (_clients) { snapshot = new ClientSessionModel[_clients.Count]; _clients.CopyTo(snapshot, 0); }
                foreach (var c in snapshot)
                    if (c.IsAuthenticated) _server.SendTo(c.SessionId, data);
            };
            _clipboard.OnLog = msg => Log(LogLevel.Warning, msg);

            _server.ClientConnected += (s, e) => Dispatch(() => OnConnected(e));
            _server.ClientDisconnected += (s, e) => Dispatch(() => OnDisconnected(e));
            _server.MessageReceived += (s, e) => OnMessage(e);
        }

        private void Start()
        {
            int port = Config.Port;
            if (port < 1 || port > 65535) { StatusText = "无效端口"; return; }

            var fps = Math.Max(1, Math.Min(Config.FrameRate, 60));
            _capture.FrameDelayMs = 1000 / fps;
            _capture.CompressType = Config.CompressType == "Zlib" ? CompressType.Zlib : CompressType.None;
            WireServices();

            _server.Start(port);
            _clipboard.Start();
            IsRunning = true;
            StatusText = string.Format("运行中 - 端口 {0}", port);
            Log(LogLevel.Info, string.Format("已启动 (端口:{0} 压缩:{1} FPS:{2})", port, Config.CompressType, fps));
            LogHelper.Info(string.Format("服务已启动 (端口:{0} 压缩:{1} FPS:{2})", port, Config.CompressType, fps));
        }

        private void Stop()
        {
            _clipboard.Stop();
            _capture.StopAll();
            _server.Stop();
            Dispatch(() => { _clients.Clear(); _log.Clear(); });
            IsRunning = false;
            StatusText = "已停止";
            LogHelper.Info("服务已停止");
        }

        private void CheckUpdate()
        {
            if (this.AlyClientStatus == AlyClientStatus.DiscoveredUpdate)
            {
                LogHelper.Info("[AlyUpdate] 用户确认下载更新");
                _requestDownloadUpdateCts.Cancel();
            }
            else if (this.AlyClientStatus == AlyClientStatus.DownloadedUpdate)
            {
                LogHelper.Info("[AlyUpdate] 用户确认应用更新");
                _requestApplyUpdateCts.Cancel();
            }
        }

        // ── events ────────────────────────────────────────

        private void OnConnected(ConnectionEventArgs e)
        {
            _clients.Add(new ClientSessionModel { SessionId = e.SessionId, RemoteEndPoint = e.RemoteEndPoint, ConnectedAt = DateTime.Now });
            Log(LogLevel.Info, string.Format("客户端 {0} 已连接 ({1})", e.SessionId, e.RemoteEndPoint));
            LogHelper.Info(string.Format("客户端已连接: ID={0} IP={1}", e.SessionId, e.RemoteEndPoint));
        }

        private void OnDisconnected(ConnectionEventArgs e)
        {
            _capture.StopForClient(e.SessionId);
            for (int i = 0; i < _clients.Count; i++)
                if (_clients[i].SessionId == e.SessionId) { _clients.RemoveAt(i); break; }
            Log(LogLevel.Info, string.Format("客户端 {0} 已断开", e.SessionId));
            LogHelper.Info(string.Format("客户端已断开: ID={0}", e.SessionId));
        }

        private void OnMessage(MessageReceivedEventArgs e)
        {
            var m = e.Message;
            if (m == null || m.Body == null) return;
            uint sid = e.SessionId;
            switch (m.Header.Type)
            {
                case MessageType.HandshakeReq: OnHandshake(sid, (HandshakeReqMessage)m.Body); break;
                case MessageType.InputEvent: _capture.HandleInput((InputEventMessage)m.Body); break;
                case MessageType.ClipboardData: _clipboard.OnRemoteClipboard((ClipboardDataMessage)m.Body); break;
                case MessageType.KeepAlive: _server.SendTo(sid, MessageCodec.Encode(MessageType.KeepAliveAck, 1, new KeepAliveAckMessage())); break;
            }
        }

        private void OnHandshake(uint sid, HandshakeReqMessage req)
        {
            ClientSessionModel client = null;
            for (int i = 0; i < _clients.Count; i++)
                if (_clients[i].SessionId == sid) { client = _clients[i]; break; }
            if (client == null) return;

            if (req.AuthToken != Config.AuthToken)
            {
                _server.SendTo(sid, MessageCodec.Encode(MessageType.HandshakeRes, 1,
                    new HandshakeResMessage { Result = HandshakeResult.AuthFailed }));
                _server.Disconnect(sid);
                Log(LogLevel.Warning, string.Format("客户端 {0} 认证失败", sid));
                LogHelper.Warn(string.Format("客户端认证失败: ID={0}", sid));
                return;
            }

            Dispatch(() => client.IsAuthenticated = true);
            var screen = _capture.GetPrimaryScreen();
            _server.SendTo(sid, MessageCodec.Encode(MessageType.HandshakeRes, 1, new HandshakeResMessage
            {
                Result = HandshakeResult.Success, SessionId = sid,
                ScreenWidth = (ushort)screen.Width, ScreenHeight = (ushort)screen.Height,
                CompressType = _capture.CompressType
            }));
            _capture.StartForClient(sid);
            Log(LogLevel.Info, string.Format("客户端 {0} 认证通过 ({1}x{2})", sid, screen.Width, screen.Height));
        }

        // ── helpers ───────────────────────────────────────

        /// <summary>线程安全的日志（自动封送到 UI 线程）。</summary>
        private void Log(LogLevel level, string msg)
        {
            Dispatch(() =>
            {
                if (_log.Count >= MaxLog) _log.RemoveAt(0);
                _log.Add(new LogEntry { Timestamp = DateTime.Now, Level = level, Message = msg });
            });
        }

        private static void Dispatch(Action action)
        {
            var app = Application.Current;
            if (app != null) app.Dispatcher.Invoke(action);
            else action();
        }
    }
}
