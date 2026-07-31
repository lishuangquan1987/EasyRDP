#nullable disable
using System;
using System.Net.Sockets;
using System.Threading;
using EasyRDP.Core.Transport;
using NLog;

namespace EasyRDP.Client.Wpf
{
    /// <summary>
    /// TCP 传输客户端。连接服务端，收发分片字节。
    /// </summary>
    public class TcpTransportClient : ITransportClient
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private TcpClient _client;
        private Thread _receiveThread;
        private volatile bool _running;
        // 发送锁：序列化所有 Send 调用，防止多线程并发 Write 导致 TCP 字节流交错。
        // 文件剪贴板后台线程与 UI 线程（输入事件/keepalive）会并发调用 Send，
        // 若不加锁，两个 stream.Write 的字节会在 socket 上交错，破坏 FramingBuffer 分帧。
        private readonly object _sendLock = new object();

        public event EventHandler<FragmentReceivedEventArgs> DataReceived;
        public event EventHandler Disconnected;
        public LogCallback OnLog { get; set; }

        public bool IsConnected
        {
            get { return _client != null && _client.Connected; }
        }

        public bool Connect(string host, int port, int timeoutMs)
        {
            Logger.Info("Connecting to {0}:{1} (timeout={2}ms)", host, port, timeoutMs);
            try
            {
                _client = new TcpClient();
                // 禁用 Nagle 算法：输入事件/心跳都是小包，Nagle + 延迟 ACK 会给交互输入增加约 40ms 延迟
                _client.NoDelay = true;
                var result = _client.BeginConnect(host, port, null, null);
                if (!result.AsyncWaitHandle.WaitOne(timeoutMs))
                {
                    Logger.Warn("Connection timeout to {0}:{1} after {2}ms", host, port, timeoutMs);
                    _client.Close();
                    _client = null;
                    return false;
                }
                _client.EndConnect(result);
                _running = true;

                _receiveThread = new Thread(ReceiveLoop);
                _receiveThread.IsBackground = true;
                _receiveThread.Start();

                Logger.Info("Connected to {0}:{1}", host, port);
                Log("Connected to " + host + ":" + port);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Connect to {0}:{1} failed", host, port);
                Log("Connect failed: " + ex.Message);
                _client = null;
                return false;
            }
        }

        public void Disconnect()
        {
            _running = false;
            if (_client != null)
            {
                try { _client.Close(); } catch { }
                _client = null;
            }

            Logger.Info("Disconnected");
            var handler = Disconnected;
            if (handler != null)
                handler(this, EventArgs.Empty);
            Log("Disconnected");
        }

        public bool Send(byte[] data)
        {
            if (_client == null || !_client.Connected)
                return false;
            // 加锁序列化 Write：确保一个分片完整写入后另一个才开始，
            // 防止并发 Write 导致字节在 TCP 流上交错（破坏服务端 FramingBuffer 分帧）
            lock (_sendLock)
            {
                if (_client == null || !_client.Connected)
                    return false;
                try
                {
                    NetworkStream stream = _client.GetStream();
                    stream.Write(data, 0, data.Length);
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Send failed");
                    return false;
                }
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
                // 防御性 try-catch：若某个分片触发消息处理异常，不能让单个坏消息杀死 ReceiveLoop
                try
                {
                    var handler = DataReceived;
                    if (handler != null)
                        handler(this, new FragmentReceivedEventArgs(0, fragData));
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "FragmentReady handler threw");
                }
            };

            try
            {
                NetworkStream stream = _client.GetStream();
                while (_running && _client.Connected)
                {
                    int bytesRead = stream.Read(buffer, 0, buffer.Length);
                    if (bytesRead <= 0)
                    {
                        Logger.Info("Receive loop: stream.Read returned {0} — server closed connection", bytesRead);
                        break;
                    }
                    framing.Feed(buffer, 0, bytesRead);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Receive error");
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
