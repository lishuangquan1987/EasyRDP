using System;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using AlyClient.CSharpSDK;
using EasyRDP.Client.Common;
using EasyRDP.Core.Logging;
using EasyRDP.Core.Protocol;
using EasyRDP.Client.Wpf.Services;

namespace EasyRDP.Client.Wpf.ViewModels
{
    public class MainViewModel : ViewModelBase
    {
        private readonly ConnectionManager _conn = new ConnectionManager();
        private readonly FrameBuffer _frameBuf = new FrameBuffer();
        private readonly InputEncoder _inputEnc = new InputEncoder();
        private readonly ClipboardSyncEngine _clipSync = new ClipboardSyncEngine();
        private readonly KeepAliveEngine _keepAlive = new KeepAliveEngine();
        private readonly WpfRenderEngine _render = new WpfRenderEngine();
        private readonly WpfInputCapturer _inputCap;
        private readonly WpfClipboardProvider _clipProv = new WpfClipboardProvider();
        private CancellationTokenSource _clipCts;
        private volatile bool _running;
        private bool _firstFrameArrived;

        private string _host = "127.0.0.1";
        private int _port = 8750;
        private string _token = "easyrdp-demo";
        private bool _isConnected;
        private bool _isConnecting;
        private string _status = "未连接";
        private double _fps;
        private int _prevFrameCount;

        public string Host { get { return _host; } set { Set(ref _host, value, "Host"); } }
        public int Port { get { return _port; } set { Set(ref _port, value, "Port"); } }
        public string Token { get { return _token; } set { Set(ref _token, value, "Token"); } }
        public bool IsConnected { get { return _isConnected; } set { Set(ref _isConnected, value, "IsConnected"); OnPropertyChanged("IsDisconnected"); } }
        public bool IsDisconnected { get { return !_isConnected; } }
        public bool IsConnecting { get { return _isConnecting; } set { Set(ref _isConnecting, value, "IsConnecting"); } }
        public string Status { get { return _status; } set { Set(ref _status, value, "Status"); } }
        public double Fps { get { return _fps; } set { Set(ref _fps, value, "Fps"); } }
        public ImageSource FrameSource { get { return _render.Source; } }
        public WpfInputCapturer InputCapturer { get { return _inputCap; } }
        public SequenceTracker SeqTracker { get { return _conn.SeqTracker; } }

        public RelayCommand ConnectCommand { get; private set; }
        public RelayCommand DisconnectCommand { get; private set; }
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
            _inputCap = new WpfInputCapturer(_inputEnc);
            ConnectCommand = new RelayCommand(Connect, () => !IsConnected && !IsConnecting);
            DisconnectCommand = new RelayCommand(Disconnect, () => IsConnected);
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
                    LogHelper.Error("[AlyUpdate] " + msg);
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

        private void Connect()
        {
            IsConnecting = true;
            Status = "连接中...";

            _conn.Connected += () => Dispatch(OnConnected);
            _conn.ConnectionFailed += r => Dispatch(() => { Status = r; IsConnecting = false; });
            _conn.Disconnected += r => Dispatch(() => OnDisconnected(r));
            _conn.MessageReceived += OnMessage;

            new Thread(() =>
            {
                bool ok = _conn.Connect(_host, _port, 5000, _token);
                if (!ok) Dispatch(() => { LogHelper.Warn(string.Format("连接失败: {0}:{1}", _host, _port)); Status = "连接失败"; IsConnecting = false; });
            }) { IsBackground = true }.Start();
        }

        private void OnConnected()
        {
            _inputCap.UpdateScreenSize(_conn.RemoteScreenWidth, _conn.RemoteScreenHeight);
            _render.Resize(_conn.RemoteScreenWidth, _conn.RemoteScreenHeight);
            OnPropertyChanged("FrameSource");

            _keepAlive.Start(() => _conn.SendMessage(MessageType.KeepAlive, new KeepAliveMessage()));
            _keepAlive.Timeout += () => Dispatch(() =>
            {
                LogHelper.Warn("KeepAlive 超时，主动断开连接");
                DisconnectCommand.Execute(null);
            });

            _clipCts = new CancellationTokenSource();
            var ct = _clipCts.Token;
            new Thread(() => ClipboardLoop(ct)) { IsBackground = true, Name = "EasyRDP-Clip" }.Start();

            _running = true;
            _prevFrameCount = 0;
            _firstFrameArrived = false;
            new Thread(FpsLoop) { IsBackground = true, Name = "EasyRDP-Fps" }.Start();

            IsConnected = true;
            IsConnecting = false;
            Status = string.Format("已连接 {0}x{1}", _conn.RemoteScreenWidth, _conn.RemoteScreenHeight);
            LogHelper.Info(string.Format("已连接到 {0}:{1} ({2}x{3})", _host, _port, _conn.RemoteScreenWidth, _conn.RemoteScreenHeight));
        }

        private void OnDisconnected(string reason)
        {
            _keepAlive.Stop();
            if (_clipCts != null) { _clipCts.Cancel(); _clipCts = null; }
            _frameBuf.Reset();
            _running = false;
            IsConnected = false;
            Status = reason;
            LogHelper.Info(string.Format("连接断开: {0}", reason));
        }

        private void Disconnect()
        {
            _conn.Disconnect("用户断开");
        }

        private void OnMessage(Message msg)
        {
            if (msg.Body == null) return;
            switch (msg.Header.Type)
            {
                case MessageType.ScreenFrame:
                    _frameBuf.ProcessFrame((ScreenFrameMessage)msg.Body);
                    byte[] px; int w, h;
                    if (_frameBuf.TryGetFrame(out px, out w, out h))
                    {
                        if (!_firstFrameArrived)
                        {
                            _firstFrameArrived = true;
                            LogHelper.Info(string.Format("首帧已渲染 ({0}x{1})", w, h));
                        }
                        Dispatch(() => { _render.Render(px, w, h); OnPropertyChanged("FrameSource"); });
                    }
                    break;
                case MessageType.ClipboardData:
                    var text = _clipSync.OnRemoteClipboard((ClipboardDataMessage)msg.Body);
                    if (text != null) Dispatch(() => _clipProv.SetText(text));
                    break;
                case MessageType.KeepAliveAck:
                    _keepAlive.OnAckReceived();
                    break;
                case MessageType.CursorUpdate:
                    HandleCursorUpdate((CursorUpdateMessage)msg.Body);
                    break;
                case MessageType.CopyRect:
                    HandleCopyRect((CopyRectMessage)msg.Body);
                    break;
            }
        }

        public void SendInput(byte[] data)
        {
            if (IsConnected) _conn.Transport.Send(data);
        }

        private void HandleCursorUpdate(CursorUpdateMessage msg)
        {
            if (!msg.Visible)
            {
                _render.SetCursor(false, 0, 0, null, 0, 0, 0, 0);
                return;
            }

            _render.SetCursor(true, msg.X, msg.Y,
                msg.ImageData != null && msg.ImageData.Length > 0 ? msg.ImageData : null,
                msg.Width, msg.Height, msg.HotspotX, msg.HotspotY);
        }

        private void HandleCopyRect(CopyRectMessage msg)
        {
            if (msg.Entries == null || msg.Entries.Length == 0) return;
            foreach (var entry in msg.Entries)
            {
                _frameBuf.CopyRegion(entry.SrcX, entry.SrcY, entry.DstX, entry.DstY, entry.Width, entry.Height);
            }
            // 触发重新渲染
            byte[] px; int w, h;
            if (_frameBuf.TryGetFrame(out px, out w, out h))
            {
                Dispatch(() => { _render.Render(px, w, h); OnPropertyChanged("FrameSource"); });
            }
        }

        private void ClipboardLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    string text = null;
                    Application.Current.Dispatcher.Invoke(new Action(() => text = _clipProv.GetText()));
                    byte[] data = _clipSync.TryEncodeLocalChange(text, _conn.SeqTracker.Next());
                    if (data != null) _conn.Transport.Send(data);
                }
                catch { }
                try { Thread.Sleep(300); } catch { break; }
            }
        }

        private void FpsLoop()
        {
            while (_running)
            {
                Thread.Sleep(2000);
                int cur = _frameBuf.FrameCount;
                Fps = (cur - _prevFrameCount) / 2.0;
                _prevFrameCount = cur;
            }
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

        public void Cleanup()
        {
            _running = false;
            _keepAlive.Stop();
            if (_clipCts != null) { _clipCts.Cancel(); _clipCts = null; }
            _conn.Dispose();
            if (_alyUpdateClient != null)
            {
                _alyUpdateClient.Cancel();
                _alyUpdateClient = null;
            }
        }

        private static void Dispatch(Action a)
        {
            var disp = Application.Current.Dispatcher;
            disp.BeginInvoke(a, System.Windows.Threading.DispatcherPriority.Normal);
        }
    }
}
