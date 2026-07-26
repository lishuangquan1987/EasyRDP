namespace EasyRDP.Core.Tests.Protocol
{
    using EasyRDP.Core.Protocol;
    using Xunit;

    /// <summary>
    /// ImageClipboardStartMessage / ImageClipboardDataMessage / ImageClipboardEndMessage
    /// 序列化/反序列化单元测试。
    /// </summary>
    public class ImageClipboardMessageTests
    {
        /// <summary>Start 消息 round-trip：典型 CF_DIB 大小（100KB）。</summary>
        [Fact]
        public void ImageClipboardStart_RoundTrip_TypicalSize()
        {
            var msg = new ImageClipboardStartMessage
            {
                TransferId = 42,
                TotalSize = 100 * 1024L
            };

            byte[] payload = msg.Pack();
            var restored = ImageClipboardStartMessage.Unpack(payload);

            Assert.Equal(42u, restored.TransferId);
            Assert.Equal(100 * 1024L, restored.TotalSize);
        }

        /// <summary>Start 消息 round-trip：边界值（0 和 large long）。</summary>
        [Fact]
        public void ImageClipboardStart_RoundTrip_BoundaryValues()
        {
            // TransferId = 0, TotalSize = 0
            var msg1 = new ImageClipboardStartMessage { TransferId = 0, TotalSize = 0 };
            var restored1 = ImageClipboardStartMessage.Unpack(msg1.Pack());
            Assert.Equal(0u, restored1.TransferId);
            Assert.Equal(0L, restored1.TotalSize);

            // 大值：uint.MaxValue + long.MaxValue
            var msg2 = new ImageClipboardStartMessage
            {
                TransferId = uint.MaxValue,
                TotalSize = long.MaxValue
            };
            var restored2 = ImageClipboardStartMessage.Unpack(msg2.Pack());
            Assert.Equal(uint.MaxValue, restored2.TransferId);
            Assert.Equal(long.MaxValue, restored2.TotalSize);
        }

        /// <summary>Data 消息 round-trip：64KB 数据块（标准块大小）。</summary>
        [Fact]
        public void ImageClipboardData_RoundTrip_64KChunk()
        {
            byte[] data = new byte[64 * 1024];
            for (int i = 0; i < data.Length; i++)
                data[i] = (byte)(i & 0xFF);

            var msg = new ImageClipboardDataMessage
            {
                TransferId = 7,
                Offset = 65536L,
                DataLen = data.Length,
                Data = data
            };

            byte[] payload = msg.Pack();
            var restored = ImageClipboardDataMessage.Unpack(payload);

            Assert.Equal(7u, restored.TransferId);
            Assert.Equal(65536L, restored.Offset);
            Assert.Equal(data.Length, restored.DataLen);
            Assert.Equal(data.Length, restored.Data.Length);

            // 校验数据完整性
            for (int i = 0; i < data.Length; i++)
                Assert.Equal(data[i], restored.Data[i]);
        }

        /// <summary>Data 消息 round-trip：最后一块不足 64KB。</summary>
        [Fact]
        public void ImageClipboardData_RoundTrip_PartialChunk()
        {
            byte[] data = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE };

            var msg = new ImageClipboardDataMessage
            {
                TransferId = 1,
                Offset = 131072L,
                DataLen = 5,
                Data = data
            };

            byte[] payload = msg.Pack();
            var restored = ImageClipboardDataMessage.Unpack(payload);

            Assert.Equal(1u, restored.TransferId);
            Assert.Equal(131072L, restored.Offset);
            Assert.Equal(5, restored.DataLen);
            Assert.Equal(5, restored.Data.Length);
            Assert.Equal(0xAA, restored.Data[0]);
            Assert.Equal(0xEE, restored.Data[4]);
        }

        /// <summary>Data 消息 round-trip：Offset = 0（第一块）。</summary>
        [Fact]
        public void ImageClipboardData_RoundTrip_FirstChunk()
        {
            byte[] data = new byte[] { 0x01, 0x02, 0x03 };

            var msg = new ImageClipboardDataMessage
            {
                TransferId = 100,
                Offset = 0L,
                DataLen = 3,
                Data = data
            };

            byte[] payload = msg.Pack();
            var restored = ImageClipboardDataMessage.Unpack(payload);

            Assert.Equal(100u, restored.TransferId);
            Assert.Equal(0L, restored.Offset);
            Assert.Equal(3, restored.DataLen);
        }

        /// <summary>End 消息 round-trip。</summary>
        [Fact]
        public void ImageClipboardEnd_RoundTrip()
        {
            var msg = new ImageClipboardEndMessage
            {
                TransferId = 999
            };

            byte[] payload = msg.Pack();
            var restored = ImageClipboardEndMessage.Unpack(payload);

            Assert.Equal(999u, restored.TransferId);
        }

        /// <summary>End 消息 round-trip：TransferId = 0。</summary>
        [Fact]
        public void ImageClipboardEnd_RoundTrip_ZeroTransferId()
        {
            var msg = new ImageClipboardEndMessage { TransferId = 0 };

            byte[] payload = msg.Pack();
            var restored = ImageClipboardEndMessage.Unpack(payload);

            Assert.Equal(0u, restored.TransferId);
        }

        /// <summary>三消息组合：模拟一个完整的图片传输流程的序列化/反序列化。</summary>
        [Fact]
        public void ImageClipboard_FullFlow_StartDataEnd_RoundTrip()
        {
            uint transferId = 12345;
            long totalSize = 1000; // 1000 bytes 总大小

            // Start
            var startMsg = new ImageClipboardStartMessage
            {
                TransferId = transferId,
                TotalSize = totalSize
            };
            var restoredStart = ImageClipboardStartMessage.Unpack(startMsg.Pack());
            Assert.Equal(transferId, restoredStart.TransferId);
            Assert.Equal(totalSize, restoredStart.TotalSize);

            // Data chunk 1 (offset=0, len=500)
            byte[] chunk1 = new byte[500];
            for (int i = 0; i < 500; i++) chunk1[i] = (byte)(i & 0xFF);
            var dataMsg1 = new ImageClipboardDataMessage
            {
                TransferId = transferId,
                Offset = 0,
                DataLen = 500,
                Data = chunk1
            };
            var restoredData1 = ImageClipboardDataMessage.Unpack(dataMsg1.Pack());
            Assert.Equal(transferId, restoredData1.TransferId);
            Assert.Equal(0L, restoredData1.Offset);
            Assert.Equal(500, restoredData1.DataLen);

            // Data chunk 2 (offset=500, len=500)
            byte[] chunk2 = new byte[500];
            for (int i = 0; i < 500; i++) chunk2[i] = (byte)((i + 500) & 0xFF);
            var dataMsg2 = new ImageClipboardDataMessage
            {
                TransferId = transferId,
                Offset = 500,
                DataLen = 500,
                Data = chunk2
            };
            var restoredData2 = ImageClipboardDataMessage.Unpack(dataMsg2.Pack());
            Assert.Equal(transferId, restoredData2.TransferId);
            Assert.Equal(500L, restoredData2.Offset);
            Assert.Equal(500, restoredData2.DataLen);

            // End
            var endMsg = new ImageClipboardEndMessage { TransferId = transferId };
            var restoredEnd = ImageClipboardEndMessage.Unpack(endMsg.Pack());
            Assert.Equal(transferId, restoredEnd.TransferId);

            // 校验数据块组合后内容正确
            byte[] assembled = new byte[1000];
            Buffer.BlockCopy(restoredData1.Data, 0, assembled, 0, 500);
            Buffer.BlockCopy(restoredData2.Data, 0, assembled, 500, 500);
            for (int i = 0; i < 1000; i++)
                Assert.Equal((byte)(i & 0xFF), assembled[i]);
        }

        /// <summary>Data 消息：Data = null 时应被处理为 0 长度。</summary>
        [Fact]
        public void ImageClipboardData_NullData_HandledAsZeroLength()
        {
            var msg = new ImageClipboardDataMessage
            {
                TransferId = 1,
                Offset = 0,
                DataLen = 0,
                Data = null
            };

            byte[] payload = msg.Pack();
            var restored = ImageClipboardDataMessage.Unpack(payload);

            Assert.Equal(1u, restored.TransferId);
            Assert.Equal(0, restored.DataLen);
            Assert.NotNull(restored.Data);
            Assert.Equal(0, restored.Data.Length);
        }
    }
}
