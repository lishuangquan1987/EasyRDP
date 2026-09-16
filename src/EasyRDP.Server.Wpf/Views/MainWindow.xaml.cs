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
