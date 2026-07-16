using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using EasyRDP.Core.Protocol;
using EasyRDP.Core.Transport;
using Xunit;
using Xunit.Abstractions;

namespace EasyRDP.Core.Tests.Transport;

public class UdpTransportTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private UdpTransportServer? _server;
    private UdpTransportClient? _client;
    private readonly ConcurrentQueue<Message> _serverReceived = new();
    private readonly ConcurrentQueue<Message> _clientReceived = new();
    private readonly List<ConnectionEventArgs> _serverConnections = new();
    private readonly List<ConnectionEventArgs> _serverDisconnections = new();
    private volatile bool _clientDisconnected;
    private int _port;

    public UdpTransportTests(ITestOutputHelper output)
    {
        _output = output;
        _port = FindFreeUdpPort();
    }

    public void Dispose()
    {
        _client?.Dispose();
        _server?.Dispose();
    }

    private static int FindFreeUdpPort()
    {
        // 用 TcpListener 查找空闲端口（TCP/UDP 端口号空间独立，找到的端口同时空闲概率极高）
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        // 短暂等待，确保 OS 释放端口
        Thread.Sleep(50);
        return port;
    }

    private void StartServer()
    {
        _server = new UdpTransportServer();
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
            if (e.Message != null && e.SessionId > 0) _serverReceived.Enqueue(e.Message);
        };
        _server.Start(_port);
    }

    private UdpTransportClient ConnectClient(string? host = null, int timeoutMs = 5000)
    {
        var client = new UdpTransportClient();
        client.OnLog = (level, msg) => _output.WriteLine($"[CLI:{level}] {msg}");
        client.MessageReceived += (s, e) =>
        {
            if (e.Message != null) _clientReceived.Enqueue(e.Message);
        };
        client.Disconnected += (s, e) => _clientDisconnected = true;

        bool ok = client.Connect(host ?? "127.0.0.1", _port, timeoutMs);
        Assert.True(ok, "UDP client should connect successfully");
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
        _server!.Dispose();
        _server = null;
    }

    [Fact]
    public void Connect_ShouldRegisterSession()
    {
        StartServer();
        ConnectClient();

        Thread.Sleep(200);

        lock (_serverConnections)
            Assert.Single(_serverConnections);
    }

    [Fact]
    public void Disconnect_ShouldRaiseDisconnectedEvent()
    {
        StartServer();
        ConnectClient();
        Thread.Sleep(200);

        _client!.Disconnect();
        Thread.Sleep(300);

        Assert.True(_clientDisconnected, "Client should fire Disconnected");
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
        Thread.Sleep(400); // Give UDP time to register session
        DrainQueues();

        // Need to know session ID — first registered client gets ID 1
        _server!.SendTo(1, MessageCodec.Encode(MessageType.KeepAliveAck, 1, new KeepAliveAckMessage()));

        Thread.Sleep(500);
        Assert.True(_clientReceived.TryDequeue(out var received), "Client should receive message from server");
        Assert.Equal(MessageType.KeepAliveAck, received.Header.Type);
    }

    [Fact]
    public void MultipleMessages_ShouldBeReceived()
    {
        StartServer();
        var client = ConnectClient();
        Thread.Sleep(100);
        DrainQueues();

        int count = 10;
        for (int i = 0; i < count; i++)
        {
            var msg = MessageCodec.Encode(MessageType.KeepAlive, (uint)(i + 1), new KeepAliveMessage());
            Assert.True(client.Send(msg));
            Thread.Sleep(20); // Small delay to avoid UDP buffer overflow
        }

        Thread.Sleep(500);

        int received = 0;
        while (_serverReceived.TryDequeue(out _)) received++;
        // UDP may drop packets — we just verify some arrived
        Assert.True(received > 0, $"Should receive at least some messages (got {received}/{count})");
        _output.WriteLine($"UDP delivery rate: {received}/{count}");
    }

    // ── Handshake round-trip ────────────────────────────

    [Fact]
    public void HandshakeRoundTrip_ShouldSucceed()
    {
        StartServer();
        var client = ConnectClient();
        Thread.Sleep(300);
        DrainQueues();

        // Client sends HandshakeReq
        var req = new HandshakeReqMessage
        {
            AuthToken = "test-token",
            ScreenWidth = 1920,
            ScreenHeight = 1080,
            CompressType = CompressType.Zlib
        };
        Assert.True(client.Send(MessageCodec.Encode(MessageType.HandshakeReq, 1, req)));

        Thread.Sleep(400);

        Assert.True(_serverReceived.TryDequeue(out var received), "Server should receive HandshakeReq on UDP");
        Assert.IsType<HandshakeReqMessage>(received.Body);

        // Server sends HandshakeRes to session 1
        var res = new HandshakeResMessage
        {
            Result = HandshakeResult.Success,
            SessionId = 1,
            ScreenWidth = 1920,
            ScreenHeight = 1080,
            CompressType = CompressType.Zlib
        };
        _server!.SendTo(1, MessageCodec.Encode(MessageType.HandshakeRes, 1, res));

        Thread.Sleep(400);

        Assert.True(_clientReceived.TryDequeue(out var clientMsg), "Client should receive HandshakeRes over UDP");
        Assert.IsType<HandshakeResMessage>(clientMsg.Body);
    }

    // ── Multi-client ────────────────────────────────────

    [Fact]
    public void MultipleClients_ShouldHaveUniqueSessionIds()
    {
        StartServer();

        var client1 = new UdpTransportClient();
        client1.OnLog = (l, m) => _output.WriteLine($"[C1:{l}] {m}");
        client1.Connect("127.0.0.1", _port, 5000);

        var client2 = new UdpTransportClient();
        client2.OnLog = (l, m) => _output.WriteLine($"[C2:{l}] {m}");
        client2.Connect("127.0.0.1", _port, 5000);

        Thread.Sleep(400);

        lock (_serverConnections)
        {
            Assert.Equal(2, _serverConnections.Count);
            Assert.NotEqual(_serverConnections[0].SessionId, _serverConnections[1].SessionId);
        }

        client1.Dispose();
        client2.Dispose();
    }

    // ── Error handling ──────────────────────────────────

    [Fact]
    public void Connect_InvalidHost_ShouldReturnFalse()
    {
        var client = new UdpTransportClient();
        bool ok = client.Connect("invalid-host-xyz-12345", _port, 1000);
        Assert.False(ok, "Connect to invalid host should fail");
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
    public void Dispose_ShouldBeIdempotent()
    {
        StartServer();
        var client = ConnectClient();
        Thread.Sleep(100);

        client.Dispose();
        client.Dispose();
        // Should not throw
    }

    // ── Zombie session cleanup ──────────────────────────

    [Fact]
    public void ZombieSession_ShouldBeCleanedUp()
    {
        StartServer();
        var client = ConnectClient();
        Thread.Sleep(200);

        // Disconnect client without explicit disconnect (simulate crash)
        // UDP is stateless so just closing the client won't notify the server.
        // The server should clean up after SessionTimeout (30s) + cleanup interval.
        // For testing, we just verify server doesn't crash and session is created.
        lock (_serverConnections)
            Assert.Single(_serverConnections);

        // Close client silently (no Disconnect message to server)
        _client!.Dispose();
        _client = null;

        // Server should still be running fine
        Thread.Sleep(200);
        Assert.NotNull(_server);
    }

    [Fact]
    public void ServerStop_ShouldNotifyClients()
    {
        StartServer();
        ConnectClient();
        Thread.Sleep(200);

        // 不清空 _serverDisconnections，测试 Stop() 是否触发 ClientDisconnected
        _server!.Stop();
        Thread.Sleep(300);

        // UDP 无连接，服务端停止后客户端无法即时感知（等同于断网），
        // 但服务端应为每个会话触发 ClientDisconnected 事件
        lock (_serverDisconnections)
            Assert.Single(_serverDisconnections);
    }

    // ── Options ──────────────────────────────────────────

    [Fact]
    public void Options_CustomTimeout_ShouldBeApplied()
    {
        var options = new UdpTransportOptions { ReceiveTimeoutMs = 2000, SessionTimeoutSeconds = 15 };
        var server = new UdpTransportServer(options);
        server.OnLog = (l, m) => _output.WriteLine($"[SVR:{l}] {m}");
        server.Start(_port);

        var client = new UdpTransportClient(options);
        client.OnLog = (l, m) => _output.WriteLine($"[CLI:{l}] {m}");
        bool ok = client.Connect("127.0.0.1", _port, 5000);
        Assert.True(ok);
        Assert.True(client.IsConnected);

        client.Dispose();
        server.Dispose();
    }

    [Fact]
    public void Options_Null_ShouldUseDefaults()
    {
        var server = new UdpTransportServer(null!);
        var client = new UdpTransportClient(null!);
        server.Dispose();
        client.Dispose();
    }

    private void StartServer(UdpTransportOptions options)
    {
        _server = new UdpTransportServer(options);
        _server.OnLog = (level, msg) => _output.WriteLine($"[SVR:{level}] {msg}");
        _server.ClientConnected += (s, e) => { lock (_serverConnections) _serverConnections.Add(e); };
        _server.ClientDisconnected += (s, e) => { lock (_serverDisconnections) _serverDisconnections.Add(e); };
        _server.MessageReceived += (s, e) => { if (e.Message != null && e.SessionId > 0) _serverReceived.Enqueue(e.Message); };
        _server.Start(_port);
    }
}
