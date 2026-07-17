using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using EasyRDP.Core.Protocol;
using EasyRDP.Core.Transport;
using EasyDesk.Core;
using EasyDesk.Core.Models;
using EasyDesk.Windows;

namespace EasyRDP.Server
{
    class Program
    {
        private static WindowsDesktopFactory _factory;
        private static IScreenCapturer _capturer;
        private static IInputSimulator _input;
        private static IClipboardService _clipboard;
        private static ICursorCapturer _cursor;
        private static CursorTracker _cursorTracker;
        private static string _lastClipboardText = "";

        private static TcpTransportServer _transport;
        private static volatile bool _running = true;
        private static int _maxClients = 10;
        private static readonly ConcurrentDictionary<uint, ClientState> _clients = new();

        private static string _authToken = "easyrdp-demo";
        private static CompressType _compressType = CompressType.Zlib;
        private static int _frameDelayMs = 66;
        private const int TileSize = 64; // keep for reference, DirtyRectDetector replaces tile-based logic

        private class ClientState : IDisposable
        {
            public uint SessionId;
            public CancellationTokenSource Cts;
            public SequenceTracker TcpSeq;
            public SequenceTracker FrameSeq;
            public bool Authenticated;
            public byte[] PrevPixels;
            public int PrevWidth;
            public int PrevHeight;

            public ClientState(uint id)
            {
                SessionId = id;
                Cts = new CancellationTokenSource();
                TcpSeq = new SequenceTracker();
                FrameSeq = new SequenceTracker();
            }

            public void Dispose()
            {
                Cts.Cancel();
                Cts.Dispose();
                PrevPixels = null;
            }
        }

        static void Main(string[] args)
        {
            Console.WriteLine("=== EasyRDP Server v1.0 ===");
            ServerConfig config = LoadConfig();

            _factory = new WindowsDesktopFactory();
            _capturer = _factory.CreateScreenCapturer();
            _input = _factory.CreateInputSimulator();
            _cursor = _factory.CreateCursorCapturer();
            _cursorTracker = new CursorTracker(
                delegate
                {
                    int cx, cy;
                    _cursor.GetCursorPosition(out cx, out cy);
                    return CursorPosition.Create((short)cx, (short)cy);
                },
                delegate
                {
                    var info = _cursor.GetCursorInfo();
                    if (info == null || info.ImageData == null || info.ImageData.Length == 0)
                        return null;
                    return new CursorShapeData
                    {
                        ImageData = info.ImageData,
                        Width = info.Width,
                        Height = info.Height,
                        HotspotX = info.HotspotX,
                        HotspotY = info.HotspotY
                    };
                });
            _cursorTracker.EnableShape = true;
            StartClipboardThread();

            _transport = new TcpTransportServer();
            _transport.OnLog = (level, msg) => Console.WriteLine("[{0}] {1}", level, msg);
            _transport.ClientConnected += OnClientConnected;
            _transport.ClientDisconnected += OnClientDisconnected;
            _transport.MessageReceived += OnMessageReceived;

            int port = config.Port;
            _cursorTracker.SendTo = (sid, data) => _transport.SendTo(sid, data);
            _transport.Start(port);

            // Clipboard monitor thread
            new Thread(ClipboardMonitorLoop) { IsBackground = true }.Start();

            Console.WriteLine("Server running on port {0}, compress={1}, fps={2}. Ctrl+C to stop.",
                port, _compressType, 1000 / _frameDelayMs);
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; _running = false; };

            while (_running) Thread.Sleep(1000);

            Console.WriteLine("Shutting down...");
            foreach (var kvp in _clients) kvp.Value.Dispose();
            _clients.Clear();
            _transport.Stop();
            _factory = null;
        }

        private static ServerConfig LoadConfig()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
            var defaults = new ServerConfig();
            if (!File.Exists(path)) { Console.WriteLine("Config not found, using defaults."); return defaults; }
            try
            {
                var cfg = JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(path));
                _authToken = cfg.AuthToken ?? defaults.AuthToken;
                _compressType = cfg.CompressType == "Zlib" ? CompressType.Zlib : CompressType.None;
                _frameDelayMs = 1000 / Math.Max(1, Math.Min(cfg.FrameRate, 60));
                Console.WriteLine("Config loaded: port={0}, compress={1}, fps={2}", cfg.Port, _compressType, 1000 / _frameDelayMs);
                return cfg;
            }
            catch (Exception ex) { Console.WriteLine("Config parse error: {0}, using defaults.", ex.Message); return defaults; }
        }

        private static void StartClipboardThread()
        {
            var initThread = new Thread(() => { _clipboard = _factory.CreateClipboardService(); });
            initThread.SetApartmentState(ApartmentState.STA);
            initThread.Start();
            initThread.Join();
        }

        private static void OnClientConnected(object sender, ConnectionEventArgs e)
        {
            if (_clients.Count >= _maxClients)
            {
                Console.WriteLine("Client {0} rejected: server busy ({1} clients)", e.SessionId, _clients.Count);
                _transport.Disconnect(e.SessionId);
                return;
            }
            Console.WriteLine("Client {0} connected: {1}", e.SessionId, e.RemoteEndPoint);
            _clients[e.SessionId] = new ClientState(e.SessionId);
        }

        private static void OnClientDisconnected(object sender, ConnectionEventArgs e)
        {
            Console.WriteLine("Client {0} disconnected", e.SessionId);
            if (_clients.TryRemove(e.SessionId, out var state)) state.Dispose();
            _cursorTracker.StopForClient(e.SessionId);
        }

        private static void OnMessageReceived(object sender, MessageReceivedEventArgs e)
        {
            var msg = e.Message;
            if (msg == null || msg.Body == null) return;

            ClientState state = null;
            if (e.SessionId != 0) _clients.TryGetValue(e.SessionId, out state);

            switch (msg.Header.Type)
            {
                case MessageType.HandshakeReq:
                    HandleHandshake((HandshakeReqMessage)msg.Body, e.SessionId);
                    break;
                case MessageType.InputEvent:
                    if (state != null && state.Authenticated) HandleInputEvent((InputEventMessage)msg.Body);
                    break;
                case MessageType.ClipboardData:
                    if (state != null && state.Authenticated) HandleClipboardData((ClipboardDataMessage)msg.Body);
                    break;
                case MessageType.KeepAlive:
                    if (state != null && state.Authenticated) SendKeepAliveAck(state);
                    break;
            }
        }

        private static void HandleHandshake(HandshakeReqMessage req, uint sessionId)
        {
            if (!_clients.TryGetValue(sessionId, out var state)) return;

            HandshakeResult result;
            if (req.AuthToken != _authToken)
            {
                result = HandshakeResult.AuthFailed;
                Console.WriteLine("Client {0} auth FAILED", sessionId);
            }
            else if (!((int)req.CompressType == (int)_compressType || req.CompressType == CompressType.None))
            {
                result = HandshakeResult.UnsupportedCompress;
                Console.WriteLine("Client {0} unsupported compress", sessionId);
            }
            else
            {
                result = HandshakeResult.Success;
                state.Authenticated = true;
                Console.WriteLine("Client {0} authenticated OK", sessionId);
            }

            var screenBounds = _capturer.GetPrimaryScreen();
            var res = new HandshakeResMessage
            {
                Result = result,
                SessionId = (result == HandshakeResult.Success) ? sessionId : 0,
                ScreenWidth = (ushort)screenBounds.Width,
                ScreenHeight = (ushort)screenBounds.Height,
                CompressType = _compressType
            };
            byte[] data = MessageCodec.Encode(MessageType.HandshakeRes, state.TcpSeq.Next(), res);
            _transport.SendTo(sessionId, data);

            if (result == HandshakeResult.Success)
            {
                var captState = state;
                new Thread(() => CaptureLoop(captState))
                { IsBackground = true, Name = string.Format("EasyRDP-Capture-{0}", sessionId) }.Start();
                _cursorTracker.StartForClient(sessionId);
            }
            else _transport.Disconnect(sessionId);
        }

        private static void HandleInputEvent(InputEventMessage evt)
        {
            if (evt.Units == null) return;
            foreach (var unit in evt.Units)
            {
                switch (evt.EventType)
                {
                    case InputEventType.MouseMove: _input.SendMouseMove(unit.X, unit.Y, unit.Absolute); break;
                    case InputEventType.MouseDown: _input.SendMouseButton((MouseButton)unit.Button, true); break;
                    case InputEventType.MouseUp: _input.SendMouseButton((MouseButton)unit.Button, false); break;
                    case InputEventType.MouseWheel: _input.SendMouseWheel(unit.WheelDelta); break;
                    case InputEventType.KeyDown: _input.SendKeyDown((VirtualKeyCode)unit.VirtualKey); break;
                    case InputEventType.KeyUp: _input.SendKeyUp((VirtualKeyCode)unit.VirtualKey); break;
                    case InputEventType.UnicodeText: _input.SendText(unit.Text); break;
                }
            }
        }

        // ── Bidirectional clipboard ────────────────────────────

        private static void HandleClipboardData(ClipboardDataMessage clip)
        {
            if (_clipboard == null || clip.Format != ClipboardFormat.UnicodeText) return;
            string text = clip.Text;
            _lastClipboardText = text; // avoid immediate re-send
            var opThread = new Thread(() =>
            {
                try { _clipboard.SetText(text); }
                catch (Exception ex) { Console.WriteLine("Clipboard error: {0}", ex.Message); }
            });
            opThread.SetApartmentState(ApartmentState.STA);
            opThread.IsBackground = true;
            opThread.Start();
            opThread.Join(2000);
        }

        private static void ClipboardMonitorLoop()
        {
            DateTime lastCheck = DateTime.MinValue;
            while (_running)
            {
                Thread.Sleep(300);
                if (_clipboard == null) continue;

                // Rate-limit clipboard polling
                if ((DateTime.Now - lastCheck).TotalMilliseconds < ProtocolConstants.ClipboardCooldownMs)
                    continue;

                var checkThread = new Thread(() =>
                {
                    try
                    {
                        string text = _clipboard.GetText();
                        if (!string.IsNullOrEmpty(text) && text != _lastClipboardText)
                        {
                            _lastClipboardText = text;
                            var clipMsg = new ClipboardDataMessage { Format = ClipboardFormat.UnicodeText, Text = text };
                            byte[] data = MessageCodec.Encode(MessageType.ClipboardData, 0, clipMsg);
                            foreach (var kvp in _clients)
                                if (kvp.Value.Authenticated)
                                    _transport.SendTo(kvp.Key, data);
                        }
                    }
                    catch { }
                });
                checkThread.SetApartmentState(ApartmentState.STA);
                checkThread.IsBackground = true;
                checkThread.Start();
                checkThread.Join(1000);
                lastCheck = DateTime.Now;
            }
        }

        private static void SendKeepAliveAck(ClientState state)
        {
            var ack = new KeepAliveAckMessage();
            byte[] data = MessageCodec.Encode(MessageType.KeepAliveAck, state.TcpSeq.Next(), ack);
            _transport.SendTo(state.SessionId, data);
        }

        // ── Incremental screen capture ─────────────────────────

        private static void CaptureLoop(ClientState state)
        {
            uint sessionId = state.SessionId;
            var ct = state.Cts.Token;
            Console.WriteLine("Capture started for client {0}", sessionId);
            int keyFrameInterval = 30; // full frame every N frames
            int frameCount = 0;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var frame = _capturer.CaptureScreen();
                    try
                    {
                        if (frame.Scan0 == IntPtr.Zero) continue;
                        int w = frame.Width, h = frame.Height;
                        long pixelCount = (long)w * h;
                        int pixelSize = (int)(pixelCount * 4);
                        byte[] curPixels = new byte[pixelSize];
                        System.Runtime.InteropServices.Marshal.Copy(frame.Scan0, curPixels, 0, pixelSize);

                        bool isKeyFrame = (frameCount % keyFrameInterval == 0)
                            || state.PrevPixels == null
                            || state.PrevWidth != w
                            || state.PrevHeight != h;

                        ScreenFrameMessage msg;
                        if (isKeyFrame)
                        {
                            msg = BuildFullFrame(w, h, curPixels);
                        }
                        else
                        {
                            msg = BuildDeltaFrame(w, h, curPixels, state.PrevPixels);
                            // If delta is larger than full frame, send full
                            if (msg.Pixels.Length >= pixelSize)
                                msg = BuildFullFrame(w, h, curPixels);
                        }

                        _transport.SendTo(sessionId, MessageCodec.Encode(MessageType.ScreenFrame, state.FrameSeq.Next(), msg));

                        // Store as previous
                        state.PrevPixels = curPixels;
                        state.PrevWidth = w;
                        state.PrevHeight = h;
                        frameCount++;

                        // curPixels ownership transferred to state, don't dispose
                        curPixels = null;
                    }
                    finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(frame.Scan0); }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Console.WriteLine("Capture error: {0}", ex.Message); }

                try { Thread.Sleep(_frameDelayMs); } catch { break; }
                if (ct.IsCancellationRequested) break;
            }
            Console.WriteLine("Capture stopped for client {0}", sessionId);
        }

        private static ScreenFrameMessage BuildFullFrame(int w, int h, byte[] rawPixels)
        {
            byte[] compressed = CompressHelper.Compress(rawPixels, _compressType);
            CompressType useCompress = (compressed.Length < rawPixels.Length) ? _compressType : CompressType.None;
            return new ScreenFrameMessage
            {
                FrameType = FrameType.Full,
                Compress = useCompress,
                Rects = new[] { new ScreenRect { X = 0, Y = 0, Width = (ushort)w, Height = (ushort)h, Offset = 0 } },
                Pixels = (useCompress == CompressType.Zlib) ? compressed : rawPixels
            };
        }

        private static ScreenFrameMessage BuildDeltaFrame(int w, int h, byte[] cur, byte[] prev)
        {
            var rects = DirtyRectDetector.Detect(cur, prev, w, h);

            if (rects.Count == 0)
            {
                return new ScreenFrameMessage
                {
                    FrameType = FrameType.Full,
                    Compress = CompressType.None,
                    Rects = new[] { new ScreenRect { X = 0, Y = 0, Width = (ushort)w, Height = (ushort)h, Offset = 0 } },
                    Pixels = new byte[0]
                };
            }

            // Extract pixel data per rectangle, merge into single blob
            int totalBytes = 0;
            for (int i = 0; i < rects.Count; i++)
                totalBytes += rects[i].Width * rects[i].Height * 4;

            byte[] allPixels = new byte[totalBytes];
            int offset = 0;
            for (int i = 0; i < rects.Count; i++)
            {
                var r = rects[i];
                int tileBytes = r.Width * r.Height * 4;
                r.Offset = (uint)offset;
                rects[i] = r;

                for (int ty = 0; ty < r.Height; ty++)
                {
                    int srcOff = ((r.Y + ty) * w + r.X) * 4;
                    Array.Copy(cur, srcOff, allPixels, offset + ty * r.Width * 4, r.Width * 4);
                }
                offset += tileBytes;
            }

            byte[] compressed = CompressHelper.Compress(allPixels, _compressType);
            CompressType useCompress = (compressed.Length < allPixels.Length) ? _compressType : CompressType.None;

            return new ScreenFrameMessage
            {
                FrameType = FrameType.Delta,
                Compress = useCompress,
                Rects = rects.ToArray(),
                Pixels = (useCompress == CompressType.Zlib) ? compressed : allPixels
            };
        }
    }

    public class ServerConfig
    {
        public int Port { get; set; } = 8750;
        public string AuthToken { get; set; } = "easyrdp-demo";
        public string CompressType { get; set; } = "Zlib";
        public int FrameRate { get; set; } = 15;
    }
}
