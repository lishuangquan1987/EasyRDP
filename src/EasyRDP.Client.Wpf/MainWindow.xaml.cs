using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using AlyClient.CSharpSDK;
using EasyRDP.Client.Wpf.ViewModels;

namespace EasyRDP.Client.Wpf
{
    public partial class MainWindow
    {
        private MainViewModel _vm;

        public MainWindow()
        {
            _vm = new MainViewModel();
            DataContext = _vm;
            InitializeComponent();
        }

        private void OnMouseMove(object s, MouseEventArgs e)
        { _vm.OnLocalMouseMove(e.GetPosition((IInputElement)s), (UIElement)s); }
        private void OnMouseDown(object s, MouseButtonEventArgs e)
        {
            // 捕获鼠标：保证拖拽过程中移出控件仍能收到 MouseUp，避免远程端按钮卡在"按下"状态
            Mouse.Capture((IInputElement)s);
            // 强制发送积压的鼠标移动，确保按下时位置与服务端一致
            _vm.FlushPendingMove(true);
            var d = _vm.InputCapturer.EncodeMouseButton(e, true, _vm.SeqTracker.Next()); _vm.SendInput(d);
        }
        private void OnMouseUp(object s, MouseButtonEventArgs e)
        {
            // 释放前先发送最新位置，避免松开坐标与按下时错位
            _vm.FlushPendingMove(true);
            var d = _vm.InputCapturer.EncodeMouseButton(e, false, _vm.SeqTracker.Next()); _vm.SendInput(d);
            // 释放捕获，恢复正常鼠标路由
            Mouse.Capture(null);
        }
        private void OnMouseWheel(object s, MouseWheelEventArgs e)
        { var d = _vm.InputCapturer.EncodeMouseWheel(e, _vm.SeqTracker.Next()); _vm.SendInput(d); }
        private void OnKeyDown(object s, KeyEventArgs e)
        { var d = _vm.InputCapturer.EncodeKey(e, true, _vm.SeqTracker.Next()); _vm.SendInput(d); }
        private void OnKeyUp(object s, KeyEventArgs e)
        { var d = _vm.InputCapturer.EncodeKey(e, false, _vm.SeqTracker.Next()); _vm.SendInput(d); }

        private void OnClosing(object s, CancelEventArgs e) { _vm.Cleanup(); }
    }

    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object v, Type t, object p, CultureInfo c)
        { return (bool)v ? Visibility.Visible : Visibility.Collapsed; }
        public object ConvertBack(object v, Type t, object p, CultureInfo c) { return null; }
    }

    public class InverseBoolConverter : IValueConverter
    {
        public object Convert(object v, Type t, object p, CultureInfo c) { return !(bool)v; }
        public object ConvertBack(object v, Type t, object p, CultureInfo c) { return !(bool)v; }
    }

    /// <summary>
    /// 将 AlyClientStatus 转换为 Visibility：仅在 DiscoveredUpdate/DownloadingUpdate/DownloadedUpdate 时可见。
    /// </summary>
    public class AlyStatusToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var status = (AlyClientStatus)value;
            return (status == AlyClientStatus.DiscoveredUpdate ||
                    status == AlyClientStatus.DownloadingUpdate ||
                    status == AlyClientStatus.DownloadedUpdate)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return null;
        }
    }
}
