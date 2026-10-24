using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
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
    /// 浏览窗口：菜单栏 File/View/Help + 品牌行（logo + 地址输入框）+ 连接分组树。
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

            // 订阅 ViewModel 的视图交互事件：打开控制窗口 / 关闭窗口
            _vm.ConnectRequested += OpenControlWindow;
            _vm.RequestClose += Close;

            // 定时静默刷新缩略图：控制窗口连接后保存了新缩略图，回到浏览窗口时
            // 卡片能自动显示最新画面（仅更新 ThumbnailSource，保留列表与选中态）。
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _refreshTimer.Tick += (s, e) => _vm.RefreshThumbnails();
            _refreshTimer.Start();

            // 连接树拖放：把连接拖到分组节点即归入该分组
            ConnectionTree.SelectedItemChanged += (s2, e2) =>
                _vm.SelectNode(e2.NewValue as ConnectionTreeNode);
            ConnectionTree.PreviewMouseLeftButtonDown += OnTreeMouseLeftButtonDown;
            ConnectionTree.PreviewMouseMove += OnTreeMouseMove;
            ConnectionTree.DragOver += OnTreeDragOver;
            ConnectionTree.Drop += OnTreeDrop;
        }

        // ====== 连接树拖放 ======

        private Point _dragStartPoint;
        private ConnectionTreeNode _dragNode;

        private void OnTreeMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(null);
            _dragNode = FindNode(e.OriginalSource as DependencyObject);
        }

        private void OnTreeMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dragNode == null) return;
            if (_dragNode.IsGroup || _dragNode.Profile == null) return;

            Point pos = e.GetPosition(null);
            if (Math.Abs(pos.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(pos.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            DragDrop.DoDragDrop(ConnectionTree, _dragNode, DragDropEffects.Move);
            _dragNode = null;
        }

        private void OnTreeDragOver(object sender, DragEventArgs e)
        {
            var source = e.Data.GetData(typeof(ConnectionTreeNode)) as ConnectionTreeNode;
            var target = FindNode(e.OriginalSource as DependencyObject);
            bool canDrop = source != null && source.Profile != null
                && target != null && target.IsGroup && !target.IsRecentGroup;
            e.Effects = canDrop ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private void OnTreeDrop(object sender, DragEventArgs e)
        {
            var source = e.Data.GetData(typeof(ConnectionTreeNode)) as ConnectionTreeNode;
            var target = FindNode(e.OriginalSource as DependencyObject);
            if (source == null || source.Profile == null) return;
            if (target == null || !target.IsGroup || target.IsRecentGroup) return;

            _vm.MoveProfileToGroup(source.Profile, target.IsUngrouped ? "" : target.GroupName);
            e.Handled = true;
        }

        /// <summary>从命中的可视元素向上找到所属的 TreeViewItem，取其节点数据。</summary>
        private static ConnectionTreeNode FindNode(DependencyObject element)
        {
            while (element != null && !(element is System.Windows.Controls.TreeViewItem))
                element = VisualTreeHelper.GetParent(element);
            var item = element as System.Windows.Controls.TreeViewItem;
            return item != null ? item.DataContext as ConnectionTreeNode : null;
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
            _vm?.StopAvailabilityProbe();
            base.OnClosing(e);
        }
    }
}
