using System;
using System.Collections.Generic;
using System.Threading;
using EasyDesk.Core;
using EasyDesk.Core.Models;
using EasyRDP.Core.Protocol;
using EasyRDP.Core.Services;
using EasyRDP.Core.Session;
using EasyRDP.Core.Transport;

namespace EasyRDP.Server.Wpf
{
    /// <summary>
    /// 服务端传输主机。管理所有 Session 生命周期、握手、心跳、并发控制。
    /// </summary>
    public class TransportHost : IDisposable
    {
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

        // Heartbeat
        private Thread _heartbeatThread;
        private volatile bool _running;
        private readonly Dictionary<uint, DateTime> _lastActivity = new Dictionary<uint, DateTime>();

        public TransportHost(
            ICaptureService captureService,
            ITransportServer transportServer,
            IInputSimulator inputSimulator)
        {
            _captureService = captureService;
            _transportServer = transportServer;
            _inputSimulator = inputSimulator;

            _transportServer.DataReceived += OnDataReceived;
            _transportServer.ClientConnected += OnClientConnected;
            _transportServer.ClientDisconnected += OnClientDisconnected;
        }

        public void Start(int port)
        {
            _running = true;
            _transportServer.Start(port);

            _heartbeatThread = new Thread(HeartbeatLoop);
            _heartbeatThread.IsBackground = true;
            _heartbeatThread.Start();
        }

        public void Stop()
        {
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

            _transportServer.Stop();
            _heartbeatThread?.Join(2000);
        }

        public void Dispose()
        {
            Stop();
        }

        private void OnClientConnected(object sender, ConnectionEventArgs e)
        {
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
            // First message must be HandshakeReq
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

            HandshakeRes res;
            if (req.Version != Constants.ProtocolVersion)
            {
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
                    res = new HandshakeRes { Result = HandshakeResult.ServerBusy };
                    SendResponse(e.SessionId, res);
                    DisconnectSession(e.SessionId);
                    return;
                }
            }

            // TODO: Auth check (Phase 6.5.1)
            // For now accept any credentials

            // Negotiate codec
            var serverCaps = EncoderFactory.GetAvailableCodecs();
            var negotiated = CodecNegotiator.Negotiate(req.Capabilities, serverCaps);
            if (!negotiated.HasValue)
            {
                res = new HandshakeRes { Result = HandshakeResult.NoCommonCodec };
                SendResponse(e.SessionId, res);
                DisconnectSession(e.SessionId);
                return;
            }

            var bounds = _captureService.GetPrimaryScreen();
            res = new HandshakeRes
            {
                Result = HandshakeResult.Success,
                Codec = negotiated.Value,
                ScreenWidth = bounds.Width,
                ScreenHeight = bounds.Height
            };
            SendResponse(e.SessionId, res);

            // Create sessions
            var streamSession = new ServerStreamSession(_captureService, (sid, data) =>
            {
                _transportServer.SendTo(sid, data);
            });

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

            streamSession.Start(e.SessionId, negotiated.Value);
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
        }

        private void DisconnectSession(uint sessionId)
        {
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
                    DisconnectSession(sid);
                }
            }
        }

        private class SessionInfo
        {
            public ServerStreamSession Stream;
            public ServerInputSession Input;
        }
    }
}
