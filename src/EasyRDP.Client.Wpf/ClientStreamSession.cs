using System;
using System.Threading;
using EasyRDP.Core.Protocol;
using EasyRDP.Core.Rendering;
using EasyRDP.Core.Session;
using EasyRDP.Core.Transport;

namespace EasyRDP.Client.Wpf
{
    /// <summary>
    /// 客户端视频流会话。双线程：接收线程解码→FrameBuffer，渲染线程→RenderTarget。
    /// </summary>
    public class ClientStreamSession : IClientStreamSession
    {
        private ITransportClient _transport;
        private IVideoDecoder _decoder;
        private FrameBuffer _frameBuffer;
        private IRenderTarget _renderTarget;
        private MessageReassembler _reassembler;
        private volatile bool _running;
        private Thread _receiveThread;
        private Thread _renderThread;
        private long _frameCount;

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
            _decoder = DecoderFactory.Create(codec);
            if (_decoder != null)
                _decoder.Initialize(width, height);
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
            _running = false;
            if (_transport != null)
                _transport.DataReceived -= OnDataReceived;

            _receiveThread?.Join(3000);
            _renderThread?.Join(3000);

            _decoder?.Dispose();
            _decoder = null;
            _frameBuffer?.Reset();
            _frameBuffer = null;
        }

        /// <summary>Disposes the session by stopping all activity and releasing resources.</summary>
        public void Dispose()
        {
            Stop();
        }

        private void OnDataReceived(object sender, FragmentReceivedEventArgs e)
        {
            _reassembler?.OnFragment(e);
        }

        private void OnMessageReceived(object sender, MessageReceivedEventArgs e)
        {
            if (e.MessageType == (byte)MessageType.VideoFrame)
            {
                var msg = VideoFrameMessage.Unpack(e.Data);
                ProcessVideoFrame(msg);
            }
            else if (e.MessageType == (byte)MessageType.CursorUpdate)
            {
                var msg = CursorUpdateMessage.Unpack(e.Data);
                ProcessCursorUpdate(msg);
            }
        }

        private void ProcessVideoFrame(VideoFrameMessage msg)
        {
            if (_frameBuffer == null) return;

            // Resolution change
            if (_decoder != null && (msg.Width != FrameWidth || msg.Height != FrameHeight))
            {
                _decoder.Reset();
                _decoder.Initialize(msg.Width, msg.Height);
                _renderTarget?.Resize(msg.Width, msg.Height);
            }

            int frameSize = msg.Width * msg.Height * 4;
            byte[] writeSlot = _frameBuffer.BorrowWriteBuffer(frameSize);
            if (writeSlot == null) return;

            if (_decoder != null)
            {
                var result = _decoder.Decode(msg.Data, writeSlot);
                if (result.Status != DecodeStatus.Ok)
                    return;
            }
            else
            {
                // Fallback: raw pixels
                int copyLen = Math.Min(msg.Data.Length, writeSlot.Length);
                Buffer.BlockCopy(msg.Data, 0, writeSlot, 0, copyLen);
            }

            _frameBuffer.CommitFrame(msg.Width, msg.Height);
            Interlocked.Increment(ref _frameCount);
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
                Thread.Sleep(1);
            }
        }
    }
}
