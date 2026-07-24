using System.Windows;
using System.Windows.Input;

namespace EasyRDP.Client.Wpf;

/// <summary>
/// 客户端主窗口（View 层）。仅负责初始化 ViewModel 和路由鼠标事件。
/// 所有业务逻辑在 MainWindowViewModel 中。
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainWindowViewModel();
        DataContext = _vm;
    }

    /// <summary>将鼠标事件路由到 ViewModel。</summary>
    private void RenderImage_MouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(RenderImage);
        _vm.HandleMouseMove(pos.X, pos.Y, RenderImage.ActualWidth, RenderImage.ActualHeight);
    }

    /// <summary>将鼠标按下事件路由到 ViewModel。</summary>
    private void RenderImage_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _vm.HandleMouseDown(e.ChangedButton);
    }

    /// <summary>将鼠标释放事件路由到 ViewModel。</summary>
    private void RenderImage_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _vm.HandleMouseUp(e.ChangedButton);
    }

    /// <summary>将滚轮事件路由到 ViewModel。</summary>
    private void RenderImage_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _vm.HandleMouseWheel(e.Delta);
    }
}
