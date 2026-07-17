using System;
using System.Collections.Generic;
using System.Threading;

namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 光标位置结构体。
    /// </summary>
    public struct CursorPosition
    {
        /// <summary>光标 X 坐标</summary>
        public short X;
        /// <summary>光标 Y 坐标</summary>
        public short Y;
        /// <summary>光标是否可见</summary>
        public bool Visible;

        /// <summary>
        /// 创建可见光标位置。
        /// </summary>
        public static CursorPosition Create(short x, short y)
        {
            return new CursorPosition { X = x, Y = y, Visible = true };
        }

        /// <summary>
        /// 不可见/无效位置。
        /// </summary>
        public static readonly CursorPosition Invisible = new CursorPosition { Visible = false };
    }

    /// <summary>
    /// 委托：获取光标位置（C# 5.0 兼容：返回结构体而非 out 参数）。
    /// </summary>
    /// <returns>光标位置信息</returns>
    public delegate CursorPosition PositionFetcher();

    /// <summary>
    /// 委托：获取光标形状原始数据。
    /// 返回 null 表示形状未变或不可用。
    /// </summary>
    /// <returns>形状数据，或 null</returns>
    public delegate CursorShapeData ShapeFetcher();

    /// <summary>
    /// 光标形状原始数据容器。
    /// ImageData 为 Windows 标准格式（AND mask + XOR BGRA mask）。
    /// </summary>
    public class CursorShapeData
    {
        /// <summary>AND mask + XOR mask 数据</summary>
        public byte[] ImageData;
        /// <summary>光标宽度</summary>
        public int Width;
        /// <summary>光标高度</summary>
        public int Height;
        /// <summary>热区 X</summary>
        public int HotspotX;
        /// <summary>热区 Y</summary>
        public int HotspotY;
    }

    /// <summary>
    /// 独立的光标追踪线程。
    /// 
    /// 与屏幕帧循环完全解耦，以高频率（默认 16ms ≈ 60Hz）独立追踪光标位置和形状。
    /// 仅当位置或形状发生变化时才发送更新，避免冗余网络流量。
    /// 
    /// 线程安全：单个客户端一个实例，使用 lock 保护客户端状态。
    /// </summary>
    public class CursorTracker : IDisposable
    {
        /// <summary>默认刷新间隔（毫秒）</summary>
        public const int DefaultIntervalMs = 16;

        /// <summary>最小发送间隔（毫秒），防止高频抖动</summary>
        public const int MinSendIntervalMs = 8;

        private readonly PositionFetcher _fetchPosition;
        private readonly ShapeFetcher _fetchShape;
        private readonly object _lock = new object();
        private readonly Dictionary<uint, CancellationTokenSource> _clientTokens = new Dictionary<uint, CancellationTokenSource>();

        /// <summary>刷新间隔（毫秒），默认 16ms</summary>
        public int IntervalMs { get; set; }

        /// <summary>消息发送回调。参数：(sessionId, encodedData)</summary>
        public Action<uint, byte[]> SendTo { get; set; }

        /// <summary>是否启用形状传输（需提供 _fetchShape）</summary>
        private volatile bool _enableShape = true;
        public bool EnableShape
        {
            get { return _enableShape; }
            set { _enableShape = value; }
        }

        /// <summary>
        /// 创建 CursorTracker。
        /// </summary>
        /// <param name="fetchPosition">获取光标位置的委托</param>
        /// <param name="fetchShape">
        /// 获取光标形状的委托（可选，仅 EnableShape=true 时使用）。
        /// 返回 null 表示形状未变/不可用。
        /// </param>
        public CursorTracker(PositionFetcher fetchPosition, ShapeFetcher fetchShape)
        {
            if (fetchPosition == null)
                throw new ArgumentNullException("fetchPosition");

            _fetchPosition = fetchPosition;
            _fetchShape = fetchShape;
            IntervalMs = DefaultIntervalMs;
        }

        /// <summary>
        /// 仅为客户端启动光标追踪线程。
        /// </summary>
        public void StartForClient(uint sessionId)
        {
            lock (_lock)
            {
                if (_clientTokens.ContainsKey(sessionId))
                    return;

                var cts = new CancellationTokenSource();
                _clientTokens[sessionId] = cts;
                var t = new Thread(() => TrackLoop(sessionId, cts.Token))
                {
                    IsBackground = true,
                    Name = string.Format("EasyRDP-Cursor-{0}", sessionId)
                };
                t.Start();
            }
        }

        /// <summary>
        /// 停止指定客户端的光标追踪。
        /// </summary>
        public void StopForClient(uint sessionId)
        {
            lock (_lock)
            {
                CancellationTokenSource cts;
                if (_clientTokens.TryGetValue(sessionId, out cts))
                {
                    cts.Cancel();
                    cts.Dispose();
                    _clientTokens.Remove(sessionId);
                }
            }
        }

        /// <summary>
        /// 停止所有客户端的光标追踪。
        /// </summary>
        public void StopAll()
        {
            lock (_lock)
            {
                foreach (var kvp in _clientTokens)
                {
                    kvp.Value.Cancel();
                    kvp.Value.Dispose();
                }
                _clientTokens.Clear();
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            StopAll();
        }

        private void TrackLoop(uint sessionId, CancellationToken ct)
        {
            var send = SendTo;
            if (send == null)
                return;

            bool fetchShape = EnableShape && _fetchShape != null;
            short lastX = -1;
            short lastY = -1;
            uint lastShapeHash = 0;
            DateTime lastSendTime = DateTime.MinValue;
            var seq = new SequenceTracker();

            while (!ct.IsCancellationRequested)
            {
                DateTime loopStart = DateTime.Now;

                try
                {
                    CursorPosition pos = _fetchPosition();

                    if (!pos.Visible)
                    {
                        // 光标不可见或查询失败，跳过
                        try { Thread.Sleep(IntervalMs); } catch { break; }
                        continue;
                    }

                    short cx = pos.X;
                    short cy = pos.Y;

                    // 位置变化检测
                    bool positionChanged = (cx != lastX || cy != lastY);
                    lastX = cx;
                    lastY = cy;

                    // 形状变化检测
                    bool shapeChanged = false;
                    CursorShapeData shape = null;
                    uint newShapeHash = lastShapeHash;

                    if (fetchShape)
                    {
                        shape = _fetchShape();
                        if (shape != null && shape.ImageData != null && shape.ImageData.Length > 0)
                        {
                            newShapeHash = CursorShapeHelper.ComputeHash(shape.ImageData);
                            shapeChanged = (newShapeHash != lastShapeHash);
                            lastShapeHash = newShapeHash;
                        }
                    }

                    // 速率限制：位置变化太快时限制发送频率
                    bool rateLimited = false;
                    if (positionChanged && !shapeChanged)
                    {
                        double msSinceLastSend = (loopStart - lastSendTime).TotalMilliseconds;
                        rateLimited = msSinceLastSend < MinSendIntervalMs;
                    }

                    // 决定是否发送
                    bool shouldSendPosition = positionChanged && !rateLimited;
                    bool shouldSendShape = shapeChanged;

                    if (shouldSendPosition || shouldSendShape)
                    {
                        var msg = new CursorUpdateMessage
                        {
                            Visible = true,
                            X = cx,
                            Y = cy
                        };

                        if (shouldSendShape && shape != null)
                        {
                            // 发送完整形状
                            msg.Width = (ushort)shape.Width;
                            msg.Height = (ushort)shape.Height;
                            msg.HotspotX = (ushort)shape.HotspotX;
                            msg.HotspotY = (ushort)shape.HotspotY;

                            // 转换为 RGBA 以便客户端直接渲染
                            byte[] rgba;
                            if (CursorShapeHelper.ConvertToRGBA(
                                shape.ImageData, shape.Width, shape.Height, out rgba))
                            {
                                msg.ImageData = rgba;
                            }
                            else
                            {
                                // 转换失败，回退为位置更新
                                msg.Width = 0;
                                msg.Height = 0;
                                msg.HotspotX = 0;
                                msg.HotspotY = 0;
                                msg.ImageData = new byte[0];
                            }
                        }
                        else
                        {
                            // 纯位置更新
                            msg.Width = 0;
                            msg.Height = 0;
                            msg.HotspotX = 0;
                            msg.HotspotY = 0;
                            msg.ImageData = new byte[0];
                        }

                        byte[] data = MessageCodec.Encode(MessageType.CursorUpdate, seq.Next(), msg);
                        send(sessionId, data);
                        lastSendTime = loopStart;
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { /* 忽略单次查询异常，继续循环 */ }

                // 计算剩余的睡眠时间
                double elapsed = (DateTime.Now - loopStart).TotalMilliseconds;
                int sleepMs = Math.Max(0, IntervalMs - (int)elapsed);
                try { Thread.Sleep(sleepMs); } catch { break; }
            }
        }
    }
}
