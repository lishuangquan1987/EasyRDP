#nullable disable
using System;
using System.ComponentModel;
using System.Windows;
using EasyRDP.Server.Wpf.ViewModels;

namespace EasyRDP.Server.Wpf.Views
{
    /// <summary>
    /// 服务端主窗口（View 层）。仅初始化 ViewModel 和组件绑定。
    /// 所有业务逻辑在 MainWindowViewModel 中。
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly MainWindowViewModel _vm;

        public MainWindow()
        {
            InitializeComponent();
            _vm = new MainWindowViewModel(Dispatcher);
            DataContext = _vm;
            // 启动即监听：窗口加载完成后触发一次（状态栏与日志控件已就绪，
            // 自动监听失败的日志能正常显示，且不会在构造期弹框）。
            Loaded += (s, e) => _vm.TryAutoListen();
        }

        /// <summary>窗口关闭前自动保存当前设置。</summary>
        protected override void OnClosing(CancelEventArgs e)
        {
            try
            {
                _vm.SaveSettings();
            }
            catch (Exception ex)
            {
                NLog.LogManager.GetCurrentClassLogger().Warn(ex, "Save settings on close failed");
            }
            try
            {
                _vm.CleanupUpdateClient();
            }
            catch (Exception ex)
            {
                NLog.LogManager.GetCurrentClassLogger().Warn(ex, "Cleanup aly update client failed");
            }
            base.OnClosing(e);
        }
    }
}
