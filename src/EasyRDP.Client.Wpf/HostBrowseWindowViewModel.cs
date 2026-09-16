using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using EasyRDP.Shared;

namespace EasyRDP.Client.Wpf
{
    /// <summary>
    /// 浏览窗口 ViewModel（严格 MVVM）：管理最近连接列表、地址栏解析与连接命令。
    /// 弹窗输入（Edit/Rename）经 IDialogService，提示（About/Sign in）经 IUserNotifier，
    /// 打开控制窗口经 ConnectRequested 事件、关闭窗口经 RequestClose 事件——均隔离 View 依赖。
    /// </summary>
    public class HostBrowseWindowViewModel : INotifyPropertyChanged
    {
        private readonly RecentConnectionStore _store;
        private readonly IDialogService _dialogs;
        private readonly IUserNotifier _notifier;
        private RecentConnection? _selected;
        private string _addressText = "";

        /// <summary>最近连接列表（缩略图网格数据源）。</summary>
        public ObservableCollection<RecentConnection> Connections { get; } = new ObservableCollection<RecentConnection>();

        /// <summary>地址输入框文本（绑定到 XAML 地址栏）。</summary>
        public string AddressText
        {
            get { return _addressText; }
            set { _addressText = value ?? ""; OnPropertyChanged(nameof(AddressText)); }
        }

        // ====== 命令 ======

        /// <summary>地址栏回车连接。</summary>
        public RelayCommand ConnectAddressCommand { get; }

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

        /// <summary>菜单 File > New Connection（聚焦地址栏）。</summary>
        public RelayCommand NewConnectionCommand { get; }

        /// <summary>菜单 File/View > Refresh（重载列表）。</summary>
        public RelayCommand RefreshCommand { get; }

        /// <summary>菜单 File > Exit（关闭窗口）。</summary>
        public RelayCommand ExitCommand { get; }

        /// <summary>菜单 Help > About。</summary>
        public RelayCommand AboutCommand { get; }

        /// <summary>Sign in...（视觉占位）。</summary>
        public RelayCommand SignInCommand { get; }

        // ====== 视图交互事件（由 View 订阅，ViewModel 不直接引用 Window） ======

        /// <summary>请求打开控制窗口并连接（参数为连接目标）。</summary>
        public event Action<RecentConnection>? ConnectRequested;

        /// <summary>请求关闭浏览窗口。</summary>
        public event Action? RequestClose;

        /// <summary>请求聚焦地址栏（File > New Connection）。</summary>
        public event Action? FocusAddressRequested;

        public HostBrowseWindowViewModel(RecentConnectionStore store, IDialogService dialogs, IUserNotifier notifier)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
            _notifier = notifier ?? new WpfUserNotifier();

            ConnectAddressCommand = new RelayCommand(ConnectFromAddressBar);
            ConnectItemCommand = new RelayCommand<RecentConnection>(ConnectItem);
            EditItemCommand = new RelayCommand<RecentConnection>(EditItem);
            RenameItemCommand = new RelayCommand<RecentConnection>(RenameItem);
            DeleteItemCommand = new RelayCommand<RecentConnection>(DeleteItem);
            SelectItemCommand = new RelayCommand<RecentConnection>(SelectItem);
            NewConnectionCommand = new RelayCommand(() => FocusAddressRequested?.Invoke());
            RefreshCommand = new RelayCommand(ReloadConnections);
            ExitCommand = new RelayCommand(() => RequestClose?.Invoke());
            AboutCommand = new RelayCommand(() =>
                _notifier.ShowInfo("EasyRDP Client\nRealVNC Viewer 风格浏览窗口", "About"));
            SignInCommand = new RelayCommand(() =>
                _notifier.ShowInfo("账号登录为视觉占位（与 RealVNC 布局一致），后续版本接入。", "Sign in"));

            ReloadConnections();
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

        // ====== 连接 ======

        /// <summary>解析地址栏 "host[:port]" 并请求连接（地址栏无用户名/密码）。</summary>
        private void ConnectFromAddressBar()
        {
            string hostPort = AddressText;
            if (string.IsNullOrWhiteSpace(hostPort)) return;

            string host = hostPort.Trim();
            string port = "2000";
            int idx = host.LastIndexOf(':');
            if (idx > 0 && int.TryParse(host.Substring(idx + 1), out int p) && p > 0 && p < 65536)
            {
                port = p.ToString();
                host = host.Substring(0, idx).Trim();
            }
            if (string.IsNullOrWhiteSpace(host)) return;

            ConnectRequested?.Invoke(new RecentConnection { Host = host, Port = port });
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
