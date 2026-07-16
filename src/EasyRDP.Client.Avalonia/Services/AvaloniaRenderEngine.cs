using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace EasyRDP.Client.Avalonia.Services;

/// <summary>
/// Avalonia WriteableBitmap 渲染引擎。
/// </summary>
public class AvaloniaRenderEngine
{
    private WriteableBitmap? _bitmap;

    public IImage? Source => _bitmap;

    public unsafe void Render(byte[] bgraPixels, int w, int h)
    {
        if (_bitmap == null || _bitmap.PixelSize.Width != w || _bitmap.PixelSize.Height != h)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Premul);
        }

        using var fb = _bitmap.Lock();
        Marshal.Copy(bgraPixels, 0, fb.Address, bgraPixels.Length);
    }

    public void Resize(int w, int h)
    {
        _bitmap?.Dispose();
        _bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
    }
}
