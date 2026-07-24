using System;
using System.Net.Sockets;
using System.Threading;
using EasyRDP.Core.Transport;

namespace EasyRDP.Client.Wpf
{
    /// <summary>
    /// TCP 传输客户端。连接服务端，收发分片字节。
    /// </summary>
    public class TcpTransportClient : ITransportClient
    {
        private TcpClient _client;
        private Thread _receiveThread;
        private volatile bool _running;

        public event EventHandler<FragmentReceivedEventArgs> DataReceived;
        public event EventHandler Disconnected;
        public LogCallback OnLog { get; set; }

        public bool IsConnected
        {
            get { return _client != null && _client.Connected; }
        }

        public bool Connect(string host, int port, int timeoutMs)
        {
            try
            {
                _client = new TcpClient();
                var result = _client.BeginConnect(host, port, null, null);
                if (!result.AsyncWaitHandle.WaitOne(timeoutMs))
                {
                    _client.Close();
                    _client = null;
                    return false;
                }
                _client.EndConnect(result);
                _running = true;

                _receiveThread = new Thread(ReceiveLoop);
                _receiveThread.IsBackground = true;
                _receiveThread.Start();

                Log("Connected to " + host + ":" + port);
                return true;
            }
            catch (Exception ex)
            {
                Log("Connect failed: " + ex.Message);
                _client = null;
                return false;
            }
        }

        public void Disconnect()
        {
            _running = false;
            try { _client.Close(); } catch { }
            _client = null;

            var handler = Disconnected;
            if (handler != null)
                handler(this, EventArgs.Empty);
            Log("Disconnected");
        }

        public bool Send(byte[] data)
        {
            if (_client == null || !_client.Connected)
                return false;
            try
            {
                NetworkStream stream = _client.GetStream();
                stream.Write(data, 0, data.Length);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            Disconnect();
        }

        private void ReceiveLoop()
        {
            byte[] buffer = new byte[65536];
            var framing = new FramingBuffer();
            framing.FragmentReady += (fragData) =>
            {
                var handler = DataReceived;
                if (handler != null)
                    handler(this, new FragmentReceivedEventArgs(0, fragData));
            };

            try
            {
                NetworkStream stream = _client.GetStream();
                while (_running && _client.Connected)
                {
                    int bytesRead = stream.Read(buffer, 0, buffer.Length);
                    if (bytesRead <= 0)
                        break;
                    framing.Feed(buffer, 0, bytesRead);
                }
            }
            catch (Exception ex)
            {
                Log("Receive error: " + ex.Message);
            }
            finally
            {
                Disconnect();
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
