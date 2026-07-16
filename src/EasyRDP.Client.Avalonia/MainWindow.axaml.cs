using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using EasyRDP.Client.Avalonia.ViewModels;

namespace EasyRDP.Client.Avalonia;

public partial class MainWindow : Window
{
    private MainViewModel _vm = null!;

    public MainWindow()
    {
        _vm = new MainViewModel();
        DataContext = _vm;
        InitializeComponent();
    }

    private void OnPointerMoved(object? s, PointerEventArgs e) =>
        _vm.SendInput(_vm.InputCapturer.EncodePointerMove(e, (Control)s!, _vm.SeqTracker.Next()));
    private void OnPointerPressed(object? s, PointerPressedEventArgs e) =>
        _vm.SendInput(_vm.InputCapturer.EncodePointerPressed(e, (Control)s!, _vm.SeqTracker.Next()));
    private void OnPointerReleased(object? s, PointerReleasedEventArgs e) =>
        _vm.SendInput(_vm.InputCapturer.EncodePointerReleased(_vm.SeqTracker.Next()));
    private void OnPointerWheel(object? s, PointerWheelEventArgs e) =>
        _vm.SendInput(_vm.InputCapturer.EncodePointerWheel(e, _vm.SeqTracker.Next()));
    private void OnKeyDown(object? s, KeyEventArgs e) =>
        _vm.SendInput(_vm.InputCapturer.EncodeKey(e, true, _vm.SeqTracker.Next()));
    private void OnKeyUp(object? s, KeyEventArgs e) =>
        _vm.SendInput(_vm.InputCapturer.EncodeKey(e, false, _vm.SeqTracker.Next()));

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _vm.Cleanup();
        base.OnClosing(e);
    }
}
