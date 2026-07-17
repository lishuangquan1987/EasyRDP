using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using Avalonia.Media;
using Avalonia.Threading;
using EasyRDP.Client.Common;
using EasyRDP.Core.Protocol;
using EasyRDP.Client.Avalonia.Services;

namespace EasyRDP.Client.Avalonia.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
    private readonly ConnectionManager _conn = new();
    private readonly FrameBuffer _frameBuf = new();
    private readonly InputEncoder _inputEnc = new();
    private readonly ClipboardSyncEngine _clipSync = new();
    private readonly KeepAliveEngine _keepAlive = new();
    private readonly AvaloniaRenderEngine _render = new();
    private readonly AvaloniaInputCapturer _inputCap;
    private readonly AvaloniaClipboardProvider _clipProv = new();
    private CancellationTokenSource? _clipCts;
    private volatile bool _running;

    private string _host = "127.0.0.1";
    private int _port = 8750;
    private string _token = "easyrdp-demo";
    private bool _isConnected;
    private bool _isConnecting;
    private string _status = "未连接";
    private double _fps;
    private int _prevFrameCount;

    public string Host { get => _host; set { _host = value; Notify(); } }
    public int Port { get => _port; set { _port = value; Notify(); } }
    public string Token { get => _token; set { _token = value; Notify(); } }
    public bool IsConnected { get => _isConnected; set { _isConnected = value; Notify(); Notify(nameof(IsDisconnected)); } }
    public bool IsDisconnected => !_isConnected;
    public bool IsConnecting { get => _isConnecting; set { _isConnecting = value; Notify(); } }
    public string Status { get => _status; set { _status = value; Notify(); } }
    public double Fps { get => _fps; set { _fps = value; Notify(); } }
    public IImage? FrameSource => _render.Source;
    public AvaloniaInputCapturer InputCapturer => _inputCap;
    public SequenceTracker SeqTracker => _conn.SeqTracker;

    public RelayCommand ConnectCommand { get; }
    public RelayCommand DisconnectCommand { get; }

    public MainViewModel()
    {
        _inputCap = new AvaloniaInputCapturer(_inputEnc);
        ConnectCommand = new RelayCommand(Connect, () => !IsConnected && !IsConnecting);
        DisconnectCommand = new RelayCommand(Disconnect, () => IsConnected);
    }

    private void Connect()
    {
        IsConnecting = true;
        Status = "连接中...";

        _conn.Connected += () => Dispatcher.UIThread.Post(OnConnected);
        _conn.ConnectionFailed += r => Dispatcher.UIThread.Post(() => { Status = r; IsConnecting = false; });
        _conn.Disconnected += r => Dispatcher.UIThread.Post(() => OnDisconnected(r));
        _conn.MessageReceived += OnMessage;

        new Thread(() =>
        {
            bool ok = _conn.Connect(_host, _port, 5000, _token);
            if (!ok) Dispatcher.UIThread.Post(() => { Status = "连接失败"; IsConnecting = false; });
        }) { IsBackground = true }.Start();
    }

    private void OnConnected()
    {
        _inputCap.UpdateScreenSize(_conn.RemoteScreenWidth, _conn.RemoteScreenHeight);
        _render.Resize(_conn.RemoteScreenWidth, _conn.RemoteScreenHeight);

        _keepAlive.Start(() => _conn.SendMessage(MessageType.KeepAlive, new KeepAliveMessage()));
        _keepAlive.Timeout += () => Dispatcher.UIThread.Post(() => DisconnectCommand.Execute(null));

        _clipCts = new CancellationTokenSource();
        var ct = _clipCts.Token;
        new Thread(() => ClipboardLoop(ct)) { IsBackground = true, Name = "EasyRDP-Ava-Clip" }.Start();

        _running = true;
        _prevFrameCount = 0;
        new Thread(FpsLoop) { IsBackground = true, Name = "EasyRDP-Ava-Fps" }.Start();

        IsConnected = true;
        IsConnecting = false;
        Status = $"已连接 {_conn.RemoteScreenWidth}x{_conn.RemoteScreenHeight}";
    }

    private void OnDisconnected(string reason)
    {
        _keepAlive.Stop();
        if (_clipCts != null) { _clipCts.Cancel(); _clipCts = null; }
        _frameBuf.Reset();
        _running = false;
        IsConnected = false;
        Status = reason;
    }

    private void Disconnect() => _conn.Disconnect("用户断开");

    private void OnMessage(Message msg)
    {
        if (msg.Body == null) return;
        switch (msg.Header.Type)
        {
            case MessageType.ScreenFrame:
                _frameBuf.ProcessFrame((ScreenFrameMessage)msg.Body);
                if (_frameBuf.TryGetFrame(out var px, out var w, out var h))
                    Dispatcher.UIThread.Post(() => { _render.Render(px!, w, h); Notify(nameof(FrameSource)); });
                break;
            case MessageType.ClipboardData:
                var text = _clipSync.OnRemoteClipboard((ClipboardDataMessage)msg.Body);
                if (text != null) _clipProv.SetText(text);
                break;
            case MessageType.KeepAliveAck:
                _keepAlive.OnAckReceived();
                break;
            case MessageType.CursorUpdate:
                HandleCursorUpdate((CursorUpdateMessage)msg.Body);
                break;
        }
    }

    public void SendInput(byte[] data) { if (IsConnected) _conn.Transport.Send(data); }

    private void HandleCursorUpdate(CursorUpdateMessage msg)
    {
        if (!msg.Visible)
        {
            _render.SetCursor(false, 0, 0, null, 0, 0, 0, 0);
            return;
        }

        _render.SetCursor(true, msg.X, msg.Y,
            msg.ImageData != null && msg.ImageData.Length > 0 ? msg.ImageData : null,
            msg.Width, msg.Height, msg.HotspotX, msg.HotspotY);
    }

    private void ClipboardLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var text = _clipProv.GetText();
                var data = _clipSync.TryEncodeLocalChange(text, _conn.SeqTracker.Next());
                if (data != null) _conn.Transport.Send(data);
            }
            catch { }
            try { Thread.Sleep(300); } catch { break; }
        }
    }

    private void FpsLoop()
    {
        while (_running)
        {
            Thread.Sleep(2000);
            int cur = _frameBuf.FrameCount;
            Fps = (cur - _prevFrameCount) / 2.0;
            _prevFrameCount = cur;
        }
    }

    public void Cleanup()
    {
        _running = false;
        _keepAlive.Stop();
        if (_clipCts != null) { _clipCts.Cancel(); _clipCts = null; }
        _conn.Dispose();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Simple ICommand for Avalonia (no WPF CommandManager dependency).
/// </summary>
public class RelayCommand : System.Windows.Input.ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;
    public RelayCommand(Action execute, Func<bool>? canExecute = null) { _execute = execute; _canExecute = canExecute; }
    public bool CanExecute(object? p) => _canExecute?.Invoke() ?? true;
    public event EventHandler? CanExecuteChanged;
    public void Execute(object? p) => _execute();
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
