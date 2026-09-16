#nullable disable
using System.Windows;
using System.Windows.Input;

namespace EasyRDP.Client.Wpf.Behaviors
{
    /// <summary>
    /// 附加行为（attached behavior）：把元素上的鼠标左键双击（MouseLeftButtonDown 且 ClickCount==2）
    /// 绑定到 ICommand。CommandParameter 未显式设置时，默认把元素的 DataContext 作为命令参数。
    /// </summary>
    public static class DoubleClickCommand
    {
        /// <summary>要执行的命令。</summary>
        public static readonly DependencyProperty CommandProperty =
            DependencyProperty.RegisterAttached("Command", typeof(ICommand), typeof(DoubleClickCommand),
                new PropertyMetadata(null, OnCommandChanged));

        /// <summary>命令参数（未设置时回退到元素 DataContext）。</summary>
        public static readonly DependencyProperty CommandParameterProperty =
            DependencyProperty.RegisterAttached("CommandParameter", typeof(object), typeof(DoubleClickCommand),
                new PropertyMetadata(null));

        private static readonly DependencyProperty IsHookedProperty =
            DependencyProperty.RegisterAttached("IsHooked", typeof(bool), typeof(DoubleClickCommand),
                new PropertyMetadata(false));

        public static ICommand GetCommand(DependencyObject obj)
        {
            return (ICommand)obj.GetValue(CommandProperty);
        }

        public static void SetCommand(DependencyObject obj, ICommand value)
        {
            obj.SetValue(CommandProperty, value);
        }

        public static object GetCommandParameter(DependencyObject obj)
        {
            return obj.GetValue(CommandParameterProperty);
        }

        public static void SetCommandParameter(DependencyObject obj, object value)
        {
            obj.SetValue(CommandParameterProperty, value);
        }

        private static void OnCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var element = d as UIElement;
            if (element == null) return;
            if ((bool)element.GetValue(IsHookedProperty)) return;

            element.AddHandler(UIElement.MouseLeftButtonDownEvent,
                new MouseButtonEventHandler(OnMouseLeftButtonDown), true);
            element.SetValue(IsHookedProperty, true);
        }

        private static void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount != 2) return;

            var element = sender as DependencyObject;
            if (element == null) return;

            var command = GetCommand(element);
            object parameter = GetCommandParameter(element) ?? (element as FrameworkElement)?.DataContext;
            if (command != null && command.CanExecute(parameter))
                command.Execute(parameter);
        }
    }
}
