using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AlyClient.CSharpSDK;
using EasyRDP.Core.Protocol;
using EasyRDP.Core.Rendering;
using EasyRDP.Core.Transport;
using EasyRDP.Shared;
using NLog;

namespace EasyRDP.Client.Wpf
{
    /// <summary>
    /// 客户端主窗口 ViewModel。管理连接、渲染测试、输入转发的全部业务逻辑。
    /// 遵循 MVVM：ViewModel 可引用 View，但 View 只通过绑定和命令与 ViewModel 交互。
    /// </summary>
    public class MainWindowViewModel : INotifyPropertyChanged
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

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
        private volatile bool _disconnecting; // 防止 Disconnect → Disconnected 事件 → Stop 重入循环
        private DispatcherTimer? _fpsTimer;
        private int _testFrameSeq;

        // 属性字段
        private string _host = "";
        private string _port = "2000";
        // 默认不预设凭据：避免弱口令（admin/admin）在局域网暴露；服务端启动时要求显式配置
        private string _username = "";
        private string _password = "";
        private string _statusText = "Disconnected";
        private bool _isConnectEnabled = true;
        private bool _isStartEnabled = true;
        private bool _isStopEnabled;
        private bool _isConnected;
        private string _frameSize = "—";
        private int _frameRate;
        private string _codecName = "—";
        private WriteableBitmap? _renderBitmap;
        // 远程屏幕尺寸（握手时记录）：用于把远程光标坐标映射到本地显示区
        private int _remoteScreenWidth;
        private int _remoteScreenHeight;
        // 剪贴板轮询：检测本地剪贴板变化，发送到服务端
        private DispatcherTimer? _clipboardTimer;
        private string _lastClipboardText = "";
        // 上次文件剪贴板签名（拼接路径），用于检测变化 + 防回环
        private string _lastClipboardFilesSig = "";
        // 上次图片剪贴板签名（CF_DIB 字节数 + 前 32 字节哈希），用于检测变化 + 防回环
        private string _lastClipboardImageSig = "";
        // 客户端文件/图片传输 ID 自增（每次剪贴板同步递增）
        private int _fileTransferIdSeq;
        // 图片块大小（64KB）：与服务端一致
        private const int ImageChunkSize = 64 * 1024;

        // 文件剪贴板下载进度：用于 UI 进度条显示
        private bool _isClipboardTransferring;
        private double _clipboardProgressValue;
        private string _clipboardProgressText = "";

        // 心跳定时器：客户端每 10 秒主动发一次 Keepalive，避免服务端 _lastActivity 超时（45s）断开。
        // 必须由客户端主动发，因为服务端 _lastActivity 只在收到客户端应用层消息时更新；
        // 客户端最小化时无输入事件，若不发心跳，服务端会在 45s 后判定超时主动断开。
        // 用 System.Threading.Timer（线程池）而非 DispatcherTimer：UI 线程卡住或最小化时仍能可靠触发。
        private Timer? _heartbeatTimer;

        // ====== 多服务器配置保存 ======
        private readonly ConnectionProfileStore _profileStore;
        private ServerProfile? _selectedProfile;
        private string _profileName = "";

        public MainWindowViewModel()
        {
            _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            ConnectCommand = new RelayCommand(async () => await ConnectAsync(), () => !_running);
            StartTestCommand = new RelayCommand(StartRenderTest, () => !_running);
            StopCommand = new RelayCommand(Stop, () => _running);
            FullscreenCommand = new RelayCommand(ToggleFullscreen);
            LockCommand = new RelayCommand(SendLockKey);
            AltTabCommand = new RelayCommand(SendAltTab);
            ResetKeysCommand = new RelayCommand(ReleaseModifierKeys);
            SaveProfileCommand = new RelayCommand(SaveProfile);
            DeleteProfileCommand = new RelayCommand(DeleteProfile, () => SelectedProfile != null);
            CheckUpdateCommand = new RelayCommand(CheckUpdate,
                () => AlyClientStatus == AlyClientStatus.DiscoveredUpdate
                    || AlyClientStatus == AlyClientStatus.DownloadedUpdate);

            // 启动时恢复已保存的多服务器配置
            _profileStore = new ConnectionProfileStore();
            System.Collections.Generic.List<ServerProfile> saved = _profileStore.Load(out string lastProfileName);
            foreach (var p in saved)
                Profiles.Add(p);
            if (Profiles.Count > 0)
            {
                var last = FindProfile(lastProfileName);
                SelectedProfile = last ?? Profiles[0];
            }

            // 启动 aly 自动更新后台检查（检查 → 下载 → 应用）
            InitializeUpdateClient();
        }

        // ====== 自动更新（aly） ======

        private AlyUpdateClient? _alyUpdateClient;
        private CancellationTokenSource _requestDownloadUpdateCts = new CancellationTokenSource();
        private CancellationTokenSource _requestApplyUpdateCts = new CancellationTokenSource();
        private readonly CancellationTokenSource _updateShutdownCts = new CancellationTokenSource();
        private Action<AlyClientStatus, string>? _onUpdateStatusChanged;
        private Action<string>? _onRequestDownloadUpdate;
        private Action<string>? _onRequestApplyUpdate;
        private Action<string>? _onUpdateErrorChanged;
        // aly-client.exe 崩溃时错误消息包含整段 Go 堆栈，且每 ~5 秒重试一次：
        // 限频 + 截断，避免日志文件被堆栈刷爆。用 long ticks + Interlocked 保证原子。
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
                    Thread.Sleep(500);
            };
            _alyUpdateClient.RequestDownloadUpdate += _onRequestDownloadUpdate;

            _onRequestApplyUpdate = newVersion =>
            {
                var confirmationToken = _requestApplyUpdateCts.Token;
                var shutdownToken = _updateShutdownCts.Token;
                while (!confirmationToken.IsCancellationRequested && !shutdownToken.IsCancellationRequested)
                    Thread.Sleep(500);
            };
            _alyUpdateClient.RequestApplyUpdate += _onRequestApplyUpdate;

            _onUpdateErrorChanged = msg =>
            {
                if (string.IsNullOrEmpty(msg))
                {
                    Interlocked.Exchange(ref _alyConsecutiveErrors, 0);
                    return;
                }
                int current = Interlocked.Increment(ref _alyConsecutiveErrors);
                if (current >= 3)
                {
                    // aly-client.exe 连续崩溃（本机 Go 1.10/386 二进制访问违例）：
                    // 停止更新轮询，避免每 ~5 秒一次的进程崩溃风暴。
                    DisableAlyUpdateClient();
                    return;
                }
                long nowTicks = DateTime.UtcNow.Ticks;
                long lastTicks = Interlocked.Read(ref _lastAlyErrorLoggedTicks);
                if (lastTicks != 0
                    && (nowTicks - lastTicks) / TimeSpan.TicksPerSecond < 60)
                    return;
                if (Interlocked.CompareExchange(
                    ref _lastAlyErrorLoggedTicks, nowTicks, lastTicks) != lastTicks)
                    return;
                string firstLine = msg;
                int nl = msg.IndexOf('\n');
                if (nl > 0)
                    firstLine = msg.Substring(0, nl);
                Logger.Warn("aly update error: {0}", firstLine);
            };
            _alyUpdateClient.ErrorStatusChanged += _onUpdateErrorChanged;
        }

        /// <summary>
        /// 线程安全地停用 aly 更新客户端（SDK 后台线程触发，可并发调用）：
        /// 原子取出引用、退订全部事件、解除确认等待、释放资源并隐藏更新状态。
        /// </summary>
        private void DisableAlyUpdateClient()
        {
            AlyUpdateClient? client = Interlocked.Exchange(ref _alyUpdateClient, null);
            if (client == null)
                return;
            if (_onUpdateStatusChanged != null) client.StatusChanged -= _onUpdateStatusChanged;
            if (_onRequestDownloadUpdate != null) client.RequestDownloadUpdate -= _onRequestDownloadUpdate;
            if (_onRequestApplyUpdate != null) client.RequestApplyUpdate -= _onRequestApplyUpdate;
            if (_onUpdateErrorChanged != null) client.ErrorStatusChanged -= _onUpdateErrorChanged;
            try { _updateShutdownCts.Cancel(); } catch (ObjectDisposedException) { }
            try { client.Dispose(); }
            catch (Exception ex) { Logger.Warn(ex, "aly update client dispose failed"); }
            Interlocked.Exchange(ref _alyConsecutiveErrors, 0);
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
        private static void ResetUpdateConfirmation(ref CancellationTokenSource cts)
        {
            // 等待循环持有的是 Token（struct），此处可安全 Cancel + Dispose 旧实例
            var old = cts;
            cts = new CancellationTokenSource();
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

        /// <summary>已保存的服务器配置列表。</summary>
        public ObservableCollection<ServerProfile> Profiles { get; } = new ObservableCollection<ServerProfile>();

        /// <summary>当前选中的服务器配置（选择后自动填充连接字段）。</summary>
        public ServerProfile? SelectedProfile
        {
            get { return _selectedProfile; }
            set
            {
                if (_selectedProfile == value) return;
                _selectedProfile = value;
                OnPropertyChanged(nameof(SelectedProfile));
                if (value != null)
                    ApplyProfile(value);
                DeleteProfileCommand.RaiseCanExecuteChanged();
                PersistProfiles(false);
            }
        }

        /// <summary>配置名称（可编辑下拉框的文本，用于新建/重命名）。</summary>
        public string ProfileName
        {
            get { return _profileName; }
            set { _profileName = value ?? ""; OnPropertyChanged(nameof(ProfileName)); }
        }

        public RelayCommand SaveProfileCommand { get; }
        public RelayCommand DeleteProfileCommand { get; }

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

        /// <summary>登录用户名（UI 可编辑，默认 admin）。</summary>
        public string Username
        {
            get { return _username; }
            set { _username = value; OnPropertyChanged(nameof(Username)); }
        }

        /// <summary>登录密码（UI 可编辑，默认 admin）。</summary>
        public string Password
        {
            get { return _password; }
            set { _password = value; OnPropertyChanged(nameof(Password)); }
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

        /// <summary>远程光标更新事件（接收线程触发，订阅者需 marshal 到 UI 线程）。
        /// 光标形状数据是 Windows AND/XOR 掩码格式（来自 EasyDesk WindowsCursorCapturer）。</summary>
        public event Action<CursorInfo>? RemoteCursorChanged;

        /// <summary>远程屏幕宽度（握手时记录）。</summary>
        public int RemoteScreenWidth
        {
            get { return _remoteScreenWidth; }
        }

        /// <summary>远程屏幕高度（握手时记录）。</summary>
        public int RemoteScreenHeight
        {
            get { return _remoteScreenHeight; }
        }

        /// <summary>文件剪贴板是否正在传输，控制进度条可见性。</summary>
        public bool IsClipboardTransferring
        {
            get { return _isClipboardTransferring; }
            set { _isClipboardTransferring = value; OnPropertyChanged(nameof(IsClipboardTransferring)); }
        }

        /// <summary>文件剪贴板下载进度值（0-100）。</summary>
        public double ClipboardProgressValue
        {
            get { return _clipboardProgressValue; }
            set { _clipboardProgressValue = value; OnPropertyChanged(nameof(ClipboardProgressValue)); }
        }

        /// <summary>文件剪贴板下载进度文本，如 "传输中: 45% (2.7GB / 6GB)"。</summary>
        public string ClipboardProgressText
        {
            get { return _clipboardProgressText; }
            set { _clipboardProgressText = value; OnPropertyChanged(nameof(ClipboardProgressText)); }
        }

        public RelayCommand ConnectCommand { get; }
        public RelayCommand StartTestCommand { get; }
        public RelayCommand StopCommand { get; }
        public RelayCommand FullscreenCommand { get; }
        public RelayCommand LockCommand { get; }
        public RelayCommand AltTabCommand { get; }
        public RelayCommand ResetKeysCommand { get; }

        // ====== 多服务器配置管理 ======

        /// <summary>把选中配置的字段填充到连接输入框。</summary>
        private void ApplyProfile(ServerProfile p)
        {
            Host = p.Host ?? "";
            Port = p.Port ?? "";
            Username = p.Username ?? "";
            Password = p.Password ?? "";
            ProfileName = p.Name ?? "";
        }

        /// <summary>保存当前连接字段为一个配置（同名替换）。</summary>
        private void SaveProfile()
        {
            string name = (ProfileName ?? "").Trim();
            if (name.Length == 0)
            {
                // 未命名时用 host:port 生成默认名
                name = (Host ?? "").Trim();
                if (name.Length == 0)
                {
                    StatusText = "Host is empty";
                    return;
                }
                name = name + ":" + (string.IsNullOrEmpty(Port) ? DefaultPort.ToString() : Port);
            }

            var profile = new ServerProfile
            {
                Name = name,
                Host = Host ?? "",
                Port = Port ?? "",
                Username = Username ?? "",
                Password = Password ?? ""
            };

            var existing = FindProfile(name);
            if (existing != null)
            {
                int idx = Profiles.IndexOf(existing);
                Profiles[idx] = profile;
            }
            else
            {
                Profiles.Add(profile);
            }

            SelectedProfile = profile;
            StatusText = "Profile saved: " + name;
            if (!PersistProfiles(true))
                StatusText = "Failed to save profiles";
            Logger.Info("Profile saved: {0}", name);
        }

        /// <summary>删除当前选中的配置。</summary>
        private void DeleteProfile()
        {
            var selected = SelectedProfile;
            if (selected == null) return;
            int idx = Profiles.IndexOf(selected);
            string name = selected.Name;
            Profiles.Remove(selected);

            if (Profiles.Count > 0)
                SelectedProfile = Profiles[Math.Min(idx, Profiles.Count - 1)];
            else
            {
                SelectedProfile = null;
                ProfileName = "";
            }
            StatusText = "Profile deleted: " + name;
            if (!PersistProfiles(true))
                StatusText = "Failed to save profiles";
            Logger.Info("Profile deleted: {0}", name);
        }

        /// <summary>按名称查找配置（忽略大小写）。</summary>
        private ServerProfile? FindProfile(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var p in Profiles)
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                    return p;
            }
            return null;
        }

        /// <summary>持久化配置列表与最后选择的配置名。</summary>
        private bool PersistProfiles(bool notify)
        {
            if (_profileStore == null) return true;
            var list = new System.Collections.Generic.List<ServerProfile>(Profiles.Count);
            foreach (var p in Profiles)
                list.Add(p.Clone());
            bool ok = _profileStore.Save(list, SelectedProfile != null ? SelectedProfile.Name : "");
            if (!ok && notify)
                StatusText = "Failed to save profiles";
            return ok;
        }

        // ====== Connect 逻辑 ======

        private async Task ConnectAsync()
        {
            if (_running) return;
            _disconnecting = false;
            SetBusy(true, "Connecting...");

            string host = _host.Trim();
            if (!int.TryParse(_port, out int port))
                port = DefaultPort;
            if (string.IsNullOrEmpty(host))
            {
                SetBusy(false, "Host is empty");
                return;
            }

            _transport = new TcpTransportClient();
            // 异步更新状态栏：OnLog 可能在接收线程触发，同步 Invoke 会阻塞接收线程
            // （TCP 接收缓冲可能被填满反压服务端），且 UI 繁忙时造成额外等待。
            _transport.OnLog = (msg) => _dispatcher.BeginInvoke(() => StatusText = msg);

            Logger.Info("Connecting to {0}:{1}...", host, port);
            bool connected = await Task.Run(() => _transport.Connect(host, port, ConnectTimeoutMs));
            if (!connected)
            {
                Logger.Warn("Connection to {0}:{1} failed", host, port);
                SetBusy(false, "Connection failed");
                return;
            }
            Logger.Info("TCP connected to {0}:{1}, sending handshake", host, port);

            // 先订阅 DataReceived，再发送握手 — 避免竞态条件导致 HandshakeRes 丢失
            var handshakeReassembler = new MessageReassembler();
            MessageReceivedEventArgs? handshakeResponse = null;
            var waitHandle = new ManualResetEventSlim(false);
            EventHandler<MessageReceivedEventArgs> onHandshakeMsg = (s, args) =>
            {
                if (args.MessageType == (byte)MessageType.HandshakeRes)
                {
                    handshakeResponse = args;
                    waitHandle.Set();
                }
            };
            handshakeReassembler.MessageReceived += onHandshakeMsg;

            EventHandler<FragmentReceivedEventArgs> onHandshakeData = (s, args) => handshakeReassembler.OnFragment(args);
            _transport.DataReceived += onHandshakeData;

            // 提前创建流会话并订阅数据事件：服务端在 HandshakeRes 之后会立即发送视频帧，
            // 若等握手完成后再订阅，首个关键帧（seq=0）会丢失，解码器只能等下一个 IDR，
            // 连接后最长约 1 秒黑屏。BeginReceive 会把管线就绪前的消息缓冲起来。
            _streamSession = new ClientStreamSession();
            _streamSession.BeginReceive(_transport);
            // 订阅不可恢复错误：解码器缺失/连续解码失败时提示并断开，避免黑屏无反馈
            _streamSession.FatalError += (s, args) =>
            {
                if (_disconnecting) return;
                string message = args != null ? args.Message : "Unknown stream error";
                Logger.Error("Stream FatalError: {0}", message);
                // BeginInvoke：同步 Invoke 会让接收线程阻塞等 UI，UI 若正在 Stop/Join
                // 渲染线程则互相等待（死锁），异步调度即可避免。
                _dispatcher.BeginInvoke(() =>
                {
                    if (_disconnecting) return;
                    SetBusy(false, "Stream error: " + message);
                    Stop();
                });
            };

            // 订阅就绪后发送握手请求
            var handshakeReq = new HandshakeReq
            {
                Version = Constants.ProtocolVersion,
                Capabilities = DecoderFactory.GetAvailableCodecs(),
                Username = _username ?? "",
                Password = _password ?? ""
            };
            byte[] reqPayload = handshakeReq.Pack();
            MessageReassembler.FragAndSend(0, (byte)MessageType.HandshakeReq, reqPayload,
                (sid, data) => _transport.Send(data), 0);
            Logger.Debug("HandshakeReq sent, waiting for response...");
            bool gotResponse = await Task.Run(() => waitHandle.Wait(HandshakeTimeoutMs));
            _transport.DataReceived -= onHandshakeData;
            handshakeReassembler.MessageReceived -= onHandshakeMsg;

            if (!gotResponse || handshakeResponse == null)
            {
                Logger.Warn("Handshake timeout after {0}ms", HandshakeTimeoutMs);
                SetBusy(false, "Handshake timeout");
                _streamSession?.Stop();
                _transport.Disconnect();
                return;
            }

            var handshakeRes = HandshakeRes.Unpack(handshakeResponse.Data);
            Logger.Info("Handshake response: result={0} codec={1} resolution={2}x{3}",
                handshakeRes.Result, handshakeRes.Codec, handshakeRes.ScreenWidth, handshakeRes.ScreenHeight);
            if (handshakeRes.Result != HandshakeResult.Success)
            {
                Logger.Warn("Handshake rejected: {0}", handshakeRes.Result);
                SetBusy(false, "Handshake failed: " + handshakeRes.Result);
                _streamSession?.Stop();
                _transport.Disconnect();
                return;
            }

            // 初始化渲染管线
            _renderTarget = new WpfRenderTarget();
            // 订阅 BitmapChanged：当 Resolution changed 触发 Resize 创建新 bitmap 时，
            // 同步更新 RenderBitmap 绑定，避免 Image.Source 指向旧 bitmap 导致黑屏
            _renderTarget.BitmapChanged += b => RenderBitmap = b;
            _frameBuffer = new FrameBuffer();

            _streamSession.RenderTarget = _renderTarget;
            _streamSession.InitPipeline(handshakeRes.Codec, handshakeRes.ScreenWidth, handshakeRes.ScreenHeight);
            _remoteScreenWidth = handshakeRes.ScreenWidth;
            _remoteScreenHeight = handshakeRes.ScreenHeight;
            // 订阅远程光标更新（形状 + 位置）：WpfRenderTarget.UpdateCursor 在接收线程触发，
            // MainWindow 订阅 RemoteCursorChanged 后在 UI 线程渲染光标叠加层
            _renderTarget.CursorChanged += OnRemoteCursorChanged;
            // 订阅服务端→客户端剪贴板同步事件：服务端用户复制 → 客户端自动设置本地剪贴板
            _streamSession.ClipboardReceived += OnClipboardReceivedFromServer;
            // 订阅文件剪贴板同步事件：服务端用户复制文件 → 客户端写入临时目录并设置 CF_HDROP
            _streamSession.FileClipboardReceived += OnFileClipboardReceivedFromServer;
            // 订阅文件剪贴板下载进度事件：更新 UI 进度条
            _streamSession.FileClipboardProgress += OnFileClipboardProgress;
            // 订阅图片剪贴板同步事件：服务端用户复制图片 → 客户端设置 CF_DIB
            _streamSession.ImageClipboardReceived += OnImageClipboardReceivedFromServer;
            // 订阅服务端分辨率变化：同步坐标映射与显示尺寸，避免映射陈旧导致鼠标落点偏移
            _streamSession.ResolutionChanged += OnRemoteResolutionChanged;

            // 必须在 InitPipeline（调用 Resize）之后才赋值 Bitmap
            RenderBitmap = _renderTarget.Bitmap;

            _inputSession = new ClientInputSession();
            _inputSession.Start(_transport, handshakeRes.ScreenWidth, handshakeRes.ScreenHeight);

            _streamSession.Start(_transport);
            Logger.Info("Client stream session started, codec={0} resolution={1}x{2}",
                handshakeRes.Codec, handshakeRes.ScreenWidth, handshakeRes.ScreenHeight);
            _running = true;
            IsConnected = true;
            // 回放握手期间缓冲的初始光标状态（含形状位图）：此时光标事件已挂接、IsConnected 已置位，
            // MainWindow 能正常渲染远程光标，否则客户端只更新位置、永远没有光标位图可显示。
            _streamSession?.FlushPendingCursor();
            CodecName = handshakeRes.Codec.ToString();
            FrameSize = string.Format("{0}x{1}", handshakeRes.ScreenWidth, handshakeRes.ScreenHeight);

            // 监听断连事件：服务端断开时自动清理状态
            EventHandler? onDisconnected = null;
            onDisconnected = (s, ev) =>
            {
                if (_disconnecting) return; // 防止重入
                _transport.Disconnected -= onDisconnected;
                // BeginInvoke：不阻塞传输接收线程，避免 Stop 期间 Join 与接收线程互等
                _dispatcher.BeginInvoke(() => Stop());
            };
            _transport.Disconnected += onDisconnected;

            // 启动 FPS 监控（每秒从 FrameBuffer 读取帧计数更新 UI）
            _fpsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            long lastFrameCount = 0;
            _fpsTimer.Tick += (s, ev) =>
            {
                // 读会话的真实帧计数：VM 自己的 _frameBuffer 在连接流程中从不被写入，
                // 旧实现 FPS 恒为 0。
                if (_streamSession != null)
                {
                    long current = _streamSession.FrameCount;
                    FrameRate = (int)(current - lastFrameCount);
                    lastFrameCount = current;
                }
            };
            _fpsTimer.Start();

            // 启动剪贴板轮询：每 800ms 检查本地剪贴板文本是否变化，变化则发送到服务端。
            // 必须在 UI 线程（STA）调用 Clipboard.ContainsText/GetText。
            // 检测到变化后通过 _transport.Send 发送 ClipboardSync 消息，服务端在 STA 线程设置系统剪贴板。
            _lastClipboardText = "";
            try
            {
                if (Clipboard.ContainsText())
                {
                    _lastClipboardText = Clipboard.GetText() ?? "";
                    Logger.Info("Clipboard initial read: len={0}", _lastClipboardText.Length);
                }
                else
                {
                    Logger.Info("Clipboard initial: no text");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Clipboard initial read failed");
            }
            _clipboardTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            int clipboardPollCount = 0;
            _clipboardTimer.Tick += (s, ev) =>
            {
                if (_transport == null || !_running) return;
                clipboardPollCount++;
                try
                {
                    bool hasText = Clipboard.ContainsText();
                    bool hasFiles = Clipboard.ContainsFileDropList();
                    bool hasImage = Clipboard.ContainsImage();
                    // 每 30 次轮询（约 24 秒）记录一次状态，确认 timer 在工作
                    if (clipboardPollCount % 30 == 0)
                        Logger.Debug("Clipboard poll #{0}: hasText={1} hasFiles={2} hasImage={3} lastTextLen={4}",
                            clipboardPollCount, hasText, hasFiles, hasImage, _lastClipboardText.Length);

                    // 优先处理文件剪贴板（CF_HDROP）：用户右键复制文件时触发
                    if (hasFiles)
                    {
                        // Owner Flag 防回环：剪贴板若是客户端从服务端同步过来并打上 SideClient 标记的，
                        // 跳过不回传，避免回环。本地用户复制时 owner=SideNone，正常发送。
                        byte owner = EasyRDP.Core.ClipboardOwnerHelper.GetOwnerFlag();
                        if (owner == EasyRDP.Core.ClipboardOwnerHelper.SideClient)
                        {
                            return; // 远程同步过来的，不回传
                        }

                        var dropList = Clipboard.GetFileDropList();
                        string[] files = new string[dropList.Count];
                        dropList.CopyTo(files, 0);
                        string sig = string.Join("|", files);
                        if (sig != _lastClipboardFilesSig)
                        {
                            Logger.Info("File clipboard changed: count={0}", files.Length);
                            _lastClipboardFilesSig = sig;
                            SendFileClipboardToServer(files);
                        }
                        // 文件覆盖了图片：清空图片签名，避免下次复制相同图片时误判为"没变化"
                        _lastClipboardImageSig = "";
                        return; // 文件和文本/图片不会同时在剪贴板上
                    }
                    _lastClipboardFilesSig = "";

                    // 图片剪贴板（CF_DIB）：用户截图/复制图片时触发
                    if (hasImage)
                    {
                        CheckImageClipboardChange();
                        // 图片覆盖了文件：_lastClipboardFilesSig 已在上方 hasFiles=false 分支清空
                        return; // 图片和文本不会同时在剪贴板上
                    }
                    _lastClipboardImageSig = "";

                    if (!hasText) return;
                    string current = Clipboard.GetText() ?? "";
                    if (current != _lastClipboardText)
                    {
                        Logger.Info("Clipboard changed: oldLen={0} newLen={1}",
                            _lastClipboardText.Length, current.Length);
                        _lastClipboardText = current;
                        SendClipboardSync(current);
                    }
                }
                catch (Exception ex)
                {
                    // 剪贴板被其他进程锁定时会抛 ExternalException — 之前是静默吞掉，导致问题无法定位
                    Logger.Warn(ex, "Clipboard poll #{0} failed", clipboardPollCount);
                }
            };
            _clipboardTimer.Start();
            Logger.Info("Clipboard poller started (interval=800ms)");

            // 启动心跳：每 10 秒发一次 Keepalive 消息。服务端 _lastActivity 收到任何客户端消息即更新，
            // 30s 不活动服务端会发 Keepalive 探测，45s 不活动判定超时主动断开。
            // 客户端主动 10s 一次可确保 _lastActivity 始终新鲜，避免最小化/无输入时被断开。
            // 用 System.Threading.Timer（线程池），即使 UI 线程卡住或窗口最小化也能可靠触发。
            _heartbeatTimer = new Timer(state =>
            {
                // 捕获局部变量：Stop() 会把 _transport 置 null，定时器回调可能在
                // Stop 返回后仍触发，局部引用避免 NullReferenceException。
                var transport = _transport;
                if (transport == null || !_running) return;
                try
                {
                    // Keepalive 消息 payload 为空，仅用于刷新服务端 _lastActivity
                    MessageReassembler.FragAndSend(0, (byte)MessageType.Keepalive, new byte[0],
                        (sid, data) => transport.Send(data), 0);
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "Heartbeat send failed");
                }
            }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
            Logger.Info("Heartbeat started (interval=10s, threadpool timer)");

            StatusText = string.Format("Connected — {0}x{1}",
                handshakeRes.ScreenWidth, handshakeRes.ScreenHeight);
            UpdateCommandState();
        }

        /// <summary>
        /// 发送剪贴板同步消息到服务端。文本通过 FragAndSend 分片发送，
        /// 服务端 TransportHost 接收后在 STA 线程设置系统剪贴板。
        /// </summary>
        private void SendClipboardSync(string text)
        {
            if (_transport == null || string.IsNullOrEmpty(text)) return;
            try
            {
                var msg = new ClipboardSyncMessage
                {
                    Format = ClipboardSyncMessage.FormatText,
                    Text = text
                };
                byte[] payload = msg.Pack();
                MessageReassembler.FragAndSend(0, (byte)MessageType.ClipboardSync, payload,
                    (sid, data) => _transport.Send(data), 0);
                Logger.Info("Clipboard sync sent: len={0}", text.Length);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "SendClipboardSync failed");
            }
        }

        /// <summary>
        /// 客户端→服务端文件剪贴板同步（延迟渲染）。
        /// 仅发送 ClipFormatList（文件元信息），不传输文件内容。
        /// 服务端收到后按需通过 ClipFileContentsReq 请求文件内容，客户端用 FileClipboardProvider 响应。
        /// 接收方控制下载速率，避免灌满 TCP 连接。
        /// </summary>
        private void SendFileClipboardToServer(string[] filePaths)
        {
            if (_transport == null || filePaths == null || filePaths.Length == 0) return;

            var transport = _transport;
            try
            {
                uint transferId = (uint)System.Threading.Interlocked.Increment(ref _fileTransferIdSeq);

                // 1) 构造元信息列表（仅文件名+大小，不含文件内容）
                var metaList = new System.Collections.Generic.List<ClipFormatListMessage.FileMeta>(filePaths.Length);
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

                // 2) 创建 FileClipboardProvider（延迟渲染发送方），响应服务端的 FileContentsReq
                var provider = new FileClipboardProvider(transferId, filePaths,
                    (sid, payload) =>
                    {
                        // 单完整帧发送：并发响应的分片若交错且共用 frameId=0，
                        // 接收端重组器会把不同响应的分片混在一起导致 payload 损坏（下载失败 → 无粘贴菜单）。
                        // 每个响应作为完整帧发送，线上交错时互不干扰。
                        MessageReassembler.SendSingleFragment(0, (byte)MessageType.ClipFileContentsRes, payload,
                            (s, d) => transport.Send(d), 0);
                    });
                _streamSession?.SetFileClipboardProvider(provider);

                // 3) 发送 ClipFormatList（仅元信息，几百字节）
                var listMsg = new ClipFormatListMessage
                {
                    TransferId = transferId,
                    Files = metaList
                };
                byte[] listPayload = listMsg.Pack();
                MessageReassembler.FragAndSend(0, (byte)MessageType.ClipFormatList, listPayload,
                    (sid, data) => transport.Send(data), 0);
                Logger.Info("ClipFormatList sent: transferId={0} fileCount={1}", transferId, metaList.Count);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "SendFileClipboardToServer failed");
            }
        }

        /// <summary>
        /// 服务端→客户端剪贴板同步回调。由 ClientStreamSession 在接收线程触发，
        /// 必须通过 Dispatcher.Invoke 转发到 UI 线程（STA）才能调用 Clipboard.SetText。
        /// 关键：先更新 _lastClipboardText，避免 _clipboardTimer 检测到变化又把文本发回服务端（回环）。
        /// </summary>
        private void OnClipboardReceivedFromServer(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            _dispatcher.Invoke(() =>
            {
                try
                {
                    // 先更新 lastClipboardText，避免 _clipboardTimer 检测到变化触发回发
                    _lastClipboardText = text;
                    Clipboard.SetText(text);
                    Logger.Info("Clipboard set from server: len={0}", text.Length);
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "Clipboard set from server failed");
                }
            });
        }

        /// <summary>
        /// 服务端→客户端文件剪贴板同步回调。文件数据已由 FileClipboardConsumer 按需下载并写入临时目录，
        /// 这里只需在 UI 线程（STA）调用 Clipboard.SetFileDropList 设置 CF_HDROP。
        /// 防回环：设置 Owner Flag + 更新 _lastClipboardFilesSig，避免 _clipboardTimer 检测到文件变化又发回服务端。
        /// </summary>
        private void OnFileClipboardReceivedFromServer(string[] localFilePaths)
        {
            if (localFilePaths == null || localFilePaths.Length == 0) return;
            if (!_running) return; // 断连后忽略，避免设置剪贴板
            _dispatcher.Invoke(() =>
            {
                try
                {
                    // WPF Clipboard.SetFileDropList 需要 System.Collections.Specialized.StringCollection
                    var fileList = new System.Collections.Specialized.StringCollection();
                    fileList.AddRange(localFilePaths);
                    Clipboard.SetFileDropList(fileList);

                    // Owner Flag 防回环：标记为 SideClient（表示"由客户端从服务端同步过来"），
                    // _clipboardTimer 轮询看到此标记即跳过，避免回发到服务端
                    EasyRDP.Core.ClipboardOwnerHelper.SetOwnerFlag(EasyRDP.Core.ClipboardOwnerHelper.SideClient);
                    _lastClipboardFilesSig = string.Join("|", localFilePaths);

                    Logger.Info("File clipboard set from server: count={0}", localFilePaths.Length);
                    foreach (var p in localFilePaths)
                        Logger.Info("  - {0}", p);

                    // 文件已设置到剪贴板，隐藏进度条
                    IsClipboardTransferring = false;
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "File clipboard set from server failed");
                    IsClipboardTransferring = false;
                }
            });
        }

        /// <summary>
        /// 文件剪贴板下载进度回调。在下载线程触发，需 marshal 到 UI 线程更新进度条属性。
        /// 参数：(downloadedBytes, totalBytes)。
        /// 断连后(_running=false)忽略回调，避免进度条闪现。
        /// </summary>
        private void OnFileClipboardProgress(long downloaded, long total)
        {
            if (total <= 0 || !_running) return;
            _dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_running) return;
                IsClipboardTransferring = true;
                double percent = (double)downloaded * 100.0 / total;
                ClipboardProgressValue = percent;
                ClipboardProgressText = string.Format("传输中: {0:F1}% ({1} / {2})",
                    percent, FormatBytes(downloaded), FormatBytes(total));
            }));
        }

        /// <summary>把字节数格式化为可读字符串（如 "2.7GB"）。</summary>
        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return string.Format("{0:F1} KB", bytes / 1024.0);
            if (bytes < 1024L * 1024 * 1024) return string.Format("{0:F1} MB", bytes / (1024.0 * 1024));
            return string.Format("{0:F2} GB", bytes / (1024.0 * 1024 * 1024));
        }

        /// <summary>
        /// 检查客户端本地图片剪贴板（CF_DIB）是否变化，变化时启动后台线程异步发送到服务端。
        /// 必须在 STA 线程调用（DispatcherTimer.Tick 内）— 只在 UI 线程读剪贴板，数据发送在后台线程。
        /// 通过 DataFormats.Dib 直接获取 CF_DIB 原始字节，避免 WPF BitmapSource 转换损失。
        /// </summary>
        private void CheckImageClipboardChange()
        {
            try
            {
                // 通过 DataFormats.Dib 直接获取 CF_DIB 原始字节
                // WPF Clipboard.ContainsImage 返回 true 时，CF_DIB 一定可用
                var ms = Clipboard.GetData(DataFormats.Dib) as System.IO.MemoryStream;
                if (ms == null)
                {
                    // 退回到 BitmapSource 转换路径（理论上不会走到，因为 ContainsImage 已为 true）
                    _lastClipboardImageSig = "";
                    return;
                }
                byte[] dibBytes = ms.ToArray();
                if (dibBytes.Length == 0)
                {
                    _lastClipboardImageSig = "";
                    return;
                }

                // 构造签名：长度 + 前 32 字节哈希
                string sig = dibBytes.Length + ":" + ComputeSimpleHash(dibBytes, 32);
                if (sig == _lastClipboardImageSig)
                    return; // 没变化

                _lastClipboardImageSig = sig;
                Logger.Info("Image clipboard changed: dibSize={0}", dibBytes.Length);

                // 后台线程异步发送：不阻塞 UI 线程
                System.Threading.Tasks.Task.Run(() =>
                {
                    try { SendImageClipboardToServer(dibBytes); }
                    catch (Exception ex) { Logger.Warn(ex, "SendImageClipboardToServer failed"); }
                });
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "CheckImageClipboardChange failed");
            }
        }

        /// <summary>
        /// 客户端→服务端图片剪贴板同步。发送流程：
        /// ImageClipboardStart → 多个 ImageClipboardData（64KB 分块）→ ImageClipboardEnd
        /// 服务端 TransportHost 接收后在 STA 线程调用 IClipboardService.SetImageDibBytes 设置 CF_DIB。
        /// </summary>
        private void SendImageClipboardToServer(byte[] dibBytes)
        {
            if (_transport == null || dibBytes == null || dibBytes.Length == 0) return;
            try
            {
                uint transferId = (uint)System.Threading.Interlocked.Increment(ref _fileTransferIdSeq);

                // 1) 发送 Start
                var startMsg = new ImageClipboardStartMessage
                {
                    TransferId = transferId,
                    TotalSize = dibBytes.Length
                };
                byte[] startPayload = startMsg.Pack();
                MessageReassembler.FragAndSend(0, (byte)MessageType.ImageClipboardStart, startPayload,
                    (sid, data) => _transport.Send(data), 0);
                Logger.Info("ImageClipboardStart sent: transferId={0} dibSize={1}",
                    transferId, dibBytes.Length);

                // 2) 分块发送 Data
                int offset = 0;
                int chunkCount = 0;
                while (offset < dibBytes.Length)
                {
                    int chunkLen = Math.Min(ImageChunkSize, dibBytes.Length - offset);
                    byte[] chunk = new byte[chunkLen];
                    Buffer.BlockCopy(dibBytes, offset, chunk, 0, chunkLen);

                    var dataMsg = new ImageClipboardDataMessage
                    {
                        TransferId = transferId,
                        Offset = offset,
                        DataLen = chunkLen,
                        Data = chunk
                    };
                    byte[] dataPayload = dataMsg.Pack();
                    MessageReassembler.FragAndSend(0, (byte)MessageType.ImageClipboardData, dataPayload,
                        (s, d) => _transport.Send(d), 0);
                    offset += chunkLen;
                    chunkCount++;
                }
                Logger.Info("ImageClipboardData sent: transferId={0} chunks={1} totalBytes={2}",
                    transferId, chunkCount, dibBytes.Length);

                // 3) 发送 End
                var endMsg = new ImageClipboardEndMessage { TransferId = transferId };
                byte[] endPayload = endMsg.Pack();
                MessageReassembler.FragAndSend(0, (byte)MessageType.ImageClipboardEnd, endPayload,
                    (sid, data) => _transport.Send(data), 0);
                Logger.Info("ImageClipboardEnd sent: transferId={0}", transferId);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "SendImageClipboardToServer pack/send failed");
            }
        }

        /// <summary>
        /// 服务端→客户端图片剪贴板同步回调。CF_DIB 数据已由 ImageClipboardReceiver 组装完毕，
        /// 这里在 UI 线程（STA）通过 DataObject 设置 CF_DIB 原始字节到本地剪贴板。
        /// 防回环：更新 _lastClipboardImageSig，避免 _clipboardTimer 检测到图片变化又发回服务端。
        /// </summary>
        private void OnImageClipboardReceivedFromServer(byte[] dibBytes)
        {
            if (dibBytes == null || dibBytes.Length == 0) return;
            _dispatcher.Invoke(() =>
            {
                try
                {
                    // 通过 DataObject 直接设置 CF_DIB 原始字节，避免 BitmapSource 转换损失
                    var dataObj = new DataObject();
                    using (var ms = new System.IO.MemoryStream(dibBytes))
                    {
                        dataObj.SetData(DataFormats.Dib, ms, false); // autoDispose=false：离开 using 后 ms 已被复制
                    }
                    Clipboard.SetDataObject(dataObj, true);

                    // 防回环：更新图片签名，避免 _clipboardTimer 检测到变化又发回服务端
                    _lastClipboardImageSig = dibBytes.Length + ":" + ComputeSimpleHash(dibBytes, 32);

                    Logger.Info("Image clipboard set from server: dibSize={0}", dibBytes.Length);
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "Image clipboard set from server failed");
                }
            });
        }

        /// <summary>
        /// 服务端分辨率变化回调（解码线程触发）：更新输入坐标映射与显示尺寸，
        /// 避免 ClientInputSession 仍按旧分辨率映射导致远程鼠标落点偏移。
        /// 属性变更与坐标映射均在 UI 线程执行（BeginInvoke 异步调度，避免解码线程阻塞）。
        /// </summary>
        private void OnRemoteResolutionChanged(int width, int height)
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                _remoteScreenWidth = width;
                _remoteScreenHeight = height;
                OnPropertyChanged(nameof(RemoteScreenWidth));
                OnPropertyChanged(nameof(RemoteScreenHeight));
                _inputSession?.OnResolutionChanged(width, height);
                FrameSize = string.Format("{0}x{1}", width, height);
                Logger.Info("Remote resolution changed: {0}x{1}", width, height);
            }));
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

        // ====== Render Test 逻辑 ======

        private void StartRenderTest()
        {
            if (_running) return;
            SetBusy(true, "Running render test...");

            _renderTarget = new WpfRenderTarget();
            _renderTarget.BitmapChanged += b => RenderBitmap = b;
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
            if (_disconnecting) return;
            _disconnecting = true;
            Logger.Info("Stopping client session");

            if (_fpsTimer != null) { _fpsTimer.Stop(); _fpsTimer = null; }
            if (_clipboardTimer != null) { _clipboardTimer.Stop(); _clipboardTimer = null; }
            if (_heartbeatTimer != null)
            {
                // 等待在途回调结束再置空，避免回调与 Stop 竞态访问 _transport
                var waitHandle = new ManualResetEvent(false);
                try
                {
                    _heartbeatTimer.Dispose(waitHandle);
                    waitHandle.WaitOne(1000);
                }
                catch { }
                finally { waitHandle.Dispose(); }
                _heartbeatTimer = null;
            }

            _running = false;
            // 顺序修复：先断开传输（停止接收线程产生新消息），再停止会话。
            // 旧顺序反过来：会话 Stop 时接收线程仍可能在 ProcessVideoFrame 中调用
            // _decoder.Decode，而 Stop 已把 _decoder 置 null → AccessViolation。
            _transport?.Disconnect();
            _streamSession?.Stop();
            _transport = null;
            _streamSession = null;
            _inputSession = null;

            // 释放鼠标捕获（右键按下期间断开时可能仍持有捕获）
            try { System.Windows.Input.Mouse.Capture(null); } catch { }

            // 断连/停止时强制退出全屏：全屏置顶窗口在断开后若保持全屏，
            // 会盖住整个桌面且用户无法把其他窗口切到前台（历史上只能重启电脑）。
            ExitFullscreen();

            // 清理渲染资源
            if (_renderTarget != null)
            {
                _renderTarget.CursorChanged -= OnRemoteCursorChanged;
                try { _renderTarget.Dispose(); } catch { }
                _renderTarget = null;
            }
            if (_frameBuffer != null)
            {
                _frameBuffer.Reset();
                _frameBuffer = null;
            }
            RenderBitmap = null;

            Logger.Info("Client session stopped");
            IsConnected = false;
            FrameSize = "—";
            FrameRate = 0;
            CodecName = "—";
            // 重置剪贴板进度条状态（断开连接时隐藏）
            IsClipboardTransferring = false;
            ClipboardProgressValue = 0;
            ClipboardProgressText = "";
            SetBusy(false, "Disconnected");
        }

        /// <summary>远程光标更新回调（接收线程）→ 转发为 RemoteCursorChanged 事件。</summary>
        private void OnRemoteCursorChanged(CursorInfo cursor)
        {
            var handler = RemoteCursorChanged;
            if (handler != null)
                handler(cursor);
        }

        // ====== 输入事件处理（由 View 代码后置调用） ======

        public void HandleMouseMove(double imageX, double imageY, double imageW, double imageH)
        {
            if (_inputSession == null || !_running) return;
            int sx, sy;
            _inputSession.MapCoordinates(imageX, imageY, imageW, imageH, out sx, out sy);
            // 节流合并：高频 MouseMove 只更新最新坐标，由 ClientInputSession 按 ~60Hz 发送
            _inputSession.QueueMouseMove(sx, sy);
        }

        public void HandleMouseDown(System.Windows.Input.MouseButton changedButton)
        {
            if (_inputSession == null || !_running) return;
            // WPF MouseButton: Left=0 Right=1 Middle=2 XButton1=3 XButton2=4，
            // EasyDesk MouseButton: Left=1 Right=2 Middle=3 XButton1=4 XButton2=5，
            // 显式映射避免依赖枚举值相邻性（+1 脆弱）。
            _inputSession.FlushPendingMouse(); // 先落地最新光标位置，保证点击位置准确
            int btn = MapMouseButton(changedButton);
            if (btn == 0) return;
            var msg = new InputEventMessage { Type = InputEventType.MouseDown, KeyCode = btn };
            _inputSession.SendInput(msg);
        }

        public void HandleMouseUp(System.Windows.Input.MouseButton changedButton)
        {
            if (_inputSession == null || !_running) return;
            _inputSession.FlushPendingMouse();
            int btn = MapMouseButton(changedButton);
            if (btn == 0) return;
            var msg = new InputEventMessage { Type = InputEventType.MouseUp, KeyCode = btn };
            _inputSession.SendInput(msg);
        }

        /// <summary>WPF MouseButton → EasyDesk MouseButton 显式映射（左=1 右=2 中=3 X1=4 X2=5）。</summary>
        private static int MapMouseButton(System.Windows.Input.MouseButton button)
        {
            switch (button)
            {
                case System.Windows.Input.MouseButton.Left: return 1;
                case System.Windows.Input.MouseButton.Right: return 2;
                case System.Windows.Input.MouseButton.Middle: return 3;
                case System.Windows.Input.MouseButton.XButton1: return 4;
                case System.Windows.Input.MouseButton.XButton2: return 5;
                default: return 0;
            }
        }

        public void HandleMouseWheel(int delta)
        {
            if (_inputSession == null || !_running) return;
            var msg = new InputEventMessage { Type = InputEventType.MouseWheel, WheelDelta = delta };
            _inputSession.SendInput(msg);
        }

        /// <summary>发送 Win+L 锁定远端会话（SendInput 可模拟；Ctrl+Alt+Del 属系统安全序列，无法模拟）。</summary>
        public void SendLockKey()
        {
            SendRemoteKeyCombo(new[] { 0x5B, 0x4C }); // VK_LWIN + 'L'
        }

        /// <summary>发送 Alt+Tab 在远端切换窗口。</summary>
        public void SendAltTab()
        {
            SendRemoteKeyCombo(new[] { 0x12, 0x09 }); // VK_MENU + VK_TAB
        }

        /// <summary>释放可能卡住的修饰键（Ctrl/Alt/Shift/Win），防止远端按键粘连。</summary>
        public void ReleaseModifierKeys()
        {
            if (_inputSession == null || !_running) return;
            int[] mods = { 0x10, 0x11, 0x12, 0x5B, 0x5C }; // Shift, Ctrl, Alt, LWin, RWin
            foreach (int vk in mods)
            {
                _inputSession.SendInput(new InputEventMessage { Type = InputEventType.KeyUp, KeyCode = vk });
            }
        }

        /// <summary>按"全部按下、逆序抬起"发送一组虚拟键（用于组合键）。</summary>
        private void SendRemoteKeyCombo(int[] virtualKeys)
        {
            if (_inputSession == null || !_running || virtualKeys == null) return;
            foreach (int vk in virtualKeys)
            {
                _inputSession.SendInput(new InputEventMessage { Type = InputEventType.KeyDown, KeyCode = vk });
            }
            for (int i = virtualKeys.Length - 1; i >= 0; i--)
            {
                _inputSession.SendInput(new InputEventMessage { Type = InputEventType.KeyUp, KeyCode = virtualKeys[i] });
            }
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

        /// <summary>
        /// 将 WPF Key 枚举映射为 Windows 虚拟键码。
        /// 使用 KeyInterop.VirtualKeyFromKey 覆盖所有键：F1-F12、Home/End/PageUp/PageDown/Insert、
        /// NumPad0-9、Oem 字符键（; , . / ' [ ] \ - = `）、CapsLock、NumLock、PrintScreen 等。
        /// </summary>
        private static int KeyToVirtualKey(System.Windows.Input.Key key)
        {
            // KeyInterop.VirtualKeyFromKey 是 WPF 内置的 Key → VK 映射，
            // 覆盖所有标准 Windows 虚拟键码，无需手动维护映射表
            return System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
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
            var window = Application.Current?.MainWindow as MainWindow;
            if (window == null) return;
            window.SetFullscreenMode(!window.IsFullscreenMode);
            Logger.Info(window.IsFullscreenMode ? "Entered fullscreen" : "Exited fullscreen");
        }

        /// <summary>
        /// 退出全屏并恢复普通窗口（断开连接时也会调用，保证用户始终能切走）。
        /// 全屏窗口的尺寸/位置由 View 层 WM_GETMINMAXINFO 钩子管理，
        /// ViewModel 只负责请求状态切换。
        /// </summary>
        public void ExitFullscreen()
        {
            var window = Application.Current?.MainWindow as MainWindow;
            if (window == null || !window.IsFullscreenMode)
                return;
            window.SetFullscreenMode(false);
            Logger.Info("Exited fullscreen");
        }

        // ====== INotifyPropertyChanged ======

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
