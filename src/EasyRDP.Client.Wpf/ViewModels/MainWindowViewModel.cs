using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EasyRDP.Client.Wpf.Models;
using EasyRDP.Client.Wpf.Rendering;
using EasyRDP.Client.Wpf.Services;
using AlyClient.CSharpSDK;
using EasyRDP.Core.Protocol;
using EasyRDP.Core.Rendering;
using EasyRDP.Core.Security;
using EasyRDP.Core.Transport;
using EasyRDP.Shared;
using NLog;

namespace EasyRDP.Client.Wpf.ViewModels
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
        /// <summary>审批模式等待上限（毫秒）：服务端操作员有 60s 决定，客户端留 15s 余量。</summary>
        private const int ApprovalTimeoutMs = 75000;

        private readonly Dispatcher _dispatcher;

        // 业务对象
        private WpfRenderTarget? _renderTarget;
        private FrameBuffer? _frameBuffer;
        private ClientStreamSession? _streamSession;
        private ClientInputSession? _inputSession;
        private ITransport? _transport;
        // 文件传输：优先走独立通道（打开失败时回退主连接）
        private ITransport? _fileChannel;
        private EasyRDP.Core.Transfer.FileTransferSession? _fileTransfer;
        private bool _isFileTransferVisible;
        private double _fileTransferPercent;
        private string _fileTransferText = "";
        // 接收完成提示的自动隐藏定时器
        private DispatcherTimer? _fileTransferNoticeTimer;
        private volatile bool _running;
        private volatile bool _disconnecting; // 防止 Disconnect → Disconnected 事件 → Stop 重入循环
        private DispatcherTimer? _fpsTimer;

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
        // D19：首帧是否已渲染（连接成功但画面未到时显示加载层而非黑屏）
        private bool _isFirstFrameRendered;
        private string _frameSize = "—";
        private int _frameRate;
        private string _codecName = "—";
        private WriteableBitmap? _renderBitmap;

        // ── 自动重连 ──
        // 非用户触发的断线（网络抖动/服务端重启）自动重连，退避 1/2/4/8/10… 秒。
        private bool _reconnecting;
        private int _reconnectAttempt;
        private CancellationTokenSource? _reconnectCts;
        // 当前是否处于"重连尝试"中：用于抑制重复错误弹框（重连失败只更新遮罩文字）
        private bool _isReconnectAttempt;
        // 最近一次连接失败是否值得重试（认证失败/版本不符/被拒绝 → 不重试）
        private bool _lastOutcomeRetryable = true;
        // 用户取消连接/重连：在途的握手完成后不再对外呈现在线状态
        private bool _cancelConnectRequested;
        private const int MaxReconnectDelayMs = 10000;
        private string _connectingMessage = "Connecting, waiting for the first frame…";
        // 远程屏幕尺寸（握手时记录）：用于把远程光标坐标映射到本地显示区
        private int _remoteScreenWidth;
        private int _remoteScreenHeight;
        // 剪贴板轮询：检测本地剪贴板变化，发送到服务端
        private DispatcherTimer? _clipboardTimer;
        private string _lastClipboardText = "";
        // 上次文件剪贴板签名（拼接路径），用于检测变化 + 防回环
        private string _lastClipboardFilesSig = "";

        // ── 剪贴板文件延迟渲染（服务端复制 → 本机粘贴时才下载）──
        // 粘贴时若下载尚未完成，先快速等 300ms；仍无结果再弹模态进度窗
        private const int PasteFastWaitMs = 300;
        // 复制后该时间窗内的 CF_HDROP 渲染请求来自系统剪贴板服务（Win11 剪贴板历史等），
        // 不是用户粘贴：忽略它、保持延迟占位，避免"复制即开始传输"。
        private const int ClipboardProbeSuppressMs = 1200;
        private FileClipboardConsumer? _pendingClipConsumer;
        private bool _pendingClipActive;
        private uint _pendingClipTransferId;
        // 占位安装时刻：用于识别系统剪贴板服务的探测式读取
        private long _pendingClipInstalledTicks;
        // 渲染重入保护：系统/本程序可能同时读取剪贴板
        private bool _renderingClipboardFiles;

        /// <summary>本窗口句柄（延迟渲染的剪贴板所有者），由 View 在窗口创建后注入。</summary>
        public IntPtr ClipboardOwnerHandle { get; set; }

        // 上次图片剪贴板签名（CF_DIB 字节数 + 前 32 字节哈希），用于检测变化 + 防回环
        private string _lastClipboardImageSig = "";

        // ── 连接详情面板（ToDesk 风格，每秒刷新）──
        private bool _isDetailsPanelVisible;
        // 详情面板折叠态：true = 仅显示右上角小"Details"角标（点击展开）
        private bool _isDetailsPanelCollapsed;
        private DateTime _connectStartTime;
        private long _lastReceivedBytes;        // 码率差分基准
        private string _connectionDurationText = "—";
        private string _receiveRateText = "—";
        private string _rttText = "—";
        private string _lossRateText = "—";
        private string _captureMethodText = "—";
        private string _remoteResolutionText = "—";
        private string _codecDetailText = "—";
        // 本地系统信息（连接前即展示，一次性获取）
        private readonly string _localCpuText;
        private readonly string _localGpuText;
        private readonly string _localMemoryText;
        private readonly string _localOsText;
        // 远端系统信息文本（来自 DiagnosticInfo，未收到前为 "—"）
        private string _remoteCpuText = "—";
        private string _remoteGpuText = "—";
        private string _remoteMemoryText = "—";
        private string _remoteOsText = "—";
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
        // 鼠标移动/光标回显诊断计数（每 20 条记录一次，用于定位点击偏移）
        private int _mouseMoveDiagCounter;
        private int _cursorEchoDiagCounter;

        // ====== 多服务器配置保存 ======
        private readonly ConnectionProfileStore _profileStore;
        private readonly RecentConnectionStore _recentStore;
        private ServerProfile? _selectedProfile;
        private string _profileName = "";

        // 用户提示抽象（默认 MessageBox，由 View 注入），避免 ViewModel 直接依赖 System.Windows.MessageBox
        private readonly IUserNotifier _notifier;

        public MainWindowViewModel()
        {
            _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            // 连接失败/握手失败/流错误等用弹框提示（连接不成功不能只靠状态栏文字，用户看不到）
            _notifier = new WpfUserNotifier();
            ConnectCommand = new RelayCommand(async () => await ConnectSafelyAsync(), () => !_running);
            StopCommand = new RelayCommand(Stop, () => _running);
            CancelReconnectCommand = new RelayCommand(CancelReconnect, () => _reconnecting);
            FullscreenCommand = new RelayCommand(ToggleFullscreen);
            LockCommand = new RelayCommand(SendLockKey);
            AltTabCommand = new RelayCommand(SendAltTab);
            ResetKeysCommand = new RelayCommand(ReleaseModifierKeys);
            SaveProfileCommand = new RelayCommand(SaveProfile);
            DeleteProfileCommand = new RelayCommand(DeleteProfile, () => SelectedProfile != null);
            CheckUpdateCommand = new RelayCommand(CheckUpdate,
                () => AlyClientStatus == AlyClientStatus.DiscoveredUpdate
                    || AlyClientStatus == AlyClientStatus.DownloadedUpdate);
            ToggleDetailsCommand = new RelayCommand(ToggleDetailsPanel);
            ToggleDetailsCollapseCommand = new RelayCommand(ToggleDetailsCollapse);
            CycleZoomCommand = new RelayCommand(CycleZoom);
            SendFileCommand = new RelayCommand(SendFilesByDialog, () => _running);

            // 本地系统信息（连接详情面板"系统性能"本地列，一次性获取）
            _localCpuText = GetLocalCpuText();
            _localGpuText = GetLocalGpuName();
            _localMemoryText = GetLocalMemoryText();
            _localOsText = Environment.OSVersion.VersionString
                + (Environment.Is64BitOperatingSystem ? " (x64)" : " (x86)");

            // 启动时恢复已保存的多服务器配置
            _profileStore = new ConnectionProfileStore();
            _recentStore = new RecentConnectionStore();
            System.Collections.Generic.List<ServerProfile> saved = _profileStore.Load(out string lastProfileName);
            foreach (var p in saved)
                Profiles.Add(p);
            if (Profiles.Count > 0)
            {
                var last = FindProfile(lastProfileName);
                SelectedProfile = last ?? Profiles[0];
            }

            // 初始化最近连接列表（从本地缓存加载；连接成功后更新）
            RefreshRecentConnections();

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

        /// <summary>最近连接列表（主页缩略图网格）。初始从 Profiles 填充；
        /// 连接成功后把当前 Host 置顶/加入。</summary>
        public ObservableCollection<RecentConnection> RecentConnections { get; } = new ObservableCollection<RecentConnection>();

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
            set
            {
                _isConnected = value;
                OnPropertyChanged(nameof(IsConnected));
                // D19：远程桌面区可见性 = 已连接 && 首帧已渲染；
                // 连接加载层可见性 = 已连接 && 首帧未渲染
                OnPropertyChanged(nameof(ShowRemoteDesktop));
                OnPropertyChanged(nameof(ShowConnectingOverlay));
            }
        }

        /// <summary>首帧像素是否已写入渲染位图（连接成功但画面未到时为 false，
        /// 用于显示"连接加载层"而非黑屏）。断开时重置。</summary>
        public bool IsFirstFrameRendered
        {
            get { return _isFirstFrameRendered; }
            set
            {
                _isFirstFrameRendered = value;
                OnPropertyChanged(nameof(IsFirstFrameRendered));
                OnPropertyChanged(nameof(ShowRemoteDesktop));
                OnPropertyChanged(nameof(ShowConnectingOverlay));
            }
        }

        /// <summary>远程桌面显示区可见性：连接成功且首帧已渲染。</summary>
        public bool ShowRemoteDesktop
        {
            get { return _isConnected && _isFirstFrameRendered; }
        }

        /// <summary>连接加载层（转圈等待）可见性：首帧尚未渲染，或正在自动重连。</summary>
        public bool ShowConnectingOverlay
        {
            get { return _reconnecting || (_isConnected && !_isFirstFrameRendered); }
        }

        /// <summary>加载层文字（连接中 / 等待服务端审批 / 正在重连）。</summary>
        public string ConnectingMessage
        {
            get { return _connectingMessage; }
            set
            {
                _connectingMessage = value ?? "";
                OnPropertyChanged(nameof(ConnectingMessage));
            }
        }

        /// <summary>是否正在自动重连（加载层显示取消按钮与重连提示）。</summary>
        public bool IsReconnecting
        {
            get { return _reconnecting; }
            private set
            {
                if (_reconnecting == value) return;
                _reconnecting = value;
                OnPropertyChanged(nameof(IsReconnecting));
                OnPropertyChanged(nameof(ShowConnectingOverlay));
                CancelReconnectCommand.RaiseCanExecuteChanged();
            }
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

        /// <summary>最近一次服务端诊断信息（连接详情面板数据源）。连接前/未收到时为 null。</summary>
        public DiagnosticInfoMessage? ServerDiagnosticInfo
        {
            get { return _serverDiagnosticInfo; }
            private set { _serverDiagnosticInfo = value; OnPropertyChanged(nameof(ServerDiagnosticInfo)); }
        }
        private DiagnosticInfoMessage? _serverDiagnosticInfo;

        /// <summary>最近一次 Keepalive RTT（毫秒），-1 表示未测到。</summary>
        public int LastRttMs
        {
            get { return _streamSession != null ? _streamSession.LastRttMs : -1; }
        }

        // ── 连接详情面板绑定属性 ──

        /// <summary>详情面板是否可见（工具栏 Details 按钮切换）。</summary>
        public bool IsDetailsPanelVisible
        {
            get { return _isDetailsPanelVisible; }
            set
            {
                if (_isDetailsPanelVisible == value) return;
                _isDetailsPanelVisible = value;
                // 重新打开面板时默认展开（记忆用户上次手动折叠状态无必要，展开是主诉求）
                if (value) _isDetailsPanelCollapsed = false;
                OnPropertyChanged(nameof(IsDetailsPanelVisible));
                OnPropertyChanged(nameof(IsDetailsPanelExpandedVisible));
                OnPropertyChanged(nameof(IsDetailsCollapsedBadgeVisible));
            }
        }

        /// <summary>
        /// 详情面板是否折叠为小角标（面板右上"—"按钮收起、"Details"角标展开）。
        /// 面板悬浮于远程画面右上角（宽 380），长时间挂机/操作远端右上区域时
        /// 会遮挡画面（面板本身不参与命中测试，但视觉遮挡远端标题栏按钮等），
        /// 提供人工折叠/展开后既保留随时查看诊断的能力，又不长期遮挡画面。
        /// 折叠态仅显示一个小的"Details"角标按钮（可点击展开）。
        /// </summary>
        public bool IsDetailsPanelCollapsed
        {
            get { return _isDetailsPanelCollapsed; }
            set
            {
                if (_isDetailsPanelCollapsed == value) return;
                _isDetailsPanelCollapsed = value;
                OnPropertyChanged(nameof(IsDetailsPanelCollapsed));
                OnPropertyChanged(nameof(IsDetailsPanelExpandedVisible));
                OnPropertyChanged(nameof(IsDetailsCollapsedBadgeVisible));
            }
        }

        /// <summary>
        /// 派生可见性：详情面板"展开态"（面板可见且未折叠）。
        /// 面板 Border 与其右上角"—"收起按钮的 Visibility 直接绑定，
        /// 避免在 XAML 里组合多重条件触发器。
        /// </summary>
        public bool IsDetailsPanelExpandedVisible
        {
            get { return _isDetailsPanelVisible && !_isDetailsPanelCollapsed; }
        }

        /// <summary>派生可见性：折叠角标"Details"按钮（面板可见但已折叠）。</summary>
        public bool IsDetailsCollapsedBadgeVisible
        {
            get { return _isDetailsPanelVisible && _isDetailsPanelCollapsed; }
        }

        /// <summary>折叠/展开详情面板（面板右上"—"按钮与折叠角标共用）。</summary>
        public RelayCommand ToggleDetailsCollapseCommand { get; }

        /// <summary>连接时长文本（如 "00:01:55"）。</summary>
        public string ConnectionDurationText { get { return _connectionDurationText; } }
        /// <summary>接收码率（KB/s）。</summary>
        public string ReceiveRateText { get { return _receiveRateText; } }
        /// <summary>往返时延（ms）。</summary>
        public string RttText { get { return _rttText; } }
        /// <summary>丢帧率百分比。</summary>
        public string LossRateText { get { return _lossRateText; } }
        /// <summary>远端采集方式名（DXGI / BitBlt）。</summary>
        public string CaptureMethodText { get { return _captureMethodText; } }
        /// <summary>远端分辨率文本（含缩放，如 "2240×1400 (150%)"）。</summary>
        public string RemoteResolutionText { get { return _remoteResolutionText; } }
        /// <summary>编解码器详情（如 "H.264 / 软编·软解"）。</summary>
        public string CodecDetailText { get { return _codecDetailText; } }

        /// <summary>本地 CPU 信息。</summary>
        public string LocalCpuText { get { return _localCpuText; } }
        /// <summary>本地 GPU 名称。</summary>
        public string LocalGpuText { get { return _localGpuText; } }
        /// <summary>本地物理内存。</summary>
        public string LocalMemoryText { get { return _localMemoryText; } }
        /// <summary>本地操作系统版本。</summary>
        public string LocalOsText { get { return _localOsText; } }
        /// <summary>远端 CPU 信息。</summary>
        public string RemoteCpuText { get { return _remoteCpuText; } }
        /// <summary>远端 GPU 名称。</summary>
        public string RemoteGpuText { get { return _remoteGpuText; } }
        /// <summary>远端物理内存。</summary>
        public string RemoteMemoryText { get { return _remoteMemoryText; } }
        /// <summary>远端操作系统版本。</summary>
        public string RemoteOsText { get { return _remoteOsText; } }

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

        // 当前连接的只读摘要（远程界面顶栏 / Details 面板显示）
        private string _connectionSummary = "";
        private System.Windows.Media.ImageSource? _connectionThumbnail;

        /// <summary>当前连接摘要（如 "办公室 · 192.168.1.10:2000 · user"）。</summary>
        public string ConnectionSummary
        {
            get { return _connectionSummary; }
            private set { _connectionSummary = value ?? ""; OnPropertyChanged(nameof(ConnectionSummary)); }
        }

        /// <summary>当前连接缩略图（Details 面板使用，可空）。</summary>
        public System.Windows.Media.ImageSource? ConnectionThumbnail
        {
            get { return _connectionThumbnail; }
            private set { _connectionThumbnail = value; OnPropertyChanged(nameof(ConnectionThumbnail)); }
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

        /// <summary>是否显示显式文件传输进度面板（仅传输期间可见）。</summary>
        public bool IsFileTransferVisible
        {
            get { return _isFileTransferVisible; }
            private set { _isFileTransferVisible = value; OnPropertyChanged(nameof(IsFileTransferVisible)); }
        }

        /// <summary>显式文件传输进度（0-100）。</summary>
        public double FileTransferPercent
        {
            get { return _fileTransferPercent; }
            private set { _fileTransferPercent = value; OnPropertyChanged(nameof(FileTransferPercent)); }
        }

        /// <summary>显式文件传输说明文字（方向/文件名/速率）。</summary>
        public string FileTransferText
        {
            get { return _fileTransferText; }
            private set { _fileTransferText = value; OnPropertyChanged(nameof(FileTransferText)); }
        }

        /// <summary>选择文件发送到远程桌面。</summary>
        public RelayCommand SendFileCommand { get; }

        public RelayCommand ConnectCommand { get; }
        /// <summary>取消自动重连（加载层上的 Cancel 按钮）。</summary>
        public RelayCommand CancelReconnectCommand { get; }
        public RelayCommand StopCommand { get; }
        public RelayCommand FullscreenCommand { get; }
        public RelayCommand LockCommand { get; }
        public RelayCommand AltTabCommand { get; }
        public RelayCommand ResetKeysCommand { get; }
        /// <summary>切换连接详情面板可见性。</summary>
        public RelayCommand ToggleDetailsCommand { get; }

        // ====== 缩放模式（RealVNC 风格：Fit/Actual/Stretch 三态循环） ======

        /// <summary>循环切换缩放模式的命令。</summary>
        public RelayCommand CycleZoomCommand { get; }

        private ZoomMode _zoomMode = ZoomMode.Fit;

        /// <summary>当前缩放模式。</summary>
        public ZoomMode ZoomMode
        {
            get { return _zoomMode; }
            private set
            {
                if (_zoomMode == value) return;
                _zoomMode = value;
                OnPropertyChanged(nameof(ZoomMode));
                OnPropertyChanged(nameof(BrushStretch));
                OnPropertyChanged(nameof(ZoomModeText));
            }
        }

        /// <summary>映射到 ImageBrush.Stretch 的枚举值（xaml 绑定）。</summary>
        public System.Windows.Media.Stretch BrushStretch
        {
            get
            {
                switch (_zoomMode)
                {
                    case ZoomMode.Actual: return System.Windows.Media.Stretch.None;
                    case ZoomMode.Stretch: return System.Windows.Media.Stretch.Fill;
                    default: return System.Windows.Media.Stretch.Uniform;
                }
            }
        }

        /// <summary>缩放模式显示文本（悬浮条按钮）。</summary>
        public string ZoomModeText
        {
            get
            {
                switch (_zoomMode)
                {
                    case ZoomMode.Actual: return "Zoom: 1:1";
                    case ZoomMode.Stretch: return "Zoom: Stretch";
                    default: return "Zoom: Fit";
                }
            }
        }

        /// <summary>三态循环切换缩放模式。</summary>
        private void CycleZoom()
        {
            ZoomMode = (ZoomMode)(((int)_zoomMode + 1) % 3);
        }

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

        /// <summary>刷新最近连接列表：从本地 JSON 缓存加载，并把当前连接 host 置顶。</summary>
        private void RefreshRecentConnections()
        {
            RecentConnections.Clear();
            var list = _recentStore != null ? _recentStore.Load() : new System.Collections.Generic.List<RecentConnection>();
            // 当前已连接：把当前 Host 放在第一位
            if (IsConnected && !string.IsNullOrWhiteSpace(Host))
            {
                var current = list.FirstOrDefault(r =>
                    string.Equals(r.Host, Host.Trim(), StringComparison.OrdinalIgnoreCase));
                if (current != null)
                {
                    list.Remove(current);
                    current.ThumbnailSource = ThumbnailCache.LoadThumbnail(current.Host);
                    RecentConnections.Add(current);
                }
                else
                {
                    RecentConnections.Add(new RecentConnection
                    {
                        Host = Host.Trim(),
                        DisplayName = SelectedProfile?.Name ?? Host.Trim(),
                        LastConnectedUtc = DateTime.UtcNow,
                        ThumbnailSource = ThumbnailCache.LoadThumbnail(Host.Trim())
                    });
                }
            }
            foreach (var r in list.OrderByDescending(x => x.LastConnectedUtc))
            {
                r.ThumbnailSource = ThumbnailCache.LoadThumbnail(r.Host);
                RecentConnections.Add(r);
            }
        }

        /// <summary>
        /// 捕获当前远程帧并保存为缩略图，同时更新 recent.json 中的 ThumbnailPath。
        /// 在连接成功后延迟调用，避免捕获握手/黑屏帧。
        /// </summary>
        private void SaveConnectionThumbnail()
        {
            try
            {
                if (_streamSession == null || string.IsNullOrWhiteSpace(Host)) return;
                int w, h;
                byte[] bgra;
                if (!_streamSession.TryCaptureFrame(out w, out h, out bgra)) return;
                string path = ThumbnailCache.SaveThumbnail(bgra, w, h, Host.Trim());
                if (path == null) return;
                var list = _recentStore.Load();
                var item = list.FirstOrDefault(r =>
                    string.Equals(r.Host, Host.Trim(), StringComparison.OrdinalIgnoreCase));
                if (item != null)
                {
                    item.ThumbnailPath = path;
                    _recentStore.Save(list);
                    _dispatcher.Invoke(() => RefreshRecentConnections());
                    Logger.Info("Connection thumbnail updated for {0}: {1}", Host.Trim(), path);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Save connection thumbnail failed");
            }
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

        /// <summary>
        /// ConnectCommand 入口：兜底 ConnectAsync 的未处理异常。
        /// 命令绑定的是 async 委托，异常若逸出会成为未观察异常并终止进程；
        /// 连接过程涉及网络、协议解析与原生解码器，必须给用户明确失败反馈。
        /// </summary>
        private async Task ConnectSafelyAsync()
        {
            try
            {
                _cancelConnectRequested = false;
                bool ok = await ConnectAsync(false);
                if (!ok)
                {
                    // 失败路径可能留下半开连接/会话对象，统一清理（不触发自动重连）
                    StopInternal(false);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Connect failed with unhandled exception");
                _notifier.ShowError("Unexpected error while connecting:\n" + ex.Message, "Connection Error");
                // 握手后、会话启动前失败会遗留传输/流会话对象，必须清理；
                // 尚未创建任何连接对象时只需复位忙碌状态。
                if (_transport != null || _streamSession != null)
                {
                    StopInternal(false);
                }
                else
                {
                    SetBusy(false, "Connection failed");
                }
            }
        }

        private async Task<bool> ConnectAsync(bool isReconnect = false)
        {
            if (_running) return false;
            _disconnecting = false;
            _isReconnectAttempt = isReconnect;
            _lastOutcomeRetryable = true;
            // D19：新连接先重置首帧状态，首帧渲染前显示"连接加载层"而非残留画面/黑屏
            IsFirstFrameRendered = false;
            if (!isReconnect) ConnectingMessage = "Connecting, waiting for the first frame…";
            SetBusy(true, isReconnect ? "Reconnecting…" : "Connecting...");

            string host = _host.Trim();
            if (!int.TryParse(_port, out int port))
                port = DefaultPort;
            if (string.IsNullOrEmpty(host))
            {
                _lastOutcomeRetryable = false;
                SetBusy(false, "Host is empty");
                return false;
            }

            var connector = new TcpTransportConnector();
            // 异步更新状态栏：OnLog 可能在接收线程触发，同步 Invoke 会阻塞接收线程
            // （TCP 接收缓冲可能被填满反压服务端），且 UI 繁忙时造成额外等待。
            connector.OnLog = (msg) => _dispatcher.BeginInvoke(() => StatusText = msg);

            Logger.Info("Connecting to {0}:{1}...", host, port);
            string endpoint = host + ":" + port;
            var connectedTransport = await Task.Run(() => connector.Connect(endpoint, ConnectTimeoutMs));
            if (connectedTransport == null)
            {
                Logger.Warn("Connection to {0}:{1} failed", host, port);
                SetBusy(false, "Connection failed");
                // 连接不成功必须有明确弹框反馈（只改状态栏文字用户看不到）；
                // 自动重连期间不弹框，避免每次退避都打断用户。
                if (!isReconnect)
                {
                    _notifier.ShowError(
                        string.Format("Unable to connect to {0}:{1}.\nPlease check:\n· the server is running (click Start)\n· the IP address and port are correct\n· the network is reachable", host, port),
                        "Connection Failed");
                }
                return false;
            }
            _transport = connectedTransport;
            Logger.Info("TCP connected to {0}:{1}, sending handshake", host, port);

            // 先订阅 MessageReceived，再 Start 接收、再发送握手 — 避免竞态导致 HandshakeRes 丢失。
            // v5 握手可能收到两种响应：AuthChallenge（口令模式）或 HandshakeRes（审批模式直接返回）。
            MessageReceivedEventArgs? handshakeResponse = null;
            MessageReceivedEventArgs? authChallengeResponse = null;
            var handshakeSignal = new ManualResetEventSlim(false);
            var challengeSignal = new ManualResetEventSlim(false);
            EventHandler<MessageReceivedEventArgs> onHandshakeMsg = (s, args) =>
            {
                if (args.MessageType == (byte)MessageType.AuthChallenge)
                {
                    authChallengeResponse = args;
                    challengeSignal.Set();
                }
                else if (args.MessageType == (byte)MessageType.HandshakeRes)
                {
                    handshakeResponse = args;
                    handshakeSignal.Set();
                }
            };
            _transport.MessageReceived += onHandshakeMsg;

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
                    if (!_running)
                    {
                        // 首次连接阶段失败：弹框告知（此时没有可恢复的会话）
                        SetBusy(false, "Stream error: " + message);
                        if (!_isReconnectAttempt)
                            _notifier.ShowError("Connection lost: " + message, "Connection Error");
                        StopInternal(false);
                        return;
                    }
                    // 已建立会话后出错：进入自动重连（不弹框，遮罩提示）
                    OnConnectionLost("stream error: " + message);
                });
            };

            // 订阅就绪后发送握手请求（v5：不携带密码，只带版本/能力/用户名/客户端随机数）
            var handshakeReq = new HandshakeReq
            {
                Version = Constants.ProtocolVersion,
                Capabilities = DecoderFactory.GetAvailableCodecs(),
                Username = _username ?? "",
                ClientNonce = AuthCrypto.RandomBytes(16)
            };
            byte[] reqPayload = handshakeReq.Pack();
            // 订阅就绪后启动接收，再发送握手请求
            _transport.Start();
            _transport.Send(Framing.BuildMessage((byte)MessageType.HandshakeReq, reqPayload));
            Logger.Debug("HandshakeReq sent, waiting for challenge or response...");

            // 第一阶段：等 AuthChallenge（口令模式）或 HandshakeRes（审批模式）。
            // 审批模式下服务端要等操作员确认（最长 60s），因此 3s 后切换提示并继续等。
            int signaled = await Task.Run(() => WaitHandle.WaitAny(
                new WaitHandle[] { challengeSignal.WaitHandle, handshakeSignal.WaitHandle }, 3000));
            if (signaled == WaitHandle.WaitTimeout)
            {
                StatusText = "Waiting for the server to accept the connection…";
                ConnectingMessage = "Waiting for the server to accept the connection…";
                Logger.Info("No handshake reply yet — server may be awaiting operator approval");
                signaled = await Task.Run(() => WaitHandle.WaitAny(
                    new WaitHandle[] { challengeSignal.WaitHandle, handshakeSignal.WaitHandle },
                    ApprovalTimeoutMs));
            }

            bool gotChallenge = signaled == 0;
            bool gotResponse = signaled == 1;

            if (gotChallenge && authChallengeResponse != null)
            {
                var challenge = AuthChallengeMessage.Unpack(authChallengeResponse.Data);
                Logger.Info("AuthChallenge received, computing proof (iterations={0})", challenge.Iterations);

                if (string.IsNullOrEmpty(_password))
                {
                    // 服务端要求口令但用户没填：直接给出可操作提示（发送空口令证明没有意义）
                    _transport.MessageReceived -= onHandshakeMsg;
                    _lastOutcomeRetryable = false;
                    SetBusy(false, "Password required");
                    if (!isReconnect)
                    {
                        _notifier.ShowError(
                            "This server requires a password.\nEnter the password configured on the server and try again.",
                            "Authentication Required");
                    }
                    _streamSession?.Stop();
                    _transport.Disconnect();
                    return false;
                }

                // 口令不上网：PBKDF2 派生密钥 + 对两个随机数做 HMAC-SHA256
                byte[] key = AuthCrypto.DeriveKey(_password, challenge.Salt, challenge.Iterations);
                byte[] proof = AuthCrypto.ComputeProof(key, challenge.ServerNonce, handshakeReq.ClientNonce, handshakeReq.Username);
                var proofMsg = new HandshakeProofMessage
                {
                    Version = Constants.ProtocolVersion,
                    Proof = proof
                };
                StatusText = "Authenticating…";
                _transport.Send(Framing.BuildMessage((byte)MessageType.HandshakeProof, proofMsg.Pack()));
                Logger.Debug("HandshakeProof sent, waiting for response...");

                bool proofAnswered = await Task.Run(() => handshakeSignal.Wait(HandshakeTimeoutMs));
                if (!proofAnswered)
                {
                    _transport.MessageReceived -= onHandshakeMsg;
                    Logger.Warn("Handshake proof timeout after {0}ms", HandshakeTimeoutMs);
                    SetBusy(false, "Handshake timeout");
                    if (!isReconnect)
                    {
                        _notifier.ShowError(
                            string.Format("Handshake with {0}:{1} timed out while authenticating.", host, port),
                            "Handshake Timeout");
                    }
                    _streamSession?.Stop();
                    _transport.Disconnect();
                    return false;
                }
                gotResponse = true;
            }

            if (_transport != null)
                _transport.MessageReceived -= onHandshakeMsg;

            if (!gotResponse || handshakeResponse == null)
            {
                Logger.Warn("Handshake timeout (challenge={0}, response={1})", gotChallenge, gotResponse);
                SetBusy(false, "Handshake timeout");
                if (!isReconnect)
                {
                    _notifier.ShowError(
                        string.Format("Handshake with {0}:{1} timed out.\nPlease verify the server version and credentials.", host, port),
                        "Handshake Timeout");
                }
                _streamSession?.Stop();
                _transport?.Disconnect();
                return false;
            }

            var handshakeRes = HandshakeRes.Unpack(handshakeResponse.Data);
            Logger.Info("Handshake response: result={0} codec={1} resolution={2}x{3}",
                handshakeRes.Result, handshakeRes.Codec, handshakeRes.ScreenWidth, handshakeRes.ScreenHeight);
            if (handshakeRes.Result != HandshakeResult.Success)
            {
                Logger.Warn("Handshake rejected: {0}", handshakeRes.Result);
                // 忙碌/内部错误可重试；认证失败、被拒绝、版本不符需用户介入
                _lastOutcomeRetryable = handshakeRes.Result == HandshakeResult.ServerBusy
                    || handshakeRes.Result == HandshakeResult.InternalError;
                SetBusy(false, "Handshake failed: " + handshakeRes.Result);
                // 认证/版本/审批失败给出可操作的提示（地址栏直连最易踩坑）
                string reason;
                switch (handshakeRes.Result)
                {
                    case HandshakeResult.AuthFailed:
                        reason = "invalid username or password (must match the USER / PASS configured on the server)";
                        break;
                    case HandshakeResult.Denied:
                        reason = "the server operator denied this connection";
                        break;
                    case HandshakeResult.VersionMismatch:
                        reason = "protocol version mismatch (update the client and server to the same build)";
                        break;
                    case HandshakeResult.ServerBusy:
                        reason = "the server is busy (too many active sessions)";
                        break;
                    default:
                        reason = handshakeRes.Result.ToString();
                        break;
                }
                if (!isReconnect)
                {
                    _notifier.ShowError(
                        string.Format("Server {0}:{1} rejected the connection: {2}", host, port, reason),
                        "Connection Failed");
                }
                else
                {
                    StatusText = "Reconnect failed: " + reason;
                }
                _streamSession?.Stop();
                _transport.Disconnect();
                return false;
            }

            // 用户已取消（关闭窗口/取消重连）：握手虽然成功也不再建立会话
            if (_cancelConnectRequested)
            {
                Logger.Info("Connect canceled by user after handshake — aborting");
                _streamSession?.Stop();
                _transport?.Disconnect();
                return false;
            }

            // 初始化渲染管线
            _renderTarget = new WpfRenderTarget();
            // 订阅 BitmapChanged：当 Resolution changed 触发 Resize 创建新 bitmap 时，
            // 同步更新 RenderBitmap 绑定，避免 Image.Source 指向旧 bitmap 导致黑屏
            _renderTarget.BitmapChanged += b => RenderBitmap = b;
            // D19：首帧像素写入后隐藏"连接加载层"、显示远程桌面（回调在 UI 线程触发）
            _renderTarget.FirstFramePresented += OnFirstFramePresented;
            _frameBuffer = new FrameBuffer();

            _streamSession.RenderTarget = _renderTarget;
            _streamSession.InitPipeline(handshakeRes.Codec, handshakeRes.ScreenWidth, handshakeRes.ScreenHeight);
            // 鼠标映射空间用内容分辨率（物理屏幕，与服务端 SetCursorPos 坐标空间一致），
            // 不能用编码分辨率——D11 降档后二者不同，混用会导致鼠标落点整体缩放偏移。
            // 旧服务端握手缺 Content* 字段（值 0）时回退到 ScreenWidth/Height。
            _remoteScreenWidth = handshakeRes.ContentWidth > 0
                ? handshakeRes.ContentWidth : handshakeRes.ScreenWidth;
            _remoteScreenHeight = handshakeRes.ContentHeight > 0
                ? handshakeRes.ContentHeight : handshakeRes.ScreenHeight;
            // 订阅远程光标更新（形状 + 位置）：WpfRenderTarget.UpdateCursor 在接收线程触发，
            // MainWindow 订阅 RemoteCursorChanged 后在 UI 线程渲染光标叠加层
            _renderTarget.CursorChanged += OnRemoteCursorChanged;
            // 订阅服务端→客户端剪贴板同步事件：服务端用户复制 → 客户端自动设置本地剪贴板
            _streamSession.ClipboardReceived += OnClipboardReceivedFromServer;
            // 订阅文件剪贴板同步事件：服务端用户复制文件 → 客户端写入临时目录并设置 CF_HDROP
            // 文件剪贴板：服务端复制文件时只发元信息，本端粘贴时才下载（延迟渲染）
            _streamSession.FilePasteOffered += OnFilePasteOffered;
            _streamSession.FileChannelSend = SendFileMessage;
            // 订阅文件剪贴板下载进度事件：更新 UI 进度条
            _streamSession.FileClipboardProgress += OnFileClipboardProgress;
            // 订阅图片剪贴板同步事件：服务端用户复制图片 → 客户端设置 CF_DIB
            _streamSession.ImageClipboardReceived += OnImageClipboardReceivedFromServer;
            // 订阅服务端分辨率变化：同步坐标映射与显示尺寸，避免映射陈旧导致鼠标落点偏移
            _streamSession.ResolutionChanged += OnRemoteResolutionChanged;

            // 必须在 InitPipeline（调用 Resize）之后才赋值 Bitmap
            RenderBitmap = _renderTarget.Bitmap;

            _inputSession = new ClientInputSession();
            // 输入会话映射空间同样用内容分辨率（与 _remoteScreenWidth/Height 一致）
            _inputSession.Start(_transport, _remoteScreenWidth, _remoteScreenHeight);

            _streamSession.Start(_transport);
            Logger.Info("Client stream session started, codec={0} resolution={1}x{2}",
                handshakeRes.Codec, handshakeRes.ScreenWidth, handshakeRes.ScreenHeight);
            _running = true;
            IsConnected = true;
            // 顶栏/Details 只读摘要（远程界面不再显示连接参数输入）
            ConnectionSummary = string.IsNullOrWhiteSpace(_username)
                ? string.Format("{0}:{1}", Host, _port)
                : string.Format("{0}:{1}  ·  {2}", Host, _port, _username);
            ConnectionThumbnail = ThumbnailCache.LoadThumbnail(Host);
            SendFileCommand.RaiseCanExecuteChanged();
            // 文件传输：握手 token 打开独立通道；通道建立失败时自动回退主连接
            StartFileTransfer(host, port.ToString(), handshakeRes.SessionToken);
            // 记录最近连接（持久化 + 主页置顶）：连同本次使用的端口/用户名/密码，
            // 地址栏直连成功后最近连接条目不再只有 IP（否则下次连接凭据丢失）
            _recentStore.Touch(Host, SelectedProfile?.Name, _port, _username, _password);
            RefreshRecentConnections();
            // 3 秒后捕获一帧保存为缩略图（等画面稳定）
            Task.Run(async () => { await Task.Delay(3000); SaveConnectionThumbnail(); });
            // 订阅服务端诊断信息（连接详情面板数据源），并立即发送请求。
            // 服务端响应异步到达，面板刷新定时器读取 ServerDiagnosticInfo。
            _streamSession.DiagnosticInfoReceived += OnDiagnosticInfoReceived;
            SendDiagnosticInfoRequest();
            // 连接详情面板统计基准：连接起点 + 码率差分归零
            _connectStartTime = DateTime.UtcNow;
            _lastReceivedBytes = 0;
            // 回放握手期间缓冲的初始光标状态（含形状位图）：此时光标事件已挂接、IsConnected 已置位，
            // MainWindow 能正常渲染远程光标，否则客户端只更新位置、永远没有光标位图可显示。
            _streamSession?.FlushPendingCursor();
            CodecName = handshakeRes.Codec.ToString();
            FrameSize = string.Format("{0}x{1}", handshakeRes.ScreenWidth, handshakeRes.ScreenHeight);

            // 监听断连事件：非用户触发的断开进入自动重连
            EventHandler? onDisconnected = null;
            onDisconnected = (s, ev) =>
            {
                if (_disconnecting) return; // 防止重入
                _transport.Disconnected -= onDisconnected;
                // BeginInvoke：不阻塞传输接收线程，避免 Stop 期间 Join 与接收线程互等
                _dispatcher.BeginInvoke(() => OnConnectionLost("transport closed"));
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
                RefreshDiagnostics();
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
            // 剪贴板变化改为及时通知：MainWindow 注册 AddClipboardFormatListener，
            // 收到 WM_CLIPBOARDUPDATE 后调用 NotifyLocalClipboardChanged()；
            // 定时器降为 5 秒兜底（监听不可用时仍能同步，且远低于原先 800ms 的干扰频率）。
            _clipboardTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5000) };
            int clipboardTimerTickCount = 0;
            _clipboardTimer.Tick += (s, ev) =>
            {
                clipboardTimerTickCount++;
                if (clipboardTimerTickCount % 12 == 0) // 每 ~60 秒一条心跳，确认监控仍在工作
                    Logger.Debug("Clipboard fallback timer tick #{0}", clipboardTimerTickCount);
                CheckLocalClipboard();
            };
            _clipboardTimer.Start();
            Logger.Info("Clipboard listener + fallback timer started (interval=5000ms)");

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
                    // Keepalive payload 携带发送时刻时间戳（UtcNow.Ticks，8 字节）。
                    // 服务端收到后原样回显 payload，客户端据此测量 RTT；
                    // 同时刷新服务端 _lastActivity 保持连接活跃。
                    byte[] payload = BitConverter.GetBytes(DateTime.UtcNow.Ticks);
                    transport.Send(Framing.BuildMessage((byte)MessageType.Keepalive, payload));
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
            // 连接成功：复位重连上下文（重连循环据此结束）
            _isReconnectAttempt = false;
            _lastOutcomeRetryable = true;
            IsReconnecting = false;
            ConnectingMessage = "Connecting, waiting for the first frame…";
            return true;
        }

        /// <summary>
        /// 发送诊断信息请求（空 payload）。服务端收到后回发 DiagnosticInfo。
        /// </summary>
        private void SendDiagnosticInfoRequest()
        {
            var transport = _transport;
            if (transport == null || !_running) return;
            try
            {
                transport.Send(Framing.BuildMessage((byte)MessageType.DiagnosticInfoRequest, new byte[0]));
                Logger.Debug("DiagnosticInfoRequest sent");
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "DiagnosticInfoRequest send failed");
            }
        }

        /// <summary>接收线程回调：服务端诊断信息到达时保存（供连接详情面板绑定）。</summary>
        private void OnDiagnosticInfoReceived(DiagnosticInfoMessage info)
        {
            // 引用赋值原子，接收线程写、UI 线程读无需锁
            ServerDiagnosticInfo = info;
            Logger.Info("DiagnosticInfo received: cpu={0} gpu={1} memMB={2} os={3}",
                info.CpuName, info.GpuName, info.TotalMemoryMb, info.OsVersion);
        }

        /// <summary>切换连接详情面板可见性。</summary>
        private void ToggleDetailsPanel()
        {
            IsDetailsPanelVisible = !IsDetailsPanelVisible;
        }

        /// <summary>折叠/展开详情面板（"—"收起按钮与折叠角标共用命令）。</summary>
        private void ToggleDetailsCollapse()
        {
            IsDetailsPanelCollapsed = !IsDetailsPanelCollapsed;
        }

        /// <summary>
        /// 每秒刷新连接详情面板数据（复用 FPS DispatcherTimer 的 Tick）。
        /// 网络诊断：连接时长/码率/RTT/丢帧率；连接详情：采集方式/分辨率/编解码器；
        /// 系统性能：本地（构造时缓存）+ 远端（DiagnosticInfo 到达后）。
        /// </summary>
        private void RefreshDiagnostics()
        {
            var session = _streamSession;
            if (session == null)
            {
                _connectionDurationText = "—";
                _receiveRateText = "—";
                OnPropertyChanged(nameof(ConnectionDurationText));
                OnPropertyChanged(nameof(ReceiveRateText));
                return;
            }

            // 连接时长
            _connectionDurationText = FormatDuration(DateTime.UtcNow - _connectStartTime);

            // 码率：接收字节差分 / 秒
            long bytes = session.ReceivedBytes;
            long delta = bytes - _lastReceivedBytes;
            _lastReceivedBytes = bytes;
            if (delta > 0)
                _receiveRateText = string.Format("{0:F1} KB/s", delta / 1024.0);
            else if (bytes > 0)
                _receiveRateText = "0.0 KB/s";

            // RTT / 丢帧率
            _rttText = session.LastRttMs >= 0 ? session.LastRttMs + " ms" : "—";
            _lossRateText = string.Format("{0:P1}", session.PacketLossRate);

            // 连接详情：编解码器（CodecName 如 "ZRLE"；本端仅解码，统一标注软件解码）
            _codecDetailText = CodecName + " / software decode";

            // 远端系统信息（DiagnosticInfo 到达后填充）
            var diag = ServerDiagnosticInfo;
            if (diag != null)
            {
                _captureMethodText = diag.CaptureMethod == 1 ? "DXGI" : "BitBlt";
                _remoteResolutionText = string.Format("{0}×{1} Scale {2}%",
                    diag.ScreenWidth, diag.ScreenHeight, diag.ScaleFactorX100);
                _remoteCpuText = string.IsNullOrEmpty(diag.CpuName) ? "—"
                    : diag.CpuName + " (" + diag.CpuCores + " cores)";
                _remoteGpuText = string.IsNullOrEmpty(diag.GpuName) ? "—" : diag.GpuName;
                _remoteMemoryText = diag.TotalMemoryMb > 0
                    ? string.Format("{0:F1} GB", diag.TotalMemoryMb / 1024.0) : "—";
                _remoteOsText = string.IsNullOrEmpty(diag.OsVersion) ? "—" : diag.OsVersion;
            }

            OnPropertyChanged(nameof(ConnectionDurationText));
            OnPropertyChanged(nameof(ReceiveRateText));
            OnPropertyChanged(nameof(RttText));
            OnPropertyChanged(nameof(LossRateText));
            OnPropertyChanged(nameof(CaptureMethodText));
            OnPropertyChanged(nameof(RemoteResolutionText));
            OnPropertyChanged(nameof(CodecDetailText));
            OnPropertyChanged(nameof(RemoteCpuText));
            OnPropertyChanged(nameof(RemoteGpuText));
            OnPropertyChanged(nameof(RemoteMemoryText));
            OnPropertyChanged(nameof(RemoteOsText));
        }

        /// <summary>格式化为 "HH:MM:SS"（超 1 小时）或 "MM:SS"。</summary>
        private static string FormatDuration(TimeSpan ts)
        {
            return ts.TotalHours >= 1
                ? string.Format("{0:00}:{1:00}:{2:00}", (int)ts.TotalHours, ts.Minutes, ts.Seconds)
                : string.Format("{0:00}:{1:00}", ts.Minutes, ts.Seconds);
        }

        /// <summary>本地 CPU 描述：型号标识 + 核心数。</summary>
        private static string GetLocalCpuText()
        {
            try
            {
                string id = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
                if (!string.IsNullOrEmpty(id))
                    return id + " (" + Environment.ProcessorCount + " cores)";
            }
            catch { }
            return Environment.ProcessorCount + " cores";
        }

        /// <summary>本地物理内存描述（GlobalMemoryStatusEx）。</summary>
        private static string GetLocalMemoryText()
        {
            try
            {
                var status = new MEMORYSTATUSEX();
                status.dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                if (GlobalMemoryStatusEx(ref status))
                    return string.Format("{0:F1} GB", status.ullTotalPhys / (1024.0 * 1024.0 * 1024.0));
            }
            catch { }
            return "—";
        }

        /// <summary>本地 GPU 名称（显示类驱动注册表 DriverDesc）。</summary>
        private static string GetLocalGpuName()
        {
            try
            {
                const string displayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(displayClass))
                {
                    if (key == null) return "—";
                    for (int i = 0; i <= 9; i++)
                    {
                        using (var sub = key.OpenSubKey(i.ToString("D4")))
                        {
                            if (sub == null) continue;
                            string desc = sub.GetValue("DriverDesc") as string;
                            if (!string.IsNullOrEmpty(desc))
                                return desc;
                        }
                    }
                }
            }
            catch { }
            return "—";
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        /// <summary>
        /// 发送剪贴板同步消息到服务端。文本作为完整消息发送，
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
                _transport.Send(Framing.BuildMessage((byte)MessageType.ClipboardSync, payload));
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
                        // 优先文件通道（不与视频流竞争），回退主连接
                        if (!SendFileMessage((byte)MessageType.ClipFileContentsRes, payload))
                            transport.Send(Framing.BuildMessage((byte)MessageType.ClipFileContentsRes, payload));
                    });
                _streamSession?.SetFileClipboardProvider(provider);

                // 3) 发送 ClipFormatList（仅元信息，几百字节）
                var listMsg = new ClipFormatListMessage
                {
                    TransferId = transferId,
                    Files = metaList
                };
                byte[] listPayload = listMsg.Pack();
                transport.Send(Framing.BuildMessage((byte)MessageType.ClipFormatList, listPayload));
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
            _dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    // 文本剪贴板覆盖文件占位：放弃延迟渲染
                    ClearPendingFileClipboard("remote-text-clipboard", true);
                    // 先更新 lastClipboardText，避免 _clipboardTimer 检测到变化触发回发
                    _lastClipboardText = text;
                    TrySetClipboard(() => Clipboard.SetText(text), "text clipboard");
                    Logger.Info("Clipboard set from server: len={0}", text.Length);
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "Clipboard set from server failed");
                }
            }));
        }

        /// <summary>
        /// 服务端复制了文件（仅元信息到达）：登记 consumer 并在剪贴板放置延迟渲染占位。
        /// 真正的下载推迟到用户粘贴时（WM_RENDERFORMAT 或 Ctrl+V 触发的渲染请求）。
        /// </summary>
        private void OnFilePasteOffered(uint transferId)
        {
            if (!_running) return;
            _dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_running) return;
                var session = _streamSession;
                var consumer = session != null ? session.GetClipConsumer(transferId) : null;
                if (consumer == null)
                {
                    Logger.Warn("File paste offered but consumer missing: transferId={0}", transferId);
                    return;
                }

                if (ClipboardOwnerHandle == IntPtr.Zero)
                {
                    Logger.Warn("File paste offered before the window handle was available");
                    return;
                }

                // 取消上一个尚未消费的占位（用户连续复制）
                ClearPendingFileClipboard("superseded", true);
                _pendingClipConsumer = consumer;
                _pendingClipTransferId = transferId;

                if (ClipboardNative.SetDelayedPlaceholder(ClipboardOwnerHandle))
                {
                    _pendingClipActive = true;
                    _pendingClipInstalledTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                    // 防回环：标记来源 + 签名占位，CheckLocalClipboard 期间直接跳过
                    EasyRDP.Core.ClipboardOwnerHelper.SetOwnerFlag(EasyRDP.Core.ClipboardOwnerHelper.SideClient);
                    _lastClipboardFilesSig = "__pending_clipboard_files__";
                    Logger.Info("Delayed file clipboard placeholder installed (transferId={0})", transferId);
                }
                else
                {
                    Logger.Warn("Failed to install the delayed file clipboard placeholder (transferId={0})", transferId);
                    _pendingClipConsumer = null;
                }
            }));
        }

        /// <summary>
        /// 处理 WM_RENDERFORMAT（用户粘贴时才索要数据）：下载文件并填充真实 CF_HDROP。
        /// 必须同步完成——系统当前已开启剪贴板会话。
        /// </summary>
        /// <returns>true 表示已处理（调用方不应再调用默认窗口过程）。</returns>
        public bool HandleClipboardRenderRequest(uint format)
        {
            if (format != (uint)EasyRDP.Core.Clipboard.DropFilesBuilder.CF_HDROP) return false;
            if (!_pendingClipActive) return false;

            // 复制后极短时间内的渲染请求来自系统剪贴板服务（Win11 剪贴板历史会在
            // 剪贴板变化瞬间读取一次），不是用户粘贴：不下载、不提供数据，
            // 保持延迟占位，等真正的粘贴请求（稍后再次触发）再下载。
            long elapsedMs = (long)((System.Diagnostics.Stopwatch.GetTimestamp() - _pendingClipInstalledTicks)
                * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            if (elapsedMs >= 0 && elapsedMs < ClipboardProbeSuppressMs)
            {
                Logger.Info("Clipboard render ignored: system probe {0}ms after copy (not a paste)", elapsedMs);
                return true;
            }

            // 同一剪贴板数据可能被多处同时读取（系统剪贴板服务/本程序轮询），只处理一次
            if (_renderingClipboardFiles) return false;

            var consumer = _pendingClipConsumer;
            if (consumer == null)
            {
                ClearPendingFileClipboard("render-without-consumer", false);
                return false;
            }

            _renderingClipboardFiles = true;
            try
            {
                return RenderPendingFileClipboardCore(consumer);
            }
            finally
            {
                _renderingClipboardFiles = false;
            }
        }

        /// <summary>实际执行：等待下载完成并把真实 CF_HDROP 交给系统（已在渲染会话中）。</summary>
        private bool RenderPendingFileClipboardCore(FileClipboardConsumer consumer)
        {
            Logger.Info("Clipboard render requested: downloading files (transferId={0})", _pendingClipTransferId);
            string[] paths;
            if (!consumer.EnsureDownloaded(PasteFastWaitMs, out paths))
            {
                // 未就绪：弹模态进度窗（内部泵消息，下载线程继续跑），传输结束自动关闭
                IsClipboardTransferring = true;
                ClipboardProgressText = "Preparing files for paste…";
                ClipboardProgressValue = 0;
                try
                {
                    var progress = new Views.FilePasteProgressWindow(this, () => consumer.IsFinished);
                    if (Application.Current != null && Application.Current.MainWindow != null)
                        progress.Owner = Application.Current.MainWindow;
                    progress.ShowDialog();
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "File paste progress window failed");
                }
                if (!consumer.EnsureDownloaded(0, out paths))
                {
                    Logger.Warn("Clipboard render: file download failed or timed out");
                    ClearPendingFileClipboard("render-failed", true);
                    IsClipboardTransferring = false;
                    ClipboardProgressText = "";
                    ClipboardProgressValue = 0;
                    return false;
                }
            }

            IntPtr hDrop = EasyRDP.Core.Clipboard.DropFilesBuilder.Build(paths);
            if (hDrop == IntPtr.Zero)
            {
                ClearPendingFileClipboard("build-dropfiles-failed", true);
                return false;
            }
            if (!ClipboardNative.SetFilesInRenderSession(hDrop))
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(hDrop);
                Logger.Warn("Clipboard render: SetClipboardData failed (err={0})",
                    System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                ClearPendingFileClipboard("set-clipboard-data-failed", true);
                return false;
            }

            _pendingClipActive = false;
            _pendingClipConsumer = null;
            // 此刻剪贴板由系统打开（渲染会话），不能调用 SetOwnerFlag；用签名防回环即可
            _lastClipboardFilesSig = string.Join("|", paths);
            IsClipboardTransferring = false;
            ClipboardProgressText = "";
            ClipboardProgressValue = 0;
            Logger.Info("Clipboard render: {0} file(s) provided", paths.Length);
            foreach (string p in paths)
                Logger.Info("  - {0}", p);
            return true;
        }

        /// <summary>WM_RENDERALLFORMATS：即将失去剪贴板所有权，放弃尚未渲染的占位。</summary>
        public void HandleClipboardRenderAllFormats()
        {
            if (!_pendingClipActive) return;
            ClearPendingFileClipboard("render-all-formats", true);
        }

        /// <summary>清理延迟渲染状态；cancelConsumer=true 时取消尚未开始的下载。</summary>
        private void ClearPendingFileClipboard(string reason, bool cancelConsumer)
        {
            var consumer = _pendingClipConsumer;
            _pendingClipConsumer = null;
            _pendingClipTransferId = 0;
            bool wasActive = _pendingClipActive;
            _pendingClipActive = false;
            // 占位仍在剪贴板上但已无数据来源：移除，避免粘贴拿到永远渲染不出来的空 CF_HDROP
            if (wasActive && ClipboardOwnerHandle != IntPtr.Zero)
                ClipboardNative.ClearIfOwner(ClipboardOwnerHandle);
            if (consumer != null && cancelConsumer)
            {
                try { consumer.Cancel(); } catch { }
            }
            if (wasActive || consumer != null)
                Logger.Info("Pending file clipboard cleared: {0}", reason);
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
                ClipboardProgressText = string.Format("Transferring: {0:F1}% ({1} / {2})",
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
        /// 检查本地剪贴板变化（文本/文件/图片），变化时同步到服务端。
        /// 由 WM_CLIPBOARDUPDATE 通知（及时）或 5 秒兜底定时器触发；必须在 STA/UI 线程调用。
        /// </summary>
        private bool _checkingClipboard;

        private void CheckLocalClipboard()
        {
            if (_transport == null || !_running) return;
            // 重入保护：剪贴板 API 内部可能泵消息，WM_CLIPBOARDUPDATE 可能在检查中途再次进入
            if (_checkingClipboard) return;
            _checkingClipboard = true;
            try
            {
                // 延迟渲染占位期间禁止读取剪贴板（ContainsFileDropList 会让系统索要数据）。
                // 若剪贴板已被接管，则放弃占位；若接管方是本端/对端同步的内容（有 owner 标记），
                // 也不能读取——读取会触发对端的 WM_RENDERFORMAT，导致"没粘贴就开始传输"。
                if (_pendingClipActive)
                {
                    if (ClipboardOwnerHandle != IntPtr.Zero
                        && ClipboardNative.GetOwner() == ClipboardOwnerHandle)
                        return;
                    ClearPendingFileClipboard("clipboard-taken-by-other-app", true);
                    if (EasyRDP.Core.ClipboardOwnerHelper.GetOwnerFlag()
                        != EasyRDP.Core.ClipboardOwnerHelper.SideNone)
                        return;
                }

                bool hasText = Clipboard.ContainsText();
                bool hasFiles = Clipboard.ContainsFileDropList();
                bool hasImage = Clipboard.ContainsImage();

                // 优先处理文件剪贴板（CF_HDROP）：用户右键复制文件时触发
                if (hasFiles)
                {
                    // Owner Flag 防回环：只要是 EasyRDP 同步过来的剪贴板（任一端标记），
                    // 都既不回传也不读取——读取本身会触发对端的延迟渲染下载。
                    byte owner = EasyRDP.Core.ClipboardOwnerHelper.GetOwnerFlag();
                    if (owner != EasyRDP.Core.ClipboardOwnerHelper.SideNone)
                    {
                        return;
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
                // 剪贴板被其他进程锁定时会抛 ExternalException — 记录但不停摆
                Logger.Warn(ex, "Clipboard check failed");
            }
            finally
            {
                _checkingClipboard = false;
            }
        }

        /// <summary>
        /// 剪贴板变化通知入口：MainWindow 收到 WM_CLIPBOARDUPDATE 后调用（UI 线程）。
        /// 及时性优于定时轮询：复制/剪切后立即同步到服务端。
        /// </summary>
        public void NotifyLocalClipboardChanged()
        {
            // CheckLocalClipboard 内部已捕获所有异常
            CheckLocalClipboard();
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
                _transport.Send(Framing.BuildMessage((byte)MessageType.ImageClipboardStart, startPayload));
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
                    _transport.Send(Framing.BuildMessage((byte)MessageType.ImageClipboardData, dataPayload));
                    offset += chunkLen;
                    chunkCount++;
                }
                Logger.Info("ImageClipboardData sent: transferId={0} chunks={1} totalBytes={2}",
                    transferId, chunkCount, dibBytes.Length);

                // 3) 发送 End
                var endMsg = new ImageClipboardEndMessage { TransferId = transferId };
                byte[] endPayload = endMsg.Pack();
                _transport.Send(Framing.BuildMessage((byte)MessageType.ImageClipboardEnd, endPayload));
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
                    // 图片剪贴板覆盖文件占位：放弃延迟渲染
                    ClearPendingFileClipboard("remote-image-clipboard", true);
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
        /// 内容坐标空间变化回调（解码线程触发）——即服务端物理屏幕尺寸。
        /// 只更新鼠标映射空间。视频显示尺寸（可能被 D11 降采样）由 ClientStreamSession
        /// 直接调整渲染目标，不经过此回调，避免降档后鼠标落点整体缩放偏移。
        /// MapCoordinates 用内容分辨率计算 aspect ratio 与坐标缩放，
        /// 与服务端 SetCursorPos 的物理像素坐标空间严格一致。
        /// </summary>
        private void OnRemoteResolutionChanged(int width, int height)
        {
            if (width <= 0 || height <= 0) return;
            if (width == _remoteScreenWidth && height == _remoteScreenHeight) return;
            Logger.Info("Content resolution changed: {0}x{1} (mouse mapping space updated from {2}x{3})",
                width, height, _remoteScreenWidth, _remoteScreenHeight);
            _remoteScreenWidth = width;
            _remoteScreenHeight = height;
            _inputSession?.OnResolutionChanged(width, height);
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

        // ====== Stop / 断线重连 ======

        /// <summary>用户主动断开（Stop 按钮）：取消自动重连并清理会话。</summary>
        private void Stop()
        {
            CancelReconnect();
            StopInternal(true);
        }

        /// <summary>关闭控制窗口时调用：停止自动重连并确保连接被释放。</summary>
        public void DisconnectAndStop()
        {
            CancelReconnect();
            StopInternal(true);
        }

        /// <summary>取消进行中的自动重连（加载层 Cancel 按钮）。</summary>
        private void CancelReconnect()
        {
            _cancelConnectRequested = true;
            var cts = _reconnectCts;
            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
            }
        }

        /// <summary>
        /// 非用户触发的连接丢失：清理旧会话后进入自动重连（不弹框）。
        /// </summary>
        private void OnConnectionLost(string reason)
        {
            if (_disconnecting || _reconnecting) return;
            if (!_running && !_isConnected) return; // 没有可恢复的会话
            Logger.Warn("Connection lost ({0}) — starting auto reconnect", reason);
            StopInternal(false);
            StartReconnectLoop();
        }

        /// <summary>
        /// 自动重连循环：退避 1/2/4/8/10… 秒重试，直到成功、用户取消，
        /// 或遇到不可重试的失败（认证失败/被拒绝/版本不符）。
        /// </summary>
        private async void StartReconnectLoop()
        {
            _reconnectAttempt = 0;
            _reconnectCts = new CancellationTokenSource();
            CancellationToken token = _reconnectCts.Token;
            IsReconnecting = true;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    _reconnectAttempt++;
                    int delayMs = ReconnectDelayMs(_reconnectAttempt);
                    string text = string.Format("Connection lost — reconnecting in {0:F0}s (attempt {1})…",
                        delayMs / 1000.0, _reconnectAttempt);
                    ConnectingMessage = text;
                    StatusText = text;
                    Logger.Info("Reconnect attempt {0} scheduled in {1}ms", _reconnectAttempt, delayMs);
                    await Task.Delay(delayMs, token);
                    if (token.IsCancellationRequested) break;

                    ConnectingMessage = string.Format("Reconnecting (attempt {0})…", _reconnectAttempt);
                    bool ok;
                    try
                    {
                        ok = await ConnectAsync(true);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, "Reconnect attempt {0} threw", _reconnectAttempt);
                        ok = false;
                        _lastOutcomeRetryable = true;
                    }

                    if (ok)
                    {
                        Logger.Info("Reconnected successfully after {0} attempt(s)", _reconnectAttempt);
                        return;
                    }
                    // 失败后清理半成品连接，避免下次尝试复用残留对象
                    StopInternal(false);
                    if (!_lastOutcomeRetryable)
                    {
                        Logger.Warn("Reconnect aborted: non-retryable failure");
                        SetBusy(false, "Reconnect failed");
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 用户取消：正常退出
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Reconnect loop failed");
            }
            finally
            {
                IsReconnecting = false;
                var cts = _reconnectCts;
                _reconnectCts = null;
                if (cts != null)
                {
                    try { cts.Dispose(); } catch { }
                }
                if (token.IsCancellationRequested)
                {
                    // 取消时可能已建立连接（取消发生在握手过程中），确保断开
                    StopInternal(true);
                }
                if (!_running)
                {
                    ConnectingMessage = "Connecting, waiting for the first frame…";
                    SetBusy(false, "Disconnected");
                }
            }
        }

        /// <summary>指数退避：1、2、4、8、10、10… 秒。</summary>
        private static int ReconnectDelayMs(int attempt)
        {
            int shift = Math.Min(Math.Max(attempt - 1, 0), 4);
            int ms = 1000 << shift;
            return Math.Min(ms, MaxReconnectDelayMs);
        }

        // ====== 文件传输（显式发送 / 接收） ======

        /// <summary>
        /// 连接成功后初始化文件传输：订阅主连接上的文件消息，并异步打开独立文件通道。
        /// 通道建立失败时 SendFileMessage 自动回退主连接（功能降级但仍可用）。
        /// </summary>
        private void StartFileTransfer(string host, string port, byte[] sessionToken)
        {
            if (_streamSession == null) return;

            var transfer = new EasyRDP.Core.Transfer.FileTransferSession(
                SendFileMessage,
                null, // 客户端没有服务端式的发送队列积压信号，调速只依赖 Send 阻塞耗时
                ResolveReceivedDirectory());
            // 进度节流到 10Hz：每 256KB 一块都投递会挤占渲染线程（传文件时画面掉帧）
            long lastProgressTicks = 0;
            transfer.ProgressChanged += p =>
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (now - lastProgressTicks < System.Diagnostics.Stopwatch.Frequency / 10)
                    return;
                lastProgressTicks = now;
                _dispatcher.BeginInvoke(new Action(() => OnFileTransferProgress(p)));
            };
            transfer.IncomingCompleted += (id, paths) =>
                _dispatcher.BeginInvoke(new Action(() => OnFilesReceived(paths)));
            transfer.OutgoingCompleted += id =>
                _dispatcher.BeginInvoke(new Action(() => OnFilesSentToRemote()));
            transfer.Failed += (id, reason, outgoing) =>
                _dispatcher.BeginInvoke(new Action(() => OnFileTransferError(reason, outgoing)));
            _fileTransfer = transfer;
            _streamSession.FileTransferMessageReceived += OnFileTransferMessage;

            Task.Run(() => OpenFileChannel(host, port, sessionToken));
        }

        /// <summary>主连接/文件通道上收到的文件或剪贴板文件分块消息。</summary>
        private void OnFileTransferMessage(byte messageType, byte[] payload)
        {
            if (messageType == (byte)MessageType.ClipFileContentsReq
                || messageType == (byte)MessageType.ClipFileContentsRes)
            {
                _streamSession?.HandleClipboardChannelMessage(messageType, payload);
                return;
            }
            var transfer = _fileTransfer;
            if (transfer != null) transfer.HandleMessage(messageType, payload);
        }

        /// <summary>发送文件协议消息：优先文件通道，未建立时走主连接。</summary>
        private bool SendFileMessage(byte messageType, byte[] payload)
        {
            ITransport? channel = _fileChannel;
            ITransport? transport = channel != null && channel.IsConnected ? channel : _transport;
            if (transport == null) return false;
            try
            {
                transport.Send(Framing.BuildMessage(messageType, payload));
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "File message send failed (type=0x{0:X2})", messageType);
                return false;
            }
        }

        /// <summary>用握手 token 打开独立文件通道（后台线程，最多等 2 秒）。</summary>
        private void OpenFileChannel(string host, string port, byte[] sessionToken)
        {
            if (sessionToken == null || sessionToken.Length == 0) return;
            string endpoint = host + ":" + port;

            ITransport? channel = null;
            try
            {
                var connector = new TcpTransportConnector();
                channel = connector.Connect(endpoint, 3000);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "File channel connect failed");
            }
            if (channel == null)
            {
                Logger.Warn("File channel unavailable for {0} — file transfer will use the main connection", endpoint);
                return;
            }

            var opened = new ManualResetEventSlim(false);
            ChannelOpenResMessage? response = null;
            channel.MessageReceived += (s, e) =>
            {
                if (e.MessageType == (byte)MessageType.ChannelOpenRes)
                {
                    try { response = ChannelOpenResMessage.Unpack(e.Data); }
                    catch (Exception ex) { Logger.Warn(ex, "ChannelOpenRes unpack failed"); }
                    opened.Set();
                }
                else
                {
                    OnFileTransferMessage(e.MessageType, e.Data);
                }
            };
            channel.Disconnected += (s, e) =>
            {
                if (ReferenceEquals(_fileChannel, channel))
                {
                    _fileChannel = null;
                    Logger.Info("File channel closed — falling back to the main connection");
                }
            };
            channel.Start();

            var req = new ChannelOpenReqMessage
            {
                Version = Constants.ProtocolVersion,
                ChannelType = ChannelType.FileTransfer,
                Token = sessionToken
            };
            channel.Send(Framing.BuildMessage((byte)MessageType.ChannelOpenReq, req.Pack()));

            bool received = opened.Wait(2000);
            opened.Dispose();
            if (!received || response == null || response.Result != ChannelOpenResult.Ok)
            {
                Logger.Warn("File channel open failed (received={0}, result={1}) — using the main connection",
                    received, response != null ? response.Result.ToString() : "none");
                try { channel.Disconnect(); } catch { }
                return;
            }
            if (!_running)
            {
                try { channel.Disconnect(); } catch { }
                return;
            }

            _fileChannel = channel;
            Logger.Info("File channel ready (session {0})", response.SessionId);
        }

        /// <summary>界面按钮入口：多选文件后发送。</summary>
        private void SendFilesByDialog()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select files to send to the remote computer",
                Multiselect = true,
                CheckFileExists = true
            };
            if (dialog.ShowDialog() != true) return;
            SendFiles(dialog.FileNames);
        }

        /// <summary>发送文件到远程桌面（按钮与拖拽到画面共用）。</summary>
        public void SendFiles(string[] paths)
        {
            if (paths == null || paths.Length == 0) return;
            var transfer = _fileTransfer;
            if (transfer == null || !IsConnected)
            {
                _notifier.ShowWarning("Connect to a computer before sending files.", "Send File");
                return;
            }
            try
            {
                uint transferId = transfer.SendFiles(paths);
                Logger.Info("File transfer {0} started ({1} file(s))", transferId, paths.Length);
                ShowFileTransfer("Preparing file transfer…", 0);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "SendFiles failed");
                _notifier.ShowError(ex.Message, "Send File");
            }
        }

        /// <summary>进度回调（已切到 UI 线程）。</summary>
        private void OnFileTransferProgress(EasyRDP.Core.Transfer.FileTransferProgress progress)
        {
            if (progress == null) return;
            string direction = progress.Outgoing ? "Sending" : "Receiving";
            FileTransferText = string.Format("{0} {1} ({2}/{3}) — {4:F1} MB / {5:F1} MB · {6}",
                direction, progress.FileName, progress.FileIndex + 1, progress.FileCount,
                progress.BytesTransferred / 1048576.0, progress.TotalBytes / 1048576.0,
                FormatRate(progress.BytesPerSecond));
            FileTransferPercent = progress.TotalBytes > 0
                ? progress.BytesTransferred * 100.0 / progress.TotalBytes
                : 0;
            IsFileTransferVisible = true;
        }

        /// <summary>本端接收完成：覆盖层提示保存位置并在 5 秒后自动隐藏（不弹模态框）。</summary>
        private void OnFilesReceived(string[] paths)
        {
            if (paths == null || paths.Length == 0) return;
            Logger.Info("Received {0} file(s): {1}", paths.Length, string.Join(", ", paths));
            string folder = ResolveReceivedDirectory();
            StatusText = string.Format("Received {0} file(s) — saved to {1}", paths.Length, folder);
            ShowFileTransferNotice(string.Format("Received {0} file(s) → {1}", paths.Length, folder));
        }

        /// <summary>在传输覆盖层显示一条完成提示，5 秒后自动隐藏。</summary>
        private void ShowFileTransferNotice(string text)
        {
            ShowFileTransfer(text, 100);
            _fileTransferNoticeTimer?.Stop();
            _fileTransferNoticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _fileTransferNoticeTimer.Tick += (s, e) =>
            {
                _fileTransferNoticeTimer?.Stop();
                _fileTransferNoticeTimer = null;
                IsFileTransferVisible = false;
            };
            _fileTransferNoticeTimer.Start();
        }

        /// <summary>本端发送完成。</summary>
        private void OnFilesSentToRemote()
        {
            IsFileTransferVisible = false;
            Logger.Info("File transfer to the remote computer completed");
        }

        /// <summary>文件传输失败（任一方向）。</summary>
        private void OnFileTransferError(string reason, bool outgoing)
        {
            IsFileTransferVisible = false;
            Logger.Warn("File transfer {0} failed: {1}", outgoing ? "send" : "receive", reason);
            _notifier.ShowError(reason, outgoing ? "Send File Failed" : "Receive File Failed");
        }

        private void ShowFileTransfer(string text, double percent)
        {
            FileTransferText = text;
            FileTransferPercent = Math.Max(0, Math.Min(100, percent));
            IsFileTransferVisible = true;
        }

        /// <summary>速率格式化（MB/s 或 KB/s）。</summary>
        private static string FormatRate(double bytesPerSecond)
        {
            if (bytesPerSecond >= 1048576)
                return (bytesPerSecond / 1048576.0).ToString("F1") + " MB/s";
            return (bytesPerSecond / 1024.0).ToString("F0") + " KB/s";
        }

        /// <summary>接收目录：下载\EasyRDP Received（无下载目录时用我的文档）。</summary>
        private static string ResolveReceivedDirectory()
        {
            string baseDir = "";
            try
            {
                baseDir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            }
            catch
            {
            }
            if (string.IsNullOrEmpty(baseDir) || !System.IO.Directory.Exists(baseDir))
            {
                try { baseDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); }
                catch { }
            }
            if (string.IsNullOrEmpty(baseDir))
                baseDir = AppDomain.CurrentDomain.BaseDirectory;
            return System.IO.Path.Combine(baseDir, "EasyRDP Received");
        }

        /// <summary>清理连接与渲染资源（userInitiated=true 表示用户主动断开，不走重连）。</summary>
        private void StopInternal(bool userInitiated)
        {
            if (_disconnecting) return;
            _disconnecting = true;
            Logger.Info("Stopping client session");
            if (userInitiated)
                IsReconnecting = false;

            if (_fpsTimer != null) { _fpsTimer.Stop(); _fpsTimer = null; }
            if (_fileTransferNoticeTimer != null) { _fileTransferNoticeTimer.Stop(); _fileTransferNoticeTimer = null; }
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

            // 文件传输：先取消（会经通道通知对端），再关闭独立通道；必须在主传输断开之前
            var fileTransfer = _fileTransfer;
            _fileTransfer = null;
            if (fileTransfer != null)
            {
                try { fileTransfer.Dispose(); } catch { }
            }
            var fileChannel = _fileChannel;
            _fileChannel = null;
            if (fileChannel != null)
            {
                try { fileChannel.Disconnect(); } catch { }
            }
            IsFileTransferVisible = false;
            SendFileCommand.RaiseCanExecuteChanged();
            ConnectionThumbnail = null;

            // 剪贴板文件延迟渲染：断连后不再有数据来源，清掉占位
            ClearPendingFileClipboard("disconnect", true);
            if (ClipboardOwnerHandle != IntPtr.Zero)
                ClipboardNative.ClearIfOwner(ClipboardOwnerHandle);

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
            // D19：断开后重置首帧状态（加载层逻辑随 IsConnected=false 一并失效）
            IsFirstFrameRendered = false;
            IsConnected = false;
            FrameSize = "—";
            FrameRate = 0;
            CodecName = "—";
            // 重置剪贴板进度条状态（断开连接时隐藏）
            IsClipboardTransferring = false;
            ClipboardProgressValue = 0;
            ClipboardProgressText = "";
            SetBusy(false, "Disconnected");
            // 释放重入锁：之后可能还要发起重连尝试（ConnectAsync 亦会重置，这里保持状态一致）
            _disconnecting = false;
        }

        /// <summary>首帧像素已写入渲染位图回调（WpfRenderTarget.FirstFramePresented，UI 线程）。
        /// 置位后远程桌面区显示、连接加载层隐藏。防御性 CheckAccess：未来若事件源
        /// 改为非 UI 线程触发，自动转调度。</summary>
        private void OnFirstFramePresented()
        {
            if (_dispatcher.CheckAccess())
            {
                IsFirstFrameRendered = true;
            }
            else
            {
                _dispatcher.BeginInvoke(new Action(() => IsFirstFrameRendered = true));
            }
        }

        /// <summary>远程光标更新回调（接收线程）→ 转发为 RemoteCursorChanged 事件。</summary>
        private void OnRemoteCursorChanged(CursorInfo cursor)
        {
            // 诊断：每 20 条回显记录一次服务端光标位置，与发送坐标对比定位偏移
            if ((++_cursorEchoDiagCounter % 20) == 0)
                Logger.Debug("Cursor echo pos=({0},{1})", cursor.X, cursor.Y);
            var handler = RemoteCursorChanged;
            if (handler != null)
                handler(cursor);
        }

        // ====== 输入事件处理（由 View 代码后置调用） ======

        public void HandleMouseMove(double imageX, double imageY, double imageW, double imageH)
        {
            if (_inputSession == null || !_running) return;
            int sx, sy;
            _inputSession.MapCoordinates(imageX, imageY, imageW, imageH, out sx, out sy, ZoomMode);
            // 诊断：每 20 条记录一次本地坐标/渲染区/映射结果，与服务端 requested 对比定位点击偏移
            if ((++_mouseMoveDiagCounter % 20) == 0)
                Logger.Debug("MouseMove local=({0:F0},{1:F0}) area={2:F0}x{3:F0} mapped=({4},{5})",
                    imageX, imageY, imageW, imageH, sx, sy);
            // 节流合并：高频 MouseMove 只更新最新坐标，由 ClientInputSession 按 ~120Hz 发送
            _inputSession.QueueMouseMove(sx, sy);
        }

        /// <summary>
        /// 鼠标按下：使用**按下瞬间的精确映射坐标**（不能用最近一次节流发送的坐标——
        /// 快速移动到 1px 细线上按下时，旧坐标可能偏 1~3px，落点已经在热区之外，
        /// 表现为"鼠标形状已变成拖动/缩放，但拖不动"）。
        /// </summary>
        public void HandleMouseDown(System.Windows.Input.MouseButton changedButton,
            double imageX, double imageY, double imageW, double imageH)
        {
            if (_inputSession == null || !_running) return;
            int btn = MapMouseButton(changedButton);
            if (btn == 0) return;

            int sx, sy;
            _inputSession.MapCoordinates(imageX, imageY, imageW, imageH, out sx, out sy, ZoomMode);
            _inputSession.SetButtonDown(true);
            var msg = new InputEventMessage { Type = InputEventType.MouseDown, KeyCode = btn, X = sx, Y = sy };
            _inputSession.SendInput(msg);
            Logger.Info("Click down button={0} precise=({1},{2})", changedButton, sx, sy);
        }

        /// <summary>鼠标抬起：同样携带精确坐标，保证 Up 位置与 Down 一致。</summary>
        public void HandleMouseUp(System.Windows.Input.MouseButton changedButton,
            double imageX, double imageY, double imageW, double imageH)
        {
            if (_inputSession == null || !_running) return;
            int btn = MapMouseButton(changedButton);
            if (btn == 0) return;

            int sx, sy;
            _inputSession.MapCoordinates(imageX, imageY, imageW, imageH, out sx, out sy, ZoomMode);
            _inputSession.SetButtonDown(false);
            var msg = new InputEventMessage { Type = InputEventType.MouseUp, KeyCode = btn, X = sx, Y = sy };
            _inputSession.SendInput(msg);
            Logger.Info("Click up button={0} precise=({1},{2})", changedButton, sx, sy);
        }

        /// <summary>
        /// 带重试的剪贴板设置：CLIPBRD_E_CANT_OPEN（0x800401D0）是剪贴板被其他
        /// 进程/线程短暂占用的竞态，重试 5 次（间隔 100ms）后仍失败才记录警告。
        /// 必须在 UI 线程（STA）调用。
        /// 注意：重试中的 Sleep 会造成最多 ~400ms 的 UI 线程阻塞（仅发生在剪贴板
        /// 被占用竞态时，低于感知阈值，可接受）。
        /// </summary>
        private static void TrySetClipboard(System.Action action, string logName)
        {
            System.Runtime.InteropServices.COMException lastEx = null;
            for (int retry = 0; retry < 5; retry++)
            {
                try
                {
                    action();
                    return;
                }
                catch (System.Runtime.InteropServices.COMException ex)
                {
                    lastEx = ex;
                    if (retry < 4) System.Threading.Thread.Sleep(100);
                }
            }
            // 5 次重试仍失败：记录警告（不抛异常，避免 UI 线程崩溃）
            NLog.LogManager.GetCurrentClassLogger().Warn(lastEx,
                "Clipboard {0} set failed after 5 retries", logName);
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
            StopCommand.RaiseCanExecuteChanged();
        }

        /// <summary>当前是否处于全屏模式（ViewModel 持有的逻辑状态，View 据此执行视觉切换）。</summary>
        private bool _isFullscreen;
        public bool IsFullscreen
        {
            get { return _isFullscreen; }
            private set { _isFullscreen = value; OnPropertyChanged(nameof(IsFullscreen)); }
        }

        /// <summary>请求 View 切换全屏（参数为是否进入全屏）。由 View 订阅并执行实际窗口操作，
        /// ViewModel 不直接引用 MainWindow（严格 MVVM）。</summary>
        public event Action<bool>? FullscreenRequested;

        /// <summary>切换全屏：通知 View 执行切换（真实窗口操作由 View 层 SetFullscreenMode 完成）。</summary>
        public void ToggleFullscreen()
        {
            RequestFullscreen(!IsFullscreen);
        }

        /// <summary>退出全屏并恢复普通窗口（断开连接时也会调用，保证用户始终能切走）。</summary>
        public void ExitFullscreen()
        {
            if (!IsFullscreen) return;
            RequestFullscreen(false);
        }

        /// <summary>请求切换全屏状态：先更新逻辑状态，再通知 View 执行视觉切换。</summary>
        private void RequestFullscreen(bool fullscreen)
        {
            IsFullscreen = fullscreen;
            var handler = FullscreenRequested;
            if (handler != null) handler(fullscreen);
            Logger.Info(fullscreen ? "Entered fullscreen" : "Exited fullscreen");
        }

        // ====== INotifyPropertyChanged ======

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
