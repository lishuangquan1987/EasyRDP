using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using EasyRDP.Client.Wpf.Models;
using EasyRDP.Client.Wpf.Services;
using EasyRDP.Shared;

namespace EasyRDP.Client.Wpf.ViewModels
{
    /// <summary>
    /// 浏览窗口 ViewModel（严格 MVVM）：管理最近连接列表、地址栏解析与连接命令。
    /// 弹窗输入（Edit/Rename）经 IDialogService，提示（About/Sign in）经 IUserNotifier，
    /// 打开控制窗口经 ConnectRequested 事件、关闭窗口经 RequestClose 事件——均隔离 View 依赖。
    /// </summary>
    public class HostBrowseWindowViewModel : INotifyPropertyChanged
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        private readonly RecentConnectionStore _store;
        private readonly ConnectionProfileStore _profileStore = new ConnectionProfileStore();
        private readonly ConnectionGroupStore _groupStore = new ConnectionGroupStore();
        private readonly IDialogService _dialogs;
        private readonly IUserNotifier _notifier;
        private RecentConnection? _selected;
        // 搜索过滤（按名称/地址实时过滤树）
        private string _searchText = "";
        // 最近使用缓存（供 Recent 横条与"上次连接"展示，ReloadTree 时刷新一次）
        private List<RecentConnection> _recentCache = new List<RecentConnection>();
        // 树中选中的连接节点（右侧详情数据源）
        private ConnectionTreeNode? _selectedNode;
        // 在线探测：key = "host:port"，值为最近一次探测结果
        private readonly Dictionary<string, ServerAvailability> _availability =
            new Dictionary<string, ServerAvailability>(StringComparer.OrdinalIgnoreCase);
        private readonly System.Windows.Threading.Dispatcher _dispatcher =
            System.Windows.Threading.Dispatcher.CurrentDispatcher;
        private readonly System.Windows.Threading.DispatcherTimer _probeTimer;
        private int _onlineCount;
        private int _offlineCount;
        // 探测频控：30 秒内不重复探测（手动刷新除外）
        private long _lastProbeTicks;
        // 局域网发现：扫描状态与取消源
        private volatile bool _scanning;
        private readonly System.Threading.CancellationTokenSource _discoveryCts =
            new System.Threading.CancellationTokenSource();
        private long _lastDiscoveryTicks;

        /// <summary>最近连接列表（缩略图网格数据源）。</summary>
        public ObservableCollection<RecentConnection> Connections { get; } = new ObservableCollection<RecentConnection>();

        /// <summary>连接分组树（各分组 / 未分组）。</summary>
        public ObservableCollection<ConnectionTreeNode> Nodes { get; } = new ObservableCollection<ConnectionTreeNode>();

        /// <summary>最近使用（顶部横条，最多 8 条；已单独展示，不再进主树）。</summary>
        public ObservableCollection<RecentConnection> Recents { get; } = new ObservableCollection<RecentConnection>();

        /// <summary>局域网发现到的陌生服务端（与已配置连接按 host:port 去重）。</summary>
        public ObservableCollection<EasyRDP.Core.Discovery.DiscoveredServer> Discovered { get; } =
            new ObservableCollection<EasyRDP.Core.Discovery.DiscoveredServer>();

        /// <summary>是否正在扫描局域网。</summary>
        public bool IsScanning
        {
            get { return _scanning; }
        }

        /// <summary>发现区状态文本（Scanning… / N found / Click Scan）。</summary>
        public string DiscoveryStatusText
        {
            get
            {
                if (_scanning) return "Scanning…";
                if (Discovered.Count > 0) return Discovered.Count + " found";
                return "Click Scan";
            }
        }

        /// <summary>最近使用为空时显示占位提示。</summary>
        public bool ShowNoRecentHint
        {
            get { return Recents.Count == 0; }
        }

        /// <summary>是否显示最近使用横条。</summary>
        public bool HasRecents
        {
            get { return Recents.Count > 0; }
        }

        /// <summary>在线统计文本（如 "Online 3 · Offline 12"）。</summary>
        public string AvailabilitySummary
        {
            get { return string.Format("Online {0} · Offline {1}", _onlineCount, _offlineCount); }
        }

        /// <summary>选中连接的服务端在线状态。</summary>
        public string SelectedAvailabilityText
        {
            get { return _selectedNode != null ? _selectedNode.AvailabilityText : ""; }
        }

        /// <summary>搜索文本：按名称或地址实时过滤分组树。</summary>
        public string SearchText
        {
            get { return _searchText; }
            set
            {
                string text = value ?? "";
                if (_searchText == text) return;
                _searchText = text;
                OnPropertyChanged(nameof(SearchText));
                ReloadTree();
            }
        }

        /// <summary>树中选中的连接（右侧详情）。</summary>
        public ConnectionTreeNode? SelectedNode
        {
            get { return _selectedNode; }
            set
            {
                if (ReferenceEquals(_selectedNode, value)) return;
                _selectedNode = value;
                OnPropertyChanged(nameof(SelectedNode));
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(SelectedName));
                OnPropertyChanged(nameof(SelectedAddress));
                OnPropertyChanged(nameof(SelectedUserText));
                OnPropertyChanged(nameof(SelectedGroupText));
                OnPropertyChanged(nameof(SelectedLastConnectedText));
                OnPropertyChanged(nameof(SelectedAvailabilityText));
                OnPropertyChanged(nameof(SelectedThumbnail));
                OnPropertyChanged(nameof(HasSelectedThumbnail));
            }
        }

        /// <summary>详情区是否已选中连接。</summary>
        public bool HasSelection
        {
            get { return _selectedNode != null && !_selectedNode.IsGroup; }
        }

        /// <summary>选中连接名称。</summary>
        public string SelectedName
        {
            get { return _selectedNode != null ? _selectedNode.DisplayName : ""; }
        }

        /// <summary>选中连接地址（host:port）。</summary>
        public string SelectedAddress
        {
            get { return _selectedNode != null ? _selectedNode.Detail : ""; }
        }

        /// <summary>选中连接用户名。</summary>
        public string SelectedUserText
        {
            get
            {
                var p = _selectedNode != null ? _selectedNode.Profile : null;
                string user = p != null ? p.Username : "";
                return "User: " + (string.IsNullOrWhiteSpace(user) ? "—" : user);
            }
        }

        /// <summary>选中连接所属分组。</summary>
        public string SelectedGroupText
        {
            get
            {
                var p = _selectedNode != null ? _selectedNode.Profile : null;
                string g = p != null ? (p.Group ?? "").Trim() : "";
                return "Group: " + (g.Length == 0 ? "Ungrouped" : g);
            }
        }

        /// <summary>选中连接的上次连接时间（相对时间）。</summary>
        public string SelectedLastConnectedText
        {
            get
            {
                var p = _selectedNode != null ? _selectedNode.Profile : null;
                if (p == null) return "";
                var hit = _recentCache.FirstOrDefault(r =>
                    string.Equals(r.Host, p.Host, StringComparison.OrdinalIgnoreCase));
                return "Last used: " + (hit != null ? FormatRelative(hit.LastConnectedUtc) : "never");
            }
        }

        /// <summary>选中连接缩略图（可空）。</summary>
        public System.Windows.Media.ImageSource? SelectedThumbnail
        {
            get { return _selectedNode != null ? _selectedNode.ThumbnailSource : null; }
        }

        /// <summary>是否有缩略图可显示（无则显示占位面板）。</summary>
        public bool HasSelectedThumbnail
        {
            get { return SelectedThumbnail != null; }
        }

        // ====== 命令 ======

        /// <summary>连接指定卡片（参数为 RecentConnection）。</summary>
        public RelayCommand<RecentConnection> ConnectItemCommand { get; }

        /// <summary>编辑指定卡片连接信息。</summary>
        public RelayCommand<RecentConnection> EditItemCommand { get; }

        /// <summary>重命名指定卡片。</summary>
        public RelayCommand<RecentConnection> RenameItemCommand { get; }

        /// <summary>删除指定卡片（含缩略图缓存）。</summary>
        public RelayCommand<RecentConnection> DeleteItemCommand { get; }

        /// <summary>单选卡片选中态。</summary>
        public RelayCommand<RecentConnection> SelectItemCommand { get; }

        /// <summary>菜单 File > New Connection（弹出完整连接表单：地址/端口/用户名/密码）。</summary>
        public RelayCommand NewConnectionCommand { get; }

        /// <summary>菜单 File/View > Refresh（重载列表）。</summary>
        public RelayCommand RefreshCommand { get; }

        /// <summary>菜单 File > Exit（关闭窗口）。</summary>
        public RelayCommand ExitCommand { get; }

        /// <summary>菜单 Help > About。</summary>
        public RelayCommand AboutCommand { get; }

        /// <summary>Sign in...（视觉占位）。</summary>
        public RelayCommand SignInCommand { get; }

        /// <summary>新建分组。</summary>
        public RelayCommand NewGroupCommand { get; }

        /// <summary>连接树中的连接。</summary>
        public RelayCommand<ConnectionTreeNode> ConnectNodeCommand { get; }

        /// <summary>编辑树中的连接。</summary>
        public RelayCommand<ConnectionTreeNode> EditNodeCommand { get; }

        /// <summary>重命名树中的连接。</summary>
        public RelayCommand<ConnectionTreeNode> RenameNodeCommand { get; }

        /// <summary>删除树中的连接。</summary>
        public RelayCommand<ConnectionTreeNode> DeleteNodeCommand { get; }

        /// <summary>在分组内新建连接（参数为分组节点）。</summary>
        public RelayCommand<ConnectionTreeNode> NewConnectionInGroupCommand { get; }

        /// <summary>重命名分组。</summary>
        public RelayCommand<ConnectionTreeNode> RenameGroupCommand { get; }

        /// <summary>删除分组（组内连接移入未分组）。</summary>
        public RelayCommand<ConnectionTreeNode> DeleteGroupCommand { get; }

        /// <summary>连接右侧详情中选中的连接。</summary>
        public RelayCommand ConnectSelectedCommand { get; }

        /// <summary>编辑右侧详情中选中的连接。</summary>
        public RelayCommand EditSelectedCommand { get; }

        /// <summary>删除右侧详情中选中的连接。</summary>
        public RelayCommand DeleteSelectedCommand { get; }

        /// <summary>扫描局域网中的 EasyRDP 服务端。</summary>
        public RelayCommand DiscoverCommand { get; }

        /// <summary>把发现到的服务端添加为连接（参数为 DiscoveredServer）。</summary>
        public RelayCommand<EasyRDP.Core.Discovery.DiscoveredServer> AddDiscoveredCommand { get; }

        // ====== 视图交互事件（由 View 订阅，ViewModel 不直接引用 Window） ======

        /// <summary>请求打开控制窗口并连接（参数为连接目标）。</summary>
        public event Action<RecentConnection>? ConnectRequested;

        /// <summary>请求关闭浏览窗口。</summary>
        public event Action? RequestClose;

        public HostBrowseWindowViewModel(RecentConnectionStore store, IDialogService dialogs, IUserNotifier notifier)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
            _notifier = notifier ?? new WpfUserNotifier();

            ConnectItemCommand = new RelayCommand<RecentConnection>(ConnectItem);
            EditItemCommand = new RelayCommand<RecentConnection>(EditItem);
            RenameItemCommand = new RelayCommand<RecentConnection>(RenameItem);
            DeleteItemCommand = new RelayCommand<RecentConnection>(DeleteItem);
            SelectItemCommand = new RelayCommand<RecentConnection>(SelectItem);
            // New Connection：弹完整表单（端口/用户名/密码），确认后保存并连接。
            // 旧实现只聚焦地址栏，地址栏无凭据输入位，用户"输入 IP 后没地方填端口/账号/密码"。
            NewConnectionCommand = new RelayCommand(NewConnection);
            RefreshCommand = new RelayCommand(ReloadConnections);
            ExitCommand = new RelayCommand(() => RequestClose?.Invoke());
            AboutCommand = new RelayCommand(() =>
                _notifier.ShowInfo("EasyRDP Client\nRealVNC Viewer 风格浏览窗口", "About"));
            NewGroupCommand = new RelayCommand(NewGroup);
            ConnectNodeCommand = new RelayCommand<ConnectionTreeNode>(ConnectNode);
            EditNodeCommand = new RelayCommand<ConnectionTreeNode>(EditNode);
            RenameNodeCommand = new RelayCommand<ConnectionTreeNode>(RenameNode);
            DeleteNodeCommand = new RelayCommand<ConnectionTreeNode>(DeleteNode);
            NewConnectionInGroupCommand = new RelayCommand<ConnectionTreeNode>(NewConnectionInGroup);
            RenameGroupCommand = new RelayCommand<ConnectionTreeNode>(RenameGroup);
            DeleteGroupCommand = new RelayCommand<ConnectionTreeNode>(DeleteGroup);
            ConnectSelectedCommand = new RelayCommand(() => ConnectNode(_selectedNode));
            EditSelectedCommand = new RelayCommand(() => EditNode(_selectedNode));
            DeleteSelectedCommand = new RelayCommand(() => DeleteNode(_selectedNode));
            DiscoverCommand = new RelayCommand(() => ScanDiscovered(true), () => !_scanning);
            AddDiscoveredCommand = new RelayCommand<EasyRDP.Core.Discovery.DiscoveredServer>(AddDiscovered);

            SignInCommand = new RelayCommand(() =>
                _notifier.ShowInfo("账号登录为视觉占位（与 RealVNC 布局一致），后续版本接入。", "Sign in"));

            ReloadConnections();
            ReloadTree();

            // 在线状态：立即探测一次，之后每 30 秒刷新（TCP 连接测试，不占会话名额）
            _probeTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(30)
            };
            _probeTimer.Tick += (s2, e2) =>
            {
                RefreshAvailability();
                ScanDiscovered(false);
            };
            _probeTimer.Start();
            RefreshAvailability();
            ScanDiscovered(false);
        }

        /// <summary>停止在线探测（窗口关闭时调用）。</summary>
        public void StopAvailabilityProbe()
        {
            try { _probeTimer?.Stop(); } catch { }
            try { _discoveryCts.Cancel(); } catch { }
        }

        // ====== 局域网发现 ======

        /// <summary>扫描局域网服务端（manual=true 时忽略 5 秒频控）。</summary>
        private async void ScanDiscovered(bool manual)
        {
            if (_scanning) return;

            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!manual && now - _lastDiscoveryTicks < System.Diagnostics.Stopwatch.Frequency * 20)
                return; // 自动扫描 20 秒内不重复
            _lastDiscoveryTicks = now;

            _scanning = true;
            OnPropertyChanged(nameof(IsScanning));
            OnPropertyChanged(nameof(DiscoveryStatusText));
            try
            {
                await DiscoveryClient.ScanAsync(
                    DiscoveryClient.DefaultTimeoutMs,
                    server => _dispatcher.BeginInvoke(new Action(() => MergeDiscovered(server))),
                    _discoveryCts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Discovery scan failed");
            }
            finally
            {
                _scanning = false;
                _dispatcher.BeginInvoke(new Action(() =>
                {
                    PruneDiscovered();
                    OnPropertyChanged(nameof(IsScanning));
                    OnPropertyChanged(nameof(DiscoveryStatusText));
                }));
            }
        }

        /// <summary>合并发现结果：跳过已配置的连接（按 host:port）与重复项。</summary>
        private void MergeDiscovered(EasyRDP.Core.Discovery.DiscoveredServer server)
        {
            if (server == null) return;
            if (IsConfigured(server.Address != null ? server.Address.ToString() : "", server.TcpPort))
                return;
            // 同一服务端可能从多块网卡各回一次（本机多网卡/虚拟网卡）：按 名称+TCP端口 去重，
            // 保留最先到达的来源地址（真实部署时来源通常只有一个）。
            string dedupeKey = (server.Name ?? "").Trim() + "|" + server.TcpPort;
            foreach (var existing in Discovered)
            {
                if (string.Equals((existing.Name ?? "").Trim() + "|" + existing.TcpPort,
                        dedupeKey, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            Discovered.Add(server);
            OnPropertyChanged(nameof(DiscoveryStatusText));
        }

        /// <summary>移除已经被添加为配置连接的发现项。</summary>
        private void PruneDiscovered()
        {
            string last;
            var profiles = _profileStore.Load(out last);
            for (int i = Discovered.Count - 1; i >= 0; i--)
            {
                var server = Discovered[i];
                string host = server.Address != null ? server.Address.ToString() : "";
                bool configured = profiles.Any(p =>
                    string.Equals((p.Host ?? "").Trim(), host, StringComparison.OrdinalIgnoreCase)
                    && ParsePort(p.Port) == server.TcpPort);
                if (configured)
                    Discovered.RemoveAt(i);
            }
            OnPropertyChanged(nameof(DiscoveryStatusText));
        }

        /// <summary>判断 host:port 是否已存在于已保存的连接中。</summary>
        private bool IsConfigured(string host, int port)
        {
            if (string.IsNullOrWhiteSpace(host)) return false;
            string last;
            foreach (var p in _profileStore.Load(out last))
            {
                if (string.Equals((p.Host ?? "").Trim(), host.Trim(), StringComparison.OrdinalIgnoreCase)
                    && ParsePort(p.Port) == port)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 发现卡片 → 打开新建连接表单（预填 IP / 端口，名称与凭据由用户填写），
        /// 确认后与「+ Connection」完全一致的逻辑：保存连接信息并直接连接。
        /// </summary>
        private void AddDiscovered(EasyRDP.Core.Discovery.DiscoveredServer server)
        {
            if (server == null) return;

            var model = new RecentConnection
            {
                Host = server.Address != null ? server.Address.ToString() : "",
                Port = server.TcpPort.ToString(),
                // 名称留空：由用户填写（发现信息只有计算机名，未必是用户想要的连接名）
                DisplayName = null
            };
            var result = _dialogs.EditConnection(model);
            if (result == null || string.IsNullOrWhiteSpace(result.Host)) return;

            SaveProfile(result, "");
            Discovered.Remove(server);
            ReloadTree();
            SelectProfileByEndpoint(result.Host.Trim(), ParsePort(result.Port));
            ConnectRequested?.Invoke(ToConnection(result));
        }

        // ====== 在线状态 ======

        /// <summary>重新探测所有已保存连接与最近连接的服务端是否在线。</summary>
        public void RefreshAvailability()
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now - _lastProbeTicks < System.Diagnostics.Stopwatch.Frequency * 5)
                return; // 5 秒内不重复探测
            _lastProbeTicks = now;

            var targets = new List<KeyValuePair<string, int>>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string last;
            foreach (var p in _profileStore.Load(out last))
            {
                if (string.IsNullOrWhiteSpace(p.Host)) continue;
                int port = ParsePort(p.Port);
                if (seen.Add(p.Host.Trim() + ":" + port))
                    targets.Add(new KeyValuePair<string, int>(p.Host.Trim(), port));
            }
            foreach (var r in _recentCache)
            {
                if (string.IsNullOrWhiteSpace(r.Host)) continue;
                int port = ParsePort(r.Port);
                if (seen.Add(r.Host.Trim() + ":" + port))
                    targets.Add(new KeyValuePair<string, int>(r.Host.Trim(), port));
            }

            // 先显示"检测中"，避免沿用上一次的陈旧状态
            foreach (var g in Nodes)
                foreach (var n in g.Children)
                    n.Availability = ServerAvailability.Unknown;
            UpdateAvailabilitySummary();

            if (targets.Count == 0) return;

            ServerAvailabilityProbe.ProbeMany(targets, 900, 8, (host, port, online) =>
            {
                _dispatcher.BeginInvoke(new Action(() =>
                {
                    string key = host.ToLowerInvariant() + ":" + port;
                    _availability[key] = online ? ServerAvailability.Online : ServerAvailability.Offline;
                    ApplyAvailability(host, port, _availability[key]);
                    UpdateAvailabilitySummary();
                }));
            });
        }

        /// <summary>把探测结果套用到所有指向同一 host:port 的节点。</summary>
        private void ApplyAvailability(string host, int port, ServerAvailability state)
        {
            foreach (var g in Nodes)
            {
                foreach (var n in g.Children)
                {
                    var p = n.Profile;
                    if (p == null || string.IsNullOrWhiteSpace(p.Host)) continue;
                    if (!string.Equals(p.Host.Trim(), host, StringComparison.OrdinalIgnoreCase)) continue;
                    if (ParsePort(p.Port) != port) continue;
                    n.Availability = state;
                    if (ReferenceEquals(n, _selectedNode))
                        OnPropertyChanged(nameof(SelectedAvailabilityText));
                }
            }
        }

        /// <summary>重算在线/离线统计。</summary>
        private void UpdateAvailabilitySummary()
        {
            int online = 0;
            int offline = 0;
            foreach (var g in Nodes)
            {
                foreach (var n in g.Children)
                {
                    if (n.Availability == ServerAvailability.Online) online++;
                    else if (n.Availability == ServerAvailability.Offline) offline++;
                }
            }
            _onlineCount = online;
            _offlineCount = offline;
            OnPropertyChanged(nameof(AvailabilitySummary));
        }

        /// <summary>端口解析（非法值回退 2000，与连接逻辑一致）。</summary>
        private static int ParsePort(string port)
        {
            int value;
            if (int.TryParse((port ?? "").Trim(), out value) && value > 0 && value < 65536)
                return value;
            return 2000;
        }

        // ====== 列表加载与刷新 ======

        /// <summary>从本地缓存重载最近连接列表（不保留选中态）。</summary>
        public void ReloadConnections()
        {
            Connections.Clear();
            foreach (var r in _store.Load())
            {
                r.ThumbnailSource = ThumbnailCache.LoadThumbnail(r.Host);
                r.IsSelected = false;
                r.IsActive = false;
                Connections.Add(r);
            }
            _selected = null;
            ReloadTree();
        }

        /// <summary>静默刷新所有卡片缩略图图像源（不重建列表，不打断选中）。</summary>
        public void RefreshThumbnails()
        {
            foreach (var r in Connections)
            {
                var img = ThumbnailCache.LoadThumbnail(r.Host);
                if (img != null)
                    r.ThumbnailSource = img;
            }
        }

        // ====== 分组树 ======

        /// <summary>
        /// 重建分组树：Recent（最近使用，虚拟）→ 具名分组 → 未分组。
        /// 分组名单独持久化（允许空分组），连接的分组字段存在 connections.json。
        /// </summary>
        public void ReloadTree()
        {
            Nodes.Clear();

            string lastProfileName;
            var profiles = _profileStore.Load(out lastProfileName);
            var groupNames = _groupStore.Load();

            // 0) 最近使用：单独横条展示（不进主树，避免与已保存连接重复）
            _recentCache = _store.Load().OrderByDescending(r => r.LastConnectedUtc).ToList();
            Recents.Clear();
            foreach (var r in _recentCache.Take(8))
            {
                r.ThumbnailSource = ThumbnailCache.LoadThumbnail(r.Host);
                Recents.Add(r);
            }
            OnPropertyChanged(nameof(HasRecents));
            OnPropertyChanged(nameof(ShowNoRecentHint));

            // 搜索过滤：名称或地址命中（不区分大小写）
            string filter = (_searchText ?? "").Trim();
            if (filter.Length > 0)
            {
                profiles = profiles.Where(p =>
                    (!string.IsNullOrEmpty(p.Name) && p.Name.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0)
                    || (!string.IsNullOrEmpty(p.Host) && p.Host.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0))
                    .ToList();
            }

            // 2) 具名分组：分组名清单 + profiles 中出现过的分组
            var ordered = new List<string>(groupNames);
            foreach (var p in profiles)
            {
                string g = (p.Group ?? "").Trim();
                if (g.Length > 0 && !ordered.Contains(g))
                    ordered.Add(g);
            }
            ordered.Sort(StringComparer.CurrentCultureIgnoreCase);
            foreach (string groupName in ordered)
            {
                var groupNode = new ConnectionTreeNode
                {
                    IsGroup = true,
                    GroupName = groupName,
                    DisplayName = groupName,
                    IsExpanded = true
                };
                foreach (var p in profiles.Where(x => string.Equals((x.Group ?? "").Trim(), groupName, StringComparison.Ordinal)))
                    groupNode.Children.Add(CreateProfileNode(p));
                // 搜索状态下不显示空分组，避免结果里夹杂无关节点
                if (filter.Length > 0 && groupNode.Children.Count == 0)
                    continue;
                groupNode.Detail = groupNode.Children.Count + " connection(s)";
                Nodes.Add(groupNode);
            }

            // 3) 未分组
            var ungrouped = profiles.Where(x => string.IsNullOrWhiteSpace(x.Group)).ToList();
            if (ungrouped.Count > 0)
            {
                var ungroupedNode = new ConnectionTreeNode
                {
                    IsGroup = true,
                    IsUngrouped = true,
                    GroupName = "",
                    DisplayName = "Ungrouped",
                    Detail = ungrouped.Count + " connection(s)",
                    IsExpanded = true
                };
                foreach (var p in ungrouped)
                    ungroupedNode.Children.Add(CreateProfileNode(p));
                Nodes.Add(ungroupedNode);
            }

            UpdateAvailabilitySummary();
        }

        /// <summary>树选中项变化（View 代码后置调用）：同步右侧详情。</summary>
        public void SelectNode(ConnectionTreeNode node)
        {
            SelectedNode = node != null && !node.IsGroup ? node : null;
        }

        /// <summary>把时间格式化为相对时间（刚刚 / N 分钟前 / N 小时前 / N 天前）。</summary>
        private static string FormatRelative(DateTime utc)
        {
            if (utc == default(DateTime)) return "never";
            TimeSpan span = DateTime.UtcNow - utc;
            if (span.TotalSeconds < 60) return "just now";
            if (span.TotalMinutes < 60) return (int)span.TotalMinutes + " min ago";
            if (span.TotalHours < 24) return (int)span.TotalHours + " h ago";
            if (span.TotalDays < 30) return (int)span.TotalDays + " d ago";
            return utc.ToLocalTime().ToString("yyyy-MM-dd");
        }

        /// <summary>把保存的连接配置包装成树节点（含缩略图）。</summary>
        private ConnectionTreeNode CreateProfileNode(ServerProfile p)
        {
            var node = new ConnectionTreeNode
            {
                DisplayName = string.IsNullOrWhiteSpace(p.Name) ? p.Host : p.Name,
                Detail = p.Host + ":" + (string.IsNullOrWhiteSpace(p.Port) ? "2000" : p.Port),
                ThumbnailSource = ThumbnailCache.LoadThumbnail(p.Host),
                Profile = p,
                GroupName = p.Group ?? ""
            };
            // 沿用最近一次探测结果（重建树时不清空在线状态）
            string key = (p.Host ?? "").Trim().ToLowerInvariant() + ":" + ParsePort(p.Port);
            ServerAvailability state;
            if (_availability.TryGetValue(key, out state))
                node.Availability = state;
            return node;
        }

        /// <summary>连接树节点（Recent 条目或保存的连接配置）。</summary>
        private void ConnectNode(ConnectionTreeNode node)
        {
            if (node == null || node.IsGroup) return;
            if (node.Recent != null)
            {
                ConnectRequested?.Invoke(node.Recent);
                return;
            }
            var p = node.Profile;
            if (p == null || string.IsNullOrWhiteSpace(p.Host)) return;
            ConnectRequested?.Invoke(new RecentConnection
            {
                Host = p.Host.Trim(),
                Port = string.IsNullOrWhiteSpace(p.Port) ? "2000" : p.Port.Trim(),
                Username = p.Username,
                Password = p.Password,
                DisplayName = string.IsNullOrWhiteSpace(p.Name) ? null : p.Name,
                SavePassword = true
            });
        }

        /// <summary>编辑保存的连接（保留分组）。</summary>
        private void EditNode(ConnectionTreeNode node)
        {
            var p = node?.Profile;
            if (p == null) return;

            var model = new RecentConnection
            {
                Host = p.Host,
                Port = string.IsNullOrWhiteSpace(p.Port) ? "2000" : p.Port,
                Username = p.Username,
                Password = p.Password,
                DisplayName = p.Name,
                SavePassword = true
            };
            var result = _dialogs.EditConnection(model);
            if (result == null || string.IsNullOrWhiteSpace(result.Host)) return;

            string oldHost = p.Host ?? "";
            string last;
            var list = _profileStore.Load(out last);
            var item = list.FirstOrDefault(x => string.Equals(x.Name, p.Name, StringComparison.Ordinal)
                                             && string.Equals(x.Host, oldHost, StringComparison.OrdinalIgnoreCase));
            if (item == null) return;

            bool hostChanged = !string.Equals(item.Host, result.Host.Trim(), StringComparison.OrdinalIgnoreCase);
            item.Host = result.Host.Trim();
            item.Port = string.IsNullOrWhiteSpace(result.Port) ? "2000" : result.Port.Trim();
            item.Username = result.Username ?? "";
            item.Password = result.Password ?? "";
            item.Name = string.IsNullOrWhiteSpace(result.DisplayName) ? item.Host : result.DisplayName.Trim();
            if (hostChanged)
                ThumbnailCache.DeleteThumbnail(oldHost);
            _profileStore.Save(list, last);
            ReloadTree();
        }

        /// <summary>重命名保存的连接。</summary>
        private void RenameNode(ConnectionTreeNode node)
        {
            var p = node?.Profile;
            if (p == null) return;
            string newName = _dialogs.PromptText("Rename Connection", "Name", p.Name);
            if (newName == null) return;
            string last;
            var list = _profileStore.Load(out last);
            var item = list.FirstOrDefault(x => string.Equals(x.Name, p.Name, StringComparison.Ordinal)
                                             && string.Equals(x.Host, p.Host, StringComparison.OrdinalIgnoreCase));
            if (item == null) return;
            item.Name = string.IsNullOrWhiteSpace(newName) ? item.Host : newName.Trim();
            _profileStore.Save(list, last);
            ReloadTree();
        }

        /// <summary>删除保存的连接（含缩略图缓存）。</summary>
        private void DeleteNode(ConnectionTreeNode node)
        {
            var p = node?.Profile;
            if (p != null)
            {
                string last;
                var list = _profileStore.Load(out last);
                var item = list.FirstOrDefault(x => string.Equals(x.Name, p.Name, StringComparison.Ordinal)
                                                 && string.Equals(x.Host, p.Host, StringComparison.OrdinalIgnoreCase));
                if (item != null)
                {
                    list.Remove(item);
                    _profileStore.Save(list, last);
                }
                ThumbnailCache.DeleteThumbnail(p.Host);
                ReloadTree();
                return;
            }
            if (node?.Recent != null)
            {
                _store.Remove(node.Recent.Host);
                ThumbnailCache.DeleteThumbnail(node.Recent.Host);
                ReloadConnections();
            }
        }

        /// <summary>在指定分组内新建连接并立即连接。</summary>
        private void NewConnectionInGroup(ConnectionTreeNode groupNode)
        {
            var result = _dialogs.EditConnection(new RecentConnection { Host = "", Port = "2000" });
            if (result == null || string.IsNullOrWhiteSpace(result.Host)) return;

            string groupName = groupNode != null && groupNode.IsGroup && !groupNode.IsRecentGroup
                ? groupNode.GroupName : "";

            SaveProfile(result, groupName);
            ReloadTree();
            SelectProfileByEndpoint(result.Host.Trim(), ParsePort(result.Port));
            ConnectRequested?.Invoke(ToConnection(result));
        }

        /// <summary>新建空分组。</summary>
        private void NewGroup()
        {
            string name = _dialogs.PromptText("New Group", "Group name", "");
            if (string.IsNullOrWhiteSpace(name)) return;
            EnsureGroup(name.Trim());
            ReloadTree();
        }

        /// <summary>重命名分组（同步更新组内连接）。</summary>
        private void RenameGroup(ConnectionTreeNode node)
        {
            if (node == null || !node.IsGroup || node.IsRecentGroup) return;
            string name = _dialogs.PromptText("Rename Group", "Group name", node.GroupName);
            if (string.IsNullOrWhiteSpace(name)) return;
            string newName = name.Trim();
            if (string.Equals(newName, node.GroupName, StringComparison.Ordinal)) return;

            var groups = _groupStore.Load();
            groups.Remove(node.GroupName);
            if (!groups.Contains(newName)) groups.Add(newName);
            _groupStore.Save(groups);

            string last;
            var list = _profileStore.Load(out last);
            foreach (var p in list)
            {
                if (string.Equals((p.Group ?? "").Trim(), node.GroupName, StringComparison.Ordinal))
                    p.Group = newName;
            }
            _profileStore.Save(list, last);
            ReloadTree();
        }

        /// <summary>删除分组（组内连接移入未分组）。</summary>
        private void DeleteGroup(ConnectionTreeNode node)
        {
            if (node == null || !node.IsGroup || node.IsRecentGroup) return;

            var groups = _groupStore.Load();
            groups.Remove(node.GroupName);
            _groupStore.Save(groups);

            string last;
            var list = _profileStore.Load(out last);
            bool changed = false;
            foreach (var p in list)
            {
                if (string.Equals((p.Group ?? "").Trim(), node.GroupName, StringComparison.Ordinal))
                {
                    p.Group = "";
                    changed = true;
                }
            }
            if (changed)
                _profileStore.Save(list, last);
            ReloadTree();
        }

        /// <summary>把连接移动到指定分组（拖放调用）；groupName 为空表示未分组。</summary>
        public void MoveProfileToGroup(ServerProfile profile, string groupName)
        {
            if (profile == null) return;
            string target = (groupName ?? "").Trim();
            if (string.Equals((profile.Group ?? "").Trim(), target, StringComparison.Ordinal)) return;

            string last;
            var list = _profileStore.Load(out last);
            var item = list.FirstOrDefault(x => string.Equals(x.Name, profile.Name, StringComparison.Ordinal)
                                             && string.Equals(x.Host, profile.Host, StringComparison.OrdinalIgnoreCase));
            if (item == null) return;
            item.Group = target;
            _profileStore.Save(list, last);
            if (target.Length > 0)
                EnsureGroup(target);
            ReloadTree();
        }

        /// <summary>确保分组名存在（不存在则加入 groups.json）。</summary>
        private void EnsureGroup(string groupName)
        {
            if (string.IsNullOrWhiteSpace(groupName)) return;
            var groups = _groupStore.Load();
            if (!groups.Contains(groupName))
            {
                groups.Add(groupName);
                _groupStore.Save(groups);
            }
        }

        // ====== 连接 ======


        /// <summary>
        /// File > New Connection：弹完整连接表单（空表单 + 默认端口），
        /// 确认后保存为最近连接并立即发起连接。
        /// </summary>
        private void NewConnection()
        {
            var result = _dialogs.EditConnection(new RecentConnection { Host = "", Port = "2000" });
            if (result == null || string.IsNullOrWhiteSpace(result.Host))
                return; // 取消
            SaveProfile(result, "");
            ReloadTree();
            SelectProfileByEndpoint(result.Host.Trim(), ParsePort(result.Port));
            // 保存后直接进入远程窗口（连接信息已落盘，可在树中复用）
            ConnectRequested?.Invoke(ToConnection(result));
        }

        /// <summary>
        /// 把编辑结果保存为「连接信息」（connections.json），按 host:port 去重。
        /// </summary>
        private void SaveProfile(ConnectionEditResult result, string groupName)
        {
            string host = (result.Host ?? "").Trim();
            if (host.Length == 0) return;

            int port = ParsePort(result.Port);
            string last;
            var list = _profileStore.Load(out last);
            var existing = list.FirstOrDefault(p =>
                string.Equals((p.Host ?? "").Trim(), host, StringComparison.OrdinalIgnoreCase)
                && ParsePort(p.Port) == port);
            if (existing != null)
                list.Remove(existing);

            list.Add(new ServerProfile
            {
                Name = string.IsNullOrWhiteSpace(result.DisplayName) ? host : result.DisplayName.Trim(),
                Host = host,
                Port = string.IsNullOrWhiteSpace(result.Port) ? "2000" : result.Port.Trim(),
                Username = result.Username ?? "",
                Password = result.Password ?? "",
                Group = groupName ?? ""
            });
            _profileStore.Save(list, last);
            if (!string.IsNullOrWhiteSpace(groupName))
                EnsureGroup(groupName.Trim());
        }

        /// <summary>在树中按 host:port 选中刚保存的连接（右侧详情跟随显示）。</summary>
        private void SelectProfileByEndpoint(string host, int port)
        {
            foreach (var group in Nodes)
            {
                foreach (var node in group.Children)
                {
                    var p = node.Profile;
                    if (p == null) continue;
                    if (!string.Equals((p.Host ?? "").Trim(), host, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (ParsePort(p.Port) != port) continue;
                    SelectedNode = node;
                    return;
                }
            }
        }

        /// <summary>把编辑结果归一化为可直接发起连接的 RecentConnection。</summary>
        private static RecentConnection ToConnection(ConnectionEditResult result)
        {
            return new RecentConnection
            {
                Host = result.Host.Trim(),
                Port = string.IsNullOrWhiteSpace(result.Port) ? "2000" : result.Port.Trim(),
                Username = result.Username,
                Password = result.Password,
                SavePassword = result.SavePassword
            };
        }

        /// <summary>请求连接指定卡片（携带保存的端口/用户名/密码）。</summary>
        private void ConnectItem(RecentConnection rc)
        {
            if (rc == null || string.IsNullOrWhiteSpace(rc.Host)) return;
            ConnectRequested?.Invoke(rc);
        }

        /// <summary>设置卡片选中态（单选，取消其它选中）。</summary>
        private void SelectItem(RecentConnection rc)
        {
            if (rc == null) return;
            if (_selected != null && !ReferenceEquals(_selected, rc))
                _selected.IsSelected = false;
            rc.IsSelected = true;
            _selected = rc;
        }

        // ====== 编辑 / 重命名 / 删除 ======

        /// <summary>编辑连接：弹编辑表单，地址变更时旧缩略图缓存失效。</summary>
        private void EditItem(RecentConnection rc)
        {
            if (rc == null) return;
            string oldHost = rc.Host;

            var result = _dialogs.EditConnection(rc);
            // 取消或地址为空：不落盘、不刷新
            if (result == null || string.IsNullOrWhiteSpace(result.Host))
                return;

            string newHost = result.Host.Trim();
            var list = _store.Load();
            var item = list.FirstOrDefault(x =>
                string.Equals(x.Host, oldHost, StringComparison.OrdinalIgnoreCase));
            if (item != null)
            {
                bool hostChanged = !string.Equals(item.Host, newHost, StringComparison.OrdinalIgnoreCase);
                item.Host = newHost;
                item.DisplayName = string.IsNullOrWhiteSpace(result.DisplayName) ? null : result.DisplayName.Trim();
                item.Port = string.IsNullOrWhiteSpace(result.Port) ? "2000" : result.Port.Trim();
                item.Username = string.IsNullOrWhiteSpace(result.Username) ? null : result.Username.Trim();
                item.Password = string.IsNullOrEmpty(result.Password) ? null : result.Password;
                if (hostChanged)
                {
                    // 地址变更：旧缩略图缓存失效，删除避免张冠李戴
                    ThumbnailCache.DeleteThumbnail(oldHost);
                    item.ThumbnailPath = null;
                }
                _store.Save(list);
            }
            ReloadConnections();
        }

        /// <summary>重命名连接：弹输入框改 DisplayName 并持久化。</summary>
        private void RenameItem(RecentConnection rc)
        {
            if (rc == null) return;
            string? newName = _dialogs.RenameConnection(rc);
            if (newName == null) return; // 取消

            string? display = string.IsNullOrWhiteSpace(newName) ? null : newName.Trim();
            rc.DisplayName = display;

            var list = _store.Load();
            var item = list.FirstOrDefault(x =>
                string.Equals(x.Host, rc.Host, StringComparison.OrdinalIgnoreCase));
            if (item != null)
            {
                item.DisplayName = display;
                _store.Save(list);
            }
            // 重载列表让卡片显示最新名字（DisplayText 依赖 DisplayName）
            ReloadConnections();
        }

        /// <summary>删除最近连接（含缩略图缓存）。</summary>
        private void DeleteItem(RecentConnection rc)
        {
            if (rc == null || string.IsNullOrWhiteSpace(rc.Host)) return;
            _store.Remove(rc.Host);
            ThumbnailCache.DeleteThumbnail(rc.Host);
            ReloadConnections();
        }

        // ====== INotifyPropertyChanged ======

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
