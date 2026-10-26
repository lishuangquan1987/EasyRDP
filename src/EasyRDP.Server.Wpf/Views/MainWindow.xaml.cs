#nullable disable
using System;
using System.ComponentModel;
using System.Windows;
using EasyRDP.Server.Wpf.Services;
using EasyRDP.Server.Wpf.ViewModels;
using NLog;

namespace EasyRDP.Server.Wpf.Views
{
    /// <summary>
    /// 服务端主窗口（View 层）。仅初始化 ViewModel、托盘与组件绑定。
    /// 所有业务逻辑在 MainWindowViewModel 中。
    /// </summary>
    public partial class MainWindow : Window
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private readonly MainWindowViewModel _vm;
        private readonly TrayIconService _tray;
        // 托盘 Exit 菜单触发：允许窗口真正关闭并退出进程
        private bool _exitRequested;
        // 首次最小化到托盘的气泡提示只显示一次
        private bool _trayHintShown;

        public MainWindow()
        {
            InitializeComponent();
            _vm = new MainWindowViewModel(Dispatcher);
            DataContext = _vm;

            // 关闭窗口 = 隐藏到托盘继续监听；托盘菜单提供恢复/启停/退出
            _tray = new TrayIconService(ShowFromTray, ToggleListeningFromTray, ExitFromTray);
            _tray.UpdateListeningState(_vm.IsRunning);
            _vm.PropertyChanged += OnViewModelPropertyChanged;

            // 启动即监听：窗口加载完成后触发一次（状态栏与日志控件已就绪，
            // 自动监听失败的日志能正常显示，且不会在构造期弹框）。
            Loaded += (s, e) => _vm.TryAutoListen();
        }

        /// <summary>跟踪 IsRunning 变化，同步托盘菜单文案。</summary>
        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainWindowViewModel.IsRunning))
                _tray.UpdateListeningState(_vm.IsRunning);
        }

        /// <summary>从托盘恢复主窗口。</summary>
        private void ShowFromTray()
        {
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
        }

        /// <summary>托盘菜单：开始/停止监听。</summary>
        private void ToggleListeningFromTray()
        {
            if (_vm.IsRunning)
            {
                if (_vm.StopCommand.CanExecute(null)) _vm.StopCommand.Execute(null);
            }
            else
            {
                // 从托盘启动时窗口可能仍隐藏：先恢复，便于看到状态/错误提示
                ShowFromTray();
                if (_vm.StartCommand.CanExecute(null)) _vm.StartCommand.Execute(null);
            }
            _tray.UpdateListeningState(_vm.IsRunning);
        }

        /// <summary>托盘菜单：退出进程（保存设置、停止监听、释放托盘）。</summary>
        private void ExitFromTray()
        {
            _exitRequested = true;
            try
            {
                if (_vm.IsRunning && _vm.StopCommand.CanExecute(null))
                    _vm.StopCommand.Execute(null);
                _vm.SaveSettings();
                _vm.CleanupUpdateClient();
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Shutdown cleanup failed");
            }
            _tray.Dispose();
            Application.Current.Shutdown();
        }

        /// <summary>窗口关闭：默认隐藏到托盘；仅托盘 Exit 或更新器请求关闭时真正退出。</summary>
        protected override void OnClosing(CancelEventArgs e)
        {
            // 用户确认应用更新后，更新器会向本进程发送 WM_CLOSE 以优雅关闭：
            // 此时必须真正退出（停止监听、保存设置、清理更新客户端），
            // 让端口被干净释放，更新重启后的新实例才能立即绑定；
            // 若按默认逻辑隐藏到托盘，2 秒后会被更新器强杀（见 ExitRequestedByUpdate）。
            if (!_exitRequested && _vm.ExitRequestedByUpdate)
            {
                _exitRequested = true;
                try
                {
                    if (_vm.IsRunning && _vm.StopCommand.CanExecute(null))
                        _vm.StopCommand.Execute(null);
                    _vm.SaveSettings();
                    _vm.CleanupUpdateClient();
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "Update exit cleanup failed");
                }
            }

            if (!_exitRequested)
            {
                e.Cancel = true;
                try
                {
                    Hide();
                    if (!_trayHintShown)
                    {
                        _trayHintShown = true;
                        _tray.ShowBalloon("EasyRDP Server",
                            "Still running in the notification area. Right-click the icon to exit.");
                    }
                    Logger.Info("Window hidden to tray (server keeps listening: {0})", _vm.IsRunning);
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "Hide to tray failed");
                }
                base.OnClosing(e);
                return;
            }

            try
            {
                _vm.SaveSettings();
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Save settings on close failed");
            }
            try
            {
                _vm.CleanupUpdateClient();
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Cleanup aly update client failed");
            }
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _tray.Dispose();
            base.OnClosing(e);
        }
    }
}
