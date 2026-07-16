using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using EasyRDP.Client.Common;
using EasyRDP.Core.Protocol;
using EasyRDP.Core.Transport;
using Xunit;

namespace EasyRDP.Client.Common.Tests;

public class ConnectionManagerTests : IDisposable
{
    private readonly TcpTransportServer _server;
    private readonly int _port;
    private ConnectionManager? _client;

    public ConnectionManagerTests()
    {
        _port = FindFreePort();
        _server = new TcpTransportServer();
        _server.Start(_port);

        // Server auto-responds to handshake
        _server.MessageReceived += (s, e) =>
        {
            if (e.Message?.Header.Type == MessageType.HandshakeReq)
            {
                var res = new HandshakeResMessage
                {
                    Result = HandshakeResult.Success,
                    SessionId = e.SessionId,
                    ScreenWidth = 1920,
                    ScreenHeight = 1080,
                    CompressType = CompressType.Zlib
                };
                byte[] data = MessageCodec.Encode(MessageType.HandshakeRes, 1, res);
                _server.SendTo(e.SessionId, data);
            }
            else if (e.Message?.Header.Type == MessageType.KeepAlive)
            {
                _server.SendTo(e.SessionId,
                    MessageCodec.Encode(MessageType.KeepAliveAck, 1, new KeepAliveAckMessage()));
            }
        };
    }

    public void Dispose()
    {
        _client?.Dispose();
        _server.Dispose();
    }

    [Fact]
    public void Connect_And_Disconnect_ShouldChangeState()
    {
        _client = new ConnectionManager();
        bool ok = _client.Connect("127.0.0.1", _port, 5000, "any-token");
        Assert.True(ok);
        Assert.Equal(ConnectionState.Connected, _client.State);
        Assert.Equal(1920, _client.RemoteScreenWidth);
        Assert.Equal(1080, _client.RemoteScreenHeight);

        _client.Disconnect("test");
        Assert.Equal(ConnectionState.Disconnected, _client.State);
    }

    [Fact]
    public void Connect_ShouldFireConnectedEvent()
    {
        _client = new ConnectionManager();
        bool fired = false;
        _client.Connected += () => fired = true;

        _client.Connect("127.0.0.1", _port, 5000, "any-token");
        Assert.True(fired);
    }

    [Fact]
    public void Connect_ShouldFireConnectionFailed_OnWrongPort()
    {
        _client = new ConnectionManager();
        string? reason = null;
        _client.ConnectionFailed += r => reason = r;

        bool ok = _client.Connect("127.0.0.1", FindFreePort(), 500, "any-token");
        Assert.False(ok);
        Assert.NotNull(reason);
    }

    [Fact]
    public void Disconnect_ShouldFireDisconnectedEvent()
    {
        _client = new ConnectionManager();
        string? reason = null;
        _client.Disconnected += r => reason = r;

        _client.Connect("127.0.0.1", _port, 5000, "any-token");
        _client.Disconnect("user close");

        Assert.Equal("user close", reason);
    }

    [Fact]
    public void SendMessage_ShouldWork()
    {
        _client = new ConnectionManager();
        _client.Connect("127.0.0.1", _port, 5000, "any-token");

        bool ok = _client.SendMessage(MessageType.KeepAlive, new KeepAliveMessage());
        Assert.True(ok);
    }

    [Fact]
    public void SendMessage_AfterDisconnect_ShouldReturnFalse()
    {
        _client = new ConnectionManager();
        _client.Connect("127.0.0.1", _port, 5000, "any-token");
        _client.Disconnect("test");

        bool ok = _client.SendMessage(MessageType.KeepAlive, new KeepAliveMessage());
        Assert.False(ok);
    }

    [Fact]
    public void MessageReceived_ShouldFire()
    {
        _client = new ConnectionManager();
        var received = new ConcurrentQueue<Message>();

        _client.MessageReceived += msg => received.Enqueue(msg);
        _client.Connect("127.0.0.1", _port, 5000, "any-token");

        // Server sent HandshakeRes during connect — already consumed internally.
        // Send a KeepAlive + get Ack
        _client.SendMessage(MessageType.KeepAlive, new KeepAliveMessage());
        Thread.Sleep(500);

        Assert.False(received.IsEmpty, "Should have received KeepAliveAck");
    }

    [Fact]
    public void SeqTracker_ShouldIncrement()
    {
        _client = new ConnectionManager();
        Assert.Equal(1u, _client.SeqTracker.Next());
        Assert.Equal(2u, _client.SeqTracker.Next());
    }

    [Fact]
    public void DoubleConnect_ShouldReturnFalse()
    {
        _client = new ConnectionManager();
        _client.Connect("127.0.0.1", _port, 5000, "any-token");

        bool ok = _client.Connect("127.0.0.1", _port, 5000, "any-token");
        Assert.False(ok);
    }

    [Fact]
    public void Dispose_ShouldCleanUp()
    {
        _client = new ConnectionManager();
        _client.Connect("127.0.0.1", _port, 5000, "any-token");
        _client.Dispose();
        // Double dispose should not throw
        _client.Dispose();
        // Transport released — can't send after dispose
        Assert.False(_client.SendMessage(MessageType.KeepAlive, new KeepAliveMessage()));
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
