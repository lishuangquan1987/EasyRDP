using System;
using Avalonia.Controls;
using Avalonia.Input;
using EasyRDP.Client.Common;

namespace EasyRDP.Client.Avalonia.Services;

/// <summary>
/// Avalonia 输入捕获 + 按键映射。
/// </summary>
public class AvaloniaInputCapturer
{
    private readonly InputEncoder _encoder;
    private int _screenW, _screenH;

    public AvaloniaInputCapturer(InputEncoder encoder) { _encoder = encoder; }

    public void UpdateScreenSize(int w, int h) { _screenW = w; _screenH = h; }

    public byte[] EncodePointerMove(PointerEventArgs e, Control el, uint seq)
    {
        var pos = e.GetPosition(el);
        short x = (short)(pos.X * _screenW / Math.Max(el.Bounds.Width, 1));
        short y = (short)(pos.Y * _screenH / Math.Max(el.Bounds.Height, 1));
        return _encoder.EncodeMouseMove(seq, true, x, y);
    }

    public byte[] EncodePointerPressed(PointerPressedEventArgs e, Control el, uint seq)
    {
        var props = e.GetCurrentPoint(el).Properties;
        byte btn = props.IsLeftButtonPressed ? (byte)0
                 : props.IsRightButtonPressed ? (byte)1
                 : props.IsMiddleButtonPressed ? (byte)2 : (byte)0;
        return _encoder.EncodeMouseButton(seq, true, btn);
    }

    public byte[] EncodePointerReleased(uint seq)
    {
        return _encoder.EncodeMouseButton(seq, false, 0);
    }

    public byte[] EncodePointerWheel(PointerWheelEventArgs e, uint seq)
    {
        short d = (short)(e.Delta.Y * 120);
        return _encoder.EncodeMouseWheel(seq, d);
    }

    public byte[] EncodeKey(KeyEventArgs e, bool isDown, uint seq)
    {
        byte vk = MapKey(e.Key);
        return _encoder.EncodeKey(seq, isDown, vk, 0);
    }

    private static byte MapKey(Key key)
    {
        if (key >= Key.A && key <= Key.Z) return (byte)((int)Key.A + (key - Key.A));
        if (key >= Key.D0 && key <= Key.D9) return (byte)((int)Key.D0 + (key - Key.D0));
        if (key >= Key.F1 && key <= Key.F12) return (byte)(0x70 + (key - Key.F1));

        switch (key)
        {
            case Key.Back: return 0x08; case Key.Tab: return 0x09; case Key.Return: return 0x0D;
            case Key.LeftShift: case Key.RightShift: return 0x10;
            case Key.LeftCtrl: case Key.RightCtrl: return 0x11;
            case Key.LeftAlt: case Key.RightAlt: return 0x12;
            case Key.Escape: return 0x1B; case Key.Space: return 0x20;
            case Key.PageUp: return 0x21; case Key.PageDown: return 0x22;
            case Key.End: return 0x23; case Key.Home: return 0x24;
            case Key.Left: return 0x25; case Key.Up: return 0x26;
            case Key.Right: return 0x27; case Key.Down: return 0x28;
            case Key.Insert: return 0x2D; case Key.Delete: return 0x2E;
            default: return (byte)key;
        }
    }
}
