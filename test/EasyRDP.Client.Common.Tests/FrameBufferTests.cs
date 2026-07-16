using EasyRDP.Client.Common;
using EasyRDP.Core.Protocol;
using Xunit;

namespace EasyRDP.Client.Common.Tests;

public class FrameBufferTests
{
    [Fact]
    public void ProcessFullFrame_ShouldReplaceBuffer()
    {
        var fb = new FrameBuffer();

        var fullFrame = BuildFullFrame(10, 10, 0xFF);
        fb.ProcessFrame(fullFrame);

        Assert.True(fb.TryGetFrame(out byte[] pixels, out int w, out int h));
        Assert.Equal(10, w);
        Assert.Equal(10, h);
        Assert.Equal(400, pixels.Length); // 10*10*4
        Assert.Equal(0xFF, pixels[0]); // B channel
        Assert.False(fb.IsDirty, "After TryGetFrame, should not be dirty");
    }

    [Fact]
    public void ProcessFullFrame_SecondFrame_ShouldReplace()
    {
        var fb = new FrameBuffer();

        fb.ProcessFrame(BuildFullFrame(10, 10, 0xFF));
        fb.TryGetFrame(out _, out _, out _); // consume

        fb.ProcessFrame(BuildFullFrame(20, 20, 0xAA));
        Assert.True(fb.TryGetFrame(out byte[] pixels, out int w, out int h));
        Assert.Equal(20, w);
        Assert.Equal(20, h);
        Assert.Equal(0xAA, pixels[0]);
    }

    [Fact]
    public void ProcessDeltaFrame_ShouldMerge()
    {
        var fb = new FrameBuffer();

        // First, establish full frame (white)
        fb.ProcessFrame(BuildFullFrame(10, 10, 0xFF));
        fb.TryGetFrame(out _, out _, out _);

        // Delta: change top-left 2x2 to black
        var deltaRects = new[]
        {
            new ScreenRect { X = 0, Y = 0, Width = 2, Height = 2, Offset = 0 }
        };
        byte[] blackPixels = new byte[2 * 2 * 4]; // all zeros = black BGRA
        var delta = BuildDeltaFrame(CompressType.None, deltaRects, blackPixels);
        fb.ProcessFrame(delta);

        Assert.True(fb.TryGetFrame(out byte[] pixels, out int w, out int h));
        // Check top-left pixel is black
        Assert.Equal(0, pixels[0]); // B
        Assert.Equal(0, pixels[1]); // G
        Assert.Equal(0, pixels[2]); // R
        // Check (9,9) is still white
        int bottomRight = (9 * 10 + 9) * 4;
        Assert.Equal(0xFF, pixels[bottomRight]);
    }

    [Fact]
    public void ProcessDeltaFrame_MultipleRects_ShouldMergeAll()
    {
        var fb = new FrameBuffer();
        fb.ProcessFrame(BuildFullFrame(10, 10, 0xFF)); // white
        fb.TryGetFrame(out _, out _, out _);

        // Two dirty rects: (0,0,2,2) black + (8,8,2,2) black
        byte[] black2x2 = new byte[2 * 2 * 4];
        var delta = BuildDeltaFrame(CompressType.None, new[]
        {
            new ScreenRect { X = 0, Y = 0, Width = 2, Height = 2, Offset = 0 },
            new ScreenRect { X = 8, Y = 8, Width = 2, Height = 2, Offset = (uint)(2*2*4) }
        },
        // 8 bytes of black for each rect
        ConcatArrays(black2x2, black2x2));
        fb.ProcessFrame(delta);

        Assert.True(fb.TryGetFrame(out byte[] pixels, out _, out _));
        Assert.Equal(0, pixels[0]); // top-left black
        Assert.Equal(0xFF, pixels[8]); // (2,0) white — outside dirty rects
        Assert.Equal(0, pixels[(9 * 10 + 8) * 4]); // bottom-right black
    }

    [Fact]
    public void ProcessDeltaFrame_WithoutFullFrame_ShouldIgnore()
    {
        var fb = new FrameBuffer();
        var delta = BuildDeltaFrame(CompressType.None, new[]
        {
            new ScreenRect { X = 0, Y = 0, Width = 2, Height = 2, Offset = 0 }
        }, new byte[16]);
        fb.ProcessFrame(delta);

        Assert.False(fb.TryGetFrame(out _, out _, out _), "Delta without full frame should be ignored");
    }

    [Fact]
    public void TryGetFrame_WithoutFrame_ShouldReturnFalse()
    {
        var fb = new FrameBuffer();
        Assert.False(fb.TryGetFrame(out _, out _, out _));
    }

    [Fact]
    public void IsDirty_ShouldTrackState()
    {
        var fb = new FrameBuffer();
        Assert.False(fb.IsDirty);

        fb.ProcessFrame(BuildFullFrame(5, 5, 0xFF));
        Assert.True(fb.IsDirty);

        fb.TryGetFrame(out _, out _, out _);
        Assert.False(fb.IsDirty);
    }

    [Fact]
    public void FrameCount_ShouldIncrement()
    {
        var fb = new FrameBuffer();
        Assert.Equal(0, fb.FrameCount);

        fb.ProcessFrame(BuildFullFrame(5, 5, 0));
        Assert.Equal(1, fb.FrameCount);

        fb.ProcessFrame(BuildFullFrame(5, 5, 0));
        Assert.Equal(2, fb.FrameCount);
    }

    [Fact]
    public void Reset_ShouldClearAll()
    {
        var fb = new FrameBuffer();
        fb.ProcessFrame(BuildFullFrame(10, 10, 0xFF));
        fb.Reset();

        Assert.Equal(0, fb.Width);
        Assert.Equal(0, fb.Height);
        Assert.Equal(0, fb.FrameCount);
        Assert.False(fb.IsDirty);
        Assert.False(fb.TryGetFrame(out _, out _, out _));
    }

    [Fact]
    public void ProcessFrame_NullOrEmpty_ShouldNotCrash()
    {
        var fb = new FrameBuffer();
        fb.ProcessFrame(null!);
        fb.ProcessFrame(new ScreenFrameMessage { Rects = new ScreenRect[0], Pixels = new byte[0] });

        Assert.False(fb.TryGetFrame(out _, out _, out _));
    }

    // ── Helpers ─────────────────────────────────────────

    private static ScreenFrameMessage BuildFullFrame(int w, int h, byte fill)
    {
        byte[] pixels = new byte[w * h * 4];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = fill;

        return new ScreenFrameMessage
        {
            FrameType = FrameType.Full,
            Compress = CompressType.None,
            Rects = new[] { new ScreenRect { X = 0, Y = 0, Width = (ushort)w, Height = (ushort)h, Offset = 0 } },
            Pixels = pixels
        };
    }

    private static ScreenFrameMessage BuildDeltaFrame(CompressType compress, ScreenRect[] rects, byte[] pixels)
    {
        return new ScreenFrameMessage
        {
            FrameType = FrameType.Delta,
            Compress = compress,
            Rects = rects,
            Pixels = pixels
        };
    }

    private static byte[] ConcatArrays(byte[] a, byte[] b)
    {
        byte[] result = new byte[a.Length + b.Length];
        Array.Copy(a, 0, result, 0, a.Length);
        Array.Copy(b, 0, result, a.Length, b.Length);
        return result;
    }
}
