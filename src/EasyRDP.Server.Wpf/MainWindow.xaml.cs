#nullable disable
using System;
using System.ComponentModel;
using System.Windows;

namespace EasyRDP.Server.Wpf
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
            // PasswordBox 不支持绑定 Password，初始值在构造后同步一次，
            // 后续变化由 PasswordChanged 事件同步到 ViewModel。
            PasswordBox.Password = _vm.Password ?? string.Empty;
        }

        /// <summary>PasswordBox 密码变化 → 同步到 ViewModel。</summary>
        private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            _vm.Password = PasswordBox.Password;
        }

        /// <summary>会话列表"踢出"按钮：路由到 ViewModel 异步断开对应会话。</summary>
        private void KickButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as System.Windows.Controls.Button;
            if (button == null || button.Tag == null) return;
            if (button.Tag is uint sessionId)
                _vm.KickSession(sessionId);
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
