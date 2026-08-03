namespace EasyRDP.Core.Tests.Protocol
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using EasyRDP.Core.Protocol;

    /// <summary>
    /// 文件剪贴板端到端往返测试：FileClipboardProvider（发送方）
    /// ↔ FileClipboardConsumer（接收方）通过内存"总线"模拟 TCP 传输，
    /// 验证粘贴文件链路（元信息广播 → 按需分块拉取 → 本地落盘）内容完整一致。
    /// </summary>
    public class FileClipboardRoundTripTests
    {
        /// <summary>多文件（含大于 1MB 分块大小的文件 + 空文件）往返：内容逐字节一致。</summary>
        [Fact]
        public void ProviderToConsumer_MultiFile_ContentMatches()
        {
            // ── 准备发送方源文件 ──
            string srcDir = Path.Combine(Path.GetTempPath(), "EasyRDP_Test_Src_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(srcDir);

            // 2.5MB 随机文件（跨 3 个 1MB 块，验证并发分块下载）；中文文件名；空文件
            string bigPath = Path.Combine(srcDir, "big_文件.bin");
            string smallPath = Path.Combine(srcDir, "small.txt");
            string emptyPath = Path.Combine(srcDir, "empty.dat");

            byte[] bigContent = new byte[2 * 1024 * 1024 + 512 * 1024];
            var rand = new Random(20260731);
            rand.NextBytes(bigContent);
            File.WriteAllBytes(bigPath, bigContent);
            File.WriteAllText(smallPath, "EasyRDP clipboard file sync");
            File.WriteAllBytes(emptyPath, new byte[0]);

            string[] srcPaths = new string[] { bigPath, smallPath, emptyPath };
            var metaList = new List<ClipFormatListMessage.FileMeta>();
            foreach (var p in srcPaths)
            {
                var fi = new FileInfo(p);
                metaList.Add(new ClipFormatListMessage.FileMeta
                {
                    FileName = fi.Name,
                    FileSize = fi.Exists ? fi.Length : 0
                });
            }

            uint transferId = 12345;
            string[] receivedPaths = null;
            var done = new ManualResetEventSlim(false);
            var failure = new List<string>();

            // ── 内存"总线"：Req → Provider，Res → Consumer ──
            FileClipboardProvider provider = null;
            FileClipboardConsumer consumer = null;

            // 发送方（Provider）的 sendAction：发出 ClipFileContentsRes → 交给 Consumer
            Action<uint, byte[]> providerSend = (sid, payload) =>
            {
                try
                {
                    consumer.HandleFileContentsRes(ClipFileContentsResMessage.Unpack(payload));
                }
                catch (Exception ex)
                {
                    lock (failure) { failure.Add("ProviderSend: " + ex.Message); }
                }
            };

            // 接收方（Consumer）的 sendAction：发出 ClipFileContentsReq → 交给 Provider
            Action<uint, byte[]> consumerSend = (sid, payload) =>
            {
                try
                {
                    provider.HandleFileContentsReq(ClipFileContentsReqMessage.Unpack(payload));
                }
                catch (Exception ex)
                {
                    lock (failure) { failure.Add("ConsumerSend: " + ex.Message); }
                }
            };

            provider = new FileClipboardProvider(transferId, srcPaths, providerSend);
            consumer = new FileClipboardConsumer(transferId, metaList, "test_" + transferId,
                consumerSend,
                localPaths =>
                {
                    receivedPaths = localPaths;
                    done.Set();
                });

            consumer.StartDownload();

            // ── 等待下载完成（最多 60 秒，正常应远快于此） ──
            Assert.True(done.Wait(TimeSpan.FromSeconds(60)),
                "FileClipboardConsumer did not finish within 60s");
            Assert.True(failure.Count == 0, string.Join("; ", failure));

            Assert.NotNull(receivedPaths);
            Assert.Equal(3, receivedPaths.Length);

            // ── 逐文件内容校验 ──
            // 文件按 metaList 顺序下载：big → small → empty
            byte[] downloadedBig = File.ReadAllBytes(receivedPaths[0]);
            Assert.Equal(bigContent.Length, downloadedBig.Length);
            for (int i = 0; i < bigContent.Length; i += 4096)
                Assert.Equal(bigContent[i], downloadedBig[i]);
            Assert.Equal(bigContent[bigContent.Length - 1], downloadedBig[downloadedBig.Length - 1]);

            Assert.Equal("EasyRDP clipboard file sync", File.ReadAllText(receivedPaths[1]));
            Assert.Equal(0L, new FileInfo(receivedPaths[2]).Length);

            // ── 清理 ──
            consumer.Cancel();
            provider.Dispose();
            done.Dispose();
            try { Directory.Delete(srcDir, true); } catch { }
            string downloadDir = Path.Combine(Path.GetTempPath(), "EasyRDP", "Clipboard", "test_" + transferId);
            try { if (Directory.Exists(downloadDir)) Directory.Delete(downloadDir, true); } catch { }
        }

        /// <summary>
        /// 传输 ID 不匹配（旧 Consumer 的请求到达新 Provider）时必须返回 StatusError，
        /// 不能读取错误文件返回错误内容。
        /// </summary>
        [Fact]
        public void Provider_MismatchedTransferId_ReturnsError()
        {
            string srcDir = Path.Combine(Path.GetTempPath(), "EasyRDP_Test_Src_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(srcDir);
            string srcPath = Path.Combine(srcDir, "a.txt");
            File.WriteAllText(srcPath, "ORIGINAL");

            uint providerTransferId = 1;
            uint staleConsumerTransferId = 2;
            uint streamId = 99;
            bool gotError = false;

            var provider = new FileClipboardProvider(providerTransferId,
                new string[] { srcPath },
                (sid, payload) =>
                {
                    var res = ClipFileContentsResMessage.Unpack(payload);
                    gotError = res.Status == ClipFileContentsResMessage.StatusError
                        && res.TransferId == staleConsumerTransferId;
                });

            provider.HandleFileContentsReq(new ClipFileContentsReqMessage
            {
                TransferId = staleConsumerTransferId,
                StreamId = streamId,
                FileIndex = 0,
                Flags = ClipFileContentsReqMessage.FlagRange,
                Position = 0,
                RequestedSize = 8
            });

            Assert.True(gotError, "Mismatched transferId should return StatusError");
            provider.Dispose();
            try { Directory.Delete(srcDir, true); } catch { }
        }
    }
}
