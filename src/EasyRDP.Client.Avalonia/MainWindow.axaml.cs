using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Interactivity;
using Avalonia.Threading;
using EasyRDP.Core.Protocol;
using EasyRDP.Core.Transport;

namespace EasyRDP.Client.Avalonia;

public partial class MainWindow : Window
{
    private ClientTransport _transport;
    private SequenceTracker _tcpSeq = new();
    private volatile bool _running;
    private volatile bool _connected;

    private WriteableBitmap _frameBitmap;
    private byte[] _frameBuffer;
    private int _screenWidth = 1920;
    private int _screenHeight = 1080;
    private int _frameCount;
    private DateTime _lastAckTime = DateTime.Now;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnConnectClick(object sender, RoutedEventArgs e)
    {
        if (_connected) return;
        Connect("127.0.0.1");
    }

    private async void Connect(string host)
    {
        StatusBar.Text = string.Format("Connecting to {0}...", host);

        _transport = new ClientTransport();
        _transport.OnLog = (level, msg) =>
            Dispatcher.UIThread.Post(() => StatusBar.Text = msg);
        _transport.MessageReceived += OnMessage;
        _transport.Disconnected += (s, a) =>
        {
            _connected = false;
            Dispatcher.UIThread.Post(() => StatusBar.Text = "Disconnected");
        };

        if (!_transport.Connect(host, ProtocolConstants.DefaultTcpPort, TransportMode.Tcp, 5000))
        {
            StatusBar.Text = "Connection failed!";
            return;
        }

        var req = new HandshakeReqMessage
        {
            AuthToken = "easyrdp-demo",
            ScreenWidth = 1920, ScreenHeight = 1080,
            CompressType = CompressType.Zlib
        };
        _transport.Send(MessageCodec.Encode(MessageType.HandshakeReq, _tcpSeq.Next(), req));

        _connected = true;
        _running = true;
        _frameCount = 0;

        _ = Task.Run(KeepAliveLoop);
        _ = Task.Run(FpsLoop);

        StatusBar.Text = "Connected, waiting for frames...";
    }

    private void OnMessage(object sender, MessageReceivedEventArgs e)
    {
        var msg = e.Message;
        if (msg == null || msg.Body == null) return;

        switch (msg.Header.Type)
        {
            case MessageType.HandshakeRes:
                HandleHandshakeRes((HandshakeResMessage)msg.Body);
                break;
            case MessageType.ScreenFrame:
                HandleScreenFrame((ScreenFrameMessage)msg.Body);
                break;
            case MessageType.CursorUpdate:
                // TODO
                break;
            case MessageType.ClipboardData:
                HandleClipboard((ClipboardDataMessage)msg.Body);
                break;
            case MessageType.KeepAliveAck:
                _lastAckTime = DateTime.Now;
                break;
            case MessageType.Disconnect:
                _connected = false;
                Dispatcher.UIThread.Post(() => StatusBar.Text = "Server disconnected");
                break;
        }
    }

    private void HandleHandshakeRes(HandshakeResMessage res)
    {
        if (res.Result == HandshakeResult.Success)
        {
            _screenWidth = res.ScreenWidth;
            _screenHeight = res.ScreenHeight;
            Dispatcher.UIThread.Post(() =>
            {
                StatusBar.Text = string.Format("Connected: {0}x{1}", _screenWidth, _screenHeight);
                ClientSize = new global::Avalonia.Size(
                    Math.Min(_screenWidth, Screens.Primary.WorkingArea.Width - 100),
                    Math.Min(_screenHeight, Screens.Primary.WorkingArea.Height - 150));
            });
        }
        else
        {
            Dispatcher.UIThread.Post(() =>
                StatusBar.Text = string.Format("Handshake failed: {0}", res.Result));
        }
    }

    private void HandleScreenFrame(ScreenFrameMessage frame)
    {
        if (frame.Rects == null || frame.Rects.Length == 0) return;

        if (frame.FrameType == FrameType.Full)
        {
            var rect = frame.Rects[0];
            int w = rect.Width, h = rect.Height;
            byte[] pixels = Decompress(frame, w * h * 4);
            if (pixels == null || pixels.Length < w * h * 4) return;
            _frameBuffer = pixels;
            _screenWidth = w;
            _screenHeight = h;
            Dispatcher.UIThread.Post(() => RenderFrame(w, h, pixels));
        }
        else
        {
            if (_frameBuffer == null) return;
            byte[] allPixels = Decompress(frame, frame.Pixels.Length * 4);
            if (allPixels == null) return;
            int stride = _screenWidth * 4;
            foreach (var rect in frame.Rects)
            {
                int tileBytes = rect.Width * rect.Height * 4;
                if ((int)rect.Offset + tileBytes > allPixels.Length) continue;
                for (int ty = 0; ty < rect.Height; ty++)
                {
                    int src = (int)rect.Offset + ty * rect.Width * 4;
                    int dst = (rect.Y + ty) * stride + rect.X * 4;
                    if (dst + rect.Width * 4 <= _frameBuffer.Length)
                        Array.Copy(allPixels, src, _frameBuffer, dst, rect.Width * 4);
                }
            }
            Dispatcher.UIThread.Post(() => RenderFrame(_screenWidth, _screenHeight, _frameBuffer));
        }
        Interlocked.Increment(ref _frameCount);
    }

    private byte[] Decompress(ScreenFrameMessage frame, int rawSize)
    {
        if (frame.Compress == CompressType.Zlib)
            return CompressHelper.Decompress(frame.Pixels, CompressType.Zlib, rawSize);
        return frame.Pixels;
    }

    private unsafe void RenderFrame(int w, int h, byte[] pixels)
    {
        try
        {
            if (_frameBitmap == null || _frameBitmap.PixelSize.Width != w || _frameBitmap.PixelSize.Height != h)
            {
                _frameBitmap?.Dispose();
                _frameBitmap = new WriteableBitmap(
                    new PixelSize(w, h), new Vector(96, 96));
                ScreenImage.Source = _frameBitmap;
            }

            using var fb = _frameBitmap.Lock();
            // pixels are already BGRA32, just copy
            fixed (byte* src = pixels)
            {
                var span = new Span<byte>((void*)fb.Address, w * h * 4);
                new Span<byte>(src, w * h * 4).CopyTo(span);
            }
        }
        catch { }
    }

    private void HandleClipboard(ClipboardDataMessage clip)
    {
        // Clipboard sync received from server
    }

    // ── Input events ──────────────────────────────────────────

    private void OnPointerMoved(object sender, PointerEventArgs e)
    {
        if (!_connected) return;
        var pos = e.GetPosition(ScreenImage);
        SendInput(InputEventType.MouseMove, new InputUnit
        {
            Absolute = false, X = (short)pos.X, Y = (short)pos.Y
        });
    }

    private void OnPointerPressed(object sender, PointerPressedEventArgs e)
    {
        if (!_connected) return;
        var props = e.GetCurrentPoint(ScreenImage).Properties;
        byte btn = 0;
        if (props.IsLeftButtonPressed) btn = 0;
        else if (props.IsRightButtonPressed) btn = 1;
        else if (props.IsMiddleButtonPressed) btn = 2;
        SendInput(InputEventType.MouseDown, new InputUnit { Button = btn });
    }

    private void OnPointerReleased(object sender, PointerReleasedEventArgs e)
    {
        if (!_connected) return;
        SendInput(InputEventType.MouseUp, new InputUnit { Button = 0 });
    }

    private void OnPointerWheel(object sender, PointerWheelEventArgs e)
    {
        if (!_connected) return;
        SendInput(InputEventType.MouseWheel, new InputUnit
        {
            WheelDelta = (short)(e.Delta.Y * 120)
        });
    }

    private void OnKeyDownEvent(object sender, KeyEventArgs e)
    {
        if (!_connected) return;
        byte vk = (byte)MapKey(e.Key);
        SendInput(InputEventType.KeyDown, new InputUnit { VirtualKey = vk });
    }

    private void OnKeyUpEvent(object sender, KeyEventArgs e)
    {
        if (!_connected) return;
        byte vk = (byte)MapKey(e.Key);
        SendInput(InputEventType.KeyUp, new InputUnit { VirtualKey = vk });
    }

    private void SendInput(InputEventType type, InputUnit unit)
    {
        if (_transport == null || !_connected) return;
        var msg = new InputEventMessage { EventType = type, Units = new[] { unit } };
        _transport.Send(MessageCodec.Encode(MessageType.InputEvent, _tcpSeq.Next(), msg));
    }

    private static int MapKey(Key key) => key switch
    {
        >= Key.A and <= Key.Z => (int)Key.A + ((int)key - (int)Key.A),
        >= Key.D0 and <= Key.D9 => (int)Key.D0 + ((int)key - (int)Key.D0),
        >= Key.F1 and <= Key.F12 => 0x70 + ((int)key - (int)Key.F1),
        Key.Back => 0x08, Key.Tab => 0x09, Key.Return => 0x0D,
        Key.LeftShift or Key.RightShift => 0x10,
        Key.LeftCtrl or Key.RightCtrl => 0x11,
        Key.LeftAlt or Key.RightAlt => 0x12,
        Key.Escape => 0x1B, Key.Space => 0x20,
        Key.PageUp => 0x21, Key.PageDown => 0x22,
        Key.End => 0x23, Key.Home => 0x24,
        Key.Left => 0x25, Key.Up => 0x26, Key.Right => 0x27, Key.Down => 0x28,
        Key.Insert => 0x2D, Key.Delete => 0x2E,
        _ => (int)key
    };

    private void KeepAliveLoop()
    {
        _lastAckTime = DateTime.Now;
        while (_running && _connected)
        {
            var keepAlive = new KeepAliveMessage();
            if (!_transport.Send(MessageCodec.Encode(MessageType.KeepAlive, _tcpSeq.Next(), keepAlive))) break;
            Thread.Sleep(ProtocolConstants.KeepAliveIntervalMs);
            if ((DateTime.Now - _lastAckTime).TotalMilliseconds > ProtocolConstants.KeepAliveTimeoutMs)
            {
                Dispatcher.UIThread.Post(() => StatusBar.Text = "Connection timeout");
                break;
            }
        }
        if (_connected) { _connected = false; _running = false; }
    }

    private void FpsLoop()
    {
        while (_running)
        {
            Thread.Sleep(2000);
            int count = Interlocked.Exchange(ref _frameCount, 0);
            double fps = count / 2.0;
            Dispatcher.UIThread.Post(() =>
                Title = string.Format("EasyRDP (Avalonia) — {0:F0} FPS", fps));
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _running = false;
        if (_connected)
        {
            var disconnect = new DisconnectMessage { Reason = DisconnectReason.UserDisconnect };
            _transport?.Send(MessageCodec.Encode(MessageType.Disconnect, _tcpSeq.Next(), disconnect));
            _connected = false;
        }
        _transport?.Disconnect();
        _frameBitmap?.Dispose();
        base.OnClosing(e);
    }
}
