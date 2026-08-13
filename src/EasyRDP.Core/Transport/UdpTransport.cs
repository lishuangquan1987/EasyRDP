namespace EasyRDP.Core.Transport
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Sockets;
    using System.Threading;
    using EasyRDP.Core.Protocol;
    using NLog;

    /// <summary>
    /// UDP 传输连接。UDP 无连接、不可靠、受 MTU 限制，因此在实现内部自建 datagram 分片：
    /// 发送侧按 MaxFragData 切片，每片加 UDP 分片头（FrameId/FragIdx/FragCount/CRC16/MessageType/PayloadLen）；
    /// 接收侧按 FrameId 重组，实时流（VideoFrame/CursorUpdate/InputEvent）允许丢旧帧、最新帧优先，
    /// 控制流（其余类型）必须完整。这是「分片下放传输实现」的 UDP 后端。
    /// </summary>
    public class UdpTransport : ITransport
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        /// <summary>UDP datagram 上限（避免 IP 分片，留余量给 IP/UDP 头）。</summary>
        public const int MaxDatagramSize = 1400;
        /// <summary>UDP 分片头字节数：FrameId(4)+FragIdx(2)+FragCount(2)+CRC16(2)+MessageType(1)+PayloadLen(4)。</summary>
        public const int UdpHeaderSize = 15;
        /// <summary>单片 FragData 上限（datagram 总大小 ≤ MaxDatagramSize）。</summary>
        public const int MaxFragData = 1200;

        private UdpClient _client;
        private readonly bool _ownsReceiveLoop;
        private readonly IPEndPoint _remote;
        private Thread _receiveThread;
        private volatile bool _running;
        private volatile bool _disconnected;
        private int _started;
        private uint _nextFrameId = 1;
        private readonly object _sendLock = new object();
        private readonly UdpReassembler _reassembler = new UdpReassembler();

        public event EventHandler<MessageReceivedEventArgs> MessageReceived;
        public event EventHandler Disconnected;
        public LogCallback OnLog { get; set; }

        /// <summary>
        /// 构造一条 UDP「连接」。ownsReceiveLoop=true 表示本实例自己启动接收线程（客户端模式）；
        /// false 表示接收由 acceptor 统一分发（服务端模式，调用方调 HandleDatagram）。
        /// </summary>
        public UdpTransport(UdpClient client, IPEndPoint remote, bool ownsReceiveLoop)
        {
            _client = client;
            _remote = remote;
            _ownsReceiveLoop = ownsReceiveLoop;
            _reassembler.MessageAssembled += (type, payload) =>
            {
                var handler = MessageReceived;
                if (handler != null)
                    handler(this, new MessageReceivedEventArgs(0, type, payload));
            };
        }

        public bool IsConnected
        {
            get { return _client != null && !_disconnected; }
        }

        /// <summary>开始接收循环（幂等）。服务端模式（acceptor 统一分发）下为 no-op。</summary>
        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0)
                return;
            if (_client == null || _disconnected)
                return;

            _running = true;
            if (_ownsReceiveLoop)
            {
                _receiveThread = new Thread(ReceiveLoop);
                _receiveThread.IsBackground = true;
                _receiveThread.Start();
            }
        }

        /// <summary>发送一条完整消息（framing 外层 + payload），内部切片为多个 datagram。</summary>
        public void Send(byte[] message)
        {
            if (message == null || message.Length < Framing.HeaderSize)
                return;

            byte messageType;
            byte[] payload;
            if (!Framing.TryParse(message, out messageType, out payload))
                return;

            int totalLen = payload.Length;
            int fragCount = (totalLen + MaxFragData - 1) / MaxFragData;
            if (fragCount == 0)
                fragCount = 1;
            if (fragCount > ushort.MaxValue)
                return; // payload 过大，拒绝

            lock (_sendLock)
            {
                if (_client == null || _disconnected)
                    return;
                // frameId 在锁内分配：多线程（heartbeat/clipboard/UI/编码）并发 Send 时
                // 保证单调递增不重复，避免两帧取到相同 frameId 导致接收端分片混淆。
                uint frameId = _nextFrameId++;
                for (int i = 0; i < fragCount; i++)
                {
                    int offset = i * MaxFragData;
                    int fragLen = Math.Min(MaxFragData, totalLen - offset);
                    if (fragLen < 0) fragLen = 0;

                    byte[] fragData = new byte[fragLen];
                    if (fragLen > 0)
                        Buffer.BlockCopy(payload, offset, fragData, 0, fragLen);

                    byte[] datagram = BuildDatagram(frameId, (ushort)i, (ushort)fragCount, messageType, (uint)totalLen, fragData);
                    try
                    {
                        _client.Send(datagram, datagram.Length, _remote);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, "UDP Send failed");
                        return;
                    }
                }
            }
        }

        public void Disconnect()
        {
            if (_disconnected)
                return;
            _disconnected = true;
            _running = false;

            if (_ownsReceiveLoop && _client != null)
            {
                try { _client.Close(); } catch { }
            }
            _client = null;

            var handler = Disconnected;
            if (handler != null)
                handler(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            Disconnect();
        }

        /// <summary>由 acceptor（服务端模式）喂入一个收到的 datagram。</summary>
        internal void HandleDatagram(byte[] datagram)
        {
            try
            {
                _reassembler.OnDatagram(datagram);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "HandleDatagram threw");
            }
        }

        private void ReceiveLoop()
        {
            try
            {
                while (_running && _client != null)
                {
                    IPEndPoint remote = null;
                    byte[] datagram = _client.Receive(ref remote);
                    if (datagram == null)
                        continue;
                    HandleDatagram(datagram);
                }
            }
            catch (Exception ex)
            {
                if (!_disconnected)
                    Logger.Warn(ex, "UDP ReceiveLoop ended");
            }
            finally
            {
                Disconnect();
            }
        }

        internal static byte[] BuildDatagram(uint frameId, ushort fragIdx, ushort fragCount, byte messageType, uint payloadLen, byte[] fragData)
        {
            byte[] dg = new byte[UdpHeaderSize + fragData.Length];
            int pos = 0;
            dg[pos++] = (byte)(frameId & 0xFF);
            dg[pos++] = (byte)((frameId >> 8) & 0xFF);
            dg[pos++] = (byte)((frameId >> 16) & 0xFF);
            dg[pos++] = (byte)((frameId >> 24) & 0xFF);
            dg[pos++] = (byte)(fragIdx & 0xFF);
            dg[pos++] = (byte)((fragIdx >> 8) & 0xFF);
            dg[pos++] = (byte)(fragCount & 0xFF);
            dg[pos++] = (byte)((fragCount >> 8) & 0xFF);
            // CRC16 占位（pos 8-9），跳过
            pos += 2;
            dg[pos++] = messageType;
            dg[pos++] = (byte)(payloadLen & 0xFF);
            dg[pos++] = (byte)((payloadLen >> 8) & 0xFF);
            dg[pos++] = (byte)((payloadLen >> 16) & 0xFF);
            dg[pos++] = (byte)((payloadLen >> 24) & 0xFF);
            // FragData（pos 15+）
            if (fragData.Length > 0)
                Buffer.BlockCopy(fragData, 0, dg, pos, fragData.Length);
            // 回填 CRC16
            ushort crc = Crc16.Compute(fragData, 0, fragData.Length);
            dg[8] = (byte)(crc & 0xFF);
            dg[9] = (byte)((crc >> 8) & 0xFF);
            return dg;
        }
    }

    /// <summary>CRC-16/XMODEM 查表实现（UDP datagram 级校验）。</summary>
    public static class Crc16
    {
        private static readonly ushort[] Table = BuildTable();

        private static ushort[] BuildTable()
        {
            ushort[] table = new ushort[256];
            for (int i = 0; i < 256; i++)
            {
                ushort crc = (ushort)(i << 8);
                for (int j = 0; j < 8; j++)
                    crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
                table[i] = crc;
            }
            return table;
        }

        public static ushort Compute(byte[] data, int offset, int length)
        {
            ushort crc = 0;
            for (int i = 0; i < length; i++)
                crc = (ushort)((crc << 8) ^ Table[(crc >> 8) ^ data[offset + i]]);
            return crc;
        }
    }

    /// <summary>
    /// UDP datagram 重组器。按 FrameId 重组分片，实时流最新帧优先（丢旧帧），控制流必须完整。
    /// 单实例由单接收线程/单 HandleDatagram 调用方串行驱动，无需加锁。
    /// </summary>
    internal class UdpReassembler
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public event Action<byte, byte[]> MessageAssembled;

        private readonly FrameState _realtime = new FrameState();
        private readonly Dictionary<byte, FrameState> _control = new Dictionary<byte, FrameState>();

        public void OnDatagram(byte[] datagram)
        {
            if (datagram == null || datagram.Length < UdpTransport.UdpHeaderSize)
                return;

            int pos = 0;
            uint frameId = (uint)(datagram[pos] | (datagram[pos + 1] << 8) | (datagram[pos + 2] << 16) | (datagram[pos + 3] << 24));
            pos += 4;
            ushort fragIdx = (ushort)(datagram[pos] | (datagram[pos + 1] << 8));
            pos += 2;
            ushort fragCount = (ushort)(datagram[pos] | (datagram[pos + 1] << 8));
            pos += 2;
            ushort expectedCrc = (ushort)(datagram[pos] | (datagram[pos + 1] << 8));
            pos += 2;
            byte messageType = datagram[pos++];
            uint payloadLen = (uint)(datagram[pos] | (datagram[pos + 1] << 8) | (datagram[pos + 2] << 16) | (datagram[pos + 3] << 24));
            pos += 4;

            int fragDataLen = datagram.Length - pos;
            if (fragDataLen < 0)
                return;
            if (fragCount == 0 || fragIdx >= fragCount)
                return;
            if (payloadLen > (uint)Constants.MaxSafePayloadSize)
                return;

            ushort actualCrc = Crc16.Compute(datagram, pos, fragDataLen);
            if (actualCrc != expectedCrc)
                return; // 损坏，丢弃

            FrameState state;
            if (IsRealtime(messageType))
                state = _realtime;
            else
            {
                if (!_control.TryGetValue(messageType, out state))
                {
                    state = new FrameState();
                    _control[messageType] = state;
                }
            }

            byte[] full;
            if (state.Process(frameId, messageType, fragIdx, fragCount, (int)payloadLen, datagram, pos, fragDataLen, out full))
            {
                var handler = MessageAssembled;
                if (handler != null)
                    handler(messageType, full);
            }
        }

        private static bool IsRealtime(byte messageType)
        {
            return messageType == (byte)MessageType.VideoFrame
                || messageType == (byte)MessageType.InputEvent
                || messageType == (byte)MessageType.CursorUpdate;
        }

        private sealed class FrameState
        {
            private uint _currentFrameId;
            private bool _initialized;
            private int _expectedFragCount;
            private int _receivedFragCount;
            private byte[][] _fragBuffers;
            private int _totalPayloadLen;
            private byte _messageType;
            private bool _completed;

            public bool Process(uint frameId, byte messageType, ushort fragIdx, ushort fragCount,
                int totalPayloadLen, byte[] data, int dataPos, int fragDataLen, out byte[] full)
            {
                full = null;

                if (!_initialized || IsNewer(frameId, _currentFrameId))
                    StartNew(frameId, messageType, totalPayloadLen, fragCount);
                else if (IsOlder(frameId, _currentFrameId))
                    return false; // 旧帧，丢弃
                else if (_completed)
                    StartNew(frameId, messageType, totalPayloadLen, fragCount);

                if (_fragBuffers != null && fragIdx < _fragBuffers.Length && _fragBuffers[fragIdx] == null)
                {
                    _fragBuffers[fragIdx] = new byte[fragDataLen];
                    Buffer.BlockCopy(data, dataPos, _fragBuffers[fragIdx], 0, fragDataLen);
                    _receivedFragCount++;
                }

                if (_receivedFragCount >= _expectedFragCount)
                {
                    full = new byte[_totalPayloadLen];
                    int off = 0;
                    for (int i = 0; i < _expectedFragCount; i++)
                    {
                        if (_fragBuffers[i] != null)
                        {
                            int len = Math.Min(_fragBuffers[i].Length, _totalPayloadLen - off);
                            if (len > 0)
                            {
                                Buffer.BlockCopy(_fragBuffers[i], 0, full, off, len);
                                off += len;
                            }
                        }
                    }
                    _completed = true;
                    _expectedFragCount = 0;
                    _receivedFragCount = 0;
                    _fragBuffers = null;
                    return true;
                }
                return false;
            }

            private void StartNew(uint frameId, byte messageType, int totalPayloadLen, ushort fragCount)
            {
                _currentFrameId = frameId;
                _initialized = true;
                _completed = false;
                _messageType = messageType;
                _totalPayloadLen = totalPayloadLen;
                _expectedFragCount = fragCount;
                _receivedFragCount = 0;
                _fragBuffers = new byte[fragCount][];
            }

            private static bool IsNewer(uint a, uint b)
            {
                return (int)(a - b) > 0;
            }

            private static bool IsOlder(uint a, uint b)
            {
                return (int)(a - b) < 0;
            }
        }
    }
}
