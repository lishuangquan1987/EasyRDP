using System;
using EasyRDP.Core.Protocol;
using Xunit;

namespace EasyRDP.Core.Tests
{
    public class H264EncoderTest
    {
        [Fact]
        public void Encode_SmallFrame_ProducesData()
        {
            var enc = new H264EncoderNative();
            Assert.True(enc.IsAvailable, "Encoder DLL not found");
            enc.Initialize(320, 240, 500000);

            // Create BGRA test image (will be converted to I420 by encoder)
            var bgra = new byte[320 * 240 * 4];
            for (int i = 0; i < bgra.Length; i += 4)
            { bgra[i] = 128; bgra[i + 1] = 128; bgra[i + 2] = 128; bgra[i + 3] = 255; }

            var result = enc.Encode(bgra, true);
            Console.WriteLine($"Encode OK: keyframe={result.IsKeyframe} len={result.Data?.Length ?? -1}");
            Assert.True(result.Data != null && result.Data.Length > 0, "Encoded data is empty!");
            enc.Dispose();
        }
    }
}
