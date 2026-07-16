using System;
using System.Collections.Generic;
using System.Threading;
using EasyDesk.Core;
using EasyDesk.Core.Models;
using EasyDesk.Windows;
using EasyRDP.Core.Protocol;

namespace EasyRDP.Server.Wpf.Services
{
    /// <summary>
    /// 截屏引擎 + 输入注入。
    /// </summary>
    public class CaptureEngine
    {
        private readonly IScreenCapturer _capturer;
        private readonly IInputSimulator _input;
        private readonly ICursorCapturer _cursor;
        private readonly Dictionary<uint, CancellationTokenSource> _clientTokens = new Dictionary<uint, CancellationTokenSource>();
        private readonly object _captureLock = new object();
        private readonly object _clientLock = new object();

        public int FrameDelayMs { get; set; }
        public CompressType CompressType { get; set; }
        public Action<uint, byte[]> SendTo { get; set; }
        public Action<string> OnLog { get; set; }

        public CaptureEngine()
        {
            var factory = new WindowsDesktopFactory();
            _capturer = factory.CreateScreenCapturer();
            _input = factory.CreateInputSimulator();
            _cursor = factory.CreateCursorCapturer();
            FrameDelayMs = 66;
            CompressType = CompressType.Zlib;
        }

        /// <summary>获取主屏幕尺寸。</summary>
        public DesktopBounds GetPrimaryScreen()
        {
            lock (_captureLock)
                return _capturer.GetPrimaryScreen();
        }

        public void StartForClient(uint sessionId)
        {
            var cts = new CancellationTokenSource();
            lock (_clientLock) _clientTokens[sessionId] = cts;
            var t = new Thread(() => CaptureLoop(sessionId, cts.Token))
            {
                IsBackground = true,
                Name = string.Format("EasyRDP-Capture-{0}", sessionId)
            };
            t.Start();
        }

        public void StopForClient(uint sessionId)
        {
            lock (_clientLock)
            {
                CancellationTokenSource cts;
                if (_clientTokens.TryGetValue(sessionId, out cts))
                {
                    cts.Cancel(); cts.Dispose(); _clientTokens.Remove(sessionId);
                }
            }
        }

        public void StopAll()
        {
            lock (_clientLock)
            {
                foreach (var kvp in _clientTokens) { kvp.Value.Cancel(); kvp.Value.Dispose(); }
                _clientTokens.Clear();
            }
        }

        public void HandleInput(InputEventMessage msg)
        {
            if (msg.Units == null) return;
            for (int i = 0; i < msg.Units.Length; i++)
            {
                var u = msg.Units[i];
                try
                {
                    switch (msg.EventType)
                    {
                        case InputEventType.MouseMove: _input.SendMouseMove(u.X, u.Y, u.Absolute); break;
                        case InputEventType.MouseDown: _input.SendMouseButton((MouseButton)u.Button, true); break;
                        case InputEventType.MouseUp: _input.SendMouseButton((MouseButton)u.Button, false); break;
                        case InputEventType.MouseWheel: _input.SendMouseWheel(u.WheelDelta); break;
                        case InputEventType.KeyDown: _input.SendKeyDown((VirtualKeyCode)u.VirtualKey); break;
                        case InputEventType.KeyUp: _input.SendKeyUp((VirtualKeyCode)u.VirtualKey); break;
                        case InputEventType.UnicodeText: _input.SendText(u.Text); break;
                    }
                }
                catch { }
            }
        }

        private void CaptureLoop(uint sessionId, CancellationToken ct)
        {
            var seq = new SequenceTracker();
            int frameCount = 0;
            byte[] prevPixels = null;
            int prevW = 0, prevH = 0;
            var send = SendTo;
            if (send == null) return;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    ScreenFrame frame;
                    lock (_captureLock) frame = _capturer.CaptureScreen();
                    try
                    {
                        if (frame.Scan0 == IntPtr.Zero) continue;
                        int w = frame.Width, h = frame.Height, pixelSize = w * h * 4;
                        byte[] curPixels = new byte[pixelSize];
                        System.Runtime.InteropServices.Marshal.Copy(frame.Scan0, curPixels, 0, pixelSize);

                        bool isKey = frameCount % 30 == 0 || prevPixels == null || prevW != w || prevH != h;
                        ScreenFrameMessage screenMsg;
                        if (isKey) screenMsg = BuildFullFrame(w, h, curPixels);
                        else
                        {
                            screenMsg = BuildDeltaFrame(w, h, curPixels, prevPixels);
                            if (screenMsg.Pixels.Length >= pixelSize)
                                screenMsg = BuildFullFrame(w, h, curPixels);
                        }
                        send(sessionId, MessageCodec.Encode(MessageType.ScreenFrame, seq.Next(), screenMsg));

                        prevPixels = curPixels; prevW = w; prevH = h; frameCount++; curPixels = null;

                        int cx, cy;
                        _cursor.GetCursorPosition(out cx, out cy);
                        send(sessionId, MessageCodec.Encode(MessageType.CursorUpdate, seq.Next(),
                            new CursorUpdateMessage { Visible = true, X = (short)cx, Y = (short)cy, HotspotX = 0, HotspotY = 0, Width = 0, Height = 0, ImageData = new byte[0] }));
                    }
                    finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(frame.Scan0); }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { var log = OnLog; if (log != null) log(string.Format("Capture error: {0}", ex.Message)); }

                try { Thread.Sleep(FrameDelayMs); } catch { break; }
            }
        }

        private ScreenFrameMessage BuildFullFrame(int w, int h, byte[] raw)
        {
            byte[] compressed = CompressHelper.Compress(raw, CompressType.Zlib);
            bool useZlib = compressed.Length < raw.Length;
            return new ScreenFrameMessage
            {
                FrameType = FrameType.Full, Compress = useZlib ? CompressType.Zlib : CompressType.None,
                Rects = new[] { new ScreenRect { X = 0, Y = 0, Width = (ushort)w, Height = (ushort)h, Offset = 0 } },
                Pixels = useZlib ? compressed : raw
            };
        }

        private ScreenFrameMessage BuildDeltaFrame(int w, int h, byte[] cur, byte[] prev)
        {
            var rects = DirtyRectDetector.Detect(cur, prev, w, h);
            if (rects.Count == 0)
                return new ScreenFrameMessage { FrameType = FrameType.Full, Compress = CompressType.None,
                    Rects = new[] { new ScreenRect { X = 0, Y = 0, Width = (ushort)w, Height = (ushort)h, Offset = 0 } }, Pixels = new byte[0] };

            int totalBytes = 0;
            for (int i = 0; i < rects.Count; i++) totalBytes += rects[i].Width * rects[i].Height * 4;
            byte[] allPixels = new byte[totalBytes];
            int offset = 0;
            for (int i = 0; i < rects.Count; i++)
            {
                var r = rects[i]; r.Offset = (uint)offset; rects[i] = r;
                int tileBytes = r.Width * r.Height * 4;
                for (int ty = 0; ty < r.Height; ty++)
                    Array.Copy(cur, ((r.Y + ty) * w + r.X) * 4, allPixels, offset + ty * r.Width * 4, r.Width * 4);
                offset += tileBytes;
            }
            byte[] compressed = CompressHelper.Compress(allPixels, CompressType.Zlib);
            bool useZlib = compressed.Length < allPixels.Length;
            return new ScreenFrameMessage { FrameType = FrameType.Delta, Compress = useZlib ? CompressType.Zlib : CompressType.None,
                Rects = rects.ToArray(), Pixels = useZlib ? compressed : allPixels };
        }
    }
}
