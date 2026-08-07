namespace EasyRDP.Core.Rendering
{
    using System.Diagnostics;
    /// <summary>
    /// 客户端本地帧缓冲。双槽双缓冲：解码线程写 slot A，渲染线程读 slot B，
    /// CommitFrame 原子交换。全链路 2 次数据搬运（解码器→写槽、读槽→GPU）。
    /// 线程安全：lock 保护所有操作（含 Sequence/FrameCount 读取——net40/XP 常为 32 位进程，
    /// long 读写非原子，必须加锁，否则 Sequence 会撕裂）。
    /// 
    /// 阶段二扩展：每槽关联 DirtyRects（ZRLE 脏矩形数组），
    /// 渲染线程借帧时可随 ReadFrameRef 取回，实现局部更新渲染。
    /// </summary>
    public class FrameBuffer
    {
        private byte[][] _slots = new byte[2][];
        // 与 _slots 对应的脏矩形数组（ZRLE 帧的区域列表；H264 帧为 null）
        private ScreenRect[][] _dirtyRects = new ScreenRect[2][];
        private int _writeSlot;
        private int _readSlot = -1;
        private int _readingSlot = -1;
        private int _width;
        private int _height;
        private int _frameCount;
        private long _sequence;
        private long _readBorrowTicks;
        private readonly object _lock = new object();

        private static readonly long ReadBorrowTimeoutTicks;

        static FrameBuffer()
        {
            ReadBorrowTimeoutTicks = 5 * Stopwatch.Frequency;
        }

        /// <summary>Gets the width of the current frame in pixels.</summary>
        public int Width
        {
            get { lock (_lock) return _width; }
        }

        /// <summary>Gets the height of the current frame in pixels.</summary>
        public int Height
        {
            get { lock (_lock) return _height; }
        }

        /// <summary>Gets the total number of frames committed since last Reset.</summary>
        public int FrameCount
        {
            get { lock (_lock) return _frameCount; }
        }

        /// <summary>Gets the monotonically increasing sequence number of the latest committed frame.</summary>
        public long Sequence
        {
            get { lock (_lock) return _sequence; }
        }

        /// <summary>
        /// 借用写缓冲区。返回 null 表示无可写槽位（reader 仍持有另一槽且未超时）。
        /// </summary>
        public byte[] BorrowWriteBuffer(int requiredSize)
        {
            lock (_lock)
            {
                if (_writeSlot == _readingSlot)
                    return null;
                var slot = _slots[_writeSlot];
                if (slot == null || slot.Length < requiredSize)
                    _slots[_writeSlot] = new byte[requiredSize];
                return _slots[_writeSlot];
            }
        }

        /// <summary>
        /// 提交写入。原子交换读写槽。
        /// 返回 false 表示 reader 仍持有且未超时——调用方应丢弃本帧。
        /// 读帧超时兜底：若 reader 借用超过 5s，视为泄漏，强制回收 _readingSlot 并继续提交。
        /// </summary>
        public bool CommitFrame(int width, int height)
        {
            return CommitFrame(width, height, null);
        }

        /// <summary>
        /// 提交写入（带脏矩形）。原子交换读写槽。
        /// dirtyRects 数组由调用方每帧新建（解码线程生成），生命周期跨借帧窗口安全；
        /// 双槽各自持有引用，互不覆盖。
        /// </summary>
        public bool CommitFrame(int width, int height, ScreenRect[] dirtyRects)
        {
            lock (_lock)
            {
                if (_readingSlot >= 0)
                {
                    long elapsed = Stopwatch.GetTimestamp() - _readBorrowTicks;
                    if (elapsed < ReadBorrowTimeoutTicks)
                        return false; // 正常占用，丢弃本帧
                    // 超时强制回收
                    _readingSlot = -1;
                }
                _width = width;
                _height = height;
                _sequence++;
                _frameCount++;
                _dirtyRects[_writeSlot] = dirtyRects;
                _readSlot = _writeSlot;
                _writeSlot = (_writeSlot + 1) % 2;
                return true;
            }
        }

        /// <summary>
        /// 借用读帧。返回内部槽位引用（零拷贝）。调用方渲染后必须调用 ReleaseReadFrame。
        /// </summary>
        public bool TryBorrowReadFrame(out ReadFrameRef frame)
        {
            lock (_lock)
            {
                if (_readSlot < 0)
                {
                    frame = new ReadFrameRef();
                    return false;
                }
                frame = new ReadFrameRef
                {
                    Pixels = _slots[_readSlot],
                    Width = _width,
                    Height = _height,
                    Sequence = _sequence,
                    DirtyRects = _dirtyRects[_readSlot]
                };
                _readingSlot = _readSlot;
                _readSlot = -1;
                _readBorrowTicks = Stopwatch.GetTimestamp();
                return true;
            }
        }

        /// <summary>
        /// 释放读帧。必须调用，否则 CommitFrame 永久返回 false（直到 5s 超时强制回收）。
        /// </summary>
        public void ReleaseReadFrame()
        {
            lock (_lock)
            {
                _readingSlot = -1;
            }
        }

        /// <summary>
        /// 重置所有状态（连接断开时调用）。
        /// </summary>
        public void Reset()
        {
            lock (_lock)
            {
                _writeSlot = 0;
                _readSlot = -1;
                _readingSlot = -1;
                _width = 0;
                _height = 0;
                _frameCount = 0;
                _sequence = 0;
                _dirtyRects[0] = null;
                _dirtyRects[1] = null;
            }
        }
    }
}
