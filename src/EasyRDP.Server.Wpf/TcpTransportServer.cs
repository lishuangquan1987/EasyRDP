#nullable disable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using EasyRDP.Core.Transport;
using NLog;

namespace EasyRDP.Server.Wpf
{
    /// <summary>
    /// TCP 传输服务端。每客户端独立 Socket + 接收线程。
    /// </summary>
    public class TcpTransportServer : ITransportServer
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        /// <summary>未完成握手的连接硬上限：防止恶意客户端只建连不发握手耗尽 FD/线程/内存。</summary>
        private const int MaxPendingConnections = 16;

        private TcpListener _listener;
        private readonly Dictionary<uint, TcpClient> _clients = new Dictionary<uint, TcpClient>();
        private readonly Dictionary<uint, object> _sessionLocks = new Dictionary<uint, object>();
        private readonly Dictionary<uint, Thread> _receiveThreads = new Dictionary<uint, Thread>();
        private uint _nextSessionId = 1;
        private volatile bool _running;
        private readonly object _lock = new object();
        // 对已断开会话写入失败的总次数（用于限频日志，避免断连竞态刷屏）
        private long _sendFailCount;
        // 对已移除会话的 SendTo 调用计数（会话移除后到流线程停止前可能仍有少量调用）
        private long _sendNotFoundCount;

        public event EventHandler<ConnectionEventArgs> ClientConnected;
        public event EventHandler<ConnectionEventArgs> ClientDisconnected;
        public event EventHandler<FragmentReceivedEventArgs> DataReceived;
        public LogCallback OnLog { get; set; }

        public void Start(int port)
        {
            Logger.Info("TcpTransportServer starting on port {0}", port);
            lock (_lock)
            {
                if (_running) return; // 防重入（线程安全）
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Start();
                _running = true;
            }

            var acceptThread = new Thread(AcceptLoop);
            acceptThread.IsBackground = true;
            acceptThread.Start();
        }

        public void Stop()
        {
            Logger.Info("TcpTransportServer stopping, active clients: {0}", _clients.Count);
            _running = false;
            try { _listener.Stop(); } catch { }

            TcpClient[] clients;
            lock (_lock)
            {
                clients = new TcpClient[_clients.Count];
                _clients.Values.CopyTo(clients, 0);
                _clients.Clear();
                _sessionLocks.Clear();
                _receiveThreads.Clear();
            }
            foreach (var c in clients)
            {
                try { c.Close(); } catch { }
            }
            // 等待接收线程退出（socket 已关闭，Read 立即返回/抛异常，Join 应很快完成）
            Thread[] threads;
            lock (_lock)
            {
                threads = new Thread[_receiveThreads.Count];
                _receiveThreads.Values.CopyTo(threads, 0);
            }
            foreach (var t in threads)
            {
                try { t.Join(2000); } catch { }
            }
        }

        public void SendTo(uint sessionId, byte[] data)
        {
            TcpClient client;
            object sessionLock;
            lock (_lock)
            {
                if (!_clients.TryGetValue(sessionId, out client))
                {
                    // 正常断连竞态（会话已移除、流线程尚未停止）也会触发此路径：
                    // 限频记录，避免每条视频/光标帧都向日志写一条 Warn。
                    long n = Interlocked.Increment(ref _sendNotFoundCount);
                    if (n == 1 || n % 100 == 0)
                        Logger.Warn("SendTo session {0} not found — dropping {1} bytes (client may have disconnected or sessionId bug)",
                            sessionId, data != null ? data.Length : 0);
                    return;
                }
                sessionLock = _sessionLocks[sessionId];
            }
            lock (sessionLock)
            {
                // 对端已关闭时跳过写入（Connected 为惰性标志，仅用于减少无效调用，
                // 真正的断连竞态仍由下面的异常兜底）
                if (!client.Connected)
                    return;
                try
                {
                    NetworkStream stream = client.GetStream();
                    stream.Write(data, 0, data.Length);
                }
                catch (Exception ex)
                {
                    // 对端断开导致的写入失败是正常断连竞态（视频/光标线程仍可能在发送）：
                    // 限频记录并异步触发清理，避免每帧都向日志框刷一条错误。
                    if (!client.Connected)
                    {
                        long n = Interlocked.Increment(ref _sendFailCount);
                        if (n == 1 || n % 100 == 0)
                            Logger.Warn("SendTo session {0} failed: client disconnected ({1})", sessionId, ex.Message);
                        try
                        {
                            // 立即关闭 socket，让接收线程干净退出（避免其 finally 再报 Receive error）
                            client.Close();
                            System.Threading.ThreadPool.QueueUserWorkItem(s => Disconnect(sessionId));
                        }
                        catch { }
                        return;
                    }
                    Logger.Error(ex, "SendTo session {0} failed: {1}", sessionId, ex.Message);
                    Log("SendTo " + sessionId + " failed: " + ex.Message);
                }
            }
        }

        public void Disconnect(uint sessionId)
        {
            TcpClient client;
            lock (_lock)
            {
                if (!_clients.TryGetValue(sessionId, out client))
                    return;
                _clients.Remove(sessionId);
                _sessionLocks.Remove(sessionId);
                _receiveThreads.Remove(sessionId);
            }
            try { client.Close(); } catch { }

            Logger.Info("Client {0} disconnected", sessionId);
            var handler = ClientDisconnected;
            if (handler != null)
                handler(this, new ConnectionEventArgs(sessionId, ""));
            Log("Client " + sessionId + " disconnected");
        }

        public void Dispose()
        {
            Stop();
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    TcpClient client = _listener.AcceptTcpClient();
                    // 禁用 Nagle：远程输入事件是小包，需要低延迟而不是吞吐量
                    client.NoDelay = true;
                    // 握手前限流：TransportHost 的 _maxSessions 只在握手成功后计数，
                    // 恶意客户端可无限建立 TCP 连接并停留在握手阶段。这里在分配
                    // sessionId/接收线程之前就设硬上限，超出直接断开。
                    int pendingCount;
                    lock (_lock)
                    {
                        pendingCount = _clients.Count;
                    }
                    if (pendingCount >= MaxPendingConnections)
                    {
                        Logger.Warn("Connection rejected: pending connections {0} >= max {1}",
                            pendingCount, MaxPendingConnections);
                        try { client.Close(); } catch { }
                        continue;
                    }
                    uint sessionId;
                    lock (_lock)
                    {
                        sessionId = _nextSessionId++;
                        _clients[sessionId] = client;
                        _sessionLocks[sessionId] = new object();
                    }

                    string remote = client.Client.RemoteEndPoint.ToString();
                    Logger.Info("Client {0} connected: {1}", sessionId, remote);
                    Log("Client " + sessionId + " connected: " + remote);

                    var handler = ClientConnected;
                    if (handler != null)
                        handler(this, new ConnectionEventArgs(sessionId, remote));

                    var thread = new Thread(() => ReceiveLoop(sessionId, client));
                    thread.IsBackground = true;
                    // 输入消息（鼠标移动/点击/键盘）都在接收线程处理：
                    // 弱机 CPU 饱和时提权保证输入及时处理，避免右键/点击延迟到秒级
                    thread.Priority = ThreadPriority.AboveNormal;
                    lock (_lock) { _receiveThreads[sessionId] = thread; }
                    thread.Start();
                }
                catch (SocketException ex)
                {
                    if (!_running) break;
                    Logger.Warn("Accept socket error: {0}", ex.Message);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Accept error");
                    Log("Accept error: " + ex.Message);
                }
            }
        }

        private void ReceiveLoop(uint sessionId, TcpClient client)
        {
            byte[] buffer = new byte[65536];
            var framing = new FramingBuffer();
            framing.FragmentReady += (fragData) =>
            {
                // 防御性 try-catch：若某个分片触发消息处理异常（如 bogus 消息 Unpack 失败），
                // 不能让单个坏消息杀死整个 ReceiveLoop 导致连接断开。
                try
                {
                    var handler = DataReceived;
                    if (handler != null)
                        handler(this, new FragmentReceivedEventArgs(sessionId, fragData));
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "FragmentReady handler threw for session {0}", sessionId);
                }
            };

            try
            {
                NetworkStream stream = client.GetStream();
                while (_running && client.Connected)
                {
                    int bytesRead = stream.Read(buffer, 0, buffer.Length);
                    if (bytesRead <= 0)
                    {
                        Logger.Info("Session {0} receive loop: stream.Read returned {1} — client closed connection",
                            sessionId, bytesRead);
                        break;
                    }
                    framing.Feed(buffer, 0, bytesRead);
                }
            }
            catch (Exception ex)
            {
                // socket 被 SendTo 断连竞态清理或对端主动关闭：属正常断连，不按异常刷错误日志
                if (!client.Connected)
                {
                    Logger.Info("Session {0} receive loop ended: socket closed", sessionId);
                }
                else
                {
                    Logger.Error(ex, "Receive error session {0}", sessionId);
                    Log("Receive error session " + sessionId + ": " + ex.Message);
                }
            }
            finally
            {
                Disconnect(sessionId);
            }
        }

        private void Log(string message)
        {
            var cb = OnLog;
            if (cb != null)
                cb(message);
        }
    }
}
