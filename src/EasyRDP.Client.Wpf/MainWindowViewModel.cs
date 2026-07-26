using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EasyRDP.Core.Protocol;
using EasyRDP.Core.Rendering;
using EasyRDP.Core.Transport;

namespace EasyRDP.Client.Wpf
{
    /// <summary>
    /// 客户端主窗口 ViewModel。管理连接、渲染测试、输入转发的全部业务逻辑。
    /// 遵循 MVVM：ViewModel 可引用 View，但 View 只通过绑定和命令与 ViewModel 交互。
    /// </summary>
    public class MainWindowViewModel : INotifyPropertyChanged
    {
        /// <summary>默认服务端端口。</summary>
        private const int DefaultPort = 2000;
        /// <summary>连接超时（毫秒）。</summary>
        private const int ConnectTimeoutMs = 5000;
        /// <summary>握手超时（毫秒）。</summary>
        private const int HandshakeTimeoutMs = 10000;
        /// <summary>测试帧宽度。</summary>
        private const int TestFrameWidth = 640;
        /// <summary>测试帧高度。</summary>
        private const int TestFrameHeight = 480;
        /// <summary>测试矩形大小。</summary>
        private const int TestBoxSize = 80;

        private readonly Dispatcher _dispatcher;

        // 业务对象
        private WpfRenderTarget? _renderTarget;
        private FrameBuffer? _frameBuffer;
        private ClientStreamSession? _streamSession;
        private ClientInputSession? _inputSession;
        private TcpTransportClient? _transport;
        private volatile bool _running;
        private int _testFrameSeq;

        // 属性字段
        private string _host = "127.0.0.1";
        private string _port = "2000";
        private string _statusText = "Disconnected";
        private bool _isConnectEnabled = true;
        private bool _isStartEnabled = true;
        private bool _isStopEnabled;
        private bool _isConnected;
        private string _frameSize = "—";
        private int _frameRate;
        private string _codecName = "—";
        private WriteableBitmap? _renderBitmap;
        private DateTime _lastFrameTime;
        private int _frameCountThisSecond;

        public MainWindowViewModel()
        {
            _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            ConnectCommand = new RelayCommand(async () => await ConnectAsync(), () => !_running);
            StartTestCommand = new RelayCommand(StartRenderTest, () => !_running);
            StopCommand = new RelayCommand(Stop, () => _running);
            FullscreenCommand = new RelayCommand(ToggleFullscreen);
        }

        // ====== 属性 ======

        public string Host
        {
            get { return _host; }
            set { _host = value; OnPropertyChanged(nameof(Host)); }
        }

        public string Port
        {
            get { return _port; }
            set { _port = value; OnPropertyChanged(nameof(Port)); }
        }

        public string StatusText
        {
            get { return _statusText; }
            set { _statusText = value; OnPropertyChanged(nameof(StatusText)); }
        }

        public bool IsConnectEnabled
        {
            get { return _isConnectEnabled; }
            set { _isConnectEnabled = value; OnPropertyChanged(nameof(IsConnectEnabled)); }
        }

        public bool IsStartEnabled
        {
            get { return _isStartEnabled; }
            set { _isStartEnabled = value; OnPropertyChanged(nameof(IsStartEnabled)); }
        }

        public bool IsStopEnabled
        {
            get { return _isStopEnabled; }
            set { _isStopEnabled = value; OnPropertyChanged(nameof(IsStopEnabled)); }
        }

        public WriteableBitmap? RenderBitmap
        {
            get { return _renderBitmap; }
            set { _renderBitmap = value; OnPropertyChanged(nameof(RenderBitmap)); }
        }

        public bool IsConnected
        {
            get { return _isConnected; }
            set { _isConnected = value; OnPropertyChanged(nameof(IsConnected)); }
        }

        public string FrameSize
        {
            get { return _frameSize; }
            set { _frameSize = value; OnPropertyChanged(nameof(FrameSize)); }
        }

        public int FrameRate
        {
            get { return _frameRate; }
            set { _frameRate = value; OnPropertyChanged(nameof(FrameRate)); OnPropertyChanged(nameof(FrameRateText)); }
        }

        public string CodecName
        {
            get { return _codecName; }
            set { _codecName = value; OnPropertyChanged(nameof(CodecName)); }
        }

        public string FrameRateText
        {
            get { return _frameRate > 0 ? string.Format("{0} FPS", _frameRate) : "—"; }
        }

        public RelayCommand ConnectCommand { get; }
        public RelayCommand StartTestCommand { get; }
        public RelayCommand StopCommand { get; }
        public RelayCommand FullscreenCommand { get; }

        // ====== Connect 逻辑 ======

        private async Task ConnectAsync()
        {
            if (_running) return;
            SetBusy(true, "Connecting...");

            string host = _host.Trim();
            if (!int.TryParse(_port, out int port))
                port = DefaultPort;

            _transport = new TcpTransportClient();
            _transport.OnLog = (msg) => _dispatcher.Invoke(() => StatusText = msg);

            bool connected = await Task.Run(() => _transport.Connect(host, port, ConnectTimeoutMs));
            if (!connected)
            {
                SetBusy(false, "Connection failed");
                return;
            }

            // 发送握手
            var handshakeReq = new HandshakeReq
            {
                Version = Constants.ProtocolVersion,
                Capabilities = DecoderFactory.GetAvailableCodecs(),
                Username = "admin",
                Password = ""
            };
            byte[] reqPayload = handshakeReq.Pack();
            MessageReassembler.FragAndSend(0, (byte)MessageType.HandshakeReq, reqPayload,
                (sid, data) => _transport.Send(data), 0);

            // 等待握手响应
            var handshakeReassembler = new MessageReassembler();
            MessageReceivedEventArgs? handshakeResponse = null;
            var waitHandle = new ManualResetEventSlim(false);

            handshakeReassembler.MessageReceived += (s, args) =>
            {
                if (args.MessageType == (byte)MessageType.HandshakeRes)
                {
                    handshakeResponse = args;
                    waitHandle.Set();
                }
            };

            _transport.DataReceived += (s, args) => handshakeReassembler.OnFragment(args);
            bool gotResponse = await Task.Run(() => waitHandle.Wait(HandshakeTimeoutMs));
            _transport.DataReceived -= (s, args) => handshakeReassembler.OnFragment(args);

            if (!gotResponse || handshakeResponse == null)
            {
                SetBusy(false, "Handshake timeout");
                _transport.Disconnect();
                return;
            }

            var handshakeRes = HandshakeRes.Unpack(handshakeResponse.Data);
            if (handshakeRes.Result != HandshakeResult.Success)
            {
                SetBusy(false, "Handshake failed: " + handshakeRes.Result);
                _transport.Disconnect();
                return;
            }

            // 初始化渲染管线
            _renderTarget = new WpfRenderTarget();
            _frameBuffer = new FrameBuffer();

            _streamSession = new ClientStreamSession();
            _streamSession.RenderTarget = _renderTarget;
            _streamSession.InitPipeline(handshakeRes.Codec, handshakeRes.ScreenWidth, handshakeRes.ScreenHeight);

            // 必须在 InitPipeline（调用 Resize）之后才赋值 Bitmap
            RenderBitmap = _renderTarget.Bitmap;

            _inputSession = new ClientInputSession();
            _inputSession.Start(_transport, handshakeRes.ScreenWidth, handshakeRes.ScreenHeight);

            _streamSession.Start(_transport);
            _running = true;
            IsConnected = true;
            CodecName = handshakeRes.Codec.ToString();
            FrameSize = string.Format("{0}x{1}", handshakeRes.ScreenWidth, handshakeRes.ScreenHeight);
            _lastFrameTime = DateTime.UtcNow;

            StatusText = string.Format("Connected — {0}x{1}",
                handshakeRes.ScreenWidth, handshakeRes.ScreenHeight);
            UpdateCommandState();
        }

        // ====== Render Test 逻辑 ======

        private void StartRenderTest()
        {
            if (_running) return;
            SetBusy(true, "Running render test...");

            _renderTarget = new WpfRenderTarget();
            _frameBuffer = new FrameBuffer();
            _testFrameSeq = 0;
            _running = true;

            _renderTarget.Resize(TestFrameWidth, TestFrameHeight);
            RenderBitmap = _renderTarget.Bitmap;

            Task.Run(() => RenderTestLoop());
        }

        private void RenderTestLoop()
        {
            int w = TestFrameWidth, h = TestFrameHeight;
            int frameSize = w * h * 4;
            int boxX = 0, boxY = 0;
            int boxDx = 3, boxDy = 2;

            while (_running)
            {
                byte[]? writeSlot = _frameBuffer!.BorrowWriteBuffer(frameSize);
                if (writeSlot == null) { Thread.Sleep(1); continue; }

                // 渐变背景
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int off = (y * w + x) * 4;
                        writeSlot[off] = (byte)(x % 256);
                        writeSlot[off + 1] = (byte)(y % 256);
                        writeSlot[off + 2] = 128;
                        writeSlot[off + 3] = 255;
                    }
                }

                // 移动矩形
                boxX += boxDx; boxY += boxDy;
                if (boxX < 0 || boxX + TestBoxSize > w) boxDx = -boxDx;
                if (boxY < 0 || boxY + TestBoxSize > h) boxDy = -boxDy;

                byte r = (byte)((_testFrameSeq * 5) % 256);
                byte g = (byte)((_testFrameSeq * 7 + 85) % 256);
                byte b = (byte)((_testFrameSeq * 11 + 170) % 256);

                for (int y = Math.Max(0, boxY); y < Math.Min(h, boxY + TestBoxSize); y++)
                {
                    for (int x = Math.Max(0, boxX); x < Math.Min(w, boxX + TestBoxSize); x++)
                    {
                        int off = (y * w + x) * 4;
                        writeSlot[off] = b;
                        writeSlot[off + 1] = g;
                        writeSlot[off + 2] = r;
                        writeSlot[off + 3] = 255;
                    }
                }

                _frameBuffer.CommitFrame(w, h);

                if (_frameBuffer.TryBorrowReadFrame(out ReadFrameRef frame))
                {
                    try
                    {
                        _dispatcher.Invoke(() => _renderTarget?.RenderFrame(frame.Pixels, frame.Width, frame.Height));
                    }
                    finally { _frameBuffer.ReleaseReadFrame(); }
                }

                _testFrameSeq++;
                Thread.Sleep(16);
            }

            _dispatcher.Invoke(() => _frameBuffer?.Reset());
        }

        // ====== Stop 逻辑 ======

        private void Stop()
        {
            _running = false;
            _streamSession?.Stop();
            _transport?.Disconnect();
            _transport = null;
            _streamSession = null;
            _inputSession = null;

            // 清理渲染资源
            if (_renderTarget != null)
            {
                try { _renderTarget.Dispose(); } catch { }
                _renderTarget = null;
            }
            if (_frameBuffer != null)
            {
                _frameBuffer.Reset();
                _frameBuffer = null;
            }
            RenderBitmap = null;

            IsConnected = false;
            FrameSize = "—";
            FrameRate = 0;
            CodecName = "—";
            SetBusy(false, "Disconnected");
        }

        // ====== 输入事件处理（由 View 代码后置调用） ======

        public void HandleMouseMove(double imageX, double imageY, double imageW, double imageH)
        {
            if (_inputSession == null || !_running) return;
            int sx, sy;
            _inputSession.MapCoordinates(imageX, imageY, imageW, imageH, out sx, out sy);
            var msg = new InputEventMessage { Type = InputEventType.MouseMove, X = sx, Y = sy };
            _inputSession.SendInput(msg);
        }

        public void HandleMouseDown(System.Windows.Input.MouseButton changedButton)
        {
            if (_inputSession == null || !_running) return;
            int btn = changedButton == System.Windows.Input.MouseButton.Left ? 1
                : changedButton == System.Windows.Input.MouseButton.Right ? 2 : 4;
            var msg = new InputEventMessage { Type = InputEventType.MouseDown, KeyCode = btn };
            _inputSession.SendInput(msg);
        }

        public void HandleMouseUp(System.Windows.Input.MouseButton changedButton)
        {
            if (_inputSession == null || !_running) return;
            int btn = changedButton == System.Windows.Input.MouseButton.Left ? 1
                : changedButton == System.Windows.Input.MouseButton.Right ? 2 : 4;
            var msg = new InputEventMessage { Type = InputEventType.MouseUp, KeyCode = btn };
            _inputSession.SendInput(msg);
        }

        public void HandleMouseWheel(int delta)
        {
            if (_inputSession == null || !_running) return;
            var msg = new InputEventMessage { Type = InputEventType.MouseWheel, WheelDelta = delta };
            _inputSession.SendInput(msg);
        }

        /// <summary>处理键盘按下事件（由 View 的 KeyDown 事件调用）。</summary>
        public void HandleKeyDown(System.Windows.Input.Key key)
        {
            if (_inputSession == null || !_running) return;
            int virtualKey = KeyToVirtualKey(key);
            if (virtualKey == 0) return;
            var msg = new InputEventMessage { Type = InputEventType.KeyDown, KeyCode = virtualKey };
            _inputSession.SendInput(msg);
        }

        /// <summary>处理键盘释放事件（由 View 的 KeyUp 事件调用）。</summary>
        public void HandleKeyUp(System.Windows.Input.Key key)
        {
            if (_inputSession == null || !_running) return;
            int virtualKey = KeyToVirtualKey(key);
            if (virtualKey == 0) return;
            var msg = new InputEventMessage { Type = InputEventType.KeyUp, KeyCode = virtualKey };
            _inputSession.SendInput(msg);
        }

        /// <summary>将 WPF Key 枚举映射为 Windows 虚拟键码。</summary>
        private static int KeyToVirtualKey(System.Windows.Input.Key key)
        {
            if (key >= System.Windows.Input.Key.A && key <= System.Windows.Input.Key.Z)
                return (int)key - (int)System.Windows.Input.Key.A + 0x41; // VK_A..VK_Z
            if (key >= System.Windows.Input.Key.D0 && key <= System.Windows.Input.Key.D9)
                return (int)key - (int)System.Windows.Input.Key.D0 + 0x30; // VK_0..VK_9
            if (key == System.Windows.Input.Key.LeftShift) return 0xA0;
            if (key == System.Windows.Input.Key.RightShift) return 0xA1;
            if (key == System.Windows.Input.Key.LeftCtrl) return 0xA2;
            if (key == System.Windows.Input.Key.RightCtrl) return 0xA3;
            if (key == System.Windows.Input.Key.LeftAlt) return 0xA4;
            if (key == System.Windows.Input.Key.RightAlt) return 0xA5;
            if (key == System.Windows.Input.Key.Enter) return 0x0D;
            if (key == System.Windows.Input.Key.Escape) return 0x1B;
            if (key == System.Windows.Input.Key.Tab) return 0x09;
            if (key == System.Windows.Input.Key.Back) return 0x08;
            if (key == System.Windows.Input.Key.Space) return 0x20;
            if (key == System.Windows.Input.Key.Delete) return 0x2E;
            if (key == System.Windows.Input.Key.Up) return 0x26;
            if (key == System.Windows.Input.Key.Down) return 0x28;
            if (key == System.Windows.Input.Key.Left) return 0x25;
            if (key == System.Windows.Input.Key.Right) return 0x27;
            return 0; // 不支持
        }

        // ====== 内部辅助 ======

        private void SetBusy(bool busy, string status)
        {
            IsConnectEnabled = !busy;
            IsStartEnabled = !busy;
            IsStopEnabled = busy;
            StatusText = status;
            UpdateCommandState();
        }

        private void UpdateCommandState()
        {
            ConnectCommand.RaiseCanExecuteChanged();
            StartTestCommand.RaiseCanExecuteChanged();
            StopCommand.RaiseCanExecuteChanged();
        }

        public void ToggleFullscreen()
        {
            var window = Application.Current?.MainWindow;
            if (window == null) return;
            if (window.WindowStyle == WindowStyle.None)
            {
                window.WindowStyle = WindowStyle.SingleBorderWindow;
                window.WindowState = WindowState.Normal;
            }
            else
            {
                window.WindowStyle = WindowStyle.None;
                window.WindowState = WindowState.Maximized;
            }
        }

        // ====== INotifyPropertyChanged ======

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
