using System;
using System.Threading;
using EasyRDP.Core.Protocol;
using EasyRDP.Core.Rendering;
using EasyRDP.Core.Session;
using EasyRDP.Core.Transport;
using NLog;

namespace EasyRDP.Client.Wpf
{
    /// <summary>
    /// 客户端视频流会话。双线程：接收线程解码→FrameBuffer，渲染线程→RenderTarget。
    /// </summary>
    public class ClientStreamSession : IClientStreamSession
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private ITransportClient _transport;
        private IVideoDecoder _decoder;
        private FrameBuffer _frameBuffer;
        private IRenderTarget _renderTarget;
        private MessageReassembler _reassembler;
        private volatile bool _running;
        private Thread _receiveThread;
        private Thread _renderThread;
        private long _frameCount;
        private int _decodeFailures;

        /// <summary>Gets the negotiated video codec used for decoding.</summary>
        public CodecId Codec { get; private set; }
        /// <summary>Gets the current frame width in pixels.</summary>
        public int FrameWidth { get { return _frameBuffer != null ? _frameBuffer.Width : 0; } }
        /// <summary>Gets the current frame height in pixels.</summary>
        public int FrameHeight { get { return _frameBuffer != null ? _frameBuffer.Height : 0; } }
        /// <summary>Gets the total number of frames received and processed.</summary>
        public long FrameCount { get { return _frameCount; } }

        /// <summary>Gets or sets the render target where decoded frames are displayed.</summary>
        public IRenderTarget RenderTarget
        {
            get { return _renderTarget; }
            set { _renderTarget = value; }
        }

        /// <summary>Raised when a non-recoverable error occurs during the stream session.</summary>
        public event EventHandler<ErrorEventArgs> FatalError;

        /// <summary>初始化渲染管线（在收到 HandshakeRes 后调用）。</summary>
        public void InitPipeline(CodecId codec, int width, int height)
        {
            Codec = codec;
            Logger.Info("InitPipeline: codec={0} resolution={1}x{2}", codec, width, height);
            _decoder = DecoderFactory.Create(codec);
            if (_decoder != null)
                _decoder.Initialize(width, height);
            else
                Logger.Error("InitPipeline: decoder not available for codec {0} — H264 decoding is mandatory", codec);
            _frameBuffer = new FrameBuffer();
            if (_renderTarget != null)
                _renderTarget.Resize(width, height);
        }

        /// <summary>Starts the stream session: begins receiving, decoding, and rendering frames.</summary>
        public void Start(ITransportClient transport)
        {
            if (_running) return;
            _transport = transport;
            _running = true;

            _reassembler = new MessageReassembler();
            _reassembler.MessageReceived += OnMessageReceived;
            _transport.DataReceived += OnDataReceived;

            _receiveThread = new Thread(ReceiveLoop);
            _receiveThread.IsBackground = true;
            _receiveThread.Start();

            _renderThread = new Thread(RenderLoop);
            _renderThread.IsBackground = true;
            _renderThread.Start();
        }

        /// <summary>Stops the stream session, terminates background threads, and cleans up resources.</summary>
        public void Stop()
        {
            Logger.Info("ClientStreamSession stopping, frames received: {0} decodeFailures: {1}", _frameCount, _decodeFailures);
            _running = false;
            if (_transport != null)
                _transport.DataReceived -= OnDataReceived;

            _receiveThread?.Join(3000);
            _renderThread?.Join(3000);

            _decoder?.Dispose();
            _decoder = null;
            _frameBuffer?.Reset();
            _frameBuffer = null;
            Logger.Info("ClientStreamSession stopped");
        }

        /// <summary>Disposes the session by stopping all activity and releasing resources.</summary>
        public void Dispose()
        {
            Stop();
        }

        private void OnDataReceived(object sender, FragmentReceivedEventArgs e)
        {
            if (e == null || e.Data == null || e.Data.Length < 16) return;

            // 探测消息类型：wire[0]=Magic, wire[1]=MessageType
            // 光标更新独立处理，不与视频帧共享 MessageReassembler 的 FrameId 命名空间
            byte msgType = e.Data[1];
            if (msgType == (byte)MessageType.CursorUpdate)
            {
                ProcessCursorFragment(e.Data);
                return;
            }

            // 其他消息类型走标准重组路径
            _reassembler?.OnFragment(e);
        }

        private void ProcessCursorFragment(byte[] wire)
        {
            // wire format: Magic(1)+Type(1)+PayloadLen(4)+FrameId(4)+FragIdx(2)+FragCount(2)+CRC16(2)+FragData
            // Minimum cursor payload: Visible(1)+X(4)+Y(4)+Width(4)+Height(4)+HotX(4)+HotY(4)+RgbaLen(4) = 29 bytes
            const int WireHeaderSize = 16;
            const int MinCursorPayload = 29;
            const int MaxCursorPayload = 1024 * 1024; // 1MB cursor data is generous

            // 验证分片参数：光标消息始终为单分片
            ushort fragIdx = (ushort)(wire[10] | (wire[11] << 8));
            ushort fragCount = (ushort)(wire[12] | (wire[13] << 8));
            if (fragIdx != 0 || fragCount != 1)
                return; // Multi-fragment cursor — discard

            int fragDataLen = wire.Length - WireHeaderSize;
            if (fragDataLen < MinCursorPayload || fragDataLen > MaxCursorPayload)
                return; // Payload out of bounds — discard

            // Verify CRC16
            ushort expectedCrc = (ushort)(wire[14] | (wire[15] << 8));
            ushort actualCrc = MessageReassembler.ComputeCrc16(wire, WireHeaderSize, fragDataLen);
            if (actualCrc != expectedCrc)
                return; // CRC mismatch — discard

            // Parse cursor payload
            byte[] cursorPayload = new byte[fragDataLen];
            Buffer.BlockCopy(wire, WireHeaderSize, cursorPayload, 0, fragDataLen);
            try
            {
                var msg = CursorUpdateMessage.Unpack(cursorPayload);
                ProcessCursorUpdate(msg);
            }
            catch (Exception)
            {
                // Malformed cursor data — discard silently
            }
        }

        private void OnMessageReceived(object sender, MessageReceivedEventArgs e)
        {
            if (e.MessageType == (byte)MessageType.VideoFrame)
            {
                var msg = VideoFrameMessage.Unpack(e.Data);
                ProcessVideoFrame(msg);
            }
        }

        private void ProcessVideoFrame(VideoFrameMessage msg)
        {
            if (_frameBuffer == null) return;
            if (msg.Data == null || msg.Data.Length == 0)
            {
                Logger.Warn("VideoFrame empty data: seq={0} size={1}x{2} — skipped", msg.SequenceNumber, msg.Width, msg.Height);
                return;
            }

            // Resolution change
            if (_decoder != null && (msg.Width != FrameWidth || msg.Height != FrameHeight))
            {
                Logger.Info("Resolution changed: {0}x{1} -> {2}x{3}",
                    FrameWidth, FrameHeight, msg.Width, msg.Height);
                _decoder.Reset();
                _decoder.Initialize(msg.Width, msg.Height);
                _renderTarget?.Resize(msg.Width, msg.Height);
            }

            int frameSize = msg.Width * msg.Height * 4;
            byte[] writeSlot = _frameBuffer.BorrowWriteBuffer(frameSize);
            if (writeSlot == null) return;

            if (_decoder == null)
            {
                // 解码器不可用 — 无法处理 H264 数据，丢弃此帧
                if (_frameCount == 0)
                    Logger.Error("No decoder available, cannot decode H264 frame seq={0}", msg.SequenceNumber);
                return;
            }

            var result = _decoder.Decode(msg.Data, writeSlot);
            if (result.Status != DecodeStatus.Ok)
            {
                _decodeFailures++;
                if (_decodeFailures <= 3 || _decodeFailures % 50 == 0)
                    Logger.Warn("Decode failed: status={0} seq={1} keyframe={2} dataLen={3} (total failures={4})",
                        result.Status, msg.SequenceNumber, msg.IsKeyframe, msg.Data.Length, _decodeFailures);
                return;
            }

            _frameBuffer.CommitFrame(msg.Width, msg.Height);
            Interlocked.Increment(ref _frameCount);

            if (_frameCount == 1)
                Logger.Info("FIRST frame decoded: seq={0} size={1}x{2} keyframe={3} dataLen={4}",
                    msg.SequenceNumber, msg.Width, msg.Height, msg.IsKeyframe, msg.Data.Length);
            else if (_frameCount % 100 == 0)
                Logger.Debug("Frames decoded: {0}, last seq={1} dataLen={2}", _frameCount, msg.SequenceNumber, msg.Data.Length);
        }

        private void ProcessCursorUpdate(CursorUpdateMessage msg)
        {
            if (_renderTarget == null) return;
            _renderTarget.UpdateCursor(new CursorInfo
            {
                Visible = msg.Visible,
                X = msg.X,
                Y = msg.Y,
                Width = msg.Width,
                Height = msg.Height,
                HotX = msg.HotX,
                HotY = msg.HotY,
                RgbaPixels = msg.RgbaPixels
            });
        }

        // null op — actual receiving is event-driven
        private void ReceiveLoop()
        {
            while (_running)
            {
                Thread.Sleep(100);
            }
        }

        private void RenderLoop()
        {
            while (_running)
            {
                ReadFrameRef frame;
                if (_frameBuffer != null && _frameBuffer.TryBorrowReadFrame(out frame))
                {
                    try
                    {
                        _renderTarget?.RenderFrame(frame.Pixels, frame.Width, frame.Height);
                    }
                    finally
                    {
                        _frameBuffer.ReleaseReadFrame();
                    }
                }
                else
                {
                    Thread.Sleep(5); // 无帧时等待，降低 CPU 占用
                }
            }
        }
    }
}
