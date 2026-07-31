namespace EasyRDP.Core.Protocol
{
    using System;
    using System.IO;
    using System.Threading;
    using NLog;

    /// <summary>
    /// 文件剪贴板延迟渲染 — 发送方。检测到 CF_HDROP 后存储文件路径列表，
    /// 等待接收方的 ClipFileContentsReq 请求，按 position+size 读取文件内容并响应。
    /// 不主动发送文件数据，完全由接收方控制下载速率。
    /// 线程安全：HandleFileContentsReq 在接收线程调用，内部加锁保护文件读取。
    /// </summary>
    public class FileClipboardProvider
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// 文件块大小上限（1MB）：单次 FileContentsRes 返回的最大数据量。
        /// 之前为 64KB，6GB 文件需要 98304 次请求-响应往返，速度仅 0.6 MB/s。
        /// 增大到 1MB 后请求数降至 6144，结合 FileStream 缓存，速度提升约 50 倍。
        /// 1MB 远小于 MaxSafePayloadSize(10MB)，MessageReassembler 可正常分片重组。
        /// </summary>
        private const int MaxChunkSize = 1024 * 1024;

        private readonly uint _transferId;
        private readonly string[] _filePaths;
        private readonly Action<uint, byte[]> _sendAction;
        private readonly object _lock = new object();
        private int _streamIdSeq;
        private bool _disposed;

        /// <summary>
        /// 缓存当前打开的 FileStream，避免每次读块都 File.OpenRead 重新打开文件。
        /// 6GB 文件以 1MB 块下载只需打开 1 次，而非 6144 次。
        /// Consumer 按顺序下载(fileIdx 递增)，同一时间只有一个文件被读取。
        /// 当请求的 fileIdx 变化时，关闭旧 FileStream 并打开新的。
        /// 所有访问在 lock(_lock) 内，线程安全。
        /// </summary>
        private int _currentFileIdx = -1;
        private FileStream _currentFs;

        /// <summary>
        /// 构造文件剪贴板延迟渲染发送方。
        /// </summary>
        /// <param name="transferId">传输 ID（与 ClipFormatListMessage.TransferId 对应）。</param>
        /// <param name="filePaths">本地文件路径数组。</param>
        /// <param name="sendAction">发送回调：(sessionId, payload) => transport.Send 或 transportServer.SendTo。</param>
        public FileClipboardProvider(uint transferId, string[] filePaths, Action<uint, byte[]> sendAction)
        {
            _transferId = transferId;
            _filePaths = filePaths != null ? filePaths : new string[0];
            _sendAction = sendAction;
        }

        /// <summary>
        /// 处理接收方发来的文件内容请求：读取文件指定位置的数据并响应。
        /// 在接收线程调用，内部加锁保证线程安全。
        /// 关键：检查 req.TransferId 是否匹配自己的 _transferId。
        /// 用户连续复制两次文件时，旧 Provider 会被 Dispose 替换为新 Provider，
        /// 但旧 Consumer 可能还在发 FileContentsReq(旧 transferId)。
        /// 若不检查 transferId，新 Provider 会用 fileIdx 读自己的文件返回错误内容。
        /// </summary>
        /// <param name="req">文件内容请求消息。</param>
        public void HandleFileContentsReq(ClipFileContentsReqMessage req)
        {
            if (req == null || _sendAction == null) return;
            lock (_lock)
            {
                if (_disposed) return;

                // transferId 不匹配：说明这是旧 Consumer 的请求，当前 Provider 是新的。
                // 必须返回 StatusError（而不是用 fileIdx 读自己的文件返回错误内容），
                // 让旧 Consumer 收到错误后中断下载，避免文件内容错乱。
                if (req.TransferId != _transferId)
                {
                    Logger.Warn("FileContentsReq transferId mismatch: req={0} provider={1} — returning error (old consumer request after provider replaced)",
                        req.TransferId, _transferId);
                    var errRes = new ClipFileContentsResMessage
                    {
                        TransferId = req.TransferId,
                        StreamId = req.StreamId,
                        Status = ClipFileContentsResMessage.StatusError,
                        Data = new byte[0]
                    };
                    try
                    {
                        byte[] errPayload = errRes.Pack();
                        _sendAction(0, errPayload);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, "FileContentsReq send error response failed: streamId={0}", req.StreamId);
                    }
                    return;
                }

                ClipFileContentsResMessage res;
                if (req.FileIndex < 0 || req.FileIndex >= _filePaths.Length)
                {
                    Logger.Warn("FileContentsReq fileIndex out of range: {0} (files={1})",
                        req.FileIndex, _filePaths.Length);
                    res = new ClipFileContentsResMessage
                    {
                        TransferId = _transferId,
                        StreamId = req.StreamId,
                        Status = ClipFileContentsResMessage.StatusError,
                        Data = new byte[0]
                    };
                }
                else
                {
                    string path = _filePaths[req.FileIndex];
                    try
                    {
                        int toRead = (int)Math.Min(req.RequestedSize, MaxChunkSize);
                        if (toRead <= 0) toRead = MaxChunkSize;

                        byte[] buffer;
                        // 复用缓存的 FileStream：fileIdx 变化时才关闭旧的、打开新的。
                        // 避免 6GB 文件以 1MB 块下载时打开 6144 次 FileStream。
                        if (_currentFs == null || _currentFileIdx != req.FileIndex)
                        {
                            if (_currentFs != null)
                            {
                                try { _currentFs.Dispose(); } catch { }
                            }
                            _currentFs = File.OpenRead(path);
                            _currentFileIdx = req.FileIndex;
                        }
                        _currentFs.Seek(req.Position, SeekOrigin.Begin);
                        buffer = new byte[toRead];
                        int read = _currentFs.Read(buffer, 0, toRead);
                        if (read < toRead)
                        {
                            byte[] trimmed = new byte[read];
                            Buffer.BlockCopy(buffer, 0, trimmed, 0, read);
                            buffer = trimmed;
                        }

                        res = new ClipFileContentsResMessage
                        {
                            TransferId = _transferId,
                            StreamId = req.StreamId,
                            Status = ClipFileContentsResMessage.StatusOk,
                            Data = buffer
                        };
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, "FileContentsReq read failed: fileIndex={0} path={1} pos={2}",
                            req.FileIndex, path, req.Position);
                        res = new ClipFileContentsResMessage
                        {
                            TransferId = _transferId,
                            StreamId = req.StreamId,
                            Status = ClipFileContentsResMessage.StatusError,
                            Data = new byte[0]
                        };
                    }
                }

                try
                {
                    byte[] payload = res.Pack();
                    _sendAction(0, payload);
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "FileContentsReq send response failed: streamId={0}", req.StreamId);
                }
            }
        }

        /// <summary>
        /// 分配下一个唯一的 StreamId（供接收方使用，虽然通常接收方自己生成）。
        /// </summary>
        public uint NextStreamId()
        {
            return (uint)Interlocked.Increment(ref _streamIdSeq);
        }

        /// <summary>
        /// 释放资源。调用后 HandleFileContentsReq 不再响应。
        /// 同时关闭缓存的 FileStream，避免文件句柄泄漏。
        /// </summary>
        public void Dispose()
        {
            lock (_lock)
            {
                _disposed = true;
                if (_currentFs != null)
                {
                    try { _currentFs.Dispose(); } catch { }
                    _currentFs = null;
                    _currentFileIdx = -1;
                }
            }
        }
    }
}
