using EasyRDP.Core.Protocol;
using EasyRDP.Core.Transport;
using Xunit;

namespace EasyRDP.Core.Tests.Transport
{
    public class Crc16Tests
    {
        [Fact]
        public void KnownValue_ShouldMatch()
        {
            // CRC-16/XMODEM 对 "123456789" 的已知值为 0x31C3
            byte[] data = System.Text.Encoding.ASCII.GetBytes("123456789");
            Assert.Equal(0x31C3, Crc16.Compute(data, 0, data.Length));
        }

        [Fact]
        public void Empty_ShouldBeZero()
        {
            Assert.Equal(0, Crc16.Compute(new byte[0], 0, 0));
        }
    }

    public class UdpReassemblerTests
    {
        [Fact]
        public void SingleDatagram_ShouldAssemble()
        {
            var r = new UdpReassembler();
            byte type = 0;
            byte[] payload = null;
            r.MessageAssembled += (t, p) => { type = t; payload = p; };

            byte[] data = new byte[] { 1, 2, 3, 4, 5 };
            byte[] dg = UdpTransport.BuildDatagram(1, 0, 1, (byte)MessageType.InputEvent, (uint)data.Length, data);
            r.OnDatagram(dg);

            Assert.Equal((byte)MessageType.InputEvent, type);
            Assert.Equal(data, payload);
        }

        [Fact]
        public void MultiFragment_ShouldReassemble()
        {
            var r = new UdpReassembler();
            byte type = 0;
            byte[] payload = null;
            r.MessageAssembled += (t, p) => { type = t; payload = p; };

            byte[] data = new byte[3000]; // 3 片（MaxFragData=1200）
            for (int i = 0; i < data.Length; i++) data[i] = (byte)(i & 0xFF);

            int fragCount = 3;
            for (int i = 0; i < fragCount; i++)
            {
                int offset = i * UdpTransport.MaxFragData;
                int len = System.Math.Min(UdpTransport.MaxFragData, data.Length - offset);
                byte[] frag = new byte[len];
                System.Buffer.BlockCopy(data, offset, frag, 0, len);
                byte[] dg = UdpTransport.BuildDatagram(7, (ushort)i, (ushort)fragCount, (byte)MessageType.VideoFrame, (uint)data.Length, frag);
                r.OnDatagram(dg);
            }

            Assert.Equal((byte)MessageType.VideoFrame, type);
            Assert.Equal(data, payload);
        }

        [Fact]
        public void OutOfOrderFragments_ShouldReassemble()
        {
            var r = new UdpReassembler();
            byte[] payload = null;
            r.MessageAssembled += (t, p) => { payload = p; };

            byte[] data = new byte[2500];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)(i & 0xFF);

            // 先喂第 2 片，再喂第 0、1 片
            int fragCount = 3;
            byte[] frag2 = Slice(data, 2 * UdpTransport.MaxFragData, UdpTransport.MaxFragData);
            byte[] frag0 = Slice(data, 0, UdpTransport.MaxFragData);
            byte[] frag1 = Slice(data, UdpTransport.MaxFragData, UdpTransport.MaxFragData);

            r.OnDatagram(UdpTransport.BuildDatagram(9, 2, (ushort)fragCount, (byte)MessageType.VideoFrame, (uint)data.Length, frag2));
            r.OnDatagram(UdpTransport.BuildDatagram(9, 0, (ushort)fragCount, (byte)MessageType.VideoFrame, (uint)data.Length, frag0));
            r.OnDatagram(UdpTransport.BuildDatagram(9, 1, (ushort)fragCount, (byte)MessageType.VideoFrame, (uint)data.Length, frag1));

            Assert.Equal(data, payload);
        }

        [Fact]
        public void CorruptDatagram_ShouldBeDropped()
        {
            var r = new UdpReassembler();
            bool assembled = false;
            r.MessageAssembled += (t, p) => { assembled = true; };

            byte[] data = new byte[] { 9, 9, 9 };
            byte[] dg = UdpTransport.BuildDatagram(1, 0, 1, (byte)MessageType.Keepalive, (uint)data.Length, data);
            // 破坏 payload 最后一个字节（CRC 不匹配）
            dg[dg.Length - 1] ^= 0xFF;
            r.OnDatagram(dg);

            Assert.False(assembled);
        }

        [Fact]
        public void StaleRealtimeFragment_ShouldBeDropped()
        {
            var r = new UdpReassembler();
            byte[] payload = null;
            r.MessageAssembled += (t, p) => { payload = p; };

            // 实时流（VideoFrame）先收到 frameId=2 的片，再收到 frameId=1 的旧片 → 旧片丢弃
            byte[] newData = new byte[] { 1, 2 };
            byte[] oldData = new byte[] { 3, 4, 5 };
            r.OnDatagram(UdpTransport.BuildDatagram(2, 0, 1, (byte)MessageType.VideoFrame, (uint)newData.Length, newData));
            r.OnDatagram(UdpTransport.BuildDatagram(1, 0, 1, (byte)MessageType.VideoFrame, (uint)oldData.Length, oldData));

            // 最终组装的是 frameId=2 的消息
            Assert.Equal(newData, payload);
        }

        private static byte[] Slice(byte[] data, int offset, int len)
        {
            int actual = System.Math.Min(len, data.Length - offset);
            byte[] frag = new byte[actual];
            System.Buffer.BlockCopy(data, offset, frag, 0, actual);
            return frag;
        }
    }
}
