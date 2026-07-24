using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using EasyRDP.Core.Protocol;
using EasyRDP.Core.Rendering;
using EasyRDP.Core.Transport;

namespace EasyRDP.Client.Wpf;

/// <summary>
/// 客户端主窗口。支持两种模式：Render Test（本地测试帧）和 Connect（远程桌面）。
/// </summary>
public partial class MainWindow : Window
{
    private WpfRenderTarget? _renderTarget;
    private FrameBuffer? _frameBuffer;
    private ClientStreamSession? _streamSession;
    private ClientInputSession? _inputSession;
    private TcpTransportClient? _transport;
    private volatile bool _running;
    private int _testFrameSeq;

    public MainWindow()
    {
        InitializeComponent();
    }

    // ====== Connect mode ======

    private async void ConnectBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;

        string host = HostBox.Text.Trim();
        if (!int.TryParse(PortBox.Text, out int port))
            port = 2000;

        ConnectBtn.IsEnabled = false;
        StartBtn.IsEnabled = false;
        StopBtn.IsEnabled = true;
        StatusText.Text = "Connecting...";

        _transport = new TcpTransportClient();
        _transport.OnLog = (msg) => Dispatcher.Invoke(() => StatusText.Text = msg);

        bool connected = await Task.Run(() => _transport.Connect(host, port, 5000));
        if (!connected)
        {
            StatusText.Text = "Connection failed";
            ConnectBtn.IsEnabled = true;
            StartBtn.IsEnabled = true;
            StopBtn.IsEnabled = false;
            return;
        }

        // Send handshake
        var handshakeReq = new HandshakeReq
        {
            Version = Constants.ProtocolVersion,
            Capabilities = DecoderFactory.GetAvailableCodecs(),
            Username = "admin",
            Password = ""
        };
        byte[] reqPayload = handshakeReq.Pack();
        MessageReassembler.FragAndSend(0, (byte)MessageType.HandshakeReq, reqPayload,
            (sid, data) => _transport.Send(data), 0);

        // Wait for HandshakeRes via ClientStreamSession
        var handshakeReassembler = new MessageReassembler();
        MessageReceivedEventArgs? handshakeResponse = null;
        var waitHandle = new ManualResetEventSlim(false);

        handshakeReassembler.MessageReceived += (s, args) =>
        {
            if (args.MessageType == (byte)MessageType.HandshakeRes)
            {
                handshakeResponse = args;
                waitHandle.Set();
            }
        };

        _transport.DataReceived += (s, args) => handshakeReassembler.OnFragment(args);

        bool gotResponse = await Task.Run(() => waitHandle.Wait(10000));
        _transport.DataReceived -= (s, args) => handshakeReassembler.OnFragment(args);

        if (!gotResponse || handshakeResponse == null)
        {
            StatusText.Text = "Handshake timeout";
            _transport.Disconnect();
            ConnectBtn.IsEnabled = true;
            StartBtn.IsEnabled = true;
            StopBtn.IsEnabled = false;
            return;
        }

        var handshakeRes = HandshakeRes.Unpack(handshakeResponse.Data);
        if (handshakeRes.Result != HandshakeResult.Success)
        {
            StatusText.Text = "Handshake failed: " + handshakeRes.Result;
            _transport.Disconnect();
            ConnectBtn.IsEnabled = true;
            StartBtn.IsEnabled = true;
            StopBtn.IsEnabled = false;
            return;
        }

        // Init render pipeline
        _renderTarget = new WpfRenderTarget();
        _frameBuffer = new FrameBuffer();
        RenderImage.Source = _renderTarget.Bitmap;

        _streamSession = new ClientStreamSession();
        _streamSession.RenderTarget = _renderTarget;
        _streamSession.InitPipeline(handshakeRes.Codec, handshakeRes.ScreenWidth, handshakeRes.ScreenHeight);

        _inputSession = new ClientInputSession();
        _inputSession.Start(_transport, handshakeRes.ScreenWidth, handshakeRes.ScreenHeight);

        _streamSession.Start(_transport);
        _running = true;

        StatusText.Text = string.Format("Connected: {0}x{1} [{2}]",
            handshakeRes.ScreenWidth, handshakeRes.ScreenHeight, handshakeRes.Codec);
    }

    // ====== Render Test mode ======

    private void StartBtn_Click(object sender, RoutedEventArgs e)
    {
        StartBtn.IsEnabled = false;
        ConnectBtn.IsEnabled = false;
        StopBtn.IsEnabled = true;
        StatusText.Text = "Running render test...";

        _renderTarget = new WpfRenderTarget();
        _frameBuffer = new FrameBuffer();
        _testFrameSeq = 0;
        _running = true;

        _renderTarget.Resize(640, 480);
        RenderImage.Source = _renderTarget.Bitmap;

        Task.Run(() => RenderTestLoop());
    }

    private void StopBtn_Click(object sender, RoutedEventArgs e)
    {
        _running = false;
        _streamSession?.Stop();
        _transport?.Disconnect();
        _transport = null;
        _streamSession = null;
        _inputSession = null;

        StopBtn.IsEnabled = false;
        StartBtn.IsEnabled = true;
        ConnectBtn.IsEnabled = true;
        StatusText.Text = "Stopped";
    }

    // ====== Input events (relay to server when connected) ======

    private void RenderImage_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_inputSession == null || !_running) return;
        var pos = e.GetPosition(RenderImage);
        int sx, sy;
        _inputSession.MapCoordinates(pos.X, pos.Y, RenderImage.ActualWidth, RenderImage.ActualHeight, out sx, out sy);
        var msg = new InputEventMessage { Type = InputEventType.MouseMove, X = sx, Y = sy };
        _inputSession.SendInput(msg);
    }

    private void RenderImage_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_inputSession == null || !_running) return;
        var btn = e.ChangedButton == MouseButton.Left ? 1
            : e.ChangedButton == MouseButton.Right ? 2 : 4;
        var msg = new InputEventMessage { Type = InputEventType.MouseDown, KeyCode = btn };
        _inputSession.SendInput(msg);
    }

    private void RenderImage_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_inputSession == null || !_running) return;
        var btn = e.ChangedButton == MouseButton.Left ? 1
            : e.ChangedButton == MouseButton.Right ? 2 : 4;
        var msg = new InputEventMessage { Type = InputEventType.MouseUp, KeyCode = btn };
        _inputSession.SendInput(msg);
    }

    private void RenderImage_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_inputSession == null || !_running) return;
        var msg = new InputEventMessage { Type = InputEventType.MouseWheel, WheelDelta = e.Delta };
        _inputSession.SendInput(msg);
    }

    // ====== Render test loop ======

    private void RenderTestLoop()
    {
        int w = 640, h = 480;
        int frameSize = w * h * 4;
        int boxX = 0, boxY = 0;
        int dx = 3, dy = 2;

        while (_running)
        {
            byte[]? writeSlot = _frameBuffer!.BorrowWriteBuffer(frameSize);
            if (writeSlot == null) { Thread.Sleep(1); continue; }

            // Gradient background
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int off = (y * w + x) * 4;
                    writeSlot[off] = (byte)(x % 256);
                    writeSlot[off + 1] = (byte)(y % 256);
                    writeSlot[off + 2] = 128;
                    writeSlot[off + 3] = 255;
                }
            }

            // Moving box
            boxX += dx; boxY += dy;
            if (boxX < 0 || boxX + 80 > w) dx = -dx;
            if (boxY < 0 || boxY + 80 > h) dy = -dy;

            byte r = (byte)((_testFrameSeq * 5) % 256);
            byte g = (byte)((_testFrameSeq * 7 + 85) % 256);
            byte b = (byte)((_testFrameSeq * 11 + 170) % 256);

            for (int y = Math.Max(0, boxY); y < Math.Min(h, boxY + 80); y++)
            {
                for (int x = Math.Max(0, boxX); x < Math.Min(w, boxX + 80); x++)
                {
                    int off = (y * w + x) * 4;
                    writeSlot[off] = b;
                    writeSlot[off + 1] = g;
                    writeSlot[off + 2] = r;
                    writeSlot[off + 3] = 255;
                }
            }

            _frameBuffer.CommitFrame(w, h);

            if (_frameBuffer.TryBorrowReadFrame(out ReadFrameRef frame))
            {
                try
                {
                    Dispatcher.Invoke(() => _renderTarget?.RenderFrame(frame.Pixels, frame.Width, frame.Height));
                }
                finally { _frameBuffer.ReleaseReadFrame(); }
            }

            _testFrameSeq++;
            Thread.Sleep(16);
        }

        Dispatcher.Invoke(() => _frameBuffer?.Reset());
    }
}
