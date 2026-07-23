using System;
using System.Collections.Generic;
using System.Threading;
using EasyDesk.Core;
using EasyDesk.Core.Models;
using EasyDesk.Windows;
using EasyRDP.Core.Logging;
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
        private readonly CursorTracker _cursorTracker;
        private readonly Dictionary<uint, CancellationTokenSource> _clientTokens = new Dictionary<uint, CancellationTokenSource>();
        private readonly object _captureLock = new object();
        private readonly object _clientLock = new object();

        public int FrameDelayMs { get; set; }
        public CompressType CompressType { get; set; }
        public Action<uint, byte[]> SendTo { get; set; }
        public Action<string> OnLog { get; set; }

        /// <summary>独立光标追踪器（60Hz，与屏幕帧循环解耦）。由外部在启动时设置 SendTo。</summary>
        public CursorTracker CursorTracker { get { return _cursorTracker; } }

        public CaptureEngine()
        {
            var factory = new WindowsDesktopFactory();
            _capturer = factory.CreateScreenCapturer();
            _input = factory.CreateInputSimulator();
            _cursor = factory.CreateCursorCapturer();

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
            LogHelper.Info(string.Format("截屏启动 ClientId={0}", sessionId));
            var t = new Thread(() => CaptureLoop(sessionId, cts.Token))
            {
                IsBackground = true,
                Name = string.Format("EasyRDP-Capture-{0}", sessionId)
            };
            t.Start();

            _cursorTracker.StartForClient(sessionId);
        }

        public void StopForClient(uint sessionId)
        {
            lock (_clientLock)
            {
                CancellationTokenSource cts;
                if (_clientTokens.TryGetValue(sessionId, out cts))
                {
                    cts.Cancel(); cts.Dispose(); _clientTokens.Remove(sessionId);
                    LogHelper.Info(string.Format("截屏停止 ClientId={0}", sessionId));
                }
            }
            _cursorTracker.StopForClient(sessionId);
        }

        public void StopAll()
        {
            lock (_clientLock)
            {
                foreach (var kvp in _clientTokens) { kvp.Value.Cancel(); kvp.Value.Dispose(); }
                _clientTokens.Clear();
            }
            _cursorTracker.StopAll();
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

        /// <summary>空闲无变化帧数达到此值时降帧到 IdleFrameDelayMs</summary>
        private const int IdleThreshold = 15;
        /// <summary>空闲时的帧间隔（毫秒），约 1fps</summary>
        private const int IdleFrameDelayMs = 1000;
        /// <summary>最小帧间隔（毫秒），最多 30fps</summary>
        private const int MinFrameDelayMs = 33;
        /// <summary>
        /// 每客户端发送队列容量。队满时丢弃最旧的非关键帧（视频流宁可丢帧不可积压），
        /// 关键帧予以保留以保证客户端可恢复。
        /// </summary>
        private const int MaxPendingFrames = 3;

        /// <summary>待发送帧（含是否关键帧标记）。</summary>
        private class PendingFrame
        {
            public byte[] Data;
            public bool IsKey;
        }

        private void CaptureLoop(uint sessionId, CancellationToken ct)
        {
            var seq = new SequenceTracker();
            int frameCount = 0;
            // 双缓冲池：bufA / bufB 轮流充当当前帧和上一帧，避免每帧 new byte[]
            byte[] bufA = null;
            byte[] bufB = null;
            bool useBufA = true;
            int prevW = 0, prevH = 0;
            // 自适应帧率状态
            int idleFrames = 0;
            int currentDelayMs = FrameDelayMs;
            var send = SendTo;
            if (send == null) return;

            // 发送队列 + 同步：截屏线程生产，独立发送线程消费，二者解耦
            var sendQueue = new LinkedList<PendingFrame>();
            var queueLock = new object();
            // senderRunning 的读写均在 queueLock 内，可见性由锁保证
            bool senderRunning = true;

            // 发送线程：从队列取帧并同步发送到客户端。网络阻塞时只阻塞本线程，不影响截屏。
            var senderThread = new Thread(() =>
            {
                while (senderRunning)
                {
                    PendingFrame pf = null;
                    lock (queueLock)
                    {
                        while (sendQueue.Count == 0 && senderRunning)
                            Monitor.Wait(queueLock);
                        if (!senderRunning) break;
                        pf = sendQueue.First.Value;
                        sendQueue.RemoveFirst();
                    }
                    if (pf == null) break;
                    try { send(sessionId, pf.Data); }
                    catch (Exception ex) { LogHelper.Error(ex, "Send error"); }
                }
            }) { IsBackground = true, Name = string.Format("EasyRDP-Sender-{0}", sessionId) };
            senderThread.Start();

            try
            {
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

                            // 从池中取出当前帧缓冲区（尺寸变化时重新分配）
                            byte[] curPixels = useBufA ? bufA : bufB;
                            if (curPixels == null || curPixels.Length != pixelSize)
                                curPixels = new byte[pixelSize];
                            System.Runtime.InteropServices.Marshal.Copy(frame.Scan0, curPixels, 0, pixelSize);

                            byte[] prevPixels = useBufA ? bufB : bufA; // 另一块就是上一帧
                            bool isKey = frameCount % 30 == 0 || prevPixels == null || prevW != w || prevH != h;
                            ScreenFrameMessage screenMsg;
                            bool hasChanges = true;
                            bool frameIsKey = isKey;

                            if (isKey)
                            {
                                screenMsg = BuildFullFrame(w, h, curPixels);
                                frameIsKey = true;
                            }
                            else
                            {
                                screenMsg = BuildDeltaFrame(w, h, curPixels, prevPixels);
                                // BuildDeltaFrame 无变化时返回空像素 Full 帧
                                if (screenMsg.Pixels == null || screenMsg.Pixels.Length == 0)
                                    hasChanges = false;
                                else if (screenMsg.Pixels.Length >= pixelSize)
                                {
                                    // 增量退化成全帧（视作关键帧，客户端据此恢复）
                                    screenMsg = BuildFullFrame(w, h, curPixels);
                                    frameIsKey = true;
                                }
                            }

                            if (hasChanges)
                            {
                                byte[] encoded = MessageCodec.Encode(MessageType.ScreenFrame, seq.Next(), screenMsg);
                                EnqueueFrame(sendQueue, queueLock, encoded, frameIsKey);
                                idleFrames = 0;
                                // 大面积变化 → 30fps；小面积 → 配置帧率
                                if (screenMsg.Rects != null && screenMsg.Rects.Length > 10)
                                    currentDelayMs = MinFrameDelayMs;
                                else
                                    currentDelayMs = FrameDelayMs;
                            }
                            else
                            {
                                idleFrames++;
                                // 空闲达到阈值后降帧到 1fps
                                if (idleFrames >= IdleThreshold)
                                    currentDelayMs = IdleFrameDelayMs;
                            }

                            // 交换缓冲区：当前帧 → 上一帧，另一块下次用
                            if (useBufA)
                                bufA = curPixels;
                            else
                                bufB = curPixels;
                            useBufA = !useBufA;

                            prevW = w; prevH = h; frameCount++;
                        }
                        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(frame.Scan0); }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { LogHelper.Error(ex, "Capture error"); var log = OnLog; if (log != null) log(string.Format("Capture error: {0}", ex.Message)); }

                    try { Thread.Sleep(currentDelayMs); } catch { break; }
                }
            }
            finally
            {
                // 在锁内停止发送线程，保证可见性
                lock (queueLock)
                {
                    senderRunning = false;
                    Monitor.PulseAll(queueLock);
                }
            }
        }

        /// <summary>
        /// 入队一帧。队列满时优先丢弃最旧的非关键帧（保留关键帧以支持客户端恢复）。
        /// </summary>
        private static void EnqueueFrame(LinkedList<PendingFrame> q, object queueLock, byte[] data, bool isKey)
        {
            lock (queueLock)
            {
                if (q.Count >= MaxPendingFrames)
                {
                    // 优先丢弃最旧的非关键帧
                    var node = q.First;
                    while (node != null)
                    {
                        if (!node.Value.IsKey)
                        {
                            q.Remove(node);
                            break;
                        }
                        node = node.Next;
                    }
                    // 若全是关键帧（罕见），丢弃最旧的以避免无限增长
                    if (q.Count >= MaxPendingFrames)
                        q.RemoveFirst();
                }
                q.AddLast(new PendingFrame { Data = data, IsKey = isKey });
                Monitor.Pulse(queueLock);
            }
        }

        private ScreenFrameMessage BuildFullFrame(int w, int h, byte[] raw)
        {
            int pixelCount = w * h;
            CompressType bestType = CompressType.Zlib;

            // 对照片/高熵/大画面用 JPEG，静止/低熵画面用 Zlib
            if (pixelCount > 10000 && CompressHelper.ShouldUseJPEG(raw, pixelCount))
                bestType = CompressType.JPEG;

            byte[] compressed = CompressHelper.Compress(raw, bestType, w, h);
            bool useCompress = compressed.Length < raw.Length && compressed.Length > 0;
            return new ScreenFrameMessage
            {
                FrameType = FrameType.Full,
                Compress = useCompress ? bestType : CompressType.None,
                Rects = new[] { new ScreenRect { X = 0, Y = 0, Width = (ushort)w, Height = (ushort)h, Offset = 0 } },
                Pixels = useCompress ? compressed : raw
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
                var r = rects[i];
                r.Offset = (uint)offset;
                rects[i] = r;
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

        /// <summary>
        /// 判断指定矩形区域内的所有像素是否相同。
        /// </summary>
        private static bool IsSolidColor(byte[] pixels, int x, int y, int w, int h, int screenW)
        {
            int firstIdx = (y * screenW + x) * 4;
            byte b = pixels[firstIdx], g = pixels[firstIdx + 1];
            byte r = pixels[firstIdx + 2], a = pixels[firstIdx + 3];
            int stride = screenW * 4;

            for (int row = 0; row < h; row++)
            {
                int rowBase = (y + row) * stride;
                for (int col = 0; col < w; col++)
                {
                    int idx = rowBase + (x + col) * 4;
                    if (pixels[idx] != b || pixels[idx + 1] != g ||
                        pixels[idx + 2] != r || pixels[idx + 3] != a)
                        return false;
                }
            }
            return true;
        }
    }
}
