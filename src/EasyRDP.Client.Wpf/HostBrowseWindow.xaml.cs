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
    /// 浏览窗口（RealVNC Viewer 风格）：显示最近连接缩略图网格。
    /// 双击卡片直连；右键弹出 连接/删除 菜单。
    /// 连接在独立的新 MainWindow（控制窗口）中打开，本窗口始终保留。
    /// </summary>
    public partial class HostBrowseWindow : Window
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly RecentConnectionStore _store = new RecentConnectionStore();

        /// <summary>最近连接列表（DataTemplate 数据源）。</summary>
        public ObservableCollection<RecentConnection> Connections { get; } = new ObservableCollection<RecentConnection>();

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
                Connections.Add(r);
            }
            StatusText.Text = Connections.Count > 0
                ? string.Format("共 {0} 个最近连接", Connections.Count)
                : "还没有最近连接——在顶部输入主机地址并点 Connect";
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

        // ====== 事件 ======

        /// <summary>地址栏 Connect 按钮。</summary>
        private void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            ConnectToHost(HostBox.Text);
        }

        /// <summary>地址栏回车。</summary>
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ConnectToHost(HostBox.Text);
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        /// <summary>卡片单击选择、双击直连。</summary>
        private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2 && sender is Border border && border.DataContext is RecentConnection rc)
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

        /// <summary>右键菜单：删除最近连接（含缩略图缓存）。</summary>
        private void MenuDelete_Click(object sender, RoutedEventArgs e)
        {
            if (!GetContextHost(sender as MenuItem, out string host)) return;
            _store.Remove(host);
            ThumbnailCache.DeleteThumbnail(host);
            ReloadConnections();
            StatusText.Text = "已删除 " + host;
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
