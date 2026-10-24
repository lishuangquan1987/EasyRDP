namespace EasyRDP.Core.Transfer
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Threading;
    using EasyRDP.Core.Protocol;
    using EasyRDP.Core.Transport;
    using NLog;

    /// <summary>文件传输进度（发送或接收方向）。</summary>
    public class FileTransferProgress
    {
        /// <summary>传输标识。</summary>
        public uint TransferId;

        /// <summary>true=本端发送，false=本端接收。</summary>
        public bool Outgoing;

        /// <summary>当前文件名。</summary>
        public string FileName;

        /// <summary>当前文件索引（从 0 开始）。</summary>
        public int FileIndex;

        /// <summary>本次传输的文件总数。</summary>
        public int FileCount;

        /// <summary>已传输字节数（发送=已确认，接收=已落盘）。</summary>
        public long BytesTransferred;

        /// <summary>本次传输总字节数。</summary>
        public long TotalBytes;

        /// <summary>瞬时速率（字节/秒）。</summary>
        public double BytesPerSecond;
    }

    /// <summary>
    /// 文件传输会话（服务端与客户端共用）。负责双向的文件收发：
    /// 发送方发清单（Offer）→ 接收方逐文件请求（Start）→ 数据块 + 累积确认（Data/Ack）
    /// → 文件结束（End）。数据走独立文件通道时不会与视频流争用发送锁。
    /// 收发消息由传输层回调 HandleMessage 注入；发送线程独立运行。
    /// </summary>
    public class FileTransferSession : IDisposable
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private static int _transferIdSeed = Environment.TickCount;
        private const int TransferTimeoutMs = 30000;

        private readonly Func<byte, byte[], bool> _send;
        private readonly Func<bool> _videoBacklogged;
        private readonly string _receiveDirectory;
        private readonly object _lock = new object();
        private readonly Dictionary<uint, OutgoingTransfer> _outgoing = new Dictionary<uint, OutgoingTransfer>();
        private IncomingTransfer _incoming;
        private bool _disposed;

        private class OutgoingTransfer
        {
            public uint Id;
            public string[] Paths;
            public List<FileTransferOfferMessage.FileMeta> Files;
            public long[] Acked;
            public long[] LastAckTicks;
            public int StartedIndex = -1;
            public bool Cancelled;
            public long TotalBytes;
            public long LastAckTicksAny;
            public long RateStartTicks;
            public long RateStartBytes;
            public FileTransferProgress LastProgress;
        }

        private class IncomingTransfer
        {
            public uint Id;
            public List<FileTransferOfferMessage.FileMeta> Files;
            public int Index;
            public FileStream Stream;
            public string CurrentPath;
            public long Received;
            public long TotalBytes;
            public long TotalReceived;
            public List<string> SavedPaths = new List<string>();
        }

        /// <summary>进度变化（发送/接收）。回调在后台线程，UI 需自行切回界面线程。</summary>
        public event Action<FileTransferProgress> ProgressChanged;

        /// <summary>本端发送完毕（对端已确认全部数据）。</summary>
        public event Action<uint> OutgoingCompleted;

        /// <summary>本端接收完毕（文件已全部落盘），参数为保存路径列表。</summary>
        public event Action<uint, string[]> IncomingCompleted;

        /// <summary>传输失败，参数：传输标识、原因、是否为本端发送方向。</summary>
        public event Action<uint, string, bool> Failed;

        /// <summary>
        /// 创建文件传输会话。
        /// </summary>
        /// <param name="send">发送一条协议消息：参数为 MessageType 字节与 payload，返回是否成功。</param>
        /// <param name="videoBacklogged">返回 true 表示视频流正在等带宽，文件通道应让路。</param>
        /// <param name="receiveDirectory">接收文件的保存目录（不存在时自动创建）。</param>
        public FileTransferSession(Func<byte, byte[], bool> send, Func<bool> videoBacklogged, string receiveDirectory)
        {
            if (send == null) throw new ArgumentNullException("send");
            _send = send;
            _videoBacklogged = videoBacklogged;
            _receiveDirectory = receiveDirectory;
        }

        /// <summary>接收文件保存目录。</summary>
        public string ReceiveDirectory
        {
            get { return _receiveDirectory; }
        }

        /// <summary>是否正在发送或接收。</summary>
        public bool IsBusy
        {
            get
            {
                lock (_lock)
                {
                    return _incoming != null || _outgoing.Count > 0;
                }
            }
        }

        // ================= 发送 =================

        /// <summary>
        /// 发起一次文件传输（异步）。返回传输标识；参数非法时抛异常。
        /// </summary>
        public uint SendFiles(string[] paths)
        {
            if (paths == null || paths.Length == 0)
                throw new ArgumentException("No file selected");
            if (paths.Length > FileTransferProtocol.MaxFiles)
                throw new ArgumentException("Too many files: " + paths.Length);

            var files = new List<FileTransferOfferMessage.FileMeta>(paths.Length);
            long total = 0;
            foreach (string path in paths)
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    throw new FileNotFoundException("File not found: " + path, path);
                var fi = new FileInfo(path);
                files.Add(new FileTransferOfferMessage.FileMeta
                {
                    FileName = Path.GetFileName(path),
                    FileSize = fi.Length
                });
                total += fi.Length;
            }

            uint transferId = NextTransferId();
            long now = Stopwatch.GetTimestamp();
            var transfer = new OutgoingTransfer
            {
                Id = transferId,
                Paths = paths,
                Files = files,
                Acked = new long[files.Count],
                LastAckTicks = new long[files.Count],
                TotalBytes = total,
                LastAckTicksAny = now,
                RateStartTicks = now
            };
            for (int i = 0; i < transfer.LastAckTicks.Length; i++)
                transfer.LastAckTicks[i] = now;

            lock (_lock)
            {
                if (_disposed) throw new ObjectDisposedException("FileTransferSession");
                _outgoing[transferId] = transfer;
            }

            // 先启动工作线程再发清单：接收方的 Start 可能紧跟 Offer 到达，
            // 工作线程此时已就绪等待，不会漏掉信号。
            var worker = new Thread(SendWorker);
            worker.IsBackground = true;
            worker.Name = "FileTransferSend-" + transferId;
            worker.Start(transfer);

            SendOffer(transfer);
            return transferId;
        }

        /// <summary>取消指定传输（发送或接收方向）。</summary>
        public void Cancel(uint transferId)
        {
            bool outgoingFound = false;
            IncomingTransfer incoming = null;
            lock (_lock)
            {
                if (_outgoing.ContainsKey(transferId))
                {
                    _outgoing[transferId].Cancelled = true;
                    outgoingFound = true;
                }
                if (_incoming != null && _incoming.Id == transferId)
                {
                    incoming = _incoming;
                    _incoming = null;
                }
                Monitor.PulseAll(_lock);
            }

            SendCancel(transferId, FileTransferProtocol.CancelUser);
            if (incoming != null)
            {
                CloseIncomingStream(incoming);
                DeletePartial(incoming);
                RaiseFailed(transferId, "Cancelled", false);
            }
            if (outgoingFound)
                RaiseFailed(transferId, "Cancelled", true);
        }

        private void SendOffer(OutgoingTransfer transfer)
        {
            var offer = new FileTransferOfferMessage
            {
                TransferId = transfer.Id,
                Files = transfer.Files
            };
            if (!_send((byte)MessageType.FileTransferOffer, offer.Pack()))
            {
                lock (_lock)
                {
                    transfer.Cancelled = true;
                    _outgoing.Remove(transfer.Id);
                    Monitor.PulseAll(_lock);
                }
                RaiseFailed(transfer.Id, "file channel unavailable", true);
            }
        }

        private void SendWorker(object state)
        {
            var transfer = (OutgoingTransfer)state;
            string error = null;
            try
            {
                var pacer = new AdaptivePacer(_videoBacklogged);
                for (int i = 0; i < transfer.Paths.Length; i++)
                {
                    WaitForStart(transfer, i, i == 0 ? 60000 : TransferTimeoutMs);
                    long length = SendOneFile(transfer, i, pacer);
                    long endSentTicks = Stopwatch.GetTimestamp();
                    SendEnd(transfer.Id, (ushort)i, FileTransferProtocol.ResultOk);
                    WaitForFinalAck(transfer, i, length, endSentTicks, TransferTimeoutMs);
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            lock (_lock)
            {
                transfer.Cancelled = transfer.Cancelled || error != null;
                _outgoing.Remove(transfer.Id);
                Monitor.PulseAll(_lock);
            }
            if (error != null)
            {
                SendCancel(transfer.Id, FileTransferProtocol.CancelError);
                RaiseFailed(transfer.Id, error, true);
            }
            else
            {
                var handler = OutgoingCompleted;
                if (handler != null) handler(transfer.Id);
            }
        }

        /// <summary>发送一个文件的全部数据块，返回实际发送字节数。</summary>
        private long SendOneFile(OutgoingTransfer transfer, int fileIndex, AdaptivePacer pacer)
        {
            string path = transfer.Paths[fileIndex];
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024))
            {
                long length = fs.Length;
                byte[] buffer = new byte[FileTransferProtocol.ChunkSize];
                long offset = 0;
                lock (_lock)
                {
                    transfer.RateStartTicks = Stopwatch.GetTimestamp();
                    transfer.RateStartBytes = transfer.LastProgress != null
                        ? transfer.LastProgress.BytesTransferred : 0;
                }

                while (offset < length)
                {
                    WaitForWindow(transfer, fileIndex, offset);
                    int read = fs.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                        throw new IOException("Unexpected end of file: " + path);

                    pacer.Acquire(read);
                    var msg = new FileTransferDataMessage
                    {
                        TransferId = transfer.Id,
                        FileIndex = (ushort)fileIndex,
                        Offset = offset,
                        Data = read == buffer.Length ? buffer : CopyOf(buffer, read)
                    };
                    long start = Stopwatch.GetTimestamp();
                    bool sent = _send((byte)MessageType.FileTransferData, msg.Pack());
                    long elapsedMs = (Stopwatch.GetTimestamp() - start) * 1000 / Stopwatch.Frequency;
                    if (!sent)
                        throw new IOException("File channel send failed");
                    pacer.ReportSendTime(elapsedMs);

                    offset += read;
                    RaiseOutgoingProgress(transfer, fileIndex, offset);
                }
                return offset;
            }
        }

        private static byte[] CopyOf(byte[] source, int length)
        {
            byte[] copy = new byte[length];
            Buffer.BlockCopy(source, 0, copy, 0, length);
            return copy;
        }

        private void SendEnd(uint transferId, ushort fileIndex, byte result)
        {
            var msg = new FileTransferEndMessage
            {
                TransferId = transferId,
                FileIndex = fileIndex,
                Result = result
            };
            _send((byte)MessageType.FileTransferEnd, msg.Pack());
        }

        private void SendCancel(uint transferId, byte reason)
        {
            var msg = new FileTransferCancelMessage { TransferId = transferId, Reason = reason };
            _send((byte)MessageType.FileTransferCancel, msg.Pack());
        }

        /// <summary>等待接收方请求指定文件（接收方需先接受清单）。</summary>
        private void WaitForStart(OutgoingTransfer transfer, int fileIndex, int timeoutMs)
        {
            long deadline = Stopwatch.GetTimestamp() + (long)timeoutMs * Stopwatch.Frequency / 1000;
            lock (_lock)
            {
                while (transfer.StartedIndex < fileIndex)
                {
                    if (transfer.Cancelled) throw new OperationCanceledException("Transfer cancelled");
                    if (Stopwatch.GetTimestamp() > deadline)
                        throw new TimeoutException("Timed out waiting for the receiver");
                    Monitor.Wait(_lock, 200);
                }
            }
        }

        /// <summary>等待滑动窗口腾出空间（未确认数据不超过 WindowBytes）。</summary>
        private void WaitForWindow(OutgoingTransfer transfer, int fileIndex, long offset)
        {
            lock (_lock)
            {
                while (transfer.Acked[fileIndex] + FileTransferProtocol.WindowBytes <= offset)
                {
                    if (transfer.Cancelled) throw new OperationCanceledException("Transfer cancelled");
                    long idleMs = (Stopwatch.GetTimestamp() - transfer.LastAckTicksAny) * 1000 / Stopwatch.Frequency;
                    if (idleMs > TransferTimeoutMs)
                        throw new TimeoutException("File transfer stalled (no acknowledgement)");
                    Monitor.Wait(_lock, 100);
                }
            }
        }

        /// <summary>
        /// 等待接收方确认该文件（末尾的 End 之后的那次 Ack 才算完成，
        /// 保证 0 字节文件也在对端落盘后才报告完成）。
        /// </summary>
        private void WaitForFinalAck(OutgoingTransfer transfer, int fileIndex, long length,
            long endSentTicks, int timeoutMs)
        {
            long deadline = Stopwatch.GetTimestamp() + (long)timeoutMs * Stopwatch.Frequency / 1000;
            lock (_lock)
            {
                while (transfer.Acked[fileIndex] < length || transfer.LastAckTicks[fileIndex] < endSentTicks)
                {
                    if (transfer.Cancelled) throw new OperationCanceledException("Transfer cancelled");
                    if (Stopwatch.GetTimestamp() > deadline)
                        throw new TimeoutException("Timed out waiting for acknowledgement");
                    Monitor.Wait(_lock, 200);
                }
            }
        }

        private void RaiseOutgoingProgress(OutgoingTransfer transfer, int fileIndex, long offsetInFile)
        {
            long done = 0;
            for (int i = 0; i < fileIndex; i++)
                done += transfer.Files[i].FileSize;
            done += offsetInFile;

            var progress = new FileTransferProgress
            {
                TransferId = transfer.Id,
                Outgoing = true,
                FileName = transfer.Files[fileIndex].FileName,
                FileIndex = fileIndex,
                FileCount = transfer.Files.Count,
                BytesTransferred = done,
                TotalBytes = transfer.TotalBytes
            };

            long now = Stopwatch.GetTimestamp();
            double seconds;
            long rateStartBytes;
            double lastRate;
            lock (_lock)
            {
                rateStartBytes = transfer.RateStartBytes;
                seconds = (now - transfer.RateStartTicks) / (double)Stopwatch.Frequency;
                lastRate = transfer.LastProgress != null ? transfer.LastProgress.BytesPerSecond : 0;
                transfer.LastProgress = progress;
                if (seconds > 1.0)
                {
                    transfer.RateStartTicks = now;
                    transfer.RateStartBytes = done;
                }
            }
            progress.BytesPerSecond = seconds > 0.2 ? (done - rateStartBytes) / seconds : lastRate;

            var handler = ProgressChanged;
            if (handler != null) handler(progress);
        }

        // ================= 接收 =================

        private void BeginIncoming(FileTransferOfferMessage offer)
        {
            if (offer.Files == null || offer.Files.Count == 0)
            {
                SendCancel(offer.TransferId, FileTransferProtocol.CancelProtocol);
                return;
            }

            long total = 0;
            foreach (FileTransferOfferMessage.FileMeta meta in offer.Files)
            {
                if (meta.FileSize > 0) total += meta.FileSize;
            }

            bool busy;
            lock (_lock)
            {
                busy = _incoming != null;
                if (!_disposed && !busy)
                {
                    _incoming = new IncomingTransfer
                    {
                        Id = offer.TransferId,
                        Files = offer.Files,
                        TotalBytes = total
                    };
                }
            }

            if (busy)
            {
                // 同一时刻只接受一路传输：拒绝新清单，避免覆盖正在写入的文件
                SendCancel(offer.TransferId, FileTransferProtocol.CancelProtocol);
                return;
            }

            // 自动接受：直接请求第一个文件
            SendStart(offer.TransferId, 0);
        }

        private void SendStart(uint transferId, ushort fileIndex)
        {
            var msg = new FileTransferStartMessage { TransferId = transferId, FileIndex = fileIndex };
            _send((byte)MessageType.FileTransferStart, msg.Pack());
        }

        private void SendAck(uint transferId, ushort fileIndex, long nextOffset)
        {
            var msg = new FileTransferAckMessage
            {
                TransferId = transferId,
                FileIndex = fileIndex,
                NextOffset = nextOffset
            };
            _send((byte)MessageType.FileTransferAck, msg.Pack());
        }

        private void HandleIncomingData(FileTransferDataMessage msg)
        {
            string failure = null;
            IncomingTransfer incoming;
            lock (_lock)
            {
                incoming = _incoming;
                if (incoming == null || incoming.Id != msg.TransferId)
                    return;
                if (msg.FileIndex != (ushort)incoming.Index)
                {
                    failure = "Unexpected file index in data";
                    _incoming = null;
                }
                else if (msg.Offset != incoming.Received)
                {
                    failure = "Unexpected data offset";
                    _incoming = null;
                }
                else
                {
                    try
                    {
                        EnsureIncomingStream(incoming);
                        incoming.Stream.Write(msg.Data, 0, msg.Data.Length);
                        incoming.Received += msg.Data.Length;
                        incoming.TotalReceived += msg.Data.Length;
                    }
                    catch (Exception ex)
                    {
                        failure = "Write failed: " + ex.Message;
                        _incoming = null;
                        CloseIncomingStream(incoming);
                        DeletePartial(incoming);
                    }
                }
            }

            if (failure != null)
            {
                SendCancel(msg.TransferId, FileTransferProtocol.CancelProtocol);
                RaiseFailed(msg.TransferId, failure, false);
                return;
            }

            SendAck(msg.TransferId, msg.FileIndex, incoming.Received);
            RaiseIncomingProgress(incoming);
        }

        private void HandleIncomingEnd(FileTransferEndMessage msg)
        {
            IncomingTransfer incoming;
            string[] saved = null;
            bool aborted = false;
            bool sendNext = false;
            ushort nextIndex = 0;
            long ackOffset = 0;
            uint transferId = msg.TransferId;
            lock (_lock)
            {
                incoming = _incoming;
                if (incoming == null || incoming.Id != msg.TransferId
                    || msg.FileIndex != (ushort)incoming.Index)
                {
                    _incoming = null;
                    aborted = true;
                }
                else if (msg.Result != FileTransferProtocol.ResultOk)
                {
                    CloseIncomingStream(incoming);
                    DeletePartial(incoming);
                    _incoming = null;
                    aborted = true;
                }
                else
                {
                    if (incoming.Stream == null)
                    {
                        // 0 字节文件没有 Data 块：这里补建空文件，保证每个清单项都落盘
                        try
                        {
                            EnsureIncomingStream(incoming);
                        }
                        catch (Exception ex)
                        {
                            _incoming = null;
                            CloseIncomingStream(incoming);
                            DeletePartial(incoming);
                            aborted = true;
                            Logger.Warn(ex, "FileTransfer: create empty file failed");
                        }
                    }
                    if (!aborted)
                    {
                        CloseIncomingStream(incoming);
                        if (!string.IsNullOrEmpty(incoming.CurrentPath))
                            incoming.SavedPaths.Add(incoming.CurrentPath);
                        incoming.CurrentPath = null;
                        ackOffset = incoming.Received;

                        if (incoming.Index + 1 >= incoming.Files.Count)
                        {
                            saved = incoming.SavedPaths.ToArray();
                            _incoming = null;
                        }
                        else
                        {
                            incoming.Index++;
                            incoming.Received = 0;
                            sendNext = true;
                            nextIndex = (ushort)incoming.Index;
                        }
                    }
                }
            }

            if (aborted)
            {
                SendCancel(transferId, FileTransferProtocol.CancelProtocol);
                RaiseFailed(transferId, "Transfer aborted by the sender", false);
                return;
            }

            // 最终确认：0 字节文件也必须回一次 Ack，发送方以 End 之后的 Ack 判完成
            SendAck(transferId, msg.FileIndex, ackOffset);
            if (saved != null)
            {
                var handler = IncomingCompleted;
                if (handler != null) handler(transferId, saved);
            }
            else if (sendNext)
            {
                SendStart(transferId, nextIndex);
            }
        }

        private void HandleCancel(FileTransferCancelMessage msg)
        {
            IncomingTransfer incoming = null;
            bool outgoingFound = false;
            lock (_lock)
            {
                if (_incoming != null && _incoming.Id == msg.TransferId)
                {
                    incoming = _incoming;
                    _incoming = null;
                }
                if (_outgoing.ContainsKey(msg.TransferId))
                {
                    _outgoing[msg.TransferId].Cancelled = true;
                    outgoingFound = true;
                }
                Monitor.PulseAll(_lock);
            }
            if (incoming != null)
            {
                CloseIncomingStream(incoming);
                DeletePartial(incoming);
                RaiseFailed(msg.TransferId, "Cancelled by the remote side", false);
            }
            if (outgoingFound)
                RaiseFailed(msg.TransferId, "Cancelled by the remote side", true);
        }

        private void EnsureIncomingStream(IncomingTransfer incoming)
        {
            if (incoming.Stream != null) return;
            if (string.IsNullOrEmpty(_receiveDirectory))
                throw new InvalidOperationException("Receive directory is not configured");
            Directory.CreateDirectory(_receiveDirectory);

            FileTransferOfferMessage.FileMeta meta = incoming.Files[incoming.Index];
            string path = BuildUniquePath(_receiveDirectory, meta.FileName);
            incoming.Stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024);
            incoming.CurrentPath = path;
        }

        /// <summary>净化文件名并去重（同名时追加 " (n)"）。</summary>
        public static string BuildUniquePath(string directory, string fileName)
        {
            string safe = SanitizeFileName(fileName);
            string candidate = Path.Combine(directory, safe);
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;

            string stem = Path.GetFileNameWithoutExtension(safe);
            string extension = Path.GetExtension(safe);
            for (int i = 1; i < 10000; i++)
            {
                candidate = Path.Combine(directory, stem + " (" + i + ")" + extension);
                if (!File.Exists(candidate) && !Directory.Exists(candidate))
                    return candidate;
            }
            return Path.Combine(directory, Guid.NewGuid().ToString("N") + extension);
        }

        /// <summary>去掉目录部分与非法字符，限制长度。</summary>
        public static string SanitizeFileName(string fileName)
        {
            string name = fileName ?? "";
            int slash = name.LastIndexOfAny(new[] { '\\', '/' });
            if (slash >= 0 && slash < name.Length - 1)
                name = name.Substring(slash + 1);
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            name = name.Trim();
            if (name.Length == 0 || name == "." || name == "..")
                name = "file";
            if (name.Length > FileTransferProtocol.MaxFileNameLength)
            {
                string extension = Path.GetExtension(name);
                int keep = FileTransferProtocol.MaxFileNameLength - extension.Length;
                name = keep > 0
                    ? name.Substring(0, keep) + extension
                    : name.Substring(0, FileTransferProtocol.MaxFileNameLength);
            }
            return name;
        }

        private void RaiseIncomingProgress(IncomingTransfer incoming)
        {
            var progress = new FileTransferProgress
            {
                TransferId = incoming.Id,
                Outgoing = false,
                FileName = incoming.Files[incoming.Index].FileName,
                FileIndex = incoming.Index,
                FileCount = incoming.Files.Count,
                BytesTransferred = incoming.TotalReceived,
                TotalBytes = incoming.TotalBytes
            };
            var handler = ProgressChanged;
            if (handler != null) handler(progress);
        }

        private void CloseIncomingStream(IncomingTransfer incoming)
        {
            FileStream stream = incoming.Stream;
            incoming.Stream = null;
            if (stream == null) return;
            try
            {
                stream.Flush(true);
            }
            catch
            {
                // Flush 失败随后仍会关闭并删除未完成文件
            }
            try
            {
                stream.Close();
            }
            catch
            {
            }
        }

        private void DeletePartial(IncomingTransfer incoming)
        {
            DeleteFile(incoming.CurrentPath);
            incoming.CurrentPath = null;
            foreach (string path in incoming.SavedPaths)
                DeleteFile(path);
            incoming.SavedPaths.Clear();
        }

        private static void DeleteFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
            }
        }

        // ================= 消息入口 =================

        /// <summary>
        /// 处理一条文件通道消息（在传输层接收线程调用）。非文件协议消息直接忽略。
        /// </summary>
        public void HandleMessage(byte messageType, byte[] payload)
        {
            try
            {
                if (messageType == (byte)MessageType.FileTransferOffer)
                {
                    BeginIncoming(FileTransferOfferMessage.Unpack(payload));
                }
                else if (messageType == (byte)MessageType.FileTransferStart)
                {
                    var msg = FileTransferStartMessage.Unpack(payload);
                    lock (_lock)
                    {
                        OutgoingTransfer transfer;
                        if (_outgoing.TryGetValue(msg.TransferId, out transfer))
                        {
                            transfer.StartedIndex = msg.FileIndex;
                            if (msg.FileIndex < transfer.Acked.Length)
                            {
                                transfer.Acked[msg.FileIndex] = 0;
                                transfer.LastAckTicks[msg.FileIndex] = Stopwatch.GetTimestamp();
                            }
                            Monitor.PulseAll(_lock);
                        }
                    }
                }
                else if (messageType == (byte)MessageType.FileTransferData)
                {
                    HandleIncomingData(FileTransferDataMessage.Unpack(payload));
                }
                else if (messageType == (byte)MessageType.FileTransferEnd)
                {
                    HandleIncomingEnd(FileTransferEndMessage.Unpack(payload));
                }
                else if (messageType == (byte)MessageType.FileTransferCancel)
                {
                    HandleCancel(FileTransferCancelMessage.Unpack(payload));
                }
                else if (messageType == (byte)MessageType.FileTransferAck)
                {
                    var msg = FileTransferAckMessage.Unpack(payload);
                    lock (_lock)
                    {
                        OutgoingTransfer transfer;
                        if (_outgoing.TryGetValue(msg.TransferId, out transfer)
                            && msg.FileIndex < transfer.Acked.Length)
                        {
                            long now = Stopwatch.GetTimestamp();
                            if (msg.NextOffset > transfer.Acked[msg.FileIndex])
                                transfer.Acked[msg.FileIndex] = msg.NextOffset;
                            transfer.LastAckTicks[msg.FileIndex] = now;
                            transfer.LastAckTicksAny = now;
                            Monitor.PulseAll(_lock);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // 解析失败说明对端实现异常：记录并由上层会话超时兜底
                Logger.Warn(ex, "FileTransfer: handle message type=0x{0:X2} failed", messageType);
            }
        }

        private static uint NextTransferId()
        {
            uint seed = (uint)Interlocked.Increment(ref _transferIdSeed);
            return seed == 0 ? 1 : seed;
        }

        private void RaiseFailed(uint transferId, string reason, bool outgoing)
        {
            Logger.Info("FileTransfer {0} failed ({1}): {2}",
                transferId, outgoing ? "send" : "receive", reason);
            var handler = Failed;
            if (handler != null) handler(transferId, reason, outgoing);
        }

        /// <summary>停止会话：取消所有传输并清理未完成的接收文件。</summary>
        public void Dispose()
        {
            IncomingTransfer incoming;
            List<OutgoingTransfer> outgoing;
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                incoming = _incoming;
                _incoming = null;
                outgoing = new List<OutgoingTransfer>(_outgoing.Values);
                foreach (OutgoingTransfer transfer in outgoing)
                    transfer.Cancelled = true;
                _outgoing.Clear();
                Monitor.PulseAll(_lock);
            }
            if (incoming != null)
            {
                CloseIncomingStream(incoming);
                DeletePartial(incoming);
            }
            foreach (OutgoingTransfer transfer in outgoing)
            {
                try { SendCancel(transfer.Id, FileTransferProtocol.CancelError); }
                catch { }
            }
            ProgressChanged = null;
            OutgoingCompleted = null;
            IncomingCompleted = null;
            Failed = null;
        }
    }
}
