using System.Net;
using System.Net.Sockets;
using System.Threading;
using EasyRDP.Core.Protocol;
using EasyRDP.Core.Transport;
using Xunit;

namespace EasyRDP.Core.Tests.Transport
{
    /// <summary>UDP 端到端收发测试（真实 localhost socket，验证 UDP 分片/重组链路）。</summary>
    public class UdpRoundTripTests
    {
        [Fact]
        public void ClientToServer_SingleFragment_ShouldDeliver()
        {
            int port = GetFreePort();
            var acceptor = new UdpTransportAcceptor();
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
                var connector = new UdpTransportConnector();
                ITransport client = connector.Connect("127.0.0.1:" + port, 3000);
                Assert.NotNull(client);
                client.Start();

                byte[] payload = new byte[] { 1, 2, 3, 4, 5 };
                client.Send(Framing.BuildMessage((byte)MessageType.InputEvent, payload));

                Assert.True(serverGot.Wait(5000), "服务端未在超时内收到 UDP 消息");
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
        public void ClientToServer_MultiFragment_ShouldReassemble()
        {
            int port = GetFreePort();
            var acceptor = new UdpTransportAcceptor();
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
                var connector = new UdpTransportConnector();
                ITransport client = connector.Connect("127.0.0.1:" + port, 3000);
                Assert.NotNull(client);
                client.Start();

                // 3000 字节 → 3 个 UDP 分片（MaxFragData=1200）
                byte[] payload = new byte[3000];
                for (int i = 0; i < payload.Length; i++)
                    payload[i] = (byte)(i & 0xFF);
                client.Send(Framing.BuildMessage((byte)MessageType.VideoFrame, payload));

                Assert.True(serverGot.Wait(8000), "服务端未在超时内收到多分片 UDP 消息");
                Assert.Equal(payload, serverPayload);

                client.Disconnect();
            }
            finally
            {
                acceptor.Stop();
            }
        }

        [Fact]
        public void ServerToClient_ShouldDeliver()
        {
            int port = GetFreePort();
            var acceptor = new UdpTransportAcceptor();
            ITransport serverTransport = null;
            byte clientType = 0;
            byte[] clientPayload = null;
            var clientGot = new ManualResetEventSlim(false);

            acceptor.ClientConnected += (s, e) =>
            {
                serverTransport = e.Transport;
                e.Transport.Start();
            };
            acceptor.Start(port.ToString());

            try
            {
                var connector = new UdpTransportConnector();
                ITransport client = connector.Connect("127.0.0.1:" + port, 3000);
                Assert.NotNull(client);
                client.MessageReceived += (s, args) =>
                {
                    clientType = args.MessageType;
                    clientPayload = args.Data;
                    clientGot.Set();
                };
                client.Start();

                // 客户端先发一个"探针"消息，让服务端建立该对端的 transport
                client.Send(Framing.BuildMessage((byte)MessageType.Keepalive, new byte[] { 0xAA }));

                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (serverTransport == null && sw.ElapsedMilliseconds < 3000)
                    Thread.Sleep(10);
                Assert.NotNull(serverTransport);

                byte[] payload = new byte[] { 9, 8, 7 };
                serverTransport.Send(Framing.BuildMessage((byte)MessageType.Keepalive, payload));

                Assert.True(clientGot.Wait(5000), "客户端未在超时内收到 UDP 回包");
                Assert.Equal((byte)MessageType.Keepalive, clientType);
                Assert.Equal(payload, clientPayload);

                client.Disconnect();
            }
            finally
            {
                acceptor.Stop();
            }
        }

        private static int GetFreePort()
        {
            // UDP 用 UdpClient 探测空闲端口
            var u = new UdpClient(0);
            int p = ((IPEndPoint)u.Client.LocalEndPoint).Port;
            u.Close();
            return p;
        }
    }
}
