using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using EasyRDP.Core.Protocol;
using EasyRDP.Core.Transport;
using Xunit;
using Xunit.Abstractions;

namespace EasyRDP.Core.Tests.Transport;

public class TcpTransportTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private TcpTransportServer? _server;
    private TcpTransportClient? _client;
    private readonly ConcurrentQueue<Message> _serverReceived = new();
    private readonly ConcurrentQueue<Message> _clientReceived = new();
    private readonly List<ConnectionEventArgs> _serverConnections = new();
    private readonly List<ConnectionEventArgs> _serverDisconnections = new();
    private volatile bool _clientDisconnected;
    private int _port;

    public TcpTransportTests(ITestOutputHelper output)
    {
        _output = output;
        _port = FindFreePort();
    }

    public void Dispose()
    {
        _client?.Dispose();
        _server?.Dispose();
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private void StartServer()
    {
        _server = new TcpTransportServer();
        _server.OnLog = (level, msg) => _output.WriteLine($"[SVR:{level}] {msg}");
        _server.ClientConnected += (s, e) =>
        {
            lock (_serverConnections) _serverConnections.Add(e);
        };
        _server.ClientDisconnected += (s, e) =>
        {
            lock (_serverDisconnections) _serverDisconnections.Add(e);
        };
        _server.MessageReceived += (s, e) =>
        {
            if (e.Message != null) _serverReceived.Enqueue(e.Message);
        };
        _server.Start(_port);
    }

    private TcpTransportClient ConnectClient(string? host = null, int timeoutMs = 5000)
    {
        var client = new TcpTransportClient();
        client.OnLog = (level, msg) => _output.WriteLine($"[CLI:{level}] {msg}");
        client.MessageReceived += (s, e) =>
        {
            if (e.Message != null) _clientReceived.Enqueue(e.Message);
        };
        client.Disconnected += (s, e) => _clientDisconnected = true;

        bool ok = client.Connect(host ?? "127.0.0.1", _port, timeoutMs);
        Assert.True(ok, "TCP client should connect successfully");
        _client = client;
        return client;
    }

    private void DrainQueues()
    {
        while (_serverReceived.TryDequeue(out _)) { }
        while (_clientReceived.TryDequeue(out _)) { }
        _clientDisconnected = false;
        lock (_serverConnections) _serverConnections.Clear();
        lock (_serverDisconnections) _serverDisconnections.Clear();
    }

    // ── Basic connectivity ──────────────────────────────

    [Fact]
    public void StartStop_Server_ShouldWork()
    {
        StartServer();
        // Stop via Dispose
        _server!.Dispose();
        _server = null;
    }

    [Fact]
    public void Connect_ShouldRaiseServerConnectionEvent()
    {
        StartServer();
        ConnectClient();

        Thread.Sleep(100);

        lock (_serverConnections)
            Assert.Single(_serverConnections);
    }

    [Fact]
    public void Disconnect_ShouldRaiseEvents()
    {
        StartServer();
        var client = ConnectClient();
        Thread.Sleep(100);

        _clientDisconnected = false;
        client.Disconnect();

        // Wait for server-side disconnect event with retry (TCP close detection takes time)
        bool serverGotDisconnect = SpinWait.SpinUntil(() =>
        {
            lock (_serverDisconnections) return _serverDisconnections.Count > 0;
        }, 3000);

        Assert.True(_clientDisconnected, "Client should fire Disconnected");
        Assert.True(serverGotDisconnect, "Server should fire ClientDisconnected");
        lock (_serverDisconnections)
            Assert.Single(_serverDisconnections);
    }

    [Fact]
    public void IsConnected_ShouldReflectState()
    {
        StartServer();
        var client = ConnectClient();
        Assert.True(client.IsConnected);

        client.Disconnect();
        Thread.Sleep(100);
        Assert.False(client.IsConnected);
    }

    // ── Message send/receive ────────────────────────────

    [Fact]
    public void ClientToServer_SingleMessage_ShouldArrive()
    {
        StartServer();
        var client = ConnectClient();
        Thread.Sleep(100);
        DrainQueues();

        var msg = MessageCodec.Encode(MessageType.KeepAlive, 1, new KeepAliveMessage());
        bool sent = client.Send(msg);
        Assert.True(sent);

        Thread.Sleep(300);
        Assert.True(_serverReceived.TryDequeue(out var received), "Server should receive message");
        Assert.Equal(MessageType.KeepAlive, received.Header.Type);
    }

    [Fact]
    public void ServerToClient_SingleMessage_ShouldArrive()
    {
        StartServer();
        ConnectClient();
        Thread.Sleep(100);
        DrainQueues();

        // Server sends to session 1
        var msg = MessageCodec.Encode(MessageType.KeepAliveAck, 1, new KeepAliveAckMessage());
        _server!.SendTo(1, msg);

        Thread.Sleep(300);
        Assert.True(_clientReceived.TryDequeue(out var received), "Client should receive message");
        Assert.Equal(MessageType.KeepAliveAck, received.Header.Type);
    }

    [Fact]
    public void MultipleMessages_ShouldArriveInOrder()
    {
        StartServer();
        var client = ConnectClient();
        Thread.Sleep(100);
        DrainQueues();

        for (int i = 0; i < 20; i++)
        {
            var msg = MessageCodec.Encode(MessageType.KeepAlive, (uint)(i + 1), new KeepAliveMessage());
            Assert.True(client.Send(msg));
        }

        Thread.Sleep(500);

        uint lastSeq = 0;
        while (_serverReceived.TryDequeue(out var msg))
        {
            Assert.True(msg.Header.Sequence > lastSeq, "Messages should arrive in order");
            lastSeq = msg.Header.Sequence;
        }
        Assert.True(lastSeq > 0, "Should have received messages");
    }

    [Fact]
    public void ConcurrentSend_ShouldNotCorrupt()
    {
        StartServer();
        var client = ConnectClient();
        Thread.Sleep(100);
        DrainQueues();

        int count = 50;
        var tasks = new List<Task>();
        for (int i = 0; i < count; i++)
        {
            int n = i;
            tasks.Add(Task.Run(() =>
            {
                var msg = MessageCodec.Encode(MessageType.KeepAlive, (uint)(n + 1), new KeepAliveMessage());
                client.Send(msg);
            }));
        }
        Task.WaitAll(tasks.ToArray());

        Thread.Sleep(500);

        int received = 0;
        while (_serverReceived.TryDequeue(out _)) received++;
        Assert.Equal(count, received);
    }

    // ── Handshake round-trip ────────────────────────────

    [Fact]
    public void HandshakeRoundTrip_ShouldSucceed()
    {
        StartServer();
        var client = ConnectClient();
        Thread.Sleep(100);
        DrainQueues();

        // Client sends HandshakeReq
        var req = new HandshakeReqMessage
        {
            AuthToken = "test-token",
            ScreenWidth = 1920,
            ScreenHeight = 1080,
            CompressType = CompressType.Zlib
        };
        byte[] reqData = MessageCodec.Encode(MessageType.HandshakeReq, 1, req);
        Assert.True(client.Send(reqData));

        Thread.Sleep(200);

        Assert.True(_serverReceived.TryDequeue(out var received), "Server should receive HandshakeReq");
        Assert.IsType<HandshakeReqMessage>(received.Body);

        // Server sends HandshakeRes
        var res = new HandshakeResMessage
        {
            Result = HandshakeResult.Success,
            SessionId = 1,
            ScreenWidth = 1920,
            ScreenHeight = 1080,
            CompressType = CompressType.Zlib
        };
        byte[] resData = MessageCodec.Encode(MessageType.HandshakeRes, 1, res);
        _server!.SendTo(1, resData);

        Thread.Sleep(200);

        Assert.True(_clientReceived.TryDequeue(out var clientMsg), "Client should receive HandshakeRes");
        Assert.IsType<HandshakeResMessage>(clientMsg.Body);
        var handshakeRes = (HandshakeResMessage)clientMsg.Body;
        Assert.Equal(HandshakeResult.Success, handshakeRes.Result);
    }

    // ── Multi-client ────────────────────────────────────

    [Fact]
    public void MultipleClients_ShouldHaveUniqueSessionIds()
    {
        StartServer();

        var client1 = new TcpTransportClient();
        client1.OnLog = (l, m) => _output.WriteLine($"[C1:{l}] {m}");
        client1.Connect("127.0.0.1", _port, 5000);

        var client2 = new TcpTransportClient();
        client2.OnLog = (l, m) => _output.WriteLine($"[C2:{l}] {m}");
        client2.Connect("127.0.0.1", _port, 5000);

        Thread.Sleep(300);

        lock (_serverConnections)
        {
            Assert.Equal(2, _serverConnections.Count);
            Assert.NotEqual(_serverConnections[0].SessionId, _serverConnections[1].SessionId);
        }

        client1.Dispose();
        client2.Dispose();
    }

    [Fact]
    public void ServerDisconnectClient_ShouldRemoveSession()
    {
        StartServer();
        ConnectClient();
        Thread.Sleep(100);

        _server!.Disconnect(1);
        Thread.Sleep(200);

        Assert.True(_clientDisconnected, "Client should be disconnected when server disconnects it");
    }

    // ── Error handling ──────────────────────────────────

    [Fact]
    public void Connect_InvalidHost_ShouldReturnFalse()
    {
        var client = new TcpTransportClient();
        bool ok = client.Connect("invalid-host-xyz-12345", _port, 1000);
        Assert.False(ok, "Connect to invalid host should fail");
        client.Dispose();
    }

    [Fact]
    public void Connect_WrongPort_ShouldReturnFalse()
    {
        var client = new TcpTransportClient();
        // Use a port that should not be in use
        bool ok = client.Connect("127.0.0.1", FindFreePort(), 500);
        Assert.False(ok, "Connect to port with no listener should fail");
        client.Dispose();
    }

    [Fact]
    public void Send_AfterDisconnect_ShouldReturnFalse()
    {
        StartServer();
        var client = ConnectClient();
        Thread.Sleep(100);

        client.Disconnect();
        Thread.Sleep(100);

        var msg = MessageCodec.Encode(MessageType.KeepAlive, 1, new KeepAliveMessage());
        Assert.False(client.Send(msg));
    }

    [Fact]
    public void Send_NullData_ShouldNotCrash()
    {
        StartServer();
        var client = ConnectClient();
        Thread.Sleep(100);

        // Send should handle gracefully — method signature takes byte[],
        // but in practice null would be a coding error. We test it won't crash.
        Assert.False(client.Send(null!));
    }

    [Fact]
    public void Disconnect_ShouldBeIdempotent()
    {
        StartServer();
        var client = ConnectClient();
        Thread.Sleep(100);

        client.Disconnect();
        client.Disconnect();
        // Should not throw
    }

    [Fact]
    public void Dispose_ShouldBeIdempotent()
    {
        StartServer();
        var client = ConnectClient();
        Thread.Sleep(100);

        client.Dispose();
        client.Dispose();
        // Should not throw
    }

    [Fact]
    public void ServerStop_ShouldNotifyAllClients()
    {
        StartServer();
        var client1 = ConnectClient();
        var client2 = new TcpTransportClient();
        client2.OnLog = (l, m) => _output.WriteLine($"[C2:{l}] {m}");
        bool c2disc = false;
        client2.Disconnected += (s, e) => c2disc = true;
        client2.Connect("127.0.0.1", _port, 5000);

        Thread.Sleep(200);

        _server!.Stop();
        Thread.Sleep(300);

        Assert.True(_clientDisconnected, "Client 1 should be disconnected");
        Assert.True(c2disc, "Client 2 should be disconnected");

        lock (_serverDisconnections)
            Assert.Equal(2, _serverDisconnections.Count);

        client2.Dispose();
    }

    // ── Options ──────────────────────────────────────────

    [Fact]
    public void Options_CustomBufferSize_ShouldBeApplied()
    {
        var options = new TcpTransportOptions { ReceiveBufferSize = 16384, SendBufferSize = 32768 };
        StartServer(options);
        var client = new TcpTransportClient(options);
        client.OnLog = (l, m) => _output.WriteLine($"[CLI:{l}] {m}");
        bool ok = client.Connect("127.0.0.1", _port, 5000);
        Assert.True(ok);
        Assert.True(client.IsConnected);
        client.Dispose();
    }

    [Fact]
    public void Options_Null_ShouldUseDefaults()
    {
        var server = new TcpTransportServer(null!);
        var client = new TcpTransportClient(null!);
        // Should not throw — uses Default internally
        server.Dispose();
        client.Dispose();
    }

    [Fact]
    public void Options_ServerMaxClients_ShouldReject()
    {
        var options = new TcpTransportOptions { MaxClients = 1 };
        StartServer(options);

        var c1 = new TcpTransportClient();
        c1.OnLog = (l, m) => _output.WriteLine($"[C1:{l}] {m}");
        c1.Connect("127.0.0.1", _port, 5000);
        Thread.Sleep(100);

        var c2 = new TcpTransportClient();
        bool c2disc = false;
        c2.Disconnected += (s, e) => c2disc = true;
        c2.Connect("127.0.0.1", _port, 5000);
        Thread.Sleep(200);

        // 第二个连接应该被拒绝——服务端主动关闭
        Assert.False(c2.IsConnected);
        c1.Dispose();
        c2.Dispose();
    }

    private void StartServer(TcpTransportOptions options)
    {
        _server = new TcpTransportServer(options);
        _server.OnLog = (level, msg) => _output.WriteLine($"[SVR:{level}] {msg}");
        _server.ClientConnected += (s, e) => { lock (_serverConnections) _serverConnections.Add(e); };
        _server.ClientDisconnected += (s, e) => { lock (_serverDisconnections) _serverDisconnections.Add(e); };
        _server.MessageReceived += (s, e) => { if (e.Message != null) _serverReceived.Enqueue(e.Message); };
        _server.Start(_port);
    }
}
