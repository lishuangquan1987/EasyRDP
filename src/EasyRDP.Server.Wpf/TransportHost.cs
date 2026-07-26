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

        // Heartbeat
        private Thread _heartbeatThread;
        private volatile bool _running;
        private readonly Dictionary<uint, DateTime> _lastActivity = new Dictionary<uint, DateTime>();

        public TransportHost(
            ICaptureService captureService,
            ITransportServer transportServer,
            IInputSimulator inputSimulator,
            ICursorCapturer cursorCapturer)
        {
            _captureService = captureService;
            _transportServer = transportServer;
            _inputSimulator = inputSimulator;
            _cursorTracker = new CursorTracker(cursorCapturer);

            _transportServer.DataReceived += OnDataReceived;
            _transportServer.ClientConnected += OnClientConnected;
            _transportServer.ClientDisconnected += OnClientDisconnected;
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
        }

        public void Stop()
        {
            Logger.Info("TransportHost stopping, active sessions: {0}", _activeCount);
            _running = false;

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
            Logger.Info("TransportHost stopped");

            _cursorTracker.StopAll();
            _transportServer.Stop();
            _heartbeatThread?.Join(2000);
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
        /// 验证凭据。硬编码表，后续应改为配置文件或外部存储。
        /// </summary>
        private static bool ValidateCredentials(string username, string password)
        {
            // 内置凭据：admin/admin, user/user
            if (username == "admin" && password == "admin") return true;
            if (username == "user" && password == "user") return true;
            return false;
        }


    }
}
