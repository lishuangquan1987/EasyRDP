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
        // 光标状态变化标志：CursorUpdate 到达后置位，渲染循环据此决定是否重绘光标
        private volatile bool _cursorDirty;
        // 鼠标移动限频：移动事件可达 100+Hz，逐包发送浪费带宽且让服务端输入队列拥塞。
        // 策略：8ms 内只保留最新位置，由 OnRendering 补发；按下/释放前强制 flush 保证位置正确。
        private const int MoveThrottleMs = 8;
        private int _pendingMoveX, _pendingMoveY;
        private volatile bool _hasPendingMove;
        private int _lastMoveSendMs;

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
            _cursorDirty = false;
            new Thread(FpsLoop) { IsBackground = true, Name = "EasyRDP-Fps" }.Start();

            // 渲染由 UI 线程的 vsync 回调驱动，每帧拉取最新帧，避免 dispatcher 队列堆积
            CompositionTarget.Rendering += OnRendering;

            IsConnected = true;
            IsConnecting = false;
            Status = string.Format("已连接 {0}x{1}", _conn.RemoteScreenWidth, _conn.RemoteScreenHeight);
            LogHelper.Info(string.Format("已连接到 {0}:{1} ({2}x{3})", _host, _port, _conn.RemoteScreenWidth, _conn.RemoteScreenHeight));
        }

        private void OnDisconnected(string reason)
        {
            CompositionTarget.Rendering -= OnRendering;
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
                    // 仅入帧缓冲，渲染由 CompositionTarget.Rendering 拉取，避免 dispatcher 队列堆积
                    _frameBuf.ProcessFrame((ScreenFrameMessage)msg.Body);
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
                case MessageType.VideoFrame:
                    HandleVideoFrame((VideoFrameMessage)msg.Body);
                    break;
            }
        }

        public void SendInput(byte[] data)
        {
            if (IsConnected) _conn.Transport.Send(data);
        }

        /// <summary>
        /// 处理本地鼠标移动事件：记录最新位置并限频发送。
        /// 仅移动事件限频；按下/释放/滚轮/键盘仍立即发送以保证响应。
        /// </summary>
        public void OnLocalMouseMove(Point position, UIElement el)
        {
            int x, y;
            _inputCap.MapToScreen(position, el, out x, out y);
            _pendingMoveX = x;
            _pendingMoveY = y;
            _hasPendingMove = true;
            FlushPendingMove(false);
        }

        /// <summary>
        /// 发送待发的鼠标移动。
        /// </summary>
        /// <param name="force">true 表示无视限频立即发送（按下/释放前调用，保证位置正确）</param>
        public void FlushPendingMove(bool force)
        {
            if (!_hasPendingMove || !IsConnected)
                return;

            int now = Environment.TickCount;
            if (force || now - _lastMoveSendMs >= MoveThrottleMs)
            {
                _lastMoveSendMs = now;
                _hasPendingMove = false;
                byte[] data = _inputCap.EncodeMouseMove(_pendingMoveX, _pendingMoveY, _conn.SeqTracker.Next());
                SendInput(data);
            }
        }

        private void HandleCursorUpdate(CursorUpdateMessage msg)
        {
            if (!msg.Visible)
            {
                _render.SetCursor(false, 0, 0, null, 0, 0, 0, 0);
            }
            else
            {
                _render.SetCursor(true, msg.X, msg.Y,
                    msg.ImageData != null && msg.ImageData.Length > 0 ? msg.ImageData : null,
                    msg.Width, msg.Height, msg.HotspotX, msg.HotspotY);
            }
            // 标记光标变化，由渲染循环重绘（含恢复旧光标位置残影）
            _cursorDirty = true;
        }

        private void HandleCopyRect(CopyRectMessage msg)
        {
            if (msg.Entries == null || msg.Entries.Length == 0) return;
            foreach (var entry in msg.Entries)
            {
                _frameBuf.CopyRegion(entry.SrcX, entry.SrcY, entry.DstX, entry.DstY, entry.Width, entry.Height);
            }
            // CopyRegion 已置 IsDirty=true，渲染循环会自动拉取
        }

        private void HandleVideoFrame(VideoFrameMessage msg)
        {
            LogHelper.Warn("收到 VideoFrame 消息，当前未处理（H.264 编解码尚未实现）");
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

        /// <summary>
        /// 渲染回调（由 UI 线程 vsync 触发，约 60Hz）。
        /// 每帧检查是否有新屏幕帧或光标变化，有则拉取最新帧并渲染。
        /// 若 UI 仍在渲染上一帧则自然跳过（下一 vsync 再试），形成背压。
        /// </summary>
        private void OnRendering(object sender, EventArgs e)
        {
            if (!_running)
                return;

            // 补发限频积压的鼠标移动，保证操作跟手
            FlushPendingMove(false);

            bool hasScreen = _frameBuf.IsDirty;
            bool cursorMoved = _cursorDirty;
            _cursorDirty = false;

            // 无新屏幕帧且光标未动 → 跳过，避免空转消耗 CPU
            if (!hasScreen && !cursorMoved)
                return;

            byte[] px;
            int w, h;
            ScreenRect[] dirtyRects;
            if (!_frameBuf.TryGetFrame(out px, out w, out h, out dirtyRects))
                return;

            if (!_firstFrameArrived)
            {
                _firstFrameArrived = true;
                LogHelper.Info(string.Format("首帧已渲染 ({0}x{1})", w, h));
            }

            // 有明确屏幕脏区 → 按区局部更新；
            // 仅有光标移动（无屏幕帧）→ 全屏刷新以恢复旧光标位置残影并绘制新位置
            ScreenRect[] rectsToDraw = (hasScreen && dirtyRects != null && dirtyRects.Length > 0)
                ? dirtyRects : null;

            _render.Render(px, w, h, rectsToDraw);
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
            CompositionTarget.Rendering -= OnRendering;
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
