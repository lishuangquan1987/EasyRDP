using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;
using System.Windows.Forms;
using EasyRDP.Core.Protocol;
using EasyRDP.Core.Transport;

namespace EasyRDP.Client
{
    public class RemoteDesktopForm : Form
    {
        private ClientTransport _transport;
        private SequenceTracker _tcpSeq = new SequenceTracker();
        private volatile bool _running;
        private volatile bool _connected;

        private PictureBox _screenBox;
        private Label _statusLabel;
        private Bitmap _frameBitmap;
        private byte[] _frameBuffer;
        private int _screenWidth = 1920;
        private int _screenHeight = 1080;
        private int _frameCount;
        private DateTime _lastFpsTime = DateTime.Now;
        private DateTime _lastAckTime = DateTime.Now;

        public RemoteDesktopForm()
        {
            Text = "EasyRDP Client";
            Size = new Size(1024, 768);
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;

            _screenBox = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Black
            };
            Controls.Add(_screenBox);

            _statusLabel = new Label
            {
                Dock = DockStyle.Bottom,
                Text = "Disconnected",
                TextAlign = ContentAlignment.MiddleLeft,
                Height = 24
            };
            Controls.Add(_statusLabel);

            var menuStrip = new MenuStrip();
            var connectMenu = new ToolStripMenuItem("Connect", null, OnConnectClick);
            menuStrip.Items.Add(connectMenu);
            MainMenuStrip = menuStrip;
            Controls.Add(menuStrip);

            FormClosing += OnFormClosing;

            // Input events
            _screenBox.MouseMove += OnMouseMove;
            _screenBox.MouseDown += OnMouseDown;
            _screenBox.MouseUp += OnMouseUp;
            _screenBox.MouseWheel += OnMouseWheel;
            KeyDown += OnKeyDownEvent;
            KeyUp += OnKeyUpEvent;
        }

        private void OnConnectClick(object sender, EventArgs e)
        {
            if (_connected) return;

            string host = "127.0.0.1";
            var input = Microsoft.VisualBasic.Interaction.InputBox(
                "Server address:", "Connect", host, -1, -1);
            if (string.IsNullOrEmpty(input)) return;
            host = input;

            Connect(host);
        }

        private void Connect(string host)
        {
            _statusLabel.Text = string.Format("Connecting to {0}...", host);
            Application.DoEvents();

            _transport = new ClientTransport();
            _transport.OnLog = (level, msg) =>
                BeginInvoke(new Action(() => _statusLabel.Text = msg));
            _transport.MessageReceived += OnMessage;
            _transport.Disconnected += (s, a) =>
            {
                _connected = false;
                BeginInvoke(new Action(() => _statusLabel.Text = "Disconnected"));
            };

            if (!_transport.Connect(host, ProtocolConstants.DefaultTcpPort, TransportMode.Tcp, 5000))
            {
                _statusLabel.Text = "Connection failed!";
                return;
            }

            // Handshake
            var req = new HandshakeReqMessage
            {
                AuthToken = "easyrdp-demo",
                ScreenWidth = 1920,
                ScreenHeight = 1080,
                CompressType = CompressType.Zlib
            };
            byte[] data = MessageCodec.Encode(MessageType.HandshakeReq, _tcpSeq.Next(), req);
            _transport.Send(data);

            _connected = true;
            _running = true;
            _frameCount = 0;

            // Start keepalive
            new Thread(KeepAliveLoop) { IsBackground = true }.Start();

            // Start FPS counter
            new Thread(FpsLoop) { IsBackground = true }.Start();

            _statusLabel.Text = "Connected, waiting for frames...";
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
                    HandleCursorUpdate((CursorUpdateMessage)msg.Body);
                    break;
                case MessageType.ClipboardData:
                    HandleClipboard((ClipboardDataMessage)msg.Body);
                    break;
                case MessageType.KeepAliveAck:
                    _lastAckTime = DateTime.Now;
                    break;
                case MessageType.Disconnect:
                    _connected = false;
                    BeginInvoke(new Action(() => _statusLabel.Text = "Server disconnected"));
                    break;
            }
        }

        private void HandleHandshakeRes(HandshakeResMessage res)
        {
            if (res.Result == HandshakeResult.Success)
            {
                _screenWidth = res.ScreenWidth;
                _screenHeight = res.ScreenHeight;
                BeginInvoke(new Action(() =>
                {
                    _statusLabel.Text = string.Format("Connected: {0}x{1}, Deflate compression",
                        _screenWidth, _screenHeight);
                    // Resize form to fit screen
                    ClientSize = new Size(
                        Math.Min(_screenWidth, Screen.PrimaryScreen.WorkingArea.Width - 100),
                        Math.Min(_screenHeight, Screen.PrimaryScreen.WorkingArea.Height - 150));
                }));
            }
            else
            {
                BeginInvoke(new Action(() =>
                    _statusLabel.Text = string.Format("Handshake failed: {0}", res.Result)));
            }
        }

        private void HandleScreenFrame(ScreenFrameMessage frame)
        {
            if (frame.Rects == null || frame.Rects.Length == 0) return;

            if (frame.FrameType == FrameType.Full)
            {
                var rect = frame.Rects[0];
                int w = rect.Width, h = rect.Height;
                byte[] pixels = DecompressPixels(frame, w * h * 4);
                if (pixels == null || pixels.Length < w * h * 4) return;
                _frameBuffer = pixels;
                _screenWidth = w;
                _screenHeight = h;
                BeginInvoke(new Action(() => RenderFrame(w, h, pixels)));
            }
            else // Delta: merge rects into frame buffer
            {
                if (_frameBuffer == null) return;
                byte[] allPixels = DecompressPixels(frame, frame.Pixels.Length * 4);
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
                BeginInvoke(new Action(() => RenderFrame(_screenWidth, _screenHeight, _frameBuffer)));
            }
            Interlocked.Increment(ref _frameCount);
        }

        private byte[] DecompressPixels(ScreenFrameMessage frame, int rawSize)
        {
            if (frame.Compress == CompressType.Zlib)
                return CompressHelper.Decompress(frame.Pixels, CompressType.Zlib, rawSize);
            return frame.Pixels;
        }

        private void RenderFrame(int w, int h, byte[] pixels)
        {
            try
            {
                if (_frameBitmap == null || _frameBitmap.Width != w || _frameBitmap.Height != h)
                {
                    _frameBitmap?.Dispose();
                    _frameBitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                }

                var bmpData = _frameBitmap.LockBits(
                    new Rectangle(0, 0, w, h),
                    ImageLockMode.WriteOnly,
                    PixelFormat.Format32bppArgb);

                // Copy BGRA → ARGB (swap R and B)
                for (int y = 0; y < h; y++)
                {
                    int srcOffset = y * w * 4;
                    IntPtr dstRow = IntPtr.Add(bmpData.Scan0, y * bmpData.Stride);
                    for (int x = 0; x < w; x++)
                    {
                        int i = srcOffset + x * 4;
                        byte b = pixels[i];
                        byte g = pixels[i + 1];
                        byte r = pixels[i + 2];
                        byte a = pixels[i + 3];
                        int color = (a << 24) | (r << 16) | (g << 8) | b;
                        System.Runtime.InteropServices.Marshal.WriteInt32(
                            IntPtr.Add(dstRow, x * 4), color);
                    }
                }

                _frameBitmap.UnlockBits(bmpData);

                var oldImage = _screenBox.Image;
                _screenBox.Image = _frameBitmap;
                oldImage?.Dispose();
            }
            catch (Exception) { /* skip bad frames */ }
        }

        private void HandleCursorUpdate(CursorUpdateMessage cursor)
        {
            // For now, just track cursor position
            if (cursor.Visible)
            {
                BeginInvoke(new Action(() =>
                {
                    Cursor.Position = PointToScreen(new Point(
                        cursor.X * _screenBox.Width / Math.Max(_screenWidth, 1),
                        cursor.Y * _screenBox.Height / Math.Max(_screenHeight, 1)));
                }));
            }
        }

        private void HandleClipboard(ClipboardDataMessage clip)
        {
            if (clip.Format == ClipboardFormat.UnicodeText)
            {
                try { Clipboard.SetText(clip.Text); }
                catch { }
            }
        }

        // ── Input events → send to server ──────────────────────

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!_connected) return;
            SendInput(InputEventType.MouseMove, new InputUnit
            {
                Absolute = false,
                X = (short)e.X, Y = (short)e.Y
            });
        }

        private void OnMouseDown(object sender, MouseEventArgs e)
        {
            if (!_connected) return;
            byte btn = (byte)MapMouseButton(e.Button);
            SendInput(InputEventType.MouseDown, new InputUnit { Button = btn });
        }

        private void OnMouseUp(object sender, MouseEventArgs e)
        {
            if (!_connected) return;
            byte btn = (byte)MapMouseButton(e.Button);
            SendInput(InputEventType.MouseUp, new InputUnit { Button = btn });
        }

        private void OnMouseWheel(object sender, MouseEventArgs e)
        {
            if (!_connected) return;
            SendInput(InputEventType.MouseWheel, new InputUnit
            {
                WheelDelta = (short)(e.Delta / 120 * 120)
            });
        }

        private void OnKeyDownEvent(object sender, KeyEventArgs e)
        {
            if (!_connected) return;
            byte vk = (byte)MapKey(e.KeyCode);
            SendInput(InputEventType.KeyDown, new InputUnit { VirtualKey = vk });
        }

        private void OnKeyUpEvent(object sender, KeyEventArgs e)
        {
            if (!_connected) return;
            byte vk = (byte)MapKey(e.KeyCode);
            SendInput(InputEventType.KeyUp, new InputUnit { VirtualKey = vk });
        }

        private void SendInput(InputEventType type, InputUnit unit)
        {
            if (_transport == null || !_connected) return;

            var msg = new InputEventMessage
            {
                EventType = type,
                Units = new[] { unit }
            };
            byte[] data = MessageCodec.Encode(MessageType.InputEvent, _tcpSeq.Next(), msg);
            _transport.Send(data);
        }

        private static int MapMouseButton(MouseButtons btn)
        {
            switch (btn)
            {
                case MouseButtons.Left: return 0;
                case MouseButtons.Right: return 1;
                case MouseButtons.Middle: return 2;
                case MouseButtons.XButton1: return 3;
                case MouseButtons.XButton2: return 4;
                default: return 0;
            }
        }

        private static int MapKey(Keys key)
        {
            // Simple mapping — for full VK mapping, use a dictionary
            if (key >= Keys.A && key <= Keys.Z)
                return (int)(Keys.A) + ((int)key - (int)Keys.A);
            if (key >= Keys.D0 && key <= Keys.D9)
                return (int)(Keys.D0) + ((int)key - (int)Keys.D0);
            if (key >= Keys.F1 && key <= Keys.F12)
                return 0x70 + ((int)key - (int)Keys.F1);

            switch (key)
            {
                case Keys.Back: return 0x08;
                case Keys.Tab: return 0x09;
                case Keys.Return: return 0x0D;
                case Keys.ShiftKey: return 0x10;
                case Keys.ControlKey: return 0x11;
                case Keys.Menu: return 0x12; // Alt
                case Keys.Pause: return 0x13;
                case Keys.CapsLock: return 0x14;
                case Keys.Escape: return 0x1B;
                case Keys.Space: return 0x20;
                case Keys.PageUp: return 0x21;
                case Keys.PageDown: return 0x22;
                case Keys.End: return 0x23;
                case Keys.Home: return 0x24;
                case Keys.Left: return 0x25;
                case Keys.Up: return 0x26;
                case Keys.Right: return 0x27;
                case Keys.Down: return 0x28;
                case Keys.Insert: return 0x2D;
                case Keys.Delete: return 0x2E;
                case Keys.LWin: return 0x5B;
                case Keys.RWin: return 0x5C;
                case Keys.NumPad0: return 0x60;
                case Keys.NumPad1: return 0x61;
                case Keys.NumPad2: return 0x62;
                case Keys.NumPad3: return 0x63;
                case Keys.NumPad4: return 0x64;
                case Keys.NumPad5: return 0x65;
                case Keys.NumPad6: return 0x66;
                case Keys.NumPad7: return 0x67;
                case Keys.NumPad8: return 0x68;
                case Keys.NumPad9: return 0x69;
                default: return (int)key;
            }
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            _running = false;
            if (_connected)
            {
                var disconnect = new DisconnectMessage { Reason = DisconnectReason.UserDisconnect };
                byte[] data = MessageCodec.Encode(MessageType.Disconnect, _tcpSeq.Next(), disconnect);
                _transport?.Send(data);
                _connected = false;
            }
            _transport?.Disconnect();
            _frameBitmap?.Dispose();
        }

        private void KeepAliveLoop()
        {
            _lastAckTime = DateTime.Now;
            while (_running && _connected)
            {
                var keepAlive = new KeepAliveMessage();
                byte[] data = MessageCodec.Encode(MessageType.KeepAlive, _tcpSeq.Next(), keepAlive);
                if (!_transport.Send(data)) break;
                Thread.Sleep(ProtocolConstants.KeepAliveIntervalMs);

                if ((DateTime.Now - _lastAckTime).TotalMilliseconds > ProtocolConstants.KeepAliveTimeoutMs)
                {
                    BeginInvoke(new Action(() => _statusLabel.Text = "Connection timeout"));
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
                BeginInvoke(new Action(() =>
                    Text = string.Format("EasyRDP Client — {0:F0} FPS", fps)));
            }
        }

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string[] args = Environment.GetCommandLineArgs();
            string host = "127.0.0.1";
            if (args.Length >= 2) host = args[1];

            var form = new RemoteDesktopForm();
            form.Shown += (s, e) => form.Connect(host);
            Application.Run(form);
        }
    }
}
