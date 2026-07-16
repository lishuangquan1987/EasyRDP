using System;
using System.Threading;
using EasyRDP.Core.Protocol;
using EasyRDP.Core.Transport;

namespace EasyRDP.Client.Common
{
    /// <summary>
    /// 客户端连接状态机。
    /// 管理 ITransportClient 生命周期：连接→握手→收发→断连。
    /// 不包含任何 UI 代码，仅纯逻辑。
    /// </summary>
    public class ConnectionManager : IDisposable
    {
        private ITransportClient _transport;
        private SequenceTracker _seqTracker;
        private ManualResetEvent _handshakeEvent;
        private volatile ConnectionState _state;
        private volatile string _failureReason;

        private int _remoteScreenWidth;
        private int _remoteScreenHeight;
        private uint _sessionId;

        /// <summary>收到消息时触发（原始消息对象，含 Header + Body）。</summary>
        public event Action<Message> MessageReceived;

        /// <summary>连接成功（握手完成）时触发。</summary>
        public event Action Connected;

        /// <summary>连接失败时触发。参数为失败原因。</summary>
        public event Action<string> ConnectionFailed;

        /// <summary>断连时触发。参数为断连原因。</summary>
        public event Action<string> Disconnected;

        /// <summary>
        /// 创建连接管理器。
        /// </summary>
        public ConnectionManager()
        {
            _seqTracker = new SequenceTracker();
            _handshakeEvent = new ManualResetEvent(false);
            _state = ConnectionState.Disconnected;
        }

        /// <summary>当前连接状态。</summary>
        public ConnectionState State
        {
            get { return _state; }
        }

        /// <summary>当前传输实例。</summary>
        public ITransportClient Transport
        {
            get { return _transport; }
        }

        /// <summary>远程屏幕宽度（握手后获得）。</summary>
        public int RemoteScreenWidth
        {
            get { return _remoteScreenWidth; }
        }

        /// <summary>远程屏幕高度（握手后获得）。</summary>
        public int RemoteScreenHeight
        {
            get { return _remoteScreenHeight; }
        }

        /// <summary>会话 ID（握手后获得）。</summary>
        public uint SessionId
        {
            get { return _sessionId; }
        }

        /// <summary>消息序号跟踪器（发送消息时使用）。</summary>
        public SequenceTracker SeqTracker
        {
            get { return _seqTracker; }
        }

        // ── 连接 ──────────────────────────────────────────

        /// <summary>
        /// 开始连接。此方法在调用线程上阻塞直到握手完成或超时。
        /// 返回 true 表示握手成功，false 表示失败（通过 ConnectionFailed 事件通知原因）。
        /// </summary>
        public bool Connect(string host, int port, int timeoutMs, string authToken)
        {
            if (_state != ConnectionState.Disconnected)
            {
                _failureReason = "Already connected or connecting";
                var cf = ConnectionFailed;
                if (cf != null) cf(_failureReason);
                return false;
            }

            _state = ConnectionState.Connecting;
            _handshakeEvent.Reset();
            _failureReason = null;

            // 创建 TCP 传输
            _transport = new TcpTransportClient();
            _transport.MessageReceived += OnTransportMessage;
            _transport.Disconnected += OnTransportDisconnected;

            if (!_transport.Connect(host, port, timeoutMs))
            {
                _state = ConnectionState.Disconnected;
                _failureReason = "TCP connection failed";
                var cf2 = ConnectionFailed;
                if (cf2 != null) cf2(_failureReason);
                return false;
            }

            // 发送握手请求
            var req = new HandshakeReqMessage
            {
                AuthToken = authToken ?? string.Empty,
                ScreenWidth = 0,
                ScreenHeight = 0,
                CompressType = CompressType.Zlib
            };
            byte[] reqData = MessageCodec.Encode(MessageType.HandshakeReq, _seqTracker.Next(), req);
            _transport.Send(reqData);

            // 等待握手响应
            bool signalled = _handshakeEvent.WaitOne(timeoutMs);

            if (!signalled)
            {
                _state = ConnectionState.Disconnected;
                _transport.Disconnect();
                _failureReason = "Handshake timeout";
                var cf3 = ConnectionFailed;
                if (cf3 != null) cf3(_failureReason);
                return false;
            }

            if (_state == ConnectionState.Connected)
            {
                var c = Connected;
                if (c != null) c();
                return true;
            }

            // 握手失败
            _transport.Disconnect();
            var cf4 = ConnectionFailed;
            if (cf4 != null) cf4(_failureReason ?? "Handshake failed");
            return false;
        }

        // ── 断连 ──────────────────────────────────────────

        /// <summary>
        /// 主动断开连接。发送 DisconnectMessage 后关闭传输。
        /// </summary>
        public void Disconnect(string reason)
        {
            if (_state == ConnectionState.Disconnected || _state == ConnectionState.Disconnecting)
                return;

            _state = ConnectionState.Disconnecting;

            if (_transport != null)
            {
                try
                {
                    var msg = new DisconnectMessage { Reason = DisconnectReason.UserDisconnect };
                    byte[] data = MessageCodec.Encode(MessageType.Disconnect, _seqTracker.Next(), msg);
                    _transport.Send(data);
                }
                catch { }

                _transport.Disconnect();
            }

            _state = ConnectionState.Disconnected;

            var d = Disconnected;
            if (d != null) d(reason ?? "User disconnected");
        }

        /// <summary>
        /// 发送消息。仅在 Connected 状态下有效。
        /// </summary>
        public bool SendMessage(MessageType type, object body)
        {
            if (_state != ConnectionState.Connected || _transport == null)
                return false;

            byte[] data = MessageCodec.Encode(type, _seqTracker.Next(), body);
            return _transport.Send(data);
        }

        // ── 传输事件处理 ──────────────────────────────────

        private void OnTransportMessage(object sender, MessageReceivedEventArgs e)
        {
            var msg = e.Message;
            if (msg == null || msg.Body == null)
                return;

            // 握手响应在连接线程中处理
            if (msg.Header.Type == MessageType.HandshakeRes)
            {
                HandleHandshakeRes((HandshakeResMessage)msg.Body);
                return;
            }

            // 其他消息转发给外部
            var handler = MessageReceived;
            if (handler != null)
                handler(msg);
        }

        private void OnTransportDisconnected(object sender, EventArgs e)
        {
            if (_state == ConnectionState.Connected)
            {
                _state = ConnectionState.Disconnected;
                var d = Disconnected;
                if (d != null) d("Transport disconnected");
            }
        }

        private void HandleHandshakeRes(HandshakeResMessage res)
        {
            if (res.Result == HandshakeResult.Success)
            {
                _remoteScreenWidth = res.ScreenWidth;
                _remoteScreenHeight = res.ScreenHeight;
                _sessionId = res.SessionId;
                _state = ConnectionState.Connected;
            }
            else
            {
                _state = ConnectionState.Disconnected;
                _failureReason = "Handshake rejected: " + res.Result.ToString();
            }

            _handshakeEvent.Set();
        }

        // ── IDisposable ───────────────────────────────────

        public void Dispose()
        {
            _handshakeEvent.Dispose();
            if (_transport != null)
            {
                _transport.Dispose();
                _transport = null;
            }
        }
    }
}
