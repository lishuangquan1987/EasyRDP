using System;
using EasyRDP.Core.Logging;
using EasyRDP.Core.Protocol;

namespace EasyRDP.Client.Common
{
    /// <summary>
    /// 客户端本地帧缓冲。
    /// 维护当前屏幕的 BGRA32 像素缓冲，支持全帧替换和增量帧脏矩形合并。
    /// 线程安全：读写通过 lock 保护。
    /// </summary>
    public class FrameBuffer
    {
        private byte[] _buffer;
        private int _width;
        private int _height;
        private volatile bool _isDirty;
        private int _frameCount;
        private readonly object _lock = new object();
        private bool _firstFrameLogged;
        private bool _noBaselineLogged;

        /// <summary>帧缓冲宽度。</summary>
        public int Width
        {
            get { lock (_lock) return _width; }
        }

        /// <summary>帧缓冲高度。</summary>
        public int Height
        {
            get { lock (_lock) return _height; }
        }

        /// <summary>自上次 TryGetFrame 消费后是否有新帧到达。</summary>
        public bool IsDirty
        {
            get { return _isDirty; }
        }

        /// <summary>累计接收帧数。</summary>
        public int FrameCount
        {
            get { return _frameCount; }
        }

        // ── 处理帧 ────────────────────────────────────────

        /// <summary>
        /// 处理收到的屏幕帧消息。
        /// 全帧 → 替换整个缓冲区；增量帧 → 逐矩形合并到现有缓冲区。
        /// </summary>
        public void ProcessFrame(ScreenFrameMessage frame)
        {
            if (frame == null || frame.Rects == null || frame.Rects.Length == 0)
                return;

            // 解压像素
            byte[] pixels;
            if (frame.Compress == CompressType.Zlib)
            {
                // 估算原始大小
                int estimatedRaw = 0;
                for (int i = 0; i < frame.Rects.Length; i++)
                {
                    estimatedRaw += frame.Rects[i].Width * frame.Rects[i].Height * 4;
                }
                pixels = CompressHelper.Decompress(frame.Pixels, frame.Compress, estimatedRaw);
            }
            else
            {
                pixels = frame.Pixels;
            }

            if (pixels == null || pixels.Length == 0)
                return;

            lock (_lock)
            {
                int rectW, rectH;

                if (frame.FrameType == FrameType.Full)
                {
                    // 全帧：单矩形覆盖整个屏幕
                    var rect = frame.Rects[0];
                    rectW = rect.Width;
                    rectH = rect.Height;
                    int size = rectW * rectH * 4;

                    bool resized = _buffer == null || _buffer.Length != size;

                    if (resized)
                    {
                        _buffer = new byte[size];
                        if (_width != 0 || _height != 0)
                            LogHelper.Info(string.Format("帧缓冲分辨率变更: {0}x{1} → {2}x{3}", _width, _height, rectW, rectH));
                    }

                    _width = rectW;
                    _height = rectH;

                    if (pixels.Length >= size)
                        Array.Copy(pixels, 0, _buffer, 0, size);

                    if (!_firstFrameLogged)
                    {
                        _firstFrameLogged = true;
                        LogHelper.Info(string.Format("收到首帧 (Full) 分辨率={0}x{1} 大小={2}KB", rectW, rectH, size / 1024));
                    }
                }
                else // Delta
                {
                    if (_buffer == null)
                    {
                        if (!_noBaselineLogged)
                        {
                            _noBaselineLogged = true;
                            LogHelper.Warn("收到增量帧但无全帧基准，已丢弃（等待首帧全帧）");
                        }
                        return; // 还没有全帧基准，忽略增量
                    }

                    int stride = _width * 4;

                    for (int i = 0; i < frame.Rects.Length; i++)
                    {
                        var rect = frame.Rects[i];
                        rectW = rect.Width;
                        rectH = rect.Height;
                        int tileBytes = rectW * rectH * 4;

                        if ((int)rect.Offset + tileBytes > pixels.Length)
                            continue;

                        // 逐行拷贝矩形像素到帧缓冲
                        for (int ty = 0; ty < rectH; ty++)
                        {
                            int srcOffset = (int)rect.Offset + ty * rectW * 4;
                            int dstOffset = ((rect.Y + ty) * stride) + rect.X * 4;

                            if (dstOffset + rectW * 4 <= _buffer.Length)
                                Array.Copy(pixels, srcOffset, _buffer, dstOffset, rectW * 4);
                        }
                    }
                }
                _isDirty = true;
                _frameCount = _frameCount + 1;
            }
        }

        // ── 消费帧 ────────────────────────────────────────

        /// <summary>
        /// 尝试获取最新帧的像素副本。
        /// 消费后 IsDirty 变为 false。
        /// 返回 true 表示有新帧，false 表示无新帧或缓冲为空。
        /// </summary>
        public bool TryGetFrame(out byte[] pixels, out int w, out int h)
        {
            lock (_lock)
            {
                if (_buffer == null)
                {
                    pixels = null;
                    w = 0;
                    h = 0;
                    return false;
                }

                pixels = new byte[_buffer.Length];
                Array.Copy(_buffer, pixels, _buffer.Length);
                w = _width;
                h = _height;
                _isDirty = false;
                return true;
            }
        }

        // ── 重置 ──────────────────────────────────────────

        /// <summary>
        /// 重置帧缓冲（断连时调用）。
        /// </summary>
        public void Reset()
        {
            lock (_lock)
            {
                _buffer = null;
                _width = 0;
                _height = 0;
                _isDirty = false;
                _frameCount = 0;
                _firstFrameLogged = false;
                _noBaselineLogged = false;
            }
        }
    }
}
