namespace EasyRDP.Core.Protocol
{
    using System;
    using System.IO;
    using System.Text;

    /// <summary>
    /// 图片剪贴板传输开始消息。携带 CF_DIB 总字节数。
    /// 发送方：检测到 CF_DIB 剪贴板变化的一方。
    /// 接收方：收到后准备 MemoryStream，等待 ImageClipboardData 消息。
    /// </summary>
    public class ImageClipboardStartMessage
    {
        /// <summary>本次传输的唯一标识（用于关联 Data/End 消息）。</summary>
        public uint TransferId;

        /// <summary>CF_DIB 数据总字节数。</summary>
        public long TotalSize;

        /// <summary>序列化为 payload。</summary>
        public byte[] Pack()
        {
            using (var ms = new MemoryStream(12))
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(TransferId);
                bw.Write(TotalSize);
                return ms.ToArray();
            }
        }

        /// <summary>从 payload 反序列化。</summary>
        public static ImageClipboardStartMessage Unpack(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                return new ImageClipboardStartMessage
                {
                    TransferId = br.ReadUInt32(),
                    TotalSize = br.ReadInt64()
                };
            }
        }
    }

    /// <summary>
    /// 图片剪贴板数据块消息。携带 CF_DIB 内容的分片。
    /// 格式与 ClipFileContentsResMessage 类似（transferId + offset + 数据块），但语义不同：
    /// 本消息用于主动推送图片分片，ClipFileContentsRes 用于响应延迟渲染请求。
    /// </summary>
    public class ImageClipboardDataMessage
    {
        /// <summary>关联的传输 ID。</summary>
        public uint TransferId;

        /// <summary>数据块在 CF_DIB 中的偏移（字节）。</summary>
        public long Offset;

        /// <summary>数据块长度（字节）。</summary>
        public int DataLen;

        /// <summary>CF_DIB 数据块。</summary>
        public byte[] Data;

        /// <summary>序列化为 payload。</summary>
        public byte[] Pack()
        {
            int dataLen = Data != null ? Data.Length : 0;
            using (var ms = new MemoryStream(16 + dataLen))
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(TransferId);
                bw.Write(Offset);
                bw.Write(dataLen);
                if (dataLen > 0)
                    bw.Write(Data, 0, dataLen);
                return ms.ToArray();
            }
        }

        /// <summary>从 payload 反序列化。</summary>
        public static ImageClipboardDataMessage Unpack(byte[] payload)
        {
            using (var ms = new MemoryStream(payload))
            using (var br = new BinaryReader(ms))
            {
                var msg = new ImageClipboardDataMessage
                {
                    TransferId = br.ReadUInt32(),
                    Offset = br.ReadInt64(),
                    DataLen = br.ReadInt32()
                };
                if (msg.DataLen > 0 && msg.DataLen < Constants.MaxSafePayloadSize)
                {
                    msg.Data = br.ReadBytes(msg.DataLen);
                }
                else
                {
                    msg.Data = new byte[0];
                    msg.DataLen = 0;
                }
                return msg;
            }
        }
    }

    /// <summary>
    /// 图片剪贴板传输完成消息。发送方所有 CF_DIB 数据发送完毕后发此消息。
    /// 接收方收到后：1) 取出完整 CF_DIB 字节；2) 调用 SetImageDibBytes 设置剪贴板。
    /// </summary>
    public class ImageClipboardEndMessage
    {
        /// <summary>关联的传输 ID。</summary>
        public uint TransferId;

        /// <summary>序列化为 payload。</summary>
        public byte[] Pack()
        {
            using (var ms = new MemoryStream(4))
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(TransferId);
                return ms.ToArray();
            }
        }

        /// <summary>从 payload 反序列化。</summary>
        public static ImageClipboardEndMessage Unpack(byte[] payload)
        {
            using (var ms = new MemoryStream(payload))
            using (var br = new BinaryReader(ms))
            {
                return new ImageClipboardEndMessage
                {
                    TransferId = br.ReadUInt32()
                };
            }
        }
    }

    /// <summary>
    /// 图片剪贴板接收器：在 MemoryStream 中累积 CF_DIB 数据块。
    /// 与 FileClipboardConsumer 不同，不需要文件系统（图片数据在内存中组装）。
    /// 线程安全：WriteChunk 加锁保护。
    /// </summary>
    public class ImageClipboardReceiver
    {
        /// <summary>传输完成回调，参数为完整的 CF_DIB 字节数组。</summary>
        public event Action<byte[]> Completed;

        private readonly uint _transferId;
        private readonly long _totalSize;
        private readonly byte[] _buffer;
        private readonly object _lock = new object();
        private bool _finished;

        /// <summary>构造接收器。</summary>
        /// <param name="transferId">传输 ID。</param>
        /// <param name="totalSize">CF_DIB 总字节数。</param>
        /// <param name="onCompleted">完成回调（可选，也可用 Completed 事件订阅）。</param>
        public ImageClipboardReceiver(uint transferId, long totalSize, Action<byte[]> onCompleted = null)
        {
            _transferId = transferId;
            _totalSize = totalSize;
            _buffer = new byte[totalSize];
            if (onCompleted != null)
                Completed += onCompleted;
        }

        /// <summary>写入数据块到指定偏移。</summary>
        public void WriteChunk(long offset, byte[] data, int dataLen)
        {
            if (data == null || dataLen <= 0) return;
            lock (_lock)
            {
                if (_finished) return;
                if (offset < 0 || offset + dataLen > _totalSize) return;
                Buffer.BlockCopy(data, 0, _buffer, (int)offset, dataLen);
            }
        }

        /// <summary>完成接收：触发 Completed 事件，返回完整 CF_DIB 字节。</summary>
        public byte[] Finish()
        {
            lock (_lock)
            {
                if (_finished) return _buffer;
                _finished = true;
            }

            var handler = Completed;
            if (handler != null)
            {
                try { handler(_buffer); }
                catch { }
            }
            return _buffer;
        }
    }
}
