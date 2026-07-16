using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using EasyRDP.Client.Common;
using EasyRDP.Core.Protocol;
using EasyRDP.Core.Transport;
using Xunit;

namespace EasyRDP.Client.Common.Tests;

public class ClientServerIntegrationTests : IDisposable
{
    private readonly TcpTransportServer _server;
    private readonly int _port;
    private ConnectionManager? _client;

    public ClientServerIntegrationTests()
    {
        _port = FindFreePort();
        _server = new TcpTransportServer();

        // Server auto-responds
        _server.MessageReceived += (s, e) =>
        {
            var msg = e.Message;
            if (msg == null || msg.Body == null) return;

            switch (msg.Header.Type)
            {
                case MessageType.HandshakeReq:
                    var res = new HandshakeResMessage
                    {
                        Result = HandshakeResult.Success,
                        SessionId = e.SessionId,
                        ScreenWidth = 1920,
                        ScreenHeight = 1080,
                        CompressType = CompressType.Zlib
                    };
                    _server.SendTo(e.SessionId, MessageCodec.Encode(MessageType.HandshakeRes, 1, res));
                    break;

                case MessageType.KeepAlive:
                    _server.SendTo(e.SessionId, MessageCodec.Encode(MessageType.KeepAliveAck, 1, new KeepAliveAckMessage()));
                    break;

                case MessageType.InputEvent:
                    // Echo back as a ScreenFrame for verification
                    var fullFrame = new ScreenFrameMessage
                    {
                        FrameType = FrameType.Full,
                        Compress = CompressType.None,
                        Rects = new[] { new ScreenRect { X = 0, Y = 0, Width = 100, Height = 100, Offset = 0 } },
                        Pixels = new byte[100 * 100 * 4] // black frame
                    };
                    _server.SendTo(e.SessionId, MessageCodec.Encode(MessageType.ScreenFrame, 1, fullFrame));
                    break;
            }
        };

        _server.Start(_port);
    }

    public void Dispose()
    {
        _client?.Dispose();
        _server.Dispose();
    }

    [Fact]
    public void Connect_And_SendMessage_ShouldWork()
    {
        _client = new ConnectionManager();
        Assert.True(_client.Connect("127.0.0.1", _port, 5000, "token"));
        Assert.Equal(ConnectionState.Connected, _client.State);
    }

    [Fact]
    public void FrameBuffer_ProcessFullFrame_FromServer()
    {
        var fb = new FrameBuffer();
        _client = new ConnectionManager();
        _client.Connect("127.0.0.1", _port, 5000, "token");

        // Send input to trigger server to send a ScreenFrame
        var encoder = new InputEncoder();
        byte[] inputData = encoder.EncodeMouseMove(_client.SeqTracker.Next(), true, 100, 200);
        _client.SendMessage(MessageType.InputEvent,
            ((InputEventMessage)MessageCodec.Decode(inputData)!.Body)!);

        // Wait for ScreenFrame response
        var received = new ConcurrentQueue<ScreenFrameMessage>();
        _client.MessageReceived += msg =>
        {
            if (msg.Body is ScreenFrameMessage sf) received.Enqueue(sf);
        };

        SpinWait.SpinUntil(() => !received.IsEmpty, 3000);
        Assert.True(received.TryDequeue(out var frame));
        Assert.Equal(FrameType.Full, frame.FrameType);
        Assert.Equal(100, frame.Rects[0].Width);
        Assert.Equal(100, frame.Rects[0].Height);

        // Process in FrameBuffer
        fb.ProcessFrame(frame);
        Assert.True(fb.TryGetFrame(out _, out int w, out int h));
        Assert.Equal(100, w);
        Assert.Equal(100, h);
    }

    [Fact]
    public void InputEncoder_RoundTrip_ThroughConnection()
    {
        _client = new ConnectionManager();
        _client.Connect("127.0.0.1", _port, 5000, "token");

        var encoder = new InputEncoder();
        byte[] data = encoder.EncodeMouseMove(_client.SeqTracker.Next(), true, 500, 600);
        Assert.True(_client.SendMessage(MessageType.InputEvent,
            ((InputEventMessage)MessageCodec.Decode(data)!.Body)!));
    }

    [Fact]
    public void KeepAliveEngine_ShouldReset_OnAck()
    {
        var engine = new KeepAliveEngine(200, 1000);
        _client = new ConnectionManager();
        _client.Connect("127.0.0.1", _port, 5000, "token");

        bool timedOut = false;
        engine.Timeout += () => timedOut = true;

        engine.Start(() => _client.SendMessage(MessageType.KeepAlive, new KeepAliveMessage()));

        // Wait for acks to prevent timeout
        _client.MessageReceived += msg =>
        {
            if (msg.Body is KeepAliveAckMessage)
                engine.OnAckReceived();
        };

        Thread.Sleep(1500);
        Assert.False(timedOut, "Should not timeout when acks are received");

        engine.Stop();
    }

    [Fact]
    public void ClipboardSyncEngine_Cooldown_ShouldSuppress()
    {
        var engine = new ClipboardSyncEngine();
        _client = new ConnectionManager();
        _client.Connect("127.0.0.1", _port, 5000, "token");

        // Simulate remote clipboard received
        engine.OnRemoteClipboard(new ClipboardDataMessage
        {
            Format = ClipboardFormat.UnicodeText,
            Text = "remote text"
        });

        // Local change during cooldown should be suppressed
        byte[] data = engine.TryEncodeLocalChange("local text", _client.SeqTracker.Next());
        Assert.Null(data);
    }

    private static int FindFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start(); int p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        return p;
    }
}
