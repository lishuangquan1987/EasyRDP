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
        { var d = _vm.InputCapturer.EncodeMouseMove(e, (UIElement)s, _vm.SeqTracker.Next()); _vm.SendInput(d); }
        private void OnMouseDown(object s, MouseButtonEventArgs e)
        { var d = _vm.InputCapturer.EncodeMouseButton(e, true, _vm.SeqTracker.Next()); _vm.SendInput(d); }
        private void OnMouseUp(object s, MouseButtonEventArgs e)
        { var d = _vm.InputCapturer.EncodeMouseButton(e, false, _vm.SeqTracker.Next()); _vm.SendInput(d); }
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
