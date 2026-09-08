using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NLog;

namespace EasyRDP.Client.Wpf
{
    /// <summary>
    /// 浏览窗口（1:1 复刻 RealVNC Viewer）：菜单栏 File/View/Help +
    /// 品牌行（logo + 地址输入框 + Sign in）+ 白色 5 列缩略图网格。
    /// 单击选中（灰高亮），双击直连；右键 连接/重命名/删除。
    /// 连接在独立的新 MainWindow（控制窗口）中打开，本窗口始终保留。
    /// </summary>
    public partial class HostBrowseWindow : Window
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly RecentConnectionStore _store = new RecentConnectionStore();
        private RecentConnection _selected;

        /// <summary>最近连接列表（DataTemplate 数据源）。</summary>
        public ObservableCollection<RecentConnection> Connections { get; } = new ObservableCollection<RecentConnection>();

        /// <summary>地址输入框文本（绑定到 XAML 地址栏）。</summary>
        public string AddressText { get; set; } = "";

        public HostBrowseWindow()
        {
            InitializeComponent();
            DataContext = this;
            ReloadConnections();
        }

        /// <summary>从本地缓存重载最近连接列表。</summary>
        private void ReloadConnections()
        {
            Connections.Clear();
            foreach (var r in _store.Load())
            {
                r.ThumbnailSource = ThumbnailCache.LoadThumbnail(r.Host);
                r.IsSelected = false;
                r.IsActive = false;
                Connections.Add(r);
            }
        }

        /// <summary>解析 "host[:port]" 输入，打开控制窗口并连接。</summary>
        private void ConnectToHost(string hostPort)
        {
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

            var control = new MainWindow();
            control.ConnectTo(host, port);
            control.Show();
            Logger.Info("HostBrowse: opened control window for {0}:{1}", host, port);
        }

        /// <summary>设置卡片选中态（单选，取消其它选中）。</summary>
        private void SelectCard(RecentConnection rc)
        {
            if (_selected != null && !ReferenceEquals(_selected, rc))
                _selected.IsSelected = false;
            if (rc != null)
            {
                rc.IsSelected = true;
                _selected = rc;
            }
        }

        // ====== 事件 ======

        /// <summary>品牌行地址输入框回车连接。</summary>
        private void HostBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ConnectToHost(HostBox.Text);
                e.Handled = true;
            }
        }

        /// <summary>卡片：单击选中、双击直连。</summary>
        private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is Border border) || !(border.DataContext is RecentConnection rc)) return;
            SelectCard(rc);
            if (e.ClickCount == 2)
            {
                ConnectToHost(rc.Host);
                e.Handled = true;
            }
        }

        /// <summary>右键菜单：连接。</summary>
        private void MenuConnect_Click(object sender, RoutedEventArgs e)
        {
            if (GetContextHost(sender as MenuItem, out string host))
                ConnectToHost(host);
        }

        /// <summary>右键菜单：重命名（弹输入框改 DisplayName 并持久化）。</summary>
        private void MenuRename_Click(object sender, RoutedEventArgs e)
        {
            if (!GetContextHost(sender as MenuItem, out string host)) return;
            var rc = Connections.FirstOrDefault(r =>
                string.Equals(r.Host, host, StringComparison.OrdinalIgnoreCase));
            if (rc == null) return;

            // 轻量输入对话框（纯代码构建，避免为一次输入新建 xaml 窗口）
            var dlg = new Window
            {
                Title = "Rename - " + host,
                Width = 360,
                Height = 150,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow,
                ShowInTaskbar = false
            };
            var panel = new StackPanel { Margin = new Thickness(14) };
            var label = new TextBlock { Text = "显示名称（留空则恢复为 IP）：", Margin = new Thickness(0, 0, 0, 6) };
            var input = new TextBox { Text = rc.DisplayName ?? "", Height = 26, VerticalContentAlignment = VerticalAlignment.Center };
            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var ok = new Button { Content = "OK", Width = 72, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var cancel = new Button { Content = "Cancel", Width = 72, IsCancel = true };
            ok.Click += (s2, e2) => { dlg.DialogResult = true; };
            btns.Children.Add(ok);
            btns.Children.Add(cancel);
            panel.Children.Add(label);
            panel.Children.Add(input);
            panel.Children.Add(btns);
            dlg.Content = panel;
            dlg.Loaded += (s2, e2) => { input.Focus(); input.SelectAll(); };

            if (dlg.ShowDialog() == true)
            {
                rc.DisplayName = string.IsNullOrWhiteSpace(input.Text) ? null : input.Text.Trim();
                var list = _store.Load();
                var item = list.FirstOrDefault(x =>
                    string.Equals(x.Host, host, StringComparison.OrdinalIgnoreCase));
                if (item != null)
                {
                    item.DisplayName = rc.DisplayName;
                    _store.Save(list);
                }
                // 重载列表让卡片显示最新名字（DisplayText 依赖 DisplayName）
                ReloadConnections();
            }
        }

        /// <summary>右键菜单：删除最近连接（含缩略图缓存）。</summary>
        private void MenuDelete_Click(object sender, RoutedEventArgs e)
        {
            if (!GetContextHost(sender as MenuItem, out string host)) return;
            _store.Remove(host);
            ThumbnailCache.DeleteThumbnail(host);
            ReloadConnections();
        }

        /// <summary>菜单 File > New Connection：聚焦地址输入框。</summary>
        private void MenuNewConnection_Click(object sender, RoutedEventArgs e)
        {
            HostBox.Focus();
            HostBox.SelectAll();
        }

        /// <summary>菜单 File/View > Refresh：重载缩略图列表。</summary>
        private void MenuRefresh_Click(object sender, RoutedEventArgs e)
        {
            ReloadConnections();
        }

        /// <summary>菜单 File > Exit。</summary>
        private void MenuExit_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>菜单 Help > About。</summary>
        private void MenuAbout_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(this, "EasyRDP Client\nRealVNC Viewer 风格浏览窗口",
                "About", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>Sign in...（视觉占位，与 RealVNC 布局一致）。</summary>
        private void SignIn_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(this, "账号登录为视觉占位（与 RealVNC 布局一致），后续版本接入。",
                "Sign in", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>从右键菜单的 PlacementTarget 取卡片 DataContext（RecentConnection.Host）。</summary>
        private static bool GetContextHost(MenuItem item, out string host)
        {
            host = null;
            var ctx = item?.Parent as ContextMenu;
            var target = ctx?.PlacementTarget as Border;
            var rc = target?.DataContext as RecentConnection;
            if (rc == null) return false;
            host = rc.Host;
            return true;
        }
    }
}
