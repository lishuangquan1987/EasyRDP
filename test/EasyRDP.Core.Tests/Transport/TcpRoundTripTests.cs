using System.Net;
using System.Net.Sockets;
using System.Threading;
using EasyRDP.Core.Protocol;
using EasyRDP.Core.Transport;
using Xunit;

namespace EasyRDP.Core.Tests.Transport
{
    /// <summary>TCP 端到端收发测试（真实 localhost socket，验证 Connector/Acceptor/Transport 收发链路）。</summary>
    public class TcpRoundTripTests
    {
        [Fact]
        public void ClientToServer_ShouldDeliverMessage()
        {
            int port = GetFreePort();
            var acceptor = new TcpTransportAcceptor();
            byte serverType = 0;
            byte[] serverPayload = null;
            var serverGot = new ManualResetEventSlim(false);

            acceptor.ClientConnected += (s, e) =>
            {
                e.Transport.MessageReceived += (s2, args) =>
                {
                    serverType = args.MessageType;
                    serverPayload = args.Data;
                    serverGot.Set();
                };
                e.Transport.Start();
            };
            acceptor.Start(port.ToString());

            try
            {
                var connector = new TcpTransportConnector();
                ITransport client = connector.Connect("127.0.0.1:" + port, 3000);
                Assert.NotNull(client);
                client.Start();

                byte[] payload = new byte[] { 1, 2, 3, 4, 5 };
                client.Send(Framing.BuildMessage((byte)MessageType.InputEvent, payload));

                Assert.True(serverGot.Wait(5000), "服务端未在超时内收到消息");
                Assert.Equal((byte)MessageType.InputEvent, serverType);
                Assert.Equal(payload, serverPayload);

                client.Disconnect();
            }
            finally
            {
                acceptor.Stop();
            }
        }

        [Fact]
        public void ServerToClient_ShouldDeliverMessage()
        {
            int port = GetFreePort();
            var acceptor = new TcpTransportAcceptor();
            ITransport serverTransport = null;
            var clientGot = new ManualResetEventSlim(false);
            byte clientType = 0;
            byte[] clientPayload = null;

            acceptor.ClientConnected += (s, e) =>
            {
                serverTransport = e.Transport;
                e.Transport.Start();
            };
            acceptor.Start(port.ToString());

            try
            {
                var connector = new TcpTransportConnector();
                ITransport client = connector.Connect("127.0.0.1:" + port, 3000);
                Assert.NotNull(client);
                client.MessageReceived += (s, args) =>
                {
                    clientType = args.MessageType;
                    clientPayload = args.Data;
                    clientGot.Set();
                };
                client.Start();

                // 等服务端 transport 就绪
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (serverTransport == null && sw.ElapsedMilliseconds < 3000)
                    Thread.Sleep(10);
                Assert.NotNull(serverTransport);

                byte[] payload = new byte[] { 9, 8, 7 };
                serverTransport.Send(Framing.BuildMessage((byte)MessageType.Keepalive, payload));

                Assert.True(clientGot.Wait(5000), "客户端未在超时内收到消息");
                Assert.Equal((byte)MessageType.Keepalive, clientType);
                Assert.Equal(payload, clientPayload);

                client.Disconnect();
            }
            finally
            {
                acceptor.Stop();
            }
        }

        [Fact]
        public void LargeMessage_ShouldRoundTripIntact()
        {
            int port = GetFreePort();
            var acceptor = new TcpTransportAcceptor();
            byte[] serverPayload = null;
            var serverGot = new ManualResetEventSlim(false);

            acceptor.ClientConnected += (s, e) =>
            {
                e.Transport.MessageReceived += (s2, args) =>
                {
                    serverPayload = args.Data;
                    serverGot.Set();
                };
                e.Transport.Start();
            };
            acceptor.Start(port.ToString());

            try
            {
                var connector = new TcpTransportConnector();
                ITransport client = connector.Connect("127.0.0.1:" + port, 3000);
                Assert.NotNull(client);
                client.Start();

                // 1MB 消息（跨多个 TCP 包，验证 MessageFramingBuffer 粘包/拆包正确）
                byte[] payload = new byte[1024 * 1024];
                for (int i = 0; i < payload.Length; i++)
                    payload[i] = (byte)(i & 0xFF);
                client.Send(Framing.BuildMessage((byte)MessageType.VideoFrame, payload));

                Assert.True(serverGot.Wait(8000), "服务端未在超时内收到大消息");
                Assert.Equal(payload, serverPayload);

                client.Disconnect();
            }
            finally
            {
                acceptor.Stop();
            }
        }

        private static int GetFreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int p = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }
    }
}
