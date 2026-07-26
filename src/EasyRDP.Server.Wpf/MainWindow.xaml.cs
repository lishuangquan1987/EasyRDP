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
        public MainWindow()
        {
            InitializeComponent();
            DataContext = new MainWindowViewModel(Dispatcher);
        }
    }
}
