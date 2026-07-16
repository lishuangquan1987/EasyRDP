using EasyRDP.Client.Common;
using EasyRDP.Core.Protocol;
using Xunit;

namespace EasyRDP.Client.Common.Tests;

public class InputEncoderTests
{
    private readonly InputEncoder _encoder = new();

    [Fact]
    public void EncodeMouseMove_ShouldDecodeCorrectly()
    {
        byte[] data = _encoder.EncodeMouseMove(1, true, (short)100, (short)200);
        var msg = MessageCodec.Decode(data);
        Assert.NotNull(msg);
        Assert.Equal(MessageType.InputEvent, msg!.Header.Type);

        var body = Assert.IsType<InputEventMessage>(msg.Body);
        Assert.Equal(InputEventType.MouseMove, body.EventType);
        Assert.Single(body.Units);
        Assert.True(body.Units[0].Absolute);
        Assert.Equal(100, body.Units[0].X);
        Assert.Equal(200, body.Units[0].Y);
    }

    [Fact]
    public void EncodeMouseButton_Down_ShouldDecodeCorrectly()
    {
        byte[] data = _encoder.EncodeMouseButton(2, true, 0); // left down
        var msg = MessageCodec.Decode(data);
        var body = Assert.IsType<InputEventMessage>(msg!.Body);
        Assert.Equal(InputEventType.MouseDown, body.EventType);
        Assert.Equal(0, body.Units[0].Button);
    }

    [Fact]
    public void EncodeMouseButton_Up_ShouldDecodeCorrectly()
    {
        byte[] data = _encoder.EncodeMouseButton(3, false, 1); // right up
        var msg = MessageCodec.Decode(data);
        var body = Assert.IsType<InputEventMessage>(msg!.Body);
        Assert.Equal(InputEventType.MouseUp, body.EventType);
        Assert.Equal(1, body.Units[0].Button);
    }

    [Fact]
    public void EncodeMouseWheel_ShouldDecodeCorrectly()
    {
        byte[] data = _encoder.EncodeMouseWheel(4, 120);
        var msg = MessageCodec.Decode(data);
        var body = Assert.IsType<InputEventMessage>(msg!.Body);
        Assert.Equal(InputEventType.MouseWheel, body.EventType);
        Assert.Equal(120, body.Units[0].WheelDelta);
    }

    [Fact]
    public void EncodeKey_Down_ShouldDecodeCorrectly()
    {
        byte[] data = _encoder.EncodeKey(5, true, 0x41, 0); // 'A' key
        var msg = MessageCodec.Decode(data);
        var body = Assert.IsType<InputEventMessage>(msg!.Body);
        Assert.Equal(InputEventType.KeyDown, body.EventType);
        Assert.Equal(0x41, body.Units[0].VirtualKey);
    }

    [Fact]
    public void EncodeKey_Up_ShouldDecodeCorrectly()
    {
        byte[] data = _encoder.EncodeKey(6, false, 0x1B, 0x0001); // Esc extended
        var msg = MessageCodec.Decode(data);
        var body = Assert.IsType<InputEventMessage>(msg!.Body);
        Assert.Equal(InputEventType.KeyUp, body.EventType);
        Assert.Equal(0x1B, body.Units[0].VirtualKey);
        Assert.Equal(0x0001, body.Units[0].KeyFlags);
    }

    [Fact]
    public void EncodeUnicodeText_ShouldDecodeCorrectly()
    {
        byte[] data = _encoder.EncodeUnicodeText(7, "Hello");
        var msg = MessageCodec.Decode(data);
        var body = Assert.IsType<InputEventMessage>(msg!.Body);
        Assert.Equal(InputEventType.UnicodeText, body.EventType);
        Assert.Equal("Hello", body.Units[0].Text);
    }

    [Fact]
    public void EncodeUnicodeText_Empty_ShouldNotCrash()
    {
        byte[] data = _encoder.EncodeUnicodeText(8, "");
        var msg = MessageCodec.Decode(data);
        Assert.NotNull(msg);
    }

    [Fact]
    public void SequenceNumbers_ShouldBePreserved()
    {
        byte[] data = _encoder.EncodeMouseMove(42, false, 0, 0);
        var msg = MessageCodec.Decode(data);
        Assert.Equal(42u, msg!.Header.Sequence);
    }
}
