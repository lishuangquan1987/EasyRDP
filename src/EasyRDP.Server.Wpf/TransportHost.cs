using System;
using System.Collections.Generic;
using System.Threading;
using EasyDesk.Core;
using EasyDesk.Core.Models;
using EasyRDP.Core.Protocol;
using EasyRDP.Core.Services;
using EasyRDP.Core.Session;
using EasyRDP.Core.Transport;
using NLog;

namespace EasyRDP.Server.Wpf
{
    /// <summary>
    /// 服务端传输主机。管理所有 Session 生命周期、握手、心跳、并发控制。
    /// </summary>
    public class TransportHost : IDisposable
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        /// <summary>会话 attached 事件（UI 绑定用），参数为 (sessionId, remoteEndPoint, codec, resolution)。</summary>
        public event Action<uint, string, string, string> SessionAttached;

        /// <summary>会话 detached 事件（UI 绑定用）。</summary>
        public event Action<uint> SessionDetached;

        private readonly ICaptureService _captureService;
        private readonly ITransportServer _transportServer;
        private readonly IInputSimulator _inputSimulator; // Shared for all input sessions

        // Session tracking
        private readonly Dictionary<uint, SessionInfo> _sessions = new Dictionary<uint, SessionInfo>();
        private readonly object _lock = new object();
        private int _maxSessions = 2; // D12 default for XP dual-core
        private int _activeCount;

        // Reassemblers per session
        private readonly Dictionary<uint, MessageReassembler> _reassemblers = new Dictionary<uint, MessageReassembler>();

        // Cursor tracking
        private readonly ICursorTracker _cursorTracker;

        // Clipboard (双向同步)：必须通过 STA 线程访问。
        // 客户端→服务端：客户端复制 → ClipboardSync 消息 → 入队 → STA 线程 IClipboardService.SetText
        // 服务端→客户端：STA 线程轮询本地剪贴板变化 → 检测到变化 → 发送 ClipboardSync 到所有客户端
        private readonly IClipboardService _clipboardService;
        private readonly Thread _clipboardThread;
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _clipboardQueue = new System.Collections.Concurrent.ConcurrentQueue<string>();
        private readonly AutoResetEvent _clipboardSignal = new AutoResetEvent(false);
        // 服务端本地剪贴板上次文本，用于检测变化 + 避免回环（客户端发来的文本设置后不再发回）
        private string _lastServerClipboardText = "";
        // 服务端本地剪贴板上次文件列表签名（拼接路径），用于检测变化 + 避免回环
        private string _lastServerClipboardFilesSig = "";
        // 服务端本地剪贴板上次图片签名（CF_DIB 字节数 + 前 32 字节哈希），用于检测变化 + 避免回环
        private string _lastServerClipboardImageSig = "";
        // ClipboardLoop 循环计数，用于周期性记录状态日志
        private int _clipboardCheckCount = 0;
        // 文件/图片传输 ID 自增（每次剪贴板同步递增）
        private uint _fileTransferIdSeq = 0;
        // 图片块大小（64KB）：平衡内存占用和分片数量
        private const int ImageChunkSize = 64 * 1024;
        // 图片传输并发控制：同时只允许一个图片剪贴板传输会话（避免大图片并发占满带宽）
        private static readonly SemaphoreSlim _imageTransferLock = new SemaphoreSlim(1, 1);

        // ── 文件剪贴板延迟渲染（RustDesk 风格）──
        // 服务端是发送方（服务端用户复制文件）：per-session Provider，响应客户端的 FileContentsReq
        // key = sessionId，value = 该 session 对应的 Provider（每次服务端用户复制文件时替换）
        private readonly Dictionary<uint, FileClipboardProvider> _serverClipProviders = new Dictionary<uint, FileClipboardProvider>();
        private readonly object _clipProviderLock = new object();
        // 服务端是接收方（客户端用户复制文件）：per-session Consumer，按需下载客户端文件
        // key = "sessionId_transferId"，value = 该传输对应的 Consumer
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, FileClipboardConsumer> _serverClipConsumers
            = new System.Collections.Concurrent.ConcurrentDictionary<string, FileClipboardConsumer>();
        // 客户端发来的文件路径队列：由 ClipboardLoop 在 STA 线程消费，调用 SetFiles
        private readonly System.Collections.Concurrent.ConcurrentQueue<string[]> _serverFileSetQueue
            = new System.Collections.Concurrent.ConcurrentQueue<string[]>();
        // 服务端接收客户端发来的图片剪贴板：key = "sessionId_transferId"
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ImageClipboardReceiver> _serverImageReceivers
            = new System.Collections.Concurrent.ConcurrentDictionary<string, ImageClipboardReceiver>();
        // 客户端发来的 CF_DIB 字节队列：由 ClipboardLoop 在 STA 线程消费，调用 SetImageDibBytes
        private readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> _serverImageSetQueue
            = new System.Collections.Concurrent.ConcurrentQueue<byte[]>();

        // Authentication credentials: username → password (plaintext, sufficient for v1)
        private readonly Dictionary<string, string> _credentials;

        // Heartbeat
        private Thread _heartbeatThread;
        private volatile bool _running;
        private readonly Dictionary<uint, DateTime> _lastActivity = new Dictionary<uint, DateTime>();

        public TransportHost(
            ICaptureService captureService,
            ITransportServer transportServer,
            IInputSimulator inputSimulator,
            ICursorCapturer cursorCapturer,
            IClipboardService clipboardService,
            Dictionary<string, string> credentials)
        {
            _captureService = captureService;
            _transportServer = transportServer;
            _inputSimulator = inputSimulator;
            _cursorTracker = new CursorTracker(cursorCapturer);
            _clipboardService = clipboardService;
            _credentials = credentials ?? new Dictionary<string, string>();

            _transportServer.DataReceived += OnDataReceived;
            _transportServer.ClientConnected += OnClientConnected;
            _transportServer.ClientDisconnected += OnClientDisconnected;

            // 剪贴板 STA 线程：IClipboardService 必须在 STA 线程调用
            if (_clipboardService != null)
            {
                _clipboardThread = new Thread(ClipboardLoop);
                _clipboardThread.IsBackground = true;
                _clipboardThread.SetApartmentState(ApartmentState.STA);
            }
        }

        public void Start(int port)
        {
            Logger.Info("TransportHost starting on port {0}", port);
            _running = true;
            _transportServer.Start(port);

            _cursorTracker.Start();

            _heartbeatThread = new Thread(HeartbeatLoop);
            _heartbeatThread.IsBackground = true;
            _heartbeatThread.Start();

            // 启动剪贴板 STA 线程
            if (_clipboardThread != null && !_clipboardThread.IsAlive)
                _clipboardThread.Start();
        }

        public void Stop()
        {
            Logger.Info("TransportHost stopping, active sessions: {0}", _activeCount);
            _running = false;

            // 唤醒剪贴板线程使其退出
            _clipboardSignal.Set();

            // Stop all sessions
            lock (_lock)
            {
                foreach (var kv in _sessions)
                {
                    try
                    {
                        kv.Value.Stream?.Stop();
                        kv.Value.Stream?.Dispose();
                        kv.Value.Input?.Dispose();
                    }
                    catch { }
                }
                _sessions.Clear();
                _reassemblers.Clear();
                _lastActivity.Clear();
            }

            // 清理所有 per-session 延迟渲染状态
            lock (_clipProviderLock)
            {
                foreach (var kv in _serverClipProviders)
                {
                    try { kv.Value.Dispose(); } catch { }
                }
                _serverClipProviders.Clear();
            }
            foreach (var kv in _serverClipConsumers)
            {
                try { kv.Value.Cancel(); } catch { }
            }
            _serverClipConsumers.Clear();

            Logger.Info("TransportHost stopped");

            _cursorTracker.StopAll();
            _transportServer.Stop();
            _heartbeatThread?.Join(2000);
            _clipboardThread?.Join(2000);
        }

        /// <summary>
        /// 剪贴板 STA 线程主循环。双向同步：
        /// 1) 处理来自客户端的剪贴板设置请求（客户端→服务端）
        /// 2) 轮询本地剪贴板变化，变化时发送到所有客户端（服务端→客户端）
        /// IClipboardService 内部用 OpenClipboard/SetClipboardData，必须在 STA 线程调用。
        /// </summary>
        private void ClipboardLoop()
        {
            Logger.Info("ClipboardLoop started (STA thread, service={0})",
                _clipboardService != null ? "yes" : "null");

            // 启动时读取一次本地剪贴板，作为 _lastServerClipboardText 初始值
            try
            {
                if (_clipboardService != null && _clipboardService.ContainsText())
                {
                    _lastServerClipboardText = _clipboardService.GetText() ?? "";
                    Logger.Info("Clipboard initial read: len={0}", _lastServerClipboardText.Length);
                }
                else
                {
                    Logger.Info("Clipboard initial: no text");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Clipboard initial read failed");
            }

            while (_running)
            {
                try
                {
                    // 1) 处理来自客户端的文本剪贴板设置请求
                    string text;
                    while (_clipboardQueue.TryDequeue(out text))
                    {
                        if (!_running) break;
                        try
                        {
                            _clipboardService?.SetText(text);
                            // 更新 last，避免监听线程检测到"变化"又发回客户端（避免回环）
                            _lastServerClipboardText = text;
                            Logger.Info("Clipboard set from client: len={0}", text != null ? text.Length : 0);
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn(ex, "Clipboard SetText failed");
                        }
                    }

                    // 2) 处理来自客户端的文件剪贴板设置请求（防回环：更新 _lastServerClipboardFilesSig + OwnerFlag）
                    string[] filePaths;
                    while (_serverFileSetQueue.TryDequeue(out filePaths))
                    {
                        if (!_running) break;
                        try
                        {
                            _clipboardService?.SetFiles(filePaths);
                            // Owner Flag 防回环：标记为 SideHost（表示"由服务端从客户端同步过来"），
                            // CheckFileClipboardChange 看到此标记即跳过，避免回发到客户端
                            EasyRDP.Core.ClipboardOwnerHelper.SetOwnerFlag(EasyRDP.Core.ClipboardOwnerHelper.SideHost);
                            // 防回环：更新签名，避免 CheckFileClipboardChange 又发回客户端
                            _lastServerClipboardFilesSig = string.Join("|", filePaths);
                            Logger.Info("File clipboard set from client: count={0}", filePaths.Length);
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn(ex, "Clipboard SetFiles failed");
                        }
                    }

                    // 3) 处理来自客户端的图片剪贴板设置请求（防回环：更新 _lastServerClipboardImageSig）
                    byte[] dibBytes;
                    while (_serverImageSetQueue.TryDequeue(out dibBytes))
                    {
                        if (!_running) break;
                        try
                        {
                            _clipboardService?.SetImageDibBytes(dibBytes);
                            // 防回环：更新签名，避免 CheckImageClipboardChange 又发回客户端
                            _lastServerClipboardImageSig = dibBytes.Length + ":" + ComputeSimpleHash(dibBytes, 32);
                            Logger.Info("Image clipboard set from client: dibSize={0}", dibBytes.Length);
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn(ex, "Clipboard SetImageDibBytes failed");
                        }
                    }

                    // 4) 检查本地剪贴板变化（服务端用户复制 → 发送到客户端）
                    CheckServerClipboardChange();
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "ClipboardLoop error");
                }

                // 800ms 间隔：与客户端轮询频率一致，足够及时
                _clipboardSignal.WaitOne(800);
            }
        }

        /// <summary>
        /// 检查服务端本地剪贴板文本是否变化，变化时发送 ClipboardSync 到所有 attached 客户端。
        /// 必须在 STA 线程调用（ClipboardLoop 内）。
        /// </summary>
        private void CheckServerClipboardChange()
        {
            if (_clipboardService == null) return;
            _clipboardCheckCount++;
            try
            {
                bool hasText = _clipboardService.ContainsText();
                bool hasFiles = _clipboardService.ContainsFiles();
                bool hasImage = _clipboardService.ContainsImage();
                // 每 10 次循环（约 8 秒）记录一次状态，确认 ClipboardLoop 在工作
                if (_clipboardCheckCount % 10 == 0)
                    Logger.Debug("Clipboard check #{0}: hasText={1} hasFiles={2} hasImage={3} lastTextLen={4}",
                        _clipboardCheckCount, hasText, hasFiles, hasImage, _lastServerClipboardText.Length);

                // 优先处理文件剪贴板（CF_HDROP）：用户右键复制文件时触发
                if (hasFiles)
                {
                    CheckFileClipboardChange();
                    // 文件覆盖了图片：清空图片签名，避免下次复制相同图片时误判为"没变化"
                    _lastServerClipboardImageSig = "";
                    return; // 文件和文本/图片不会同时在剪贴板上
                }

                // 图片剪贴板（CF_DIB）：用户截图/复制图片时触发
                if (hasImage)
                {
                    CheckImageClipboardChange();
                    // 图片覆盖了文件：清空文件签名，避免下次复制相同文件时误判为"没变化"
                    _lastServerClipboardFilesSig = "";
                    return; // 图片和文本不会同时在剪贴板上
                }

                // 没有文件/图片时清空对应签名
                _lastServerClipboardFilesSig = "";
                _lastServerClipboardImageSig = "";

                if (!hasText) return;
                string current = _clipboardService.GetText() ?? "";
                if (current != _lastServerClipboardText)
                {
                    _lastServerClipboardText = current;
                    SendClipboardToClients(current);
                    Logger.Info("Clipboard sync to clients: len={0}", current.Length);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "CheckServerClipboardChange failed");
            }
        }

        /// <summary>
        /// 检查服务端本地文件剪贴板是否变化，变化时启动后台线程异步发送到所有客户端。
        /// 必须在 STA 线程调用（ClipboardLoop 内）— 只在 STA 线程读剪贴板，文件读取和网络发送在后台线程。
        /// Owner Flag 防回环：剪贴板若是服务端从客户端同步过来并打上 SideHost 标记的，跳过不回传。
        /// </summary>
        private void CheckFileClipboardChange()
        {
            try
            {
                // Owner Flag 防回环：剪贴板若是服务端从客户端同步过来并打上 SideHost 标记的，跳过不回传
                byte owner = EasyRDP.Core.ClipboardOwnerHelper.GetOwnerFlag();
                if (owner == EasyRDP.Core.ClipboardOwnerHelper.SideHost)
                {
                    return; // 远程同步过来的，不回传
                }

                string[] files = _clipboardService.GetFileList();
                if (files == null || files.Length == 0)
                {
                    _lastServerClipboardFilesSig = "";
                    return;
                }

                // 构造签名用于检测变化（拼接所有路径）
                string sig = string.Join("|", files);
                if (sig == _lastServerClipboardFilesSig)
                    return; // 没变化

                _lastServerClipboardFilesSig = sig;
                Logger.Info("File clipboard changed: count={0}", files.Length);

                // 复制文件路径数组（避免后台线程访问时数组被修改）
                string[] pathsCopy = (string[])files.Clone();

                // 后台线程异步发送：不阻塞 STA 线程，ClipboardLoop 可继续检测剪贴板变化
                // net40 没有 Task.Run，用 Task.Factory.StartNew
                System.Threading.Tasks.Task.Factory.StartNew(() =>
                {
                    try { SendFileClipboardToClients(pathsCopy); }
                    catch (Exception ex) { Logger.Warn(ex, "SendFileClipboardToClients background task failed"); }
                });
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "CheckFileClipboardChange failed");
            }
        }

        /// <summary>
        /// 发送文件剪贴板到所有已 attached 的客户端（延迟渲染：仅发元信息，文件内容由客户端按需拉取）。
        /// 流程：为每个 session 创建 FileClipboardProvider → 发送 ClipFormatList（仅元信息）。
        /// 客户端收到后通过 ClipFileContentsReq 按需请求文件内容，Provider 响应 ClipFileContentsRes。
        /// 接收方控制下载速率，避免灌满 TCP 连接。
        /// </summary>
        private void SendFileClipboardToClients(string[] filePaths)
        {
            uint transferId = ++_fileTransferIdSeq;

            // 1) 构造文件元信息列表（仅文件名+大小，不含文件内容）
            var metaList = new List<ClipFormatListMessage.FileMeta>(filePaths.Length);
            foreach (var path in filePaths)
            {
                try
                {
                    var fi = new System.IO.FileInfo(path);
                    metaList.Add(new ClipFormatListMessage.FileMeta
                    {
                        FileName = System.IO.Path.GetFileName(path),
                        FileSize = fi.Exists ? fi.Length : 0
                    });
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "GetFileInfo failed for {0}", path);
                    metaList.Add(new ClipFormatListMessage.FileMeta
                    {
                        FileName = System.IO.Path.GetFileName(path),
                        FileSize = 0
                    });
                }
            }

            // 2) 收集目标 session，并为每个 session 创建 FileClipboardProvider
            List<uint> sessionIds;
            lock (_lock)
            {
                sessionIds = new List<uint>(_sessions.Keys);
            }
            if (sessionIds.Count == 0)
            {
                Logger.Info("No attached clients — skipping file clipboard transfer");
                return;
            }

            Logger.Info("ClipFormatList transfer started: transferId={0} fileCount={1} sessions={2}",
                transferId, metaList.Count, sessionIds.Count);

            // 3) 为每个 session 创建 Provider（响应客户端的 FileContentsReq）
            //    替换旧 Provider（Dispose 旧的，避免资源泄漏）
            foreach (var sid in sessionIds)
            {
                // 捕获循环变量到局部变量，避免闭包捕获 foreach 变量
                // 关键：FileClipboardProvider 内部调用 _sendAction(0, payload) 传 0（Provider 不知道 sessionId），
                // 所以这里必须用 targetSid 而非 sidArg，否则响应会发到 sessionId=0（不存在）导致客户端永远收不到
                uint targetSid = sid;
                var provider = new FileClipboardProvider(transferId, filePaths,
                    (sidArg, payload) =>
                    {
                        // sidArg 被忽略（Provider 内部传 0）；用 targetSid 作为实际目标 session
                        // FragAndSend 签名：(frameId, messageType, payload, sendAction, sessionId)
                        // 最后一个参数 sessionId 才是传给 sendAction 的实参。
                        // 控制流 frameId=0；sessionId=targetSid（必须真实，否则 SendTo 静默失败）
                        MessageReassembler.FragAndSend(0, (byte)MessageType.ClipFileContentsRes, payload,
                            (s, d) => _transportServer.SendTo(s, d), targetSid);
                    });
                lock (_clipProviderLock)
                {
                    FileClipboardProvider old;
                    if (_serverClipProviders.TryGetValue(sid, out old))
                    {
                        try { old.Dispose(); } catch { }
                    }
                    _serverClipProviders[sid] = provider;
                }
            }

            // 4) 发送 ClipFormatList（仅元信息，几百字节）到每个 session
            var listMsg = new ClipFormatListMessage
            {
                TransferId = transferId,
                Files = metaList
            };
            byte[] listPayload = listMsg.Pack();
            foreach (var sid in sessionIds)
            {
                try
                {
                    // FragAndSend 签名：(frameId, messageType, payload, sendAction, sessionId)
                    // 控制流 frameId=0；sessionId=sid（传给 sendAction → SendTo）
                    MessageReassembler.FragAndSend(0, (byte)MessageType.ClipFormatList, listPayload,
                        (s, data) => _transportServer.SendTo(s, data), sid);
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "SendClipFormatList failed for session {0}", sid);
                }
            }
            Logger.Info("ClipFormatList sent: transferId={0} fileCount={1} sessions={2}",
                transferId, metaList.Count, sessionIds.Count);
        }

        // ── 图片剪贴板同步（CF_DIB）──

        /// <summary>
        /// 检查服务端本地图片剪贴板是否变化，变化时启动后台线程异步发送到所有客户端。
        /// 必须在 STA 线程调用（ClipboardLoop 内）— 只在 STA 线程读剪贴板，数据发送在后台线程。
        /// </summary>
        private void CheckImageClipboardChange()
        {
            try
            {
                byte[] dibBytes = _clipboardService.GetImageDibBytes();
                if (dibBytes == null || dibBytes.Length == 0)
                {
                    _lastServerClipboardImageSig = "";
                    return;
                }

                // 构造签名：长度 + 前 32 字节哈希（用简单的字节拼接，无需加密库）
                string sig = dibBytes.Length + ":" + ComputeSimpleHash(dibBytes, 32);
                if (sig == _lastServerClipboardImageSig)
                    return; // 没变化

                _lastServerClipboardImageSig = sig;
                Logger.Info("Image clipboard changed: dibSize={0}", dibBytes.Length);

                // 后台线程异步发送：不阻塞 STA 线程
                // 注意：dibBytes 已是独立数组（GetImageDibBytes 返回新数组），可直接传给后台线程
                System.Threading.Tasks.Task.Factory.StartNew(() =>
                {
                    try { SendImageClipboardToClients(dibBytes); }
                    catch (Exception ex) { Logger.Warn(ex, "SendImageClipboardToClients background task failed"); }
                });
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "CheckImageClipboardChange failed");
            }
        }

        /// <summary>
        /// 发送图片剪贴板到所有已 attached 的客户端（后台线程调用）。
        /// 流程：ImageClipboardStart → 多个 ImageClipboardData（64KB 分块）→ ImageClipboardEnd
        /// 用 SemaphoreSlim 限制并发：避免大图片并发占满带宽。
        /// </summary>
        private void SendImageClipboardToClients(byte[] dibBytes)
        {
            _imageTransferLock.Wait();
            try
            {
                uint transferId = ++_fileTransferIdSeq;

                // 收集目标 session
                List<uint> sessionIds;
                lock (_lock)
                {
                    sessionIds = new List<uint>(_sessions.Keys);
                }
                if (sessionIds.Count == 0)
                {
                    Logger.Info("No attached clients — skipping image clipboard transfer");
                    return;
                }

                Logger.Info("ImageClipboard transfer started: transferId={0} dibSize={1} sessions={2}",
                    transferId, dibBytes.Length, sessionIds.Count);

                // 1) 发送 Start
                var startMsg = new ImageClipboardStartMessage
                {
                    TransferId = transferId,
                    TotalSize = dibBytes.Length
                };
                byte[] startPayload = startMsg.Pack();
                foreach (var sid in sessionIds)
                {
                    try
                    {
                        // FragAndSend 签名：(frameId, messageType, payload, sendAction, sessionId)
                        // 控制流 frameId=0；sessionId=sid（传给 sendAction → SendTo）
                        MessageReassembler.FragAndSend(0, (byte)MessageType.ImageClipboardStart, startPayload,
                            (s, data) => _transportServer.SendTo(s, data), sid);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, "SendImageClipboardStart failed for session {0}", sid);
                    }
                }
                Logger.Info("ImageClipboardStart sent: transferId={0} dibSize={1}", transferId, dibBytes.Length);

                // 2) 分块发送 Data
                int offset = 0;
                while (offset < dibBytes.Length)
                {
                    int chunkLen = Math.Min(ImageChunkSize, dibBytes.Length - offset);
                    byte[] chunk;
                    if (chunkLen == ImageChunkSize && offset == 0)
                    {
                        // 第一块可以直接用原数组引用（只读）
                        chunk = dibBytes;
                    }
                    else
                    {
                        chunk = new byte[chunkLen];
                        Buffer.BlockCopy(dibBytes, offset, chunk, 0, chunkLen);
                    }

                    var dataMsg = new ImageClipboardDataMessage
                    {
                        TransferId = transferId,
                        Offset = offset,
                        DataLen = chunkLen,
                        Data = chunk
                    };
                    byte[] dataPayload = dataMsg.Pack();
                    foreach (var sid in sessionIds)
                    {
                        try
                        {
                            // FragAndSend 签名：(frameId, messageType, payload, sendAction, sessionId)
                            // 控制流 frameId=0；sessionId=sid（传给 sendAction → SendTo）
                            MessageReassembler.FragAndSend(0, (byte)MessageType.ImageClipboardData, dataPayload,
                                (s, d) => _transportServer.SendTo(s, d), sid);
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn(ex, "SendImageClipboardData failed for session {0}", sid);
                        }
                    }
                    offset += chunkLen;
                }
                Logger.Info("ImageClipboardData sent: transferId={0} chunks={1} totalBytes={2}",
                    transferId, (dibBytes.Length + ImageChunkSize - 1) / ImageChunkSize, dibBytes.Length);

                // 3) 发送 End
                var endMsg = new ImageClipboardEndMessage { TransferId = transferId };
                byte[] endPayload = endMsg.Pack();
                foreach (var sid in sessionIds)
                {
                    try
                    {
                        // FragAndSend 签名：(frameId, messageType, payload, sendAction, sessionId)
                        // 控制流 frameId=0；sessionId=sid（传给 sendAction → SendTo）
                        MessageReassembler.FragAndSend(0, (byte)MessageType.ImageClipboardEnd, endPayload,
                            (s, data) => _transportServer.SendTo(s, data), sid);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, "SendImageClipboardEnd failed for session {0}", sid);
                    }
                }
                Logger.Info("ImageClipboardEnd sent: transferId={0}", transferId);
            }
            finally
            {
                _imageTransferLock.Release();
            }
        }

        /// <summary>计算字节数组的前 N 字节的简单哈希（用于图片签名，非加密用途）。</summary>
        private static string ComputeSimpleHash(byte[] data, int sampleLen)
        {
            int len = Math.Min(sampleLen, data.Length);
            long hash = 0;
            for (int i = 0; i < len; i++)
            {
                hash = (hash << 3) ^ data[i];
            }
            return hash.ToString("X");
        }

        /// <summary>
        /// 发送剪贴板文本到所有已 attached 的客户端。
        /// </summary>
        private void SendClipboardToClients(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                var msg = new ClipboardSyncMessage
                {
                    Format = ClipboardSyncMessage.FormatText,
                    Text = text
                };
                byte[] payload = msg.Pack();

                List<uint> sessionIds;
                lock (_lock)
                {
                    sessionIds = new List<uint>(_sessions.Keys);
                }

                foreach (var sid in sessionIds)
                {
                    try
                    {
                        // FragAndSend 签名：(frameId, messageType, payload, sendAction, sessionId)
                        // 控制流 frameId=0；sessionId=sid（传给 sendAction → SendTo）
                        MessageReassembler.FragAndSend(0, (byte)MessageType.ClipboardSync, payload,
                            (s, data) => _transportServer.SendTo(s, data), sid);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, "SendClipboardToClients failed for session {0}", sid);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "SendClipboardToClients pack failed");
            }
        }

        /// <summary>
        /// 把客户端发来的剪贴板文本入队，由 STA 线程异步设置到系统剪贴板。
        /// 线程安全：可从任意线程调用。
        /// </summary>
        public void EnqueueClipboardText(string text)
        {
            if (_clipboardService == null || string.IsNullOrEmpty(text)) return;
            _clipboardQueue.Enqueue(text);
            _clipboardSignal.Set();
        }

        public void Dispose()
        {
            Stop();
        }

        private void OnClientConnected(object sender, ConnectionEventArgs e)
        {
            Logger.Info("Client connected: sessionId={0}", e.SessionId);
            // Create reassembler for this session
            var reassembler = new MessageReassembler();
            reassembler.MessageReceived += (s, args) => OnMessageReceived(args);

            lock (_lock)
            {
                _reassemblers[e.SessionId] = reassembler;
                _lastActivity[e.SessionId] = DateTime.UtcNow;
            }
        }

        private void OnDataReceived(object sender, FragmentReceivedEventArgs e)
        {
            MessageReassembler reassembler;
            lock (_lock)
            {
                if (!_reassemblers.TryGetValue(e.SessionId, out reassembler))
                    return;
                _lastActivity[e.SessionId] = DateTime.UtcNow;
            }
            reassembler.OnFragment(e);
        }

        private void OnMessageReceived(MessageReceivedEventArgs e)
        {
            if (e.MessageType == (byte)MessageType.HandshakeReq)
            {
                HandleHandshake(e);
            }
            else
            {
                // Route to appropriate session
                SessionInfo info;
                lock (_lock)
                {
                    if (!_sessions.TryGetValue(e.SessionId, out info))
                        return;
                }

                if (e.MessageType == (byte)MessageType.InputEvent && info.Input != null)
                {
                    var inputMsg = InputEventMessage.Unpack(e.Data);
                    info.Input.HandleInput(inputMsg);
                }
                else if (e.MessageType == (byte)MessageType.ClipboardSync)
                {
                    HandleClipboardSync(e);
                }
                else if (e.MessageType == (byte)MessageType.ClipFormatList)
                {
                    HandleClipFormatListFromClient(e);
                }
                else if (e.MessageType == (byte)MessageType.ClipFileContentsReq)
                {
                    HandleClipFileContentsReqFromClient(e);
                }
                else if (e.MessageType == (byte)MessageType.ClipFileContentsRes)
                {
                    HandleClipFileContentsResFromClient(e);
                }
                else if (e.MessageType == (byte)MessageType.ImageClipboardStart
                         || e.MessageType == (byte)MessageType.ImageClipboardData
                         || e.MessageType == (byte)MessageType.ImageClipboardEnd)
                {
                    HandleImageClipboardFromClient(e);
                }
            }
        }

        /// <summary>
        /// 处理客户端发来的 ClipFormatList（延迟渲染）：客户端用户复制文件，仅发元信息。
        /// 创建 per-session FileClipboardConsumer，启动后台下载线程按需拉取文件内容。
        /// 下载完成后入队，由 ClipboardLoop 在 STA 线程调用 SetFiles 设置 CF_HDROP。
        /// </summary>
        private void HandleClipFormatListFromClient(MessageReceivedEventArgs e)
        {
            try
            {
                var msg = ClipFormatListMessage.Unpack(e.Data);
                string sessionTag = "server_" + e.SessionId;
                string key = e.SessionId + "_" + msg.TransferId;

                // 创建 Consumer：通过 transportServer 向客户端发送 ClipFileContentsReq
                var consumer = new FileClipboardConsumer(msg.TransferId, msg.Files, sessionTag,
                    (sidArg, payload) =>
                    {
                        // sidArg=0（Consumer 不区分 session，由本回调封装）；用 e.SessionId 发送
                        // FragAndSend 签名：(frameId, messageType, payload, sendAction, sessionId)
                        // 控制流 frameId=0；sessionId=e.SessionId（传给 sendAction → SendTo）
                        // 之前误把 e.SessionId 传给 frameId、0 传给 sessionId，
                        // 导致 SendTo(0, ...) 静默失败，FileContentsReq 永远发不到客户端
                        MessageReassembler.FragAndSend(0, (byte)MessageType.ClipFileContentsReq, payload,
                            (s, d) => _transportServer.SendTo(s, d), e.SessionId);
                    },
                    localPaths =>
                    {
                        // 下载完成（无论成功失败）后从字典移除，避免长期运行内存累积
                        FileClipboardConsumer removed;
                        _serverClipConsumers.TryRemove(key, out removed);

                        Logger.Info("Server file clipboard download complete: session={0} transferId={1} files={2}",
                            e.SessionId, msg.TransferId, localPaths != null ? localPaths.Length : 0);
                        if (localPaths != null && localPaths.Length > 0)
                            EnqueueServerClipboardFiles(localPaths);
                    });

                _serverClipConsumers[key] = consumer;

                // 里程碑式进度日志：每 10% 记录一次，避免日志过多
                int lastMilestone = -1;
                consumer.ProgressChanged += (downloaded, total) =>
                {
                    if (total <= 0) return;
                    int percent = (int)((downloaded * 100) / total);
                    int milestone = percent / 10 * 10; // 0, 10, 20, ..., 100
                    if (milestone > lastMilestone)
                    {
                        lastMilestone = milestone;
                        Logger.Info("Server clipboard download progress: session={0} transferId={1} {2}% ({3} / {4} bytes)",
                            e.SessionId, msg.TransferId, milestone, downloaded, total);
                    }
                };

                consumer.StartDownload();
                Logger.Info("Server received ClipFormatList: session={0} transferId={1} fileCount={2}",
                    e.SessionId, msg.TransferId, msg.Files.Count);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "HandleClipFormatListFromClient failed");
            }
        }

        /// <summary>
        /// 处理客户端发来的 ClipFileContentsReq（延迟渲染）：客户端请求服务端的文件内容。
        /// 路由到 per-session FileClipboardProvider，由 Provider 读取文件并响应 ClipFileContentsRes。
        /// </summary>
        private void HandleClipFileContentsReqFromClient(MessageReceivedEventArgs e)
        {
            try
            {
                var msg = ClipFileContentsReqMessage.Unpack(e.Data);
                lock (_clipProviderLock)
                {
                    FileClipboardProvider provider;
                    if (_serverClipProviders.TryGetValue(e.SessionId, out provider))
                    {
                        provider.HandleFileContentsReq(msg);
                    }
                    else
                    {
                        Logger.Warn("ClipFileContentsReq from session {0} but no provider: transferId={1}",
                            e.SessionId, msg.TransferId);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "HandleClipFileContentsReqFromClient failed");
            }
        }

        /// <summary>
        /// 处理客户端发来的 ClipFileContentsRes（延迟渲染）：客户端返回的文件内容块。
        /// 路由到 per-session FileClipboardConsumer，由 Consumer 写入临时文件。
        /// </summary>
        private void HandleClipFileContentsResFromClient(MessageReceivedEventArgs e)
        {
            try
            {
                var msg = ClipFileContentsResMessage.Unpack(e.Data);
                string key = e.SessionId + "_" + msg.TransferId;
                FileClipboardConsumer consumer;
                if (_serverClipConsumers.TryGetValue(key, out consumer))
                {
                    consumer.HandleFileContentsRes(msg);
                }
                else
                {
                    Logger.Warn("ClipFileContentsRes for unknown transfer: session={0} transferId={1}",
                        e.SessionId, msg.TransferId);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "HandleClipFileContentsResFromClient failed");
            }
        }

        /// <summary>
        /// 由 FileClipboardConsumer 的 onFinish 回调调用：文件接收完毕，把文件路径设置到服务端本地剪贴板。
        /// 必须转发到 STA 线程（ClipboardLoop）调用 IClipboardService.SetFiles。
        /// </summary>
        public void EnqueueServerClipboardFiles(string[] filePaths)
        {
            if (_clipboardService == null || filePaths == null || filePaths.Length == 0) return;
            // 入队，由 ClipboardLoop 在 STA 线程处理
            _serverFileSetQueue.Enqueue(filePaths);
            _clipboardSignal.Set();
        }

        /// <summary>
        /// 处理客户端发来的图片剪贴板消息（ImageClipboardStart/Data/End）。
        /// 用 per-session 的 ImageClipboardReceiver 管理接收状态。
        /// 收到 End 时，把 CF_DIB 字节入队，由 ClipboardLoop 在 STA 线程调用 SetImageDibBytes。
        /// </summary>
        private void HandleImageClipboardFromClient(MessageReceivedEventArgs e)
        {
            try
            {
                if (e.MessageType == (byte)MessageType.ImageClipboardStart)
                {
                    var msg = ImageClipboardStartMessage.Unpack(e.Data);
                    string key = e.SessionId + "_" + msg.TransferId;
                    var receiver = new ImageClipboardReceiver(msg.TransferId, msg.TotalSize);
                    _serverImageReceivers[key] = receiver;
                    Logger.Info("Server received ImageClipboardStart: session={0} transferId={1} totalSize={2}",
                        e.SessionId, msg.TransferId, msg.TotalSize);
                }
                else if (e.MessageType == (byte)MessageType.ImageClipboardData)
                {
                    var msg = ImageClipboardDataMessage.Unpack(e.Data);
                    string key = e.SessionId + "_" + msg.TransferId;
                    ImageClipboardReceiver receiver;
                    if (!_serverImageReceivers.TryGetValue(key, out receiver))
                    {
                        Logger.Warn("Server ImageClipboardData for unknown transfer: {0}", key);
                        return;
                    }
                    receiver.WriteChunk(msg.Offset, msg.Data, msg.DataLen);
                }
                else if (e.MessageType == (byte)MessageType.ImageClipboardEnd)
                {
                    var msg = ImageClipboardEndMessage.Unpack(e.Data);
                    string key = e.SessionId + "_" + msg.TransferId;
                    ImageClipboardReceiver receiver;
                    if (!_serverImageReceivers.TryRemove(key, out receiver))
                    {
                        Logger.Warn("Server ImageClipboardEnd for unknown transfer: {0}", key);
                        return;
                    }
                    byte[] dibBytes = receiver.Finish();
                    Logger.Info("Server ImageClipboardEnd: session={0} transferId={1} dibSize={2}",
                        e.SessionId, msg.TransferId, dibBytes != null ? dibBytes.Length : 0);
                    if (dibBytes != null && dibBytes.Length > 0)
                        EnqueueServerClipboardImage(dibBytes);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "HandleImageClipboardFromClient failed");
            }
        }

        /// <summary>
        /// 图片剪贴板接收完毕入队：由 ClipboardLoop 在 STA 线程调用 SetImageDibBytes 设置 CF_DIB。
        /// </summary>
        public void EnqueueServerClipboardImage(byte[] dibBytes)
        {
            if (_clipboardService == null || dibBytes == null || dibBytes.Length == 0) return;
            _serverImageSetQueue.Enqueue(dibBytes);
            _clipboardSignal.Set();
        }

        /// <summary>
        /// 处理客户端发来的剪贴板同步消息。把文本入队，由 STA 线程设置到系统剪贴板。
        /// </summary>
        private void HandleClipboardSync(MessageReceivedEventArgs e)
        {
            try
            {
                var msg = ClipboardSyncMessage.Unpack(e.Data);
                if (msg.Format == ClipboardSyncMessage.FormatText)
                {
                    string text = msg.Text;
                    if (!string.IsNullOrEmpty(text))
                    {
                        EnqueueClipboardText(text);
                        Logger.Info("Clipboard sync from session {0}: len={1}", e.SessionId, text.Length);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ClipboardSync unpack failed from session {0}", e.SessionId);
            }
        }

        private void HandleHandshake(MessageReceivedEventArgs e)
        {
            var req = HandshakeReq.Unpack(e.Data);
            Logger.Info("Handshake request from sessionId={0}: version={1} username={2}",
                e.SessionId, req.Version, req.Username);

            HandshakeRes res;
            if (req.Version != Constants.ProtocolVersion)
            {
                Logger.Warn("Version mismatch: client={0} server={1}", req.Version, Constants.ProtocolVersion);
                res = new HandshakeRes { Result = HandshakeResult.VersionMismatch };
                SendResponse(e.SessionId, res);
                DisconnectSession(e.SessionId);
                return;
            }

            // Check concurrency limit
            lock (_lock)
            {
                if (_activeCount >= _maxSessions)
                {
                    Logger.Warn("Server busy: activeCount={0} maxSessions={1}", _activeCount, _maxSessions);
                    res = new HandshakeRes { Result = HandshakeResult.ServerBusy };
                    SendResponse(e.SessionId, res);
                    DisconnectSession(e.SessionId);
                    return;
                }
            }

            // 简单认证：硬编码凭据表（后续应改为配置文件或外部凭据存储）
            if (!ValidateCredentials(req.Username, req.Password))
            {
                Logger.Warn("Auth failed for username='{0}'", req.Username);
                res = new HandshakeRes { Result = HandshakeResult.AuthFailed };
                SendResponse(e.SessionId, res);
                DisconnectSession(e.SessionId);
                return;
            }

            // Negotiate codec
            var serverCaps = EncoderFactory.GetAvailableCodecs();
            var negotiated = CodecNegotiator.Negotiate(req.Capabilities, serverCaps);
            if (!negotiated.HasValue)
            {
                // Server has no encoder (e.g. OpenH264 DLL wrong arch on Win7 32-bit).
                // Accept anyway — ServerStreamSession falls back to raw pixels.
                if (serverCaps == CodecCapabilities.None)
                {
                    Logger.Warn("No encoder available on server — falling back to raw pixels");
                    negotiated = PickFallbackCodec(req.Capabilities);
                }
                else
                {
                    Logger.Warn("No common codec: clientCaps={0} serverCaps={1}", req.Capabilities, serverCaps);
                    res = new HandshakeRes { Result = HandshakeResult.NoCommonCodec };
                    SendResponse(e.SessionId, res);
                    DisconnectSession(e.SessionId);
                    return;
                }
            }

            try
            {
                var bounds = _captureService.GetPrimaryScreen();

                // Create sessions first (don't send Success until Start() passes)
                var streamSession = new ServerStreamSession(_captureService, (sid, data) =>
                {
                    _transportServer.SendTo(sid, data);
                }, _cursorTracker);

                var inputSession = new ServerInputSession(_inputSimulator);

                lock (_lock)
                {
                    _sessions[e.SessionId] = new SessionInfo
                    {
                        Stream = streamSession,
                        Input = inputSession
                    };
                    _activeCount++;
                }

                // Start — may throw if encoder init fails
                streamSession.Start(e.SessionId, negotiated.Value);

                // Only send Success after session fully starts
                res = new HandshakeRes
                {
                    Result = HandshakeResult.Success,
                    Codec = negotiated.Value,
                    ScreenWidth = bounds.Width,
                    ScreenHeight = bounds.Height
                };
                SendResponse(e.SessionId, res);
                Logger.Info("Handshake success: sessionId={0} codec={1} resolution={2}x{3}",
                    e.SessionId, negotiated.Value, bounds.Width, bounds.Height);
                Logger.Info("Session {0} stream started with codec {1}", e.SessionId, negotiated.Value);

                // Fire session attached event
                var handler = SessionAttached;
                if (handler != null)
                {
                    string remote = "?";
                    string codec = negotiated.Value.ToString();
                    string resolution = bounds.Width + "x" + bounds.Height;
                    handler(e.SessionId, remote, codec, resolution);
                }
            }
            catch (Exception ex)
            {
                // Session startup failed — send error response and clean up
                Logger.Error(ex, "Handshake session startup failed for sessionId={0}", e.SessionId);
                res = new HandshakeRes { Result = HandshakeResult.InternalError };
                SendResponse(e.SessionId, res);
                DisconnectSession(e.SessionId);
            }
        }

        private void SendResponse(uint sessionId, HandshakeRes res)
        {
            byte[] payload = res.Pack();
            var sentFragments = new List<byte[]>();
            MessageReassembler.FragAndSend(0, (byte)MessageType.HandshakeRes, payload,
                (sid, data) => _transportServer.SendTo(sid, data), sessionId);
        }

        private void OnClientDisconnected(object sender, ConnectionEventArgs e)
        {
            DisconnectSession(e.SessionId);

            var handler = SessionDetached;
            if (handler != null) handler(e.SessionId);
        }

        private void DisconnectSession(uint sessionId)
        {
            Logger.Info("Disconnecting session {0}", sessionId);
            SessionInfo info;
            lock (_lock)
            {
                if (!_sessions.TryGetValue(sessionId, out info))
                    return;
                _sessions.Remove(sessionId);
                _reassemblers.Remove(sessionId);
                _lastActivity.Remove(sessionId);
                _activeCount--;
            }

            // 清理 per-session 延迟渲染状态：Dispose Provider（停止响应 FileContentsReq）
            lock (_clipProviderLock)
            {
                FileClipboardProvider provider;
                if (_serverClipProviders.TryGetValue(sessionId, out provider))
                {
                    _serverClipProviders.Remove(sessionId);
                    try { provider.Dispose(); } catch { }
                }
            }
            // 清理 per-session Consumer：Cancel 正在进行的下载
            string sessionPrefix = sessionId + "_";
            foreach (var kv in _serverClipConsumers)
            {
                if (kv.Key.StartsWith(sessionPrefix))
                {
                    FileClipboardConsumer removed;
                    if (_serverClipConsumers.TryRemove(kv.Key, out removed))
                    {
                        try { removed.Cancel(); } catch { }
                    }
                }
            }

            try { info.Stream?.Stop(); } catch { }
            try { info.Stream?.Dispose(); } catch { }
            try { info.Input?.Dispose(); } catch { }

            _transportServer.Disconnect(sessionId);
            Logger.Info("Session {0} disconnected", sessionId);
        }

        private void HeartbeatLoop()
        {
            while (_running)
            {
                Thread.Sleep(10000); // 10s interval

                List<uint> timedOut = new List<uint>();
                lock (_lock)
                {
                    var now = DateTime.UtcNow;
                    foreach (var kv in _lastActivity)
                    {
                        if ((now - kv.Value).TotalSeconds > 45) // 30s + 15s grace
                        {
                            timedOut.Add(kv.Key);
                        }
                        else if ((now - kv.Value).TotalSeconds > 30)
                        {
                            // Send keepalive
                            var empty = new byte[0];
                            MessageReassembler.FragAndSend(0, (byte)MessageType.Keepalive, empty,
                                (sid, data) => _transportServer.SendTo(sid, data), kv.Key);
                        }
                    }
                }

                foreach (var sid in timedOut)
                {
                    Logger.Warn("Session {0} heartbeat timeout — disconnecting", sid);
                    DisconnectSession(sid);
                }
            }
        }

        /// <summary>
        /// 服务端无编码器时，从客户端能力中挑选一个可用编码（ServerStreamSession 会回退到原始像素）。
        /// </summary>
        private static CodecId PickFallbackCodec(CodecCapabilities clientCaps)
        {
            if ((clientCaps & CodecCapabilities.H264Hardware) != 0)
                return CodecId.H264Hardware;
            if ((clientCaps & CodecCapabilities.H264Software) != 0)
                return CodecId.H264Software;
            return CodecId.H264Software; // 保底
        }

        /// <summary>
        /// 验证凭据。读取 UI 配置的凭据表（_credentials）。
        /// 若 _credentials 为空，默认拒绝所有连接。
        /// </summary>
        private bool ValidateCredentials(string username, string password)
        {
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
                return false;
            string stored;
            if (_credentials.TryGetValue(username, out stored))
                return stored == password;
            return false;
        }


    }
}
