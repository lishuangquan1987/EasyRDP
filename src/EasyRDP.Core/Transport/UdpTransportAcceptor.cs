namespace EasyRDP.Core.Transport
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Sockets;
    using System.Threading;
    using NLog;

    /// <summary>
    /// UDP 传输监听器。实现 ITransportAcceptor。
    /// UDP 无连接，因此 acceptor 持有单监听 socket，收到某对端第一个 datagram 时为其创建
    /// UdpTransport（ownsReceiveLoop=false，接收由本 acceptor 统一分发）并触发 ClientConnected。
    /// </summary>
    public class UdpTransportAcceptor : ITransportAcceptor
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private UdpClient _listener;
        private readonly Dictionary<IPEndPoint, UdpTransport> _transports = new Dictionary<IPEndPoint, UdpTransport>();
        private readonly object _lock = new object();
        private volatile bool _running;
        private Thread _receiveThread;

        public event EventHandler<TransportAcceptedEventArgs> ClientConnected;
        public LogCallback OnLog { get; set; }

        public void Start(string endpoint)
        {
            int port;
            if (!TryParseListenEndpoint(endpoint, out port))
            {
                Logger.Error("Start failed: invalid endpoint '{0}'", endpoint);
                Log("Start failed: invalid endpoint '" + endpoint + "'");
                return;
            }

            lock (_lock)
            {
                if (_running)
                    return;
                _listener = new UdpClient(port);
                _running = true;
            }

            _receiveThread = new Thread(ReceiveLoop);
            _receiveThread.IsBackground = true;
            _receiveThread.Start();
        }

        public void Stop()
        {
            _running = false;
            try { if (_listener != null) _listener.Close(); } catch { }

            UdpTransport[] transports;
            lock (_lock)
            {
                transports = new UdpTransport[_transports.Count];
                _transports.Values.CopyTo(transports, 0);
                _transports.Clear();
            }
            foreach (var t in transports)
            {
                try { t.Disconnect(); } catch { }
            }
        }

        public void Dispose()
        {
            Stop();
        }

        private void ReceiveLoop()
        {
            while (_running)
            {
                try
                {
                    IPEndPoint remote = null;
                    byte[] data = _listener.Receive(ref remote);
                    if (data == null || remote == null)
                        continue;

                    UdpTransport transport;
                    bool isNew = false;
                    lock (_lock)
                    {
                        if (!_transports.TryGetValue(remote, out transport))
                        {
                            transport = new UdpTransport(_listener, remote, false);
                            transport.Disconnected += (s, e) => RemoveTransport(remote);
                            _transports[remote] = transport;
                            isNew = true;
                        }
                    }

                    if (isNew)
                    {
                        Logger.Info("UDP client connected: {0}", remote);
                        Log("Client connected: " + remote);
                        var handler = ClientConnected;
                        if (handler != null)
                            handler(this, new TransportAcceptedEventArgs(transport, remote.ToString()));
                    }

                    transport.HandleDatagram(data);
                }
                catch (SocketException ex)
                {
                    if (!_running) break;
                    Logger.Warn("UDP receive socket error: {0}", ex.Message);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "UDP receive error");
                }
            }
        }

        private void RemoveTransport(IPEndPoint remote)
        {
            lock (_lock) { _transports.Remove(remote); }
        }

        private static bool TryParseListenEndpoint(string endpoint, out int port)
        {
            port = 0;
            if (string.IsNullOrEmpty(endpoint))
                return false;
            string portStr = endpoint;
            int idx = endpoint.LastIndexOf(':');
            if (idx >= 0)
                portStr = endpoint.Substring(idx + 1);
            if (!int.TryParse(portStr, out port))
                return false;
            if (port < 1 || port > 65535)
                return false;
            return true;
        }

        private void Log(string message)
        {
            var cb = OnLog;
            if (cb != null)
                cb(message);
        }
    }
}
