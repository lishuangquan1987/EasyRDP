using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using EasyRDP.Core.Transport;

namespace EasyRDP.Server.Wpf
{
    /// <summary>
    /// TCP 传输服务端。每客户端独立 Socket + 接收线程。
    /// </summary>
    public class TcpTransportServer : ITransportServer
    {
        private TcpListener _listener;
        private readonly Dictionary<uint, TcpClient> _clients = new Dictionary<uint, TcpClient>();
        private readonly Dictionary<uint, object> _sessionLocks = new Dictionary<uint, object>();
        private readonly Dictionary<uint, Thread> _receiveThreads = new Dictionary<uint, Thread>();
        private uint _nextSessionId = 1;
        private volatile bool _running;
        private readonly object _lock = new object();

        public event EventHandler<ConnectionEventArgs> ClientConnected;
        public event EventHandler<ConnectionEventArgs> ClientDisconnected;
        public event EventHandler<FragmentReceivedEventArgs> DataReceived;
        public LogCallback OnLog { get; set; }

        public void Start(int port)
        {
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            _running = true;

            Log("Server started on port " + port);

            var acceptThread = new Thread(AcceptLoop);
            acceptThread.IsBackground = true;
            acceptThread.Start();
        }

        public void Stop()
        {
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
            Log("Server stopped");
        }

        public void SendTo(uint sessionId, byte[] data)
        {
            TcpClient client;
            object sessionLock;
            lock (_lock)
            {
                if (!_clients.TryGetValue(sessionId, out client))
                    return;
                sessionLock = _sessionLocks[sessionId];
            }
            lock (sessionLock)
            {
                try
                {
                    NetworkStream stream = client.GetStream();
                    stream.Write(data, 0, data.Length);
                }
                catch (Exception ex)
                {
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
                    uint sessionId;
                    lock (_lock)
                    {
                        sessionId = _nextSessionId++;
                        _clients[sessionId] = client;
                        _sessionLocks[sessionId] = new object();
                    }

                    string remote = client.Client.RemoteEndPoint.ToString();
                    Log("Client " + sessionId + " connected: " + remote);

                    var handler = ClientConnected;
                    if (handler != null)
                        handler(this, new ConnectionEventArgs(sessionId, remote));

                    var thread = new Thread(() => ReceiveLoop(sessionId, client));
                    thread.IsBackground = true;
                    lock (_lock) { _receiveThreads[sessionId] = thread; }
                    thread.Start();
                }
                catch (SocketException)
                {
                    if (!_running) break;
                }
                catch (Exception ex)
                {
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
                var handler = DataReceived;
                if (handler != null)
                    handler(this, new FragmentReceivedEventArgs(sessionId, fragData));
            };

            try
            {
                NetworkStream stream = client.GetStream();
                while (_running && client.Connected)
                {
                    int bytesRead = stream.Read(buffer, 0, buffer.Length);
                    if (bytesRead <= 0)
                        break;
                    framing.Feed(buffer, 0, bytesRead);
                }
            }
            catch (Exception ex)
            {
                Log("Receive error session " + sessionId + ": " + ex.Message);
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
