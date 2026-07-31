using EasyRDP.Core.Protocol;
using Xunit;

namespace EasyRDP.Core.Tests.Protocol
{
    public class BinaryPackerTests
    {
        [Fact]
        public void Byte_RoundTrip_ShouldBeEqual()
        {
            var bp = new BinaryPacker();
            bp.WriteByte(0xAB);
            byte[] data = bp.GetBytes();
            Assert.Single(data);
            var reader = BinaryPacker.From(data);
            Assert.Equal(0xAB, reader.ReadByte());
        }

        [Fact]
        public void Int32_RoundTrip_ShouldBeEqual()
        {
            var bp = new BinaryPacker();
            bp.WriteInt32(123456789);
            var reader = BinaryPacker.From(bp.GetBytes());
            Assert.Equal(123456789, reader.ReadInt32());
        }

        [Fact]
        public void UInt32_RoundTrip_ShouldBeEqual()
        {
            var bp = new BinaryPacker();
            bp.WriteUInt32(3000000000);
            var reader = BinaryPacker.From(bp.GetBytes());
            Assert.Equal(3000000000u, reader.ReadUInt32());
        }

        [Fact]
        public void Int64_RoundTrip_ShouldBeEqual()
        {
            var bp = new BinaryPacker();
            bp.WriteInt64(123456789012345L);
            var reader = BinaryPacker.From(bp.GetBytes());
            Assert.Equal(123456789012345L, reader.ReadInt64());
        }

        [Fact]
        public void String_RoundTrip_ShouldBeEqual()
        {
            var bp = new BinaryPacker();
            bp.WriteString("Hello World");
            var reader = BinaryPacker.From(bp.GetBytes());
            Assert.Equal("Hello World", reader.ReadString());
        }

        [Fact]
        public void String_Null_ShouldReadEmpty()
        {
            var bp = new BinaryPacker();
            bp.WriteString(null);
            var reader = BinaryPacker.From(bp.GetBytes());
            Assert.Equal("", reader.ReadString());
        }

        [Fact]
        public void String_Empty_ShouldBeEqual()
        {
            var bp = new BinaryPacker();
            bp.WriteString("");
            var reader = BinaryPacker.From(bp.GetBytes());
            Assert.Equal("", reader.ReadString());
        }

        [Fact]
        public void String_Chinese_ShouldBeEqual()
        {
            var bp = new BinaryPacker();
            bp.WriteString("中文测试");
            var reader = BinaryPacker.From(bp.GetBytes());
            Assert.Equal("中文测试", reader.ReadString());
        }

        [Fact]
        public void Bytes_RoundTrip_ShouldBeEqual()
        {
            var bp = new BinaryPacker();
            byte[] input = new byte[] { 0x01, 0x02, 0x03, 0xFF };
            bp.WriteBytes(input);
            var reader = BinaryPacker.From(bp.GetBytes());
            byte[] output = reader.ReadBytes();
            Assert.Equal(input, output);
        }

        [Fact]
        public void Bytes_Null_ShouldReadNull()
        {
            var bp = new BinaryPacker();
            bp.WriteBytes(null);
            var reader = BinaryPacker.From(bp.GetBytes());
            Assert.Null(reader.ReadBytes());
        }

        [Fact]
        public void Bytes_Empty_ShouldReadNull()
        {
            var bp = new BinaryPacker();
            bp.WriteBytes(new byte[0]);
            var reader = BinaryPacker.From(bp.GetBytes());
            Assert.Null(reader.ReadBytes());
        }

        [Fact]
        public void MultipleFields_RoundTrip_ShouldBeEqual()
        {
            var bp = new BinaryPacker();
            bp.WriteByte(0xE5);
            bp.WriteInt32(42);
            bp.WriteString("test");
            bp.WriteInt64(999L);
            bp.WriteBytes(new byte[] { 0xAA, 0xBB });

            var reader = BinaryPacker.From(bp.GetBytes());
            Assert.Equal(0xE5, reader.ReadByte());
            Assert.Equal(42, reader.ReadInt32());
            Assert.Equal("test", reader.ReadString());
            Assert.Equal(999L, reader.ReadInt64());
            Assert.Equal(new byte[] { 0xAA, 0xBB }, reader.ReadBytes());
        }
    }
}
