namespace EasyRDP.Core.Tests.Protocol
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using EasyRDP.Core.Protocol;
    using Xunit;

    /// <summary>
    /// 文件剪贴板延迟渲染端到端集成测试：模拟 Provider ↔ Consumer 的完整交互，
    /// 不依赖真实网络，只验证消息层和文件组装层的协作。
    /// 测试场景：
    /// 1. 单文件 &gt; 64KB（多分块下载）
    /// 2. 多文件混合大小
    /// 3. 空文件（0 字节）
    /// 4. 不存在的文件（Provider 返回 StatusError，Consumer 优雅处理）
    /// </summary>
    public class DelayedRenderingIntegrationTests : IDisposable
    {
        private readonly string _testRootDir;

        public DelayedRenderingIntegrationTests()
        {
            _testRootDir = Path.Combine(Path.GetTempPath(), "EasyRDPDelayedRenderingTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_testRootDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_testRootDir, true); } catch { }
        }

        /// <summary>
        /// 模拟 Provider ↔ Consumer 的消息传递：在内存中直接调用对方的方法。
        /// 返回 Consumer 下载完成的本地文件路径（或 null 表示失败/超时）。
        /// 使用闭包捕获实现双向引用（Provider 先创建，捕获 consumer 变量；Consumer 后创建，捕获 provider）。
        /// </summary>
        private static string[] RunDelayedRenderingRoundTrip(
            uint transferId,
            string[] sourceFiles,
            List<ClipFormatListMessage.FileMeta> metaList,
            string sessionTag,
            int timeoutMs)
        {
            // 构造文件元信息（如果 metaList 为 null，则从 sourceFiles 构造）
            if (metaList == null)
            {
                metaList = new List<ClipFormatListMessage.FileMeta>(sourceFiles.Length);
                for (int i = 0; i < sourceFiles.Length; i++)
                {
                    long size = 0;
                    try { size = new FileInfo(sourceFiles[i]).Length; } catch { }
                    metaList.Add(new ClipFormatListMessage.FileMeta
                    {
                        FileName = Path.GetFileName(sourceFiles[i]),
                        FileSize = size
                    });
                }
            }

            // 完成信号
            string[] resultPaths = null;
            var doneSignal = new ManualResetEventSlim(false);

            // 前向声明 consumer：Provider 的 sendAction 需要引用 Consumer，
            // 但 Consumer 在 Provider 之后创建。闭包捕获变量（非值），后续赋值可见。
            FileClipboardConsumer consumer = null;

            // 创建 Provider（发送方）：响应 Consumer 的 FileContentsReq
            // Provider 的 sendAction 收到的是 ClipFileContentsRes 的 payload
            var provider = new FileClipboardProvider(transferId, sourceFiles,
                (sid, resPayload) =>
                {
                    // 模拟网络传输：反序列化为 Res，转发给 Consumer
                    var res = ClipFileContentsResMessage.Unpack(resPayload);
                    if (consumer != null)
                    {
                        lock (consumer)
                        {
                            consumer.HandleFileContentsRes(res);
                        }
                    }
                });

            // 创建 Consumer（接收方）：按需发送 FileContentsReq
            // Consumer 的 sendAction 收到的是 ClipFileContentsReq 的 payload
            consumer = new FileClipboardConsumer(transferId, metaList, sessionTag,
                (sid, reqPayload) =>
                {
                    // 模拟网络传输：反序列化为 Req，转发给 Provider
                    var req = ClipFileContentsReqMessage.Unpack(reqPayload);
                    provider.HandleFileContentsReq(req);
                },
                localPaths =>
                {
                    resultPaths = localPaths;
                    doneSignal.Set();
                });

            try
            {
                // 启动 Consumer 后台下载（此时 consumer 已赋值，Provider 的 lambda 可见）
                consumer.StartDownload();

                // 等待完成或超时
                if (!doneSignal.Wait(timeoutMs))
                {
                    consumer.Cancel();
                    return null;
                }
                return resultPaths;
            }
            finally
            {
                provider.Dispose();
            }
        }

        /// <summary>单文件 200KB（多分块下载）：验证文件内容完全一致。</summary>
        [Fact]
        public void EndToEnd_SingleFile_200KB_ContentVerified()
        {
            // 1) 创建 200KB 源文件（> 64KB chunk size，需要多块下载）
            string srcFile = Path.Combine(_testRootDir, "source_200k.bin");
            byte[] srcData = new byte[200 * 1024];
            var rng = new Random(42);
            rng.NextBytes(srcData);
            File.WriteAllBytes(srcFile, srcData);

            // 2) 运行延迟渲染 round-trip
            string[] downloaded = RunDelayedRenderingRoundTrip(
                transferId: 1,
                sourceFiles: new[] { srcFile },
                metaList: null,
                sessionTag: "test_single",
                timeoutMs: 10000);

            // 3) 验证
            Assert.NotNull(downloaded);
            Assert.Equal(1, downloaded.Length);
            Assert.True(File.Exists(downloaded[0]));

            byte[] downloadedData = File.ReadAllBytes(downloaded[0]);
            Assert.Equal(srcData.Length, downloadedData.Length);
            for (int i = 0; i < srcData.Length; i++)
                Assert.Equal(srcData[i], downloadedData[i]);
        }

        /// <summary>多文件混合大小：1KB + 200KB + 0 字节，验证全部下载成功。</summary>
        [Fact]
        public void EndToEnd_MultipleFiles_MixedSizes_AllDownloaded()
        {
            // 1) 创建源文件
            string[] srcFiles = new string[3];
            byte[][] srcDataArray = new byte[3][];

            // 1KB 文件
            srcFiles[0] = Path.Combine(_testRootDir, "small.bin");
            srcDataArray[0] = new byte[1024];
            for (int i = 0; i < 1024; i++) srcDataArray[0][i] = (byte)(i & 0xFF);
            File.WriteAllBytes(srcFiles[0], srcDataArray[0]);

            // 200KB 文件
            srcFiles[1] = Path.Combine(_testRootDir, "large.bin");
            srcDataArray[1] = new byte[200 * 1024];
            var rng = new Random(123);
            rng.NextBytes(srcDataArray[1]);
            File.WriteAllBytes(srcFiles[1], srcDataArray[1]);

            // 0 字节文件
            srcFiles[2] = Path.Combine(_testRootDir, "empty.bin");
            srcDataArray[2] = new byte[0];
            File.WriteAllBytes(srcFiles[2], srcDataArray[2]);

            // 2) 运行延迟渲染 round-trip
            string[] downloaded = RunDelayedRenderingRoundTrip(
                transferId: 2,
                sourceFiles: srcFiles,
                metaList: null,
                sessionTag: "test_multi",
                timeoutMs: 15000);

            // 3) 验证：3 个文件全部下载成功，内容一致
            Assert.NotNull(downloaded);
            Assert.Equal(3, downloaded.Length);

            for (int i = 0; i < 3; i++)
            {
                Assert.True(File.Exists(downloaded[i]),
                    "File " + i + " does not exist: " + downloaded[i]);
                byte[] downloadedData = File.ReadAllBytes(downloaded[i]);
                Assert.Equal(srcDataArray[i].Length, downloadedData.Length);
                for (int j = 0; j < srcDataArray[i].Length; j++)
                    Assert.Equal(srcDataArray[i][j], downloadedData[j]);
            }
        }

        /// <summary>空文件（0 字节）：Consumer 应创建 0 字节文件，不发任何 FileContentsReq。</summary>
        [Fact]
        public void EndToEnd_EmptyFile_CreatesZeroByteFile()
        {
            // 1) 创建 0 字节源文件
            string srcFile = Path.Combine(_testRootDir, "empty.bin");
            File.WriteAllBytes(srcFile, new byte[0]);

            // 2) 运行延迟渲染 round-trip
            string[] downloaded = RunDelayedRenderingRoundTrip(
                transferId: 3,
                sourceFiles: new[] { srcFile },
                metaList: null,
                sessionTag: "test_empty",
                timeoutMs: 5000);

            // 3) 验证：下载成功，文件大小为 0
            Assert.NotNull(downloaded);
            Assert.Equal(1, downloaded.Length);
            Assert.True(File.Exists(downloaded[0]));
            Assert.Equal(0, new FileInfo(downloaded[0]).Length);
        }

        /// <summary>源文件不存在：Provider 返回 StatusError，Consumer 优雅处理，不崩溃。</summary>
        [Fact]
        public void EndToEnd_MissingFile_ProviderReturnsError_ConsumerHandlesGracefully()
        {
            // 1) 源文件不存在
            string srcFile = Path.Combine(_testRootDir, "nonexistent.bin");

            // 2) 构造元信息（FileSize=100，但文件实际不存在）
            var metaList = new List<ClipFormatListMessage.FileMeta>
            {
                new ClipFormatListMessage.FileMeta { FileName = "nonexistent.bin", FileSize = 100 }
            };

            // 3) 运行延迟渲染 round-trip
            // Provider 会因 File.OpenRead 抛 FileNotFoundException，返回 StatusError
            // Consumer 收到 StatusError 后会 break 当前文件下载，但仍会触发 onFinish（已下载的文件）
            string[] downloaded = RunDelayedRenderingRoundTrip(
                transferId: 4,
                sourceFiles: new[] { srcFile },
                metaList: metaList,
                sessionTag: "test_missing",
                timeoutMs: 10000);

            // 4) 验证：不崩溃。Consumer 可能返回 null（无成功下载的文件）或空数组
            // 关键是测试本身不超时、不抛异常
            // 由于文件不存在，Provider 返回 StatusError，Consumer 的 TaskCompletionSource 会抛异常
            // Consumer catch 异常后 break，不会触发 onFinish（因为没有成功下载的文件）
            // 因此 downloaded 为 null 是预期行为
            Assert.True(downloaded == null || downloaded.Length == 0,
                "Missing file should not produce downloaded paths");
        }

        /// <summary>中文文件名 round-trip：验证 UTF-8 编码正确处理。</summary>
        [Fact]
        public void EndToEnd_ChineseFileName_FileDownloaded()
        {
            // 1) 创建中文文件名源文件
            string srcFile = Path.Combine(_testRootDir, "测试文件.txt");
            byte[] srcData = new byte[] { 0xEF, 0xBB, 0xBF, 0xE4, 0xBD, 0xA0, 0xE5, 0xA5, 0xBD }; // UTF-8 BOM + "你好"
            File.WriteAllBytes(srcFile, srcData);

            // 2) 运行延迟渲染 round-trip
            string[] downloaded = RunDelayedRenderingRoundTrip(
                transferId: 5,
                sourceFiles: new[] { srcFile },
                metaList: null,
                sessionTag: "test_chinese",
                timeoutMs: 5000);

            // 3) 验证
            Assert.NotNull(downloaded);
            Assert.Equal(1, downloaded.Length);
            Assert.True(File.Exists(downloaded[0]));

            byte[] downloadedData = File.ReadAllBytes(downloaded[0]);
            Assert.Equal(srcData.Length, downloadedData.Length);
            for (int i = 0; i < srcData.Length; i++)
                Assert.Equal(srcData[i], downloadedData[i]);
        }
    }
}
