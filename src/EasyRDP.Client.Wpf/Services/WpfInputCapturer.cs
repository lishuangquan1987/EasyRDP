using System;
using System.Windows;
using System.Windows.Input;
using EasyRDP.Client.Common;

namespace EasyRDP.Client.Wpf.Services
{
    /// <summary>
    /// WPF 输入捕获 + 按键映射 → InputEncoder 编码。
    /// </summary>
    public class WpfInputCapturer
    {
        private readonly InputEncoder _encoder;
        private int _screenW, _screenH;

        public WpfInputCapturer(InputEncoder encoder) { _encoder = encoder; }

        public void UpdateScreenSize(int w, int h) { _screenW = w; _screenH = h; }

        public byte[] EncodeMouseMove(MouseEventArgs e, UIElement el, uint seq)
        {
            var pos = e.GetPosition(el);
            short x = (short)(pos.X * _screenW / Math.Max(el.RenderSize.Width, 1));
            short y = (short)(pos.Y * _screenH / Math.Max(el.RenderSize.Height, 1));
            return _encoder.EncodeMouseMove(seq, true, x, y);
        }

        public byte[] EncodeMouseButton(MouseButtonEventArgs e, bool isDown, uint seq)
        {
            byte btn = MapButton(e.ChangedButton);
            return _encoder.EncodeMouseButton(seq, isDown, btn);
        }

        public byte[] EncodeMouseWheel(MouseWheelEventArgs e, uint seq)
        {
            short d = (short)(e.Delta / 120 * 120);
            return _encoder.EncodeMouseWheel(seq, d);
        }

        public byte[] EncodeKey(KeyEventArgs e, bool isDown, uint seq)
        {
            byte vk = MapKey(e.Key);
            return _encoder.EncodeKey(seq, isDown, vk, 0);
        }

        private static byte MapButton(MouseButton btn)
        {
            if (btn == MouseButton.Left) return 0;
            if (btn == MouseButton.Right) return 1;
            if (btn == MouseButton.Middle) return 2;
            if (btn == MouseButton.XButton1) return 3;
            if (btn == MouseButton.XButton2) return 4;
            return 0;
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
                default: return (byte)KeyInterop.VirtualKeyFromKey(key);
            }
        }
    }
}
