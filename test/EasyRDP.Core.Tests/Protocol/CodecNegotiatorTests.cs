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

        [Fact]
        public void BothSupportH264SoftwareAndVp8_ShouldReturnH264Software()
        {
            // H264 软编优先级高于 VP8（弱机 CPU 友好优先）
            var result = CodecNegotiator.Negotiate(
                CodecCapabilities.H264Software | CodecCapabilities.Vp8Software,
                CodecCapabilities.H264Software | CodecCapabilities.Vp8Software);
            Assert.Equal(CodecId.H264Software, result);
        }

        [Fact]
        public void BothSupportH264SoftwareAndZrle_ShouldReturnH264Software()
        {
            // H264 软编优先级高于 ZRLE（弱机编码速度优先；ZRLE 作为 H264 不可用回退）
            var result = CodecNegotiator.Negotiate(
                CodecCapabilities.H264Software | CodecCapabilities.Zrle,
                CodecCapabilities.H264Software | CodecCapabilities.Zrle);
            Assert.Equal(CodecId.H264Software, result);
        }

        [Fact]
        public void BothSupportZrleAndVp8_ShouldReturnZrle()
        {
            // ZRLE 优先级高于 VP8
            var result = CodecNegotiator.Negotiate(
                CodecCapabilities.Zrle | CodecCapabilities.Vp8Software,
                CodecCapabilities.Zrle | CodecCapabilities.Vp8Software);
            Assert.Equal(CodecId.Zrle, result);
        }

        [Fact]
        public void Vp8ServerOnlyH264Client_ShouldReturnNoCommon()
        {
            // 客户端不支持 VP8 时交集为空
            var result = CodecNegotiator.Negotiate(
                CodecCapabilities.H264Software,
                CodecCapabilities.Vp8Software);
            Assert.Null(result);
        }
    }
}
