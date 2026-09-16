using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using EasyRDP.Client.Wpf.Dialogs;
using EasyRDP.Client.Wpf.Models;
using EasyRDP.Client.Wpf.Services;
using EasyRDP.Client.Wpf.ViewModels;
using EasyRDP.Shared;
using NLog;

namespace EasyRDP.Client.Wpf.Views
{
    /// <summary>
    /// 浏览窗口（1:1 复刻 RealVNC Viewer）：菜单栏 File/View/Help +
    /// 品牌行（logo + 地址输入框 + Sign in）+ 白色 5 列缩略图网格。
    /// 严格 MVVM：本 View 仅负责初始化 ViewModel、订阅视图交互事件、
    /// 打开控制窗口、聚焦地址栏与定时静默刷新缩略图；所有业务逻辑在 HostBrowseWindowViewModel。
    /// </summary>
    public partial class HostBrowseWindow : Window
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly HostBrowseWindowViewModel _vm;
        private readonly DispatcherTimer _refreshTimer;

        public HostBrowseWindow()
        {
            InitializeComponent();
            _vm = new HostBrowseWindowViewModel(
                new RecentConnectionStore(),
                new DialogService(this),
                new WpfUserNotifier());
            DataContext = _vm;

            // 订阅 ViewModel 的视图交互事件：打开控制窗口 / 关闭窗口 / 聚焦地址栏
            _vm.ConnectRequested += OpenControlWindow;
            _vm.RequestClose += Close;
            _vm.FocusAddressRequested += FocusAddressBar;

            // 定时静默刷新缩略图：控制窗口连接后保存了新缩略图，回到浏览窗口时
            // 卡片能自动显示最新画面（仅更新 ThumbnailSource，保留列表与选中态）。
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _refreshTimer.Tick += (s, e) => _vm.RefreshThumbnails();
            _refreshTimer.Start();
        }

        /// <summary>聚焦地址输入框并全选（File > New Connection）。</summary>
        private void FocusAddressBar()
        {
            HostBox.Focus();
            HostBox.SelectAll();
        }

        /// <summary>打开新控制窗口（MainWindow）并立即连接。</summary>
        private void OpenControlWindow(RecentConnection rc)
        {
            if (rc == null || string.IsNullOrWhiteSpace(rc.Host)) return;
            var control = new MainWindow();
            control.ConnectTo(
                rc.Host.Trim(),
                string.IsNullOrWhiteSpace(rc.Port) ? "2000" : rc.Port.Trim(),
                rc.Username,
                rc.Password);
            control.Show();
            Logger.Info("HostBrowse: opened control window for {0}:{1}", rc.Host.Trim(), rc.Port);
        }

        /// <summary>窗口关闭前停止缩略图刷新定时器。</summary>
        protected override void OnClosing(CancelEventArgs e)
        {
            _refreshTimer?.Stop();
            base.OnClosing(e);
        }
    }
}
