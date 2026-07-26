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
    /// 通过 ClipFileContentsReq/Res 按需拉取文件内容（64KB/块），写入临时文件。
    /// 下载完成后调用 onFinish 回调，参数为本地文件路径数组。
    /// 接收方控制下载速率，避免灌满 TCP 连接。
    /// </summary>
    public class FileClipboardConsumer
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// 单次请求的块大小（1MB）。
        /// 之前为 64KB，6GB 文件需要 98304 次请求-响应往返，速度仅 0.6 MB/s。
        /// 增大到 1MB 后请求数降至 6144，结合 Provider 的 FileStream 缓存，
        /// 速度预计提升约 50 倍（6GB 文件约 2-5 分钟，取决于网络和磁盘）。
        /// 1MB 块会被 MessageReassembler 分片为约 744 个 1400 字节 TCP 包连续发送，
        /// 利用 TCP 流水线，无需额外并发控制。
        /// </summary>
        private const int ChunkSize = 1024 * 1024;

        /// <summary>
        /// 单次请求超时（30秒）。
        /// 之前为 10 秒，但 1MB 块在低速网络下传输可能超过 10 秒
        /// （1MB / 1Mbps = 8 秒，加上磁盘 I/O 和分片开销）。
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
        private int _streamIdSeq;
        private volatile bool _cancelled;

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
        }

        /// <summary>
        /// 取消下载。调用后后台线程会在下一个请求点退出。
        /// </summary>
        public void Cancel()
        {
            _cancelled = true;
        }

        /// <summary>
        /// 后台线程：逐文件、逐块下载。每块 64KB，通过 TaskCompletionSource
        /// 等待接收线程的响应，实现接收方控速。
        /// </summary>
        private void DownloadFiles()
        {
            try
            {
                Directory.CreateDirectory(_tempDir);
                var localPaths = new List<string>();

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

                    bool downloadSuccess = false;
                    try
                    {
                        long position = 0;
                        using (var fs = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                        {
                            while (position < meta.FileSize && !_cancelled)
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
                                    Position = position,
                                    RequestedSize = ChunkSize
                                };

                                try
                                {
                                    byte[] reqPayload = req.Pack();
                                    _sendAction(0, reqPayload);
                                }
                                catch (Exception ex)
                                {
                                    Logger.Warn(ex, "Send FileContentsReq failed: streamId={0} fileIdx={1}", streamId, fileIdx);
                                    _pendingRequests.TryRemove(streamId, out tcs);
                                    break;
                                }

                                byte[] data;
                                try
                                {
                                    Task waitTask = tcs.Task;
                                    if (!waitTask.Wait(RequestTimeoutMs))
                                    {
                                        Logger.Warn("FileContentsReq timeout: streamId={0} fileIdx={1} pos={2}",
                                            streamId, fileIdx, position);
                                        _pendingRequests.TryRemove(streamId, out tcs);
                                        break;
                                    }
                                    data = tcs.Task.Result;
                                }
                                catch (Exception ex)
                                {
                                    Logger.Warn(ex, "FileContentsReq response error: streamId={0} fileIdx={1}", streamId, fileIdx);
                                    break;
                                }

                                if (data == null || data.Length == 0)
                                {
                                    Logger.Warn("FileContentsReq returned empty data: fileIdx={0} pos={1}", fileIdx, position);
                                    break;
                                }

                                fs.Write(data, 0, data.Length);
                                position += data.Length;
                            }
                        }

                        // 下载完整（position == meta.FileSize）或被取消但已写入部分数据时，视为成功
                        // 否则视为失败：从 localPaths 移除并删除部分文件
                        if (position == meta.FileSize)
                        {
                            downloadSuccess = true;
                            Logger.Info("File {0}/{1}: '{2}' downloaded {3}/{4} bytes",
                                fileIdx + 1, _files.Count, meta.FileName, position, meta.FileSize);
                        }
                        else
                        {
                            Logger.Warn("File {0}/{1}: '{2}' incomplete download {3}/{4} bytes — removed from results",
                                fileIdx + 1, _files.Count, meta.FileName, position, meta.FileSize);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, "Download file failed: fileIdx={0} name={1}", fileIdx, meta.FileName);
                    }

                    // 下载失败：从 localPaths 移除并删除部分文件
                    if (!downloadSuccess)
                    {
                        localPaths.RemoveAt(localPaths.Count - 1);
                        try { if (File.Exists(localPath)) File.Delete(localPath); } catch { }
                    }
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
