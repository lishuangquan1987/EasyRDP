using EasyRDP.Core.Protocol;
using Xunit;

namespace EasyRDP.Core.Tests.Protocol
{
    public class CodecNegotiatorTests
    {
        [Fact]
        public void BothSupportHardware_ShouldReturnHardware()
        {
            var result = CodecNegotiator.Negotiate(
                CodecCapabilities.H264Software | CodecCapabilities.H264Hardware,
                CodecCapabilities.H264Software | CodecCapabilities.H264Hardware);
            Assert.Equal(CodecId.H264Hardware, result);
        }

        [Fact]
        public void BothOnlySoftware_ShouldReturnSoftware()
        {
            var result = CodecNegotiator.Negotiate(
                CodecCapabilities.H264Software,
                CodecCapabilities.H264Software);
            Assert.Equal(CodecId.H264Software, result);
        }

        [Fact]
        public void ClientHardwareServerSoftwareOnly_ShouldReturnNoCommon()
        {
            var result = CodecNegotiator.Negotiate(
                CodecCapabilities.H264Hardware,
                CodecCapabilities.H264Software);
            Assert.Null(result);
        }

        [Fact]
        public void ClientSoftwareServerHardwareOnly_ShouldReturnNoCommon()
        {
            var result = CodecNegotiator.Negotiate(
                CodecCapabilities.H264Software,
                CodecCapabilities.H264Hardware);
            Assert.Null(result);
        }

        [Fact]
        public void ClientNone_ShouldReturnNoCommon()
        {
            var result = CodecNegotiator.Negotiate(
                CodecCapabilities.None,
                CodecCapabilities.H264Software);
            Assert.Null(result);
        }

        [Fact]
        public void ServerNone_ShouldReturnNoCommon()
        {
            var result = CodecNegotiator.Negotiate(
                CodecCapabilities.H264Software,
                CodecCapabilities.None);
            Assert.Null(result);
        }

        [Fact]
        public void BothNone_ShouldReturnNoCommon()
        {
            var result = CodecNegotiator.Negotiate(
                CodecCapabilities.None,
                CodecCapabilities.None);
            Assert.Null(result);
        }

        [Fact]
        public void ClientHasExtra_IntersectionOnlySoftware_ShouldReturnSoftware()
        {
            // Client claims Hardware+Software, server only Software
            var result = CodecNegotiator.Negotiate(
                CodecCapabilities.H264Software | CodecCapabilities.H264Hardware,
                CodecCapabilities.H264Software);
            Assert.Equal(CodecId.H264Software, result);
        }
    }
}
