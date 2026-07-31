namespace EasyRDP.Core.Protocol
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Collections.Concurrent;
    using NLog;

    /// <summary>
    /// 文件剪贴板延迟渲染 — 接收方。收到 ClipFormatList 后启动后台下载线程，
    /// 通过 ClipFileContentsReq/Res 按需拉取文件内容，写入临时文件。
    /// 下载完成后调用 onFinish 回调，参数为本地文件路径数组。
    /// 接收方控制下载速率，避免灌满 TCP 连接。
    /// 并发流水线：同时维护 Concurrency 个 in-flight 请求，减少 RTT 串行等待。
    /// </summary>
    public class FileClipboardConsumer
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// 单次请求的块大小（1MB）。
        /// 1MB 块会被 MessageReassembler 分片为约 744 个 1400 字节 TCP 包连续发送，
        /// 利用 TCP 流水线，无需额外并发控制。
        /// </summary>
        private const int ChunkSize = 1024 * 1024;

        /// <summary>
        /// 并发请求数（滑动窗口大小）。
        /// Consumer 同时发 Concurrency 个 FileContentsReq，收到响应后立即发下一个。
        /// 相比串行模式，减少 RTT 串行等待，6GB 文件预计从 184 秒降到约 52 秒。
        /// 4 路是折中值：太少(1-2)提升有限；太多会长时间占满发送锁，
        /// 让同一 socket 上的视频帧/输入事件（无 QoS 优先级）被显著拖慢。
        /// </summary>
        private const int Concurrency = 4;

        /// <summary>
        /// 单次请求超时（30秒）。
        /// 1MB 块在低速网络下传输可能较慢（1MB / 1Mbps = 8 秒），
        /// 30 秒超时对局域网和广域网都足够。
        /// </summary>
        private const int RequestTimeoutMs = 30000;

        private readonly uint _transferId;
        private readonly List<ClipFormatListMessage.FileMeta> _files;
        private readonly string _sessionTag;
        private readonly Action<uint, byte[]> _sendAction;
        private readonly Action<string[]> _onFinish;
        private readonly string _tempDir;
        private readonly ConcurrentDictionary<uint, TaskCompletionSource<byte[]>> _pendingRequests
            = new ConcurrentDictionary<uint, TaskCompletionSource<byte[]>>();
#if NET40
        // net40 兼容路径的等待句柄表（不使用 Microsoft.Bcl.Async，避免破坏 net40 项目引用解析）
        private readonly ConcurrentDictionary<uint, Net40ChunkWait> _net40Pending
            = new ConcurrentDictionary<uint, Net40ChunkWait>();
#endif
        private int _streamIdSeq;
        private volatile bool _cancelled;

#if NET40
        /// <summary>net40 单块请求的等待状态：响应数据 + 完成信号。</summary>
        private sealed class Net40ChunkWait : IDisposable
        {
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public byte[] Data;
            public bool Failed;

            public void Dispose()
            {
                Done.Dispose();
            }
        }
#endif

        /// <summary>
        /// 进度变化事件：(downloadedBytes, totalBytes)。
        /// 每个块下载完成后触发，调用方可用于更新 UI 进度条。
        /// 在下载线程触发，调用方需自行 marshal 到 UI 线程。
        /// </summary>
        public event Action<long, long> ProgressChanged;

        /// <summary>
        /// 构造文件剪贴板延迟渲染接收方。
        /// </summary>
        /// <param name="transferId">传输 ID。</param>
        /// <param name="files">文件元信息列表（来自 ClipFormatListMessage）。</param>
        /// <param name="sessionTag">会话标签，用于临时目录命名（如 "client" 或 "server_1"）。</param>
        /// <param name="sendAction">发送回调：(sessionId, payload) => transport.Send 或 transportServer.SendTo。</param>
        /// <param name="onFinish">完成回调，参数为本地文件路径数组。</param>
        public FileClipboardConsumer(uint transferId,
            List<ClipFormatListMessage.FileMeta> files,
            string sessionTag,
            Action<uint, byte[]> sendAction,
            Action<string[]> onFinish)
        {
            _transferId = transferId;
            _files = files != null ? files : new List<ClipFormatListMessage.FileMeta>();
            _sessionTag = sessionTag != null ? sessionTag : "client";
            _sendAction = sendAction;
            _onFinish = onFinish;
            _tempDir = Path.Combine(Path.GetTempPath(), "EasyRDP", "Clipboard", _sessionTag, transferId.ToString());
        }

        /// <summary>
        /// 启动后台下载线程。在接收线程调用（收到 ClipFormatList 后立即启动）。
        /// 下载完成后在后台线程触发 onFinish 回调。
        /// </summary>
        public void StartDownload()
        {
            ThreadPool.QueueUserWorkItem(state => DownloadFiles());
        }

        /// <summary>
        /// 处理发送方发来的文件内容响应。在接收线程调用。
        /// 根据 StreamId 匹配 pending request 并完成 TaskCompletionSource。
        /// </summary>
        public void HandleFileContentsRes(ClipFileContentsResMessage res)
        {
            if (res == null) return;
#if NET40
            Net40ChunkWait wait;
            if (_net40Pending.TryRemove(res.StreamId, out wait))
            {
                if (res.Status == ClipFileContentsResMessage.StatusOk)
                    wait.Data = res.Data != null ? res.Data : new byte[0];
                else
                    wait.Failed = true;
                wait.Done.Set();
            }
#else
            TaskCompletionSource<byte[]> tcs;
            if (_pendingRequests.TryRemove(res.StreamId, out tcs))
            {
                if (res.Status == ClipFileContentsResMessage.StatusOk)
                {
                    tcs.SetResult(res.Data != null ? res.Data : new byte[0]);
                }
                else
                {
                    tcs.SetException(new IOException("FileContentsReq failed with status " + res.Status));
                }
            }
#endif
        }

        /// <summary>
        /// 取消下载。调用后后台线程会在下一个请求点退出。
        /// </summary>
        public void Cancel()
        {
            _cancelled = true;
#if NET40
            // 唤醒所有等待中的 worker，让其尽快退出
            foreach (var kv in _net40Pending)
            {
                kv.Value.Failed = true;
                kv.Value.Done.Set();
            }
            _net40Pending.Clear();
#endif
        }

        /// <summary>
        /// 后台线程：逐文件下载，每个文件内部用 Concurrency 路并发流水线拉取块。
        /// 滑动窗口模式：同时发 Concurrency 个请求，每收到一个响应就发下一个。
        /// 相比串行模式，减少 RTT 串行等待，大幅提升吞吐量。
        /// </summary>
        private void DownloadFiles()
        {
            try
            {
                Directory.CreateDirectory(_tempDir);
                var localPaths = new List<string>();

                // 计算总大小（用于进度报告）
                long totalSize = 0;
                foreach (var f in _files) totalSize += f.FileSize > 0 ? f.FileSize : 0;
                long totalDownloaded = 0;
                int lastReportedPercent = -1;
                object progressLock = new object();

                for (int fileIdx = 0; fileIdx < _files.Count; fileIdx++)
                {
                    if (_cancelled) break;

                    var meta = _files[fileIdx];
                    string safeName = MakeSafeFileName(meta.FileName, fileIdx);
                    string localPath = Path.Combine(_tempDir, safeName);
                    localPaths.Add(localPath);

                    if (meta.FileSize <= 0)
                    {
                        File.Create(localPath).Close();
                        Logger.Info("File {0}/{1}: '{2}' is empty — created 0-byte file",
                            fileIdx + 1, _files.Count, meta.FileName);
                        continue;
                    }

                    bool downloadSuccess = DownloadFileConcurrent(fileIdx, meta, localPath,
                        delta =>
                        {
                            // 线程安全的进度累加
                            long downloaded = Interlocked.Add(ref totalDownloaded, delta);
                            // lock 保护 lastReportedPercent 读写（多 Task 并发回调）
                            lock (progressLock)
                            {
                                int percent = totalSize > 0 ? (int)((downloaded * 100) / totalSize) : 0;
                                if (percent != lastReportedPercent)
                                {
                                    lastReportedPercent = percent;
                                    try
                                    {
                                        var handler = ProgressChanged;
                                        if (handler != null) handler(downloaded, totalSize);
                                    }
                                    catch (Exception ex) { Logger.Warn(ex, "ProgressChanged callback failed"); }
                                }
                            }
                        });

                    if (!downloadSuccess)
                    {
                        localPaths.RemoveAt(localPaths.Count - 1);
                        try { if (File.Exists(localPath)) File.Delete(localPath); } catch { }
                    }
                }

                // 最终进度报告（100%）
                if (!_cancelled && totalSize > 0)
                {
                    try
                    {
                        var handler = ProgressChanged;
                        if (handler != null) handler(totalSize, totalSize);
                    }
                    catch { }
                }

                if (!_cancelled && localPaths.Count > 0)
                {
                    Logger.Info("FileClipboardConsumer download complete: transferId={0} files={1}",
                        _transferId, localPaths.Count);
                    if (_onFinish != null)
                    {
                        try { _onFinish(localPaths.ToArray()); }
                        catch (Exception ex) { Logger.Warn(ex, "FileClipboardConsumer onFinish callback failed"); }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "FileClipboardConsumer DownloadFiles failed: transferId={0}", _transferId);
            }
        }

        /// <summary>
        /// 并发下载单个文件：用 SemaphoreSlim 控制最多 Concurrency 个 in-flight 请求。
        /// 每个块在独立 Task 中等待响应，收到后按 position 写入文件（lock 保护 FileStream）。
        /// 所有块发完后，Task.WaitAll 等待全部完成。
        /// </summary>
        /// <param name="fileIdx">文件索引。</param>
        /// <param name="meta">文件元信息。</param>
        /// <param name="localPath">本地保存路径。</param>
        /// <param name="onChunkCompleted">块下载完成回调，参数为本块字节数。</param>
        /// <returns>true 表示下载完整；false 表示失败或不完整。</returns>
        private bool DownloadFileConcurrent(int fileIdx, ClipFormatListMessage.FileMeta meta,
            string localPath, Action<long> onChunkCompleted)
        {
#if NET40
            // net40 无 async/await 运行时支持；不使用 Microsoft.Bcl.Async（它会破坏
            // net40 项目的项目引用解析），改用线程 + ManualResetEventSlim 实现等价并发。
            return DownloadFileConcurrentNet40(fileIdx, meta, localPath, onChunkCompleted);
#else
            long fileSize = meta.FileSize;
            int totalChunks = (int)((fileSize + ChunkSize - 1) / ChunkSize);
            var semaphore = new SemaphoreSlim(Concurrency);
            object writeLock = new object();
            int failedFlag = 0; // 0=ok, 1=failed (Volatile 读写)
            int completedChunks = 0;

            using (var fs = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                var tasks = new List<Task>();

                for (int chunkIdx = 0; chunkIdx < totalChunks; chunkIdx++)
                {
                    if (_cancelled || Thread.VolatileRead(ref failedFlag) == 1) break;

                    // 等待窗口空位：最多 Concurrency 个 in-flight
                    semaphore.Wait();

                    long reqPos = (long)chunkIdx * ChunkSize;
                    int toRead = (int)Math.Min(ChunkSize, fileSize - reqPos);

                    // 捕获局部变量供 lambda 使用
                    long capturedPos = reqPos;
                    int capturedRead = toRead;
                    int capturedChunkIdx = chunkIdx;

                    // 非 net40 路径（netstandard2.0/net8）可直接使用 Task.Run
                    var task = Task.Run(async () =>
                    {
                        try
                        {
                            uint streamId = (uint)Interlocked.Increment(ref _streamIdSeq);
                            var tcs = new TaskCompletionSource<byte[]>();
                            _pendingRequests[streamId] = tcs;

                            var req = new ClipFileContentsReqMessage
                            {
                                TransferId = _transferId,
                                StreamId = streamId,
                                FileIndex = fileIdx,
                                Flags = ClipFileContentsReqMessage.FlagRange,
                                Position = capturedPos,
                                RequestedSize = capturedRead
                            };

                            try
                            {
                                byte[] reqPayload = req.Pack();
                                _sendAction(0, reqPayload);
                            }
                            catch (Exception ex)
                            {
                                Logger.Warn(ex, "Send FileContentsReq failed: streamId={0} chunkIdx={1}",
                                    streamId, capturedChunkIdx);
                                _pendingRequests.TryRemove(streamId, out tcs);
                                Thread.VolatileWrite(ref failedFlag, 1);
                                return;
                            }

                            // 等待响应或超时
                            var winner = await Task.WhenAny(tcs.Task, Task.Delay(RequestTimeoutMs));
                            if (winner != tcs.Task)
                            {
                                _pendingRequests.TryRemove(streamId, out tcs);
                                Logger.Warn("FileContentsReq timeout: streamId={0} chunkIdx={1} pos={2}",
                                    streamId, capturedChunkIdx, capturedPos);
                                Thread.VolatileWrite(ref failedFlag, 1);
                                return;
                            }

                            byte[] data;
                            try
                            {
                                data = await tcs.Task;
                            }
                            catch (Exception ex)
                            {
                                Logger.Warn(ex, "FileContentsReq response error: streamId={0} chunkIdx={1}",
                                    streamId, capturedChunkIdx);
                                Thread.VolatileWrite(ref failedFlag, 1);
                                return;
                            }

                            if (data == null || data.Length == 0)
                            {
                                Logger.Warn("FileContentsReq returned empty data: chunkIdx={0} pos={1}",
                                    capturedChunkIdx, capturedPos);
                                Thread.VolatileWrite(ref failedFlag, 1);
                                return;
                            }

                            // 按 position 写入文件（lock 保护 FileStream 线程安全）
                            lock (writeLock)
                            {
                                fs.Seek(capturedPos, SeekOrigin.Begin);
                                fs.Write(data, 0, data.Length);
                            }

                            Interlocked.Increment(ref completedChunks);
                            var chunkHandler = onChunkCompleted;
                            if (chunkHandler != null) chunkHandler(data.Length);
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn(ex, "DownloadChunk failed: chunkIdx={0} pos={1}",
                                capturedChunkIdx, capturedPos);
                            Thread.VolatileWrite(ref failedFlag, 1);
                        }
                        finally
                        {
                            // 必须释放 semaphore 许可，否则窗口耗尽后死锁
                            // （所有 return 分支和异常都必须执行此操作）
                            semaphore.Release();
                        }
                    });

                    tasks.Add(task);
                }

                // 等待所有 in-flight 请求完成
                try { Task.WaitAll(tasks.ToArray()); }
                catch (Exception ex) { Logger.Warn(ex, "Task.WaitAll failed during download"); }
                semaphore.Dispose();

                // 确保数据刷到磁盘
                try { fs.Flush(true); } catch { }
            }

            bool success = Thread.VolatileRead(ref failedFlag) == 0
                && completedChunks == totalChunks
                && !_cancelled;

            if (success)
            {
                Logger.Info("File {0}/{1}: '{2}' downloaded {3}/{4} bytes (chunks={5}, concurrency={6})",
                    fileIdx + 1, _files.Count, meta.FileName, fileSize, fileSize,
                    completedChunks, Concurrency);
            }
            else
            {
                Logger.Warn("File {0}/{1}: '{2}' incomplete download {3}/{4} bytes (chunks={5}/{6})",
                    fileIdx + 1, _files.Count, meta.FileName,
                    (long)completedChunks * ChunkSize, fileSize, completedChunks, totalChunks);
            }

            return success;
#endif
        }

#if NET40
        /// <summary>
        /// net40 兼容的并发下载：Concurrency 个后台 worker 线程构成滑动窗口，
        /// 每线程领取下一个块号 → 发送 FileContentsReq → 等待响应（超时 30s）→ 按位置写文件。
        /// </summary>
        private bool DownloadFileConcurrentNet40(int fileIdx, ClipFormatListMessage.FileMeta meta,
            string localPath, Action<long> onChunkCompleted)
        {
            long fileSize = meta.FileSize;
            int totalChunks = (int)((fileSize + ChunkSize - 1) / ChunkSize);
            var st = new Net40DownloadState
            {
                FileIdx = fileIdx,
                FileSize = fileSize,
                TotalChunks = totalChunks,
                Semaphore = new SemaphoreSlim(Concurrency),
                WriteLock = new object(),
                OnChunkCompleted = onChunkCompleted
            };

            using (var fs = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                st.Fs = fs;
                var workers = new List<Thread>();
                for (int w = 0; w < Concurrency; w++)
                {
                    var t = new Thread(() => DownloadChunkWorkerNet40(meta, st));
                    t.IsBackground = true;
                    t.Start();
                    workers.Add(t);
                }
                foreach (var t in workers)
                {
                    try { t.Join(); } catch { }
                }
                st.Semaphore.Dispose();
                try { fs.Flush(true); } catch { }
            }

            bool success = Thread.VolatileRead(ref st.FailedFlag) == 0
                && st.CompletedChunks == totalChunks
                && !_cancelled;

            if (success)
            {
                Logger.Info("File {0}/{1}: '{2}' downloaded {3}/{4} bytes (chunks={5}, concurrency={6})",
                    fileIdx + 1, _files.Count, meta.FileName, fileSize, fileSize,
                    st.CompletedChunks, Concurrency);
            }
            else
            {
                Logger.Warn("File {0}/{1}: '{2}' incomplete download {3}/{4} bytes (chunks={5}/{6})",
                    fileIdx + 1, _files.Count, meta.FileName,
                    (long)st.CompletedChunks * ChunkSize, fileSize, st.CompletedChunks, totalChunks);
            }

            return success;
        }

        /// <summary>net40 下载状态（线程间共享，C#5 匿名方法无法捕获 ref 参数，故用容器类）。</summary>
        private sealed class Net40DownloadState
        {
            public int FileIdx;
            public long FileSize;
            public int TotalChunks;
            public FileStream Fs;
            public object WriteLock;
            public SemaphoreSlim Semaphore;
            public Action<long> OnChunkCompleted;
            public int FailedFlag;
            public int CompletedChunks;
            public int NextChunkIdx;
        }

        /// <summary>net40 单块下载 worker：领取块号 → 发送请求 → 等待响应/超时 → 写文件。</summary>
        private void DownloadChunkWorkerNet40(ClipFormatListMessage.FileMeta meta, Net40DownloadState st)
        {
            try
            {
                while (!_cancelled && Thread.VolatileRead(ref st.FailedFlag) == 0)
                {
                    int chunkIdx = Interlocked.Increment(ref st.NextChunkIdx) - 1;
                    if (chunkIdx >= st.TotalChunks) return;

                    // 限流：最多 Concurrency 个 in-flight 请求
                    st.Semaphore.Wait();
                    Net40ChunkWait wait = null;
                    try
                    {
                        long reqPos = (long)chunkIdx * ChunkSize;
                        int toRead = (int)Math.Min(ChunkSize, st.FileSize - reqPos);
                        uint streamId = (uint)Interlocked.Increment(ref _streamIdSeq);
                        wait = new Net40ChunkWait();
                        _net40Pending[streamId] = wait;

                        var req = new ClipFileContentsReqMessage
                        {
                            TransferId = _transferId,
                            StreamId = streamId,
                            FileIndex = st.FileIdx,
                            Flags = ClipFileContentsReqMessage.FlagRange,
                            Position = reqPos,
                            RequestedSize = toRead
                        };

                        try
                        {
                            byte[] reqPayload = req.Pack();
                            _sendAction(0, reqPayload);
                        }
                        catch (Exception ex)
                        {
                            Net40ChunkWait removed;
                            _net40Pending.TryRemove(streamId, out removed);
                            Logger.Warn(ex, "Send FileContentsReq failed: streamId={0} chunkIdx={1}",
                                streamId, chunkIdx);
                            Thread.VolatileWrite(ref st.FailedFlag, 1);
                            return;
                        }

                        // 等待响应或超时（30s）
                        if (!wait.Done.Wait(RequestTimeoutMs))
                        {
                            Net40ChunkWait removed;
                            _net40Pending.TryRemove(streamId, out removed);
                            Logger.Warn("FileContentsReq timeout: streamId={0} chunkIdx={1} pos={2}",
                                streamId, chunkIdx, reqPos);
                            Thread.VolatileWrite(ref st.FailedFlag, 1);
                            return;
                        }

                        if (wait.Failed || wait.Data == null || wait.Data.Length == 0)
                        {
                            Logger.Warn("FileContentsReq failed or empty: chunkIdx={0} pos={1}",
                                chunkIdx, reqPos);
                            Thread.VolatileWrite(ref st.FailedFlag, 1);
                            return;
                        }

                        // 按 position 写入文件（lock 保护 FileStream 线程安全）
                        lock (st.WriteLock)
                        {
                            st.Fs.Seek(reqPos, SeekOrigin.Begin);
                            st.Fs.Write(wait.Data, 0, wait.Data.Length);
                        }

                        Interlocked.Increment(ref st.CompletedChunks);
                        var chunkHandler = st.OnChunkCompleted;
                        if (chunkHandler != null) chunkHandler(wait.Data.Length);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, "DownloadChunk failed: chunkIdx={0}", chunkIdx);
                        Thread.VolatileWrite(ref st.FailedFlag, 1);
                    }
                    finally
                    {
                        st.Semaphore.Release();
                        if (wait != null) wait.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Download worker failed");
                Thread.VolatileWrite(ref st.FailedFlag, 1);
            }
        }
#endif

        /// <summary>把文件名转换为本地安全的文件名。</summary>
        private static string MakeSafeFileName(string fileName, int index)
        {
            if (string.IsNullOrEmpty(fileName))
                return "file_" + index;

            string name = Path.GetFileName(fileName);
            char[] invalid = Path.GetInvalidFileNameChars();
            foreach (char c in invalid)
                name = name.Replace(c, '_');

            if (string.IsNullOrEmpty(name))
                name = "file_" + index;

            return name;
        }
    }
}
