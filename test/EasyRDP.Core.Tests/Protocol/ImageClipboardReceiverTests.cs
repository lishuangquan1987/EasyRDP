namespace EasyRDP.Core.Tests.Protocol
{
    using System;
    using System.Threading;
    using EasyRDP.Core.Protocol;
    using Xunit;

    /// <summary>
    /// ImageClipboardReceiver 单元测试：验证 CF_DIB 数据块接收、组装、回调逻辑。
    /// </summary>
    public class ImageClipboardReceiverTests
    {
        /// <summary>完整接收：Start → Data×3 → End，验证 CF_DIB 内容正确。</summary>
        [Fact]
        public void Receiver_FullFlow_ContentVerified()
        {
            // 准备：构造 200KB 的"CF_DIB 数据"，分成 4 块（64K + 64K + 64K + 8K）
            int totalSize = 200 * 1024;
            byte[] expectedContent = new byte[totalSize];
            var rand = new Random(42);
            rand.NextBytes(expectedContent);

            byte[] result = null;
            var receiver = new ImageClipboardReceiver(1, totalSize, bytes => result = bytes);

            // 分 4 块写入
            int chunkSize = 64 * 1024;
            int offset = 0;
            int chunkIdx = 0;
            while (offset < totalSize)
            {
                int len = Math.Min(chunkSize, totalSize - offset);
                byte[] chunk = new byte[len];
                Buffer.BlockCopy(expectedContent, offset, chunk, 0, len);
                receiver.WriteChunk(offset, chunk, len);
                offset += len;
                chunkIdx++;
            }
            Assert.Equal(4, chunkIdx); // 验证确实分了 4 块

            byte[] finishedBytes = receiver.Finish();
            Assert.NotNull(finishedBytes);
            Assert.Equal(totalSize, finishedBytes.Length);

            // 逐字节验证
            for (int i = 0; i < totalSize; i++)
                Assert.Equal(expectedContent[i], finishedBytes[i]);

            // 回调应被触发
            Assert.NotNull(result);
            Assert.Same(finishedBytes, result);
        }

        /// <summary>小数据（1 块）：单块即可完成。</summary>
        [Fact]
        public void Receiver_SingleChunk_SmallData()
        {
            byte[] data = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };
            var receiver = new ImageClipboardReceiver(2, data.Length);

            receiver.WriteChunk(0, data, data.Length);
            byte[] result = receiver.Finish();

            Assert.Equal(5, result.Length);
            Assert.Equal(0x01, result[0]);
            Assert.Equal(0x05, result[4]);
        }

        /// <summary>数据块乱序到达：先写第二块再写第一块，验证内容正确。</summary>
        [Fact]
        public void Receiver_OutOfOrderChunks_ContentCorrect()
        {
            int totalSize = 100;
            var receiver = new ImageClipboardReceiver(3, totalSize);

            byte[] part1 = new byte[50];
            byte[] part2 = new byte[50];
            for (int i = 0; i < 50; i++)
            {
                part1[i] = 0xAA;
                part2[i] = 0xBB;
            }

            // 先写第二块（offset=50）
            receiver.WriteChunk(50, part2, 50);
            // 再写第一块（offset=0）
            receiver.WriteChunk(0, part1, 50);

            byte[] result = receiver.Finish();
            Assert.Equal(100, result.Length);
            for (int i = 0; i < 50; i++)
                Assert.Equal(0xAA, result[i]);
            for (int i = 50; i < 100; i++)
                Assert.Equal(0xBB, result[i]);
        }

        /// <summary>完成后再次 WriteChunk 应被忽略（_finished 标志保护）。</summary>
        [Fact]
        public void Receiver_WriteChunk_AfterFinish_Ignored()
        {
            int totalSize = 10;
            var receiver = new ImageClipboardReceiver(4, totalSize);

            byte[] initial = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A };
            receiver.WriteChunk(0, initial, 10);
            byte[] result1 = receiver.Finish();
            Assert.Equal(10, result1.Length);

            // Finish 后再写一块，应被忽略
            byte[] extra = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
            receiver.WriteChunk(0, extra, 5);

            // 内容不应改变
            byte[] result2 = receiver.Finish();
            Assert.Equal(10, result2.Length);
            Assert.Equal(0x01, result2[0]);
            Assert.Equal(0x0A, result2[9]);
        }

        /// <summary>越界写入（offset + len > totalSize）应被忽略。</summary>
        [Fact]
        public void Receiver_OutOfBoundsWrite_Ignored()
        {
            int totalSize = 10;
            var receiver = new ImageClipboardReceiver(5, totalSize);

            // 正常写入
            byte[] valid = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };
            receiver.WriteChunk(0, valid, 5);

            // 越界写入：offset + len > totalSize
            byte[] overflow = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
            receiver.WriteChunk(8, overflow, 6); // 8 + 6 = 14 > 10
            // 部分越界：offset + len 刚好等于 totalSize 是有效的
            receiver.WriteChunk(5, new byte[] { 0x06, 0x07, 0x08, 0x09, 0x0A }, 5);

            byte[] result = receiver.Finish();
            Assert.Equal(10, result.Length);
            Assert.Equal(0x01, result[0]);
            Assert.Equal(0x05, result[4]);
            Assert.Equal(0x06, result[5]);
            Assert.Equal(0x0A, result[9]);
        }

        /// <summary>WriteChunk with null data 或 dataLen <= 0 应被忽略。</summary>
        [Fact]
        public void Receiver_NullOrZeroData_Ignored()
        {
            int totalSize = 10;
            var receiver = new ImageClipboardReceiver(6, totalSize);

            byte[] valid = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A };
            receiver.WriteChunk(0, valid, 10);

            // null data
            receiver.WriteChunk(0, null, 5);
            // dataLen = 0
            receiver.WriteChunk(0, new byte[5], 0);
            // dataLen < 0
            receiver.WriteChunk(0, new byte[5], -1);

            byte[] result = receiver.Finish();
            Assert.Equal(10, result.Length);
            // 内容不变
            Assert.Equal(0x01, result[0]);
            Assert.Equal(0x0A, result[9]);
        }

        /// <summary>Completed 事件订阅 + 触发：单订阅者。</summary>
        [Fact]
        public void Receiver_CompletedEvent_SingleSubscriber()
        {
            int totalSize = 16;
            var receiver = new ImageClipboardReceiver(7, totalSize);

            byte[] eventResult = null;
            int eventCallCount = 0;
            receiver.Completed += bytes =>
            {
                eventResult = bytes;
                Interlocked.Increment(ref eventCallCount);
            };

            byte[] data = new byte[totalSize];
            for (int i = 0; i < totalSize; i++) data[i] = (byte)i;
            receiver.WriteChunk(0, data, totalSize);

            byte[] finished = receiver.Finish();
            Assert.Equal(totalSize, finished.Length);
            Assert.Equal(1, eventCallCount);
            Assert.Same(finished, eventResult);
        }

        /// <summary>Completed 事件订阅 + 触发：多订阅者都收到。</summary>
        [Fact]
        public void Receiver_CompletedEvent_MultipleSubscribers()
        {
            int totalSize = 8;
            var receiver = new ImageClipboardReceiver(8, totalSize);

            byte[] result1 = null;
            byte[] result2 = null;
            receiver.Completed += bytes => result1 = bytes;
            receiver.Completed += bytes => result2 = bytes;

            byte[] data = new byte[totalSize];
            receiver.WriteChunk(0, data, totalSize);
            receiver.Finish();

            Assert.NotNull(result1);
            Assert.NotNull(result2);
            Assert.Equal(totalSize, result1.Length);
            Assert.Equal(totalSize, result2.Length);
        }

        /// <summary>构造函数 onCompleted 回调 + Completed 事件都触发。</summary>
        [Fact]
        public void Receiver_ConstructorCallback_PlusEvent_BothTriggered()
        {
            int totalSize = 4;
            byte[] constructorCallbackResult = null;
            byte[] eventResult = null;

            var receiver = new ImageClipboardReceiver(9, totalSize,
                bytes => constructorCallbackResult = bytes);
            receiver.Completed += bytes => eventResult = bytes;

            byte[] data = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
            receiver.WriteChunk(0, data, totalSize);
            receiver.Finish();

            Assert.NotNull(constructorCallbackResult);
            Assert.NotNull(eventResult);
            Assert.Equal(0xAA, constructorCallbackResult[0]);
            Assert.Equal(0xDD, constructorCallbackResult[3]);
            Assert.Equal(0xAA, eventResult[0]);
            Assert.Equal(0xDD, eventResult[3]);
        }

        /// <summary>Completed 回调抛异常不应影响 Finish 返回结果。</summary>
        [Fact]
        public void Receiver_CallbackThrows_FinishStillReturnsResult()
        {
            int totalSize = 4;
            var receiver = new ImageClipboardReceiver(10, totalSize,
                bytes => { throw new InvalidOperationException("test exception"); });

            byte[] data = new byte[] { 0x01, 0x02, 0x03, 0x04 };
            receiver.WriteChunk(0, data, totalSize);

            // 不应抛异常
            byte[] result = receiver.Finish();
            Assert.NotNull(result);
            Assert.Equal(4, result.Length);
            Assert.Equal(0x04, result[3]);
        }

        /// <summary>Finish 多次调用返回相同的 buffer，回调只触发一次。</summary>
        [Fact]
        public void Receiver_FinishMultipleTimes_ReturnsSameBuffer()
        {
            int totalSize = 8;
            int callCount = 0;
            var receiver = new ImageClipboardReceiver(11, totalSize,
                bytes => Interlocked.Increment(ref callCount));

            byte[] data = new byte[totalSize];
            receiver.WriteChunk(0, data, totalSize);

            byte[] first = receiver.Finish();
            byte[] second = receiver.Finish();
            byte[] third = receiver.Finish();

            Assert.Same(first, second);
            Assert.Same(second, third);
            Assert.Equal(1, callCount); // 回调只触发一次
        }

        /// <summary>大块数据（1MB）：验证大块组装正确。</summary>
        [Fact]
        public void Receiver_LargeData_1MB_ContentCorrect()
        {
            int totalSize = 1024 * 1024; // 1MB
            byte[] expectedContent = new byte[totalSize];
            var rand = new Random(123);
            rand.NextBytes(expectedContent);

            var receiver = new ImageClipboardReceiver(12, totalSize);

            // 模拟实际传输的 64KB 分块
            int chunkSize = 64 * 1024;
            int offset = 0;
            while (offset < totalSize)
            {
                int len = Math.Min(chunkSize, totalSize - offset);
                byte[] chunk = new byte[len];
                Buffer.BlockCopy(expectedContent, offset, chunk, 0, len);
                receiver.WriteChunk(offset, chunk, len);
                offset += len;
            }

            byte[] result = receiver.Finish();
            Assert.Equal(totalSize, result.Length);

            // 抽样验证（避免逐字节比较 1MB 太慢）
            for (int i = 0; i < totalSize; i += 1024)
                Assert.Equal(expectedContent[i], result[i]);
            // 验证首尾
            Assert.Equal(expectedContent[0], result[0]);
            Assert.Equal(expectedContent[totalSize - 1], result[totalSize - 1]);
        }
    }
}
