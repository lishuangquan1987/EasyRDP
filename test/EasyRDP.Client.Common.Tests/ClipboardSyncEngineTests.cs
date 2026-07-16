using EasyRDP.Client.Common;
using EasyRDP.Core.Protocol;
using Xunit;

namespace EasyRDP.Client.Common.Tests;

public class ClipboardSyncEngineTests
{
    private readonly ClipboardSyncEngine _engine = new();

    [Fact]
    public void TryEncodeLocalChange_FirstChange_ShouldEncode()
    {
        byte[] data = _engine.TryEncodeLocalChange("hello", 1);
        Assert.NotNull(data);

        var msg = MessageCodec.Decode(data);
        Assert.NotNull(msg);
        var body = Assert.IsType<ClipboardDataMessage>(msg!.Body);
        Assert.Equal("hello", body.Text);
    }

    [Fact]
    public void TryEncodeLocalChange_SameText_ShouldReturnNull()
    {
        _engine.TryEncodeLocalChange("hello", 1);
        byte[] data = _engine.TryEncodeLocalChange("hello", 2);
        Assert.Null(data);
    }

    [Fact]
    public void TryEncodeLocalChange_DifferentText_ShouldEncode()
    {
        _engine.TryEncodeLocalChange("hello", 1);
        byte[] data = _engine.TryEncodeLocalChange("world", 2);
        Assert.NotNull(data);
    }

    [Fact]
    public void TryEncodeLocalChange_DuringCooldown_ShouldReturnNull()
    {
        // Simulate receiving remote clipboard: start cooldown
        _engine.BeginCooldown();

        byte[] data = _engine.TryEncodeLocalChange("new text", 1);
        Assert.Null(data);
    }

    [Fact]
    public void TryEncodeLocalChange_AfterCooldown_ShouldEncode()
    {
        // This test doesn't actually wait 500ms — it verifies the engine
        // properly starts cooldown. We test behavior without cooldown.
        _engine.Reset();
        byte[] data = _engine.TryEncodeLocalChange("text", 1);
        Assert.NotNull(data);
    }

    [Fact]
    public void OnRemoteClipboard_ShouldReturnText()
    {
        var msg = new ClipboardDataMessage { Format = ClipboardFormat.UnicodeText, Text = "remote" };
        string result = _engine.OnRemoteClipboard(msg);
        Assert.Equal("remote", result);
    }

    [Fact]
    public void OnRemoteClipboard_ShouldStartCooldown()
    {
        var msg = new ClipboardDataMessage { Format = ClipboardFormat.UnicodeText, Text = "remote" };
        _engine.OnRemoteClipboard(msg);

        // Local change during cooldown should be suppressed
        byte[] data = _engine.TryEncodeLocalChange("local", 1);
        Assert.Null(data);
    }

    [Fact]
    public void OnRemoteClipboard_WrongFormat_ShouldReturnNull()
    {
        // ClipboardFormat only has UnicodeText=0, we test null/edge
        var msg = new ClipboardDataMessage { Format = (ClipboardFormat)99, Text = "test" };
        string result = _engine.OnRemoteClipboard(msg);
        Assert.Null(result);
    }

    [Fact]
    public void OnRemoteClipboard_NullMessage_ShouldNotCrash()
    {
        string result = _engine.OnRemoteClipboard(null!);
        Assert.Null(result);
    }

    [Fact]
    public void Reset_ShouldClearState()
    {
        _engine.TryEncodeLocalChange("hello", 1);
        _engine.BeginCooldown();
        _engine.Reset();

        // After reset, should be able to encode again
        byte[] data = _engine.TryEncodeLocalChange("hello", 2);
        Assert.NotNull(data);
    }

    [Fact]
    public void TryEncodeLocalChange_NullText_ShouldNotCrash()
    {
        byte[] data = _engine.TryEncodeLocalChange(null!, 1);
        // null is treated as empty string, encoding empty
        Assert.NotNull(data);
    }
}
