namespace EasyRDP.Core.Transport
{
    using System;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using EasyRDP.Core.Protocol;
    using NLog;

    /// <summary>
    /// WebSocket 传输连接（手写 RFC 6455 帧协议，net40 兼容，无 ClientWebSocket 依赖）。
    /// 每个「完整 WebSocket 消息」对应一条 ITransport 完整消息（framing 外层 + payload）。
    /// 客户端→服务端帧带掩码，服务端→客户端帧不带掩码。
    /// </summary>
    public class WebSocketTransport : ITransport
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        /// <summary>WebSocket GUID（RFC 6455 Sec-WebSocket-Accept 计算用）。</summary>
        public const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        private Stream _stream;
        private readonly bool _isClient;
        private Thread _receiveThread;
        private volatile bool _running;
        private volatile bool _disconnected;
        private int _started;
        private readonly object _sendLock = new object();
        // 分片消息累积缓冲（WS message 可能跨多个帧）
        private MemoryStream _messageBuffer;

        public event EventHandler<MessageReceivedEventArgs> MessageReceived;
        public event EventHandler Disconnected;
        public LogCallback OnLog { get; set; }

        /// <summary>包装一条已完成 WebSocket 握手的流。isClient 决定发送帧是否加掩码。</summary>
        public WebSocketTransport(Stream stream, bool isClient)
        {
            _stream = stream;
            _isClient = isClient;
        }

        public bool IsConnected
        {
            get { return _stream != null && !_disconnected; }
        }

        /// <summary>开始接收循环（幂等）。</summary>
        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0)
                return;
            if (_stream == null || _disconnected)
                return;

            _running = true;
            _receiveThread = new Thread(ReceiveLoop);
            _receiveThread.IsBackground = true;
            _receiveThread.Start();
        }

        /// <summary>发送一条完整消息（framing 外层 + payload），作为一个 binary 帧（FIN=1）。</summary>
        public void Send(byte[] message)
        {
            if (message == null)
                return;
            lock (_sendLock)
            {
                if (_stream == null || _disconnected)
                    return;
                try
                {
                    WriteFrame(0x2, message, true); // opcode=binary, FIN=1
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "WebSocket Send failed");
                    Disconnect();
                }
            }
        }

        public void Disconnect()
        {
            if (_disconnected)
                return;
            _disconnected = true;
            _running = false;

            if (_stream != null)
            {
                try { _stream.Close(); } catch { }
                _stream = null;
            }

            var handler = Disconnected;
            if (handler != null)
                handler(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            Disconnect();
        }

        /// <summary>计算 Sec-WebSocket-Accept（服务端握手响应用）。</summary>
        public static string ComputeAcceptKey(string secWebSocketKey)
        {
            string combined = secWebSocketKey + WebSocketGuid;
            byte[] hash;
            using (var sha1 = SHA1.Create())
            {
                hash = sha1.ComputeHash(Encoding.ASCII.GetBytes(combined));
            }
            return Convert.ToBase64String(hash);
        }

        private void ReceiveLoop()
        {
            try
            {
                while (_running && _stream != null)
                {
                    byte[] payload;
                    int opcode;
                    bool fin;
                    if (!ReadFrame(out opcode, out fin, out payload))
                        break;

                    if (opcode == 0x8) // close
                    {
                        break;
                    }
                    else if (opcode == 0x9) // ping → pong
                    {
                        WriteFrame(0xA, payload, false);
                    }
                    else if (opcode == 0xA) // pong
                    {
                        // ignore
                    }
                    else if (opcode == 0x1 || opcode == 0x2) // text/binary
                    {
                        OnDataFrame(opcode, fin, payload);
                    }
                    else if (opcode == 0x0) // continuation
                    {
                        OnDataFrame(opcode, fin, payload);
                    }
                    // 其余 opcode 忽略
                }
            }
            catch (Exception ex)
            {
                if (!_disconnected)
                {
                    Logger.Warn(ex, "WebSocket ReceiveLoop ended");
                    Log("Receive error: " + ex.Message);
                }
            }
            finally
            {
                Disconnect();
            }
        }

        private void OnDataFrame(int opcode, bool fin, byte[] payload)
        {
            if (opcode == 0x1 || opcode == 0x2)
            {
                // 新消息首帧（可能分片）
                if (fin)
                {
                    DeliverMessage(payload);
                }
                else
                {
                    _messageBuffer = new MemoryStream();
                    if (payload.Length > 0)
                        _messageBuffer.Write(payload, 0, payload.Length);
                }
            }
            else // continuation (0x0)
            {
                if (_messageBuffer == null)
                    return; // 无首帧，丢弃
                if (payload.Length > 0)
                    _messageBuffer.Write(payload, 0, payload.Length);
                if (fin)
                {
                    byte[] full = _messageBuffer.ToArray();
                    _messageBuffer.Dispose();
                    _messageBuffer = null;
                    DeliverMessage(full);
                }
            }
        }

        private void DeliverMessage(byte[] wire)
        {
            byte messageType;
            byte[] payload;
            if (!Framing.TryParse(wire, out messageType, out payload))
            {
                Logger.Warn("WebSocket: invalid message dropped ({0} bytes)", wire.Length);
                return;
            }
            var handler = MessageReceived;
            if (handler != null)
                handler(this, new MessageReceivedEventArgs(0, messageType, payload));
        }

        // ── 帧编解码 ──

        private void WriteFrame(int opcode, byte[] payload, bool clientMask)
        {
            var header = new MemoryStream();
            int b0 = 0x80 | (opcode & 0x0F); // FIN=1
            header.WriteByte((byte)b0);

            int len = payload != null ? payload.Length : 0;
            byte maskBit = clientMask ? (byte)0x80 : (byte)0x00;
            if (len < 126)
            {
                header.WriteByte((byte)(maskBit | len));
            }
            else if (len <= 0xFFFF)
            {
                header.WriteByte((byte)(maskBit | 126));
                header.WriteByte((byte)((len >> 8) & 0xFF));
                header.WriteByte((byte)(len & 0xFF));
            }
            else
            {
                header.WriteByte((byte)(maskBit | 127));
                long l = len;
                for (int i = 7; i >= 0; i--)
                    header.WriteByte((byte)((l >> (i * 8)) & 0xFF));
            }

            byte[] headerBytes = header.ToArray();
            _stream.Write(headerBytes, 0, headerBytes.Length);

            if (len > 0)
            {
                if (clientMask)
                {
                    byte[] maskKey = new byte[4];
                    var rng = new Random();
                    rng.NextBytes(maskKey);
                    _stream.Write(maskKey, 0, 4);
                    byte[] masked = new byte[len];
                    for (int i = 0; i < len; i++)
                        masked[i] = (byte)(payload[i] ^ maskKey[i % 4]);
                    _stream.Write(masked, 0, len);
                }
                else
                {
                    _stream.Write(payload, 0, len);
                }
            }
            _stream.Flush();
        }

        private bool ReadFrame(out int opcode, out bool fin, out byte[] payload)
        {
            opcode = 0;
            fin = false;
            payload = null;

            int b0 = _stream.ReadByte();
            int b1 = _stream.ReadByte();
            if (b0 < 0 || b1 < 0)
                return false;

            fin = (b0 & 0x80) != 0;
            opcode = b0 & 0x0F;
            bool masked = (b1 & 0x80) != 0;
            long len = b1 & 0x7F;

            if (len == 126)
                len = ReadUInt16();
            else if (len == 127)
                len = ReadInt64();

            byte[] maskKey = null;
            if (masked)
            {
                maskKey = ReadExact(4);
            }

            if (len < 0 || len > Constants.MaxSafePayloadSize)
                return false; // 异常长度

            payload = ReadExact((int)len);

            if (masked && maskKey != null && payload.Length > 0)
            {
                for (int i = 0; i < payload.Length; i++)
                    payload[i] = (byte)(payload[i] ^ maskKey[i % 4]);
            }
            return true;
        }

        private int ReadUInt16()
        {
            byte[] b = ReadExact(2);
            return (b[0] << 8) | b[1];
        }

        private long ReadInt64()
        {
            byte[] b = ReadExact(8);
            long v = 0;
            for (int i = 0; i < 8; i++)
                v = (v << 8) | b[i];
            return v;
        }

        private byte[] ReadExact(int count)
        {
            byte[] buf = new byte[count];
            int off = 0;
            while (off < count)
            {
                int n = _stream.Read(buf, off, count - off);
                if (n <= 0)
                    throw new IOException("Stream closed while reading");
                off += n;
            }
            return buf;
        }

        private void Log(string message)
        {
            var cb = OnLog;
            if (cb != null)
                cb(message);
        }
    }
}
