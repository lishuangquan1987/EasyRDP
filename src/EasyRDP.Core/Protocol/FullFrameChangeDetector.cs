using System;
using System.Runtime.InteropServices;

namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 原始帧变化检测器：全帧 memcmp 逐字节比较。
    /// 行为与 ServerStreamSession 之前的 _prevBgra + ByteArraysEqual 完全一致，
    /// 提取为独立类以便通过 IFrameChangeDetector 抽象切换。
    /// 静态帧 ~1ms 跳过；任意字节差异即返回 ShouldEncode=true。
    /// </summary>
    /// <remarks>
    /// 内存占用：width × height × 4 字节（1080p ≈ 8.3MB）。
    /// 线程安全：非线程安全，仅由 ServerStreamSession.EncodeLoop 单线程调用。
    /// </remarks>
    public sealed class FullFrameChangeDetector : IFrameChangeDetector
    {
        // 参考帧（上次成功编码的像素）。null 表示首次调用或 Reset 后
        private byte[] _prevPixels;
        private int _prevLen;
        // 临时缓存：Detect 时拷贝当前帧，Commit 时提升为 _prevPixels
        private byte[] _pendingPixels;
        private int _pendingLen;

        /// <summary>
        /// 对比当前帧与参考帧。完全相同返回 ShouldEncode=false，否则 true。
        /// 副作用：拷贝当前帧到内部 _pendingPixels，等待 Commit() 提升为参考帧。
        /// </summary>
        public FrameChangeResult Detect(byte[] pixels, int width, int height)
        {
            if (pixels == null)
                throw new ArgumentNullException("pixels");
            int len = width * height * 4;
            if (len <= 0)
                throw new ArgumentOutOfRangeException("len");

            // 拷贝当前帧到 pending（无论是否变化，Commit 时都需要）
            EnsurePendingBuffer(len);
            Buffer.BlockCopy(pixels, 0, _pendingPixels, 0, len);
            _pendingLen = len;

            int totalBlocks = ((width + 31) >> 5) * ((height + 31) >> 5);

            // 首次调用或 Reset 后无基准帧 → 必须编码
            if (_prevPixels == null || _prevLen != len)
            {
                return FrameChangeResult.Changed(totalBlocks, totalBlocks);
            }

            // memcmp 快速路径：完全相同则跳过编码
            bool equal = MemcmpEqual(pixels, _prevPixels, len);

            if (equal)
                return FrameChangeResult.Unchanged(totalBlocks);

            return FrameChangeResult.Changed(totalBlocks, totalBlocks);
        }

        /// <summary>
        /// 将 _pendingPixels 提升为 _prevPixels。
        /// 编码成功后调用；编码失败时禁止调用（保持参考帧为上次成功帧）。
        /// </summary>
        public void Commit()
        {
            if (_pendingPixels == null || _pendingLen == 0)
                return; // Detect 从未被调用，空操作

            // 交换 pending 和 prev：避免再次拷贝
            var tmp = _prevPixels;
            _prevPixels = _pendingPixels;
            _prevLen = _pendingLen;
            _pendingPixels = tmp;
            // 若 prev 缓冲不够大，下次 EnsurePendingBuffer 会重分配
        }

        /// <summary>清空内部缓存，下次 Detect 必然返回 ShouldEncode=true。</summary>
        public void Reset()
        {
            _prevPixels = null;
            _prevLen = 0;
            _pendingPixels = null;
            _pendingLen = 0;
        }

        /// <summary>确保 _pendingPixels 至少可容纳 len 字节。</summary>
        private void EnsurePendingBuffer(int len)
        {
            if (_pendingPixels == null || _pendingPixels.Length < len)
                _pendingPixels = new byte[len];
        }

        /// <summary>
        /// memcmp 包装：返回 true 表示两段内存前 count 字节完全相同。
        /// 首字节不同即提前返回，内容变化时开销极小。
        /// 8.3MB 完全相同时逐字节比较要 ~30ms，memcmp 走 CRT 优化路径仅需 ~1ms。
        /// </summary>
        private static bool MemcmpEqual(byte[] a, byte[] b, int count)
        {
            if (a == null || b == null) return false;
            if (a.Length < count || b.Length < count) return false;
            if (ReferenceEquals(a, b)) return true;
            try
            {
                return Memcmp(a, b, (IntPtr)count) == 0;
            }
            catch
            {
                // msvcrt.dll 缺失等异常情况退回逐字节比较
                for (int i = 0; i < count; i++)
                {
                    if (a[i] != b[i]) return false;
                }
                return true;
            }
        }

        /// <summary>
        /// msvcrt.memcmp — Windows 内置 CRT 内存比较。
        /// 返回 0 表示相等。byte[] 自动 pin 后传首地址。
        /// </summary>
        [DllImport("msvcrt.dll", EntryPoint = "memcmp", SetLastError = false)]
        private static extern int Memcmp(byte[] a, byte[] b, IntPtr count);
    }
}
