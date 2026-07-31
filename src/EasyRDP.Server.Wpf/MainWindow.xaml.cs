#nullable disable
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
    }
}
