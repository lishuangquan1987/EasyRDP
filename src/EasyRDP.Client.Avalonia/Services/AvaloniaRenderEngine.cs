using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace EasyRDP.Client.Avalonia.Services;

/// <summary>
/// Avalonia WriteableBitmap 渲染引擎。
/// 支持自绘光标叠加，消除系统光标闪烁。
/// </summary>
public class AvaloniaRenderEngine
{
    private WriteableBitmap? _bitmap;
    private int _screenW, _screenH;

    // 光标状态
    private byte[]? _cursorRgba;
    private int _cursorW, _cursorH;
    private int _cursorHotX, _cursorHotY;
    private int _cursorX, _cursorY;
    private bool _cursorVisible;

    public IImage? Source => _bitmap;

    public unsafe void Render(byte[] bgraPixels, int w, int h)
    {
        _screenW = w;
        _screenH = h;

        if (_bitmap == null || _bitmap.PixelSize.Width != w || _bitmap.PixelSize.Height != h)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Premul);
        }

        using var fb = _bitmap.Lock();
        Marshal.Copy(bgraPixels, 0, fb.Address, bgraPixels.Length);

        // 叠加自绘光标
        DrawCursorOverlay((byte*)fb.Address, w, h, fb.RowBytes);
    }

    public void Resize(int w, int h)
    {
        _screenW = w;
        _screenH = h;
        _bitmap?.Dispose();
        _bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
    }

    /// <summary>
    /// 更新光标状态。
    /// </summary>
    public void SetCursor(bool visible, int x, int y,
        byte[]? rgbaPixels, int cursorW, int cursorH, int hotX, int hotY)
    {
        _cursorVisible = visible;
        _cursorX = x;
        _cursorY = y;

        if (rgbaPixels != null && cursorW > 0 && cursorH > 0)
        {
            _cursorRgba = rgbaPixels;
            _cursorW = cursorW;
            _cursorH = cursorH;
            _cursorHotX = hotX;
            _cursorHotY = hotY;
        }
    }

    /// <summary>
    /// 在帧缓冲上叠加绘制光标（Alpha 混合）。
    /// </summary>
    private unsafe void DrawCursorOverlay(byte* pixels, int w, int h, int rowBytes)
    {
        if (!_cursorVisible || _cursorRgba == null || _cursorW <= 0 || _cursorH <= 0)
            return;

        try
        {
            int drawX = _cursorX - _cursorHotX;
            int drawY = _cursorY - _cursorHotY;

            for (int cy = 0; cy < _cursorH; cy++)
            {
                int destY = drawY + cy;
                if (destY < 0 || destY >= h) continue;

                for (int cx = 0; cx < _cursorW; cx++)
                {
                    int destX = drawX + cx;
                    if (destX < 0 || destX >= w) continue;

                    int srcIdx = (cy * _cursorW + cx) * 4;
                    byte a = _cursorRgba[srcIdx + 3]; // RGBA → A
                    if (a == 0) continue;

                    byte* dest = pixels + destY * rowBytes + destX * 4;
                    // framebuffer is BGRA

                    if (a == 255)
                    {
                        // Windows 光标 XOR mask 不含 alpha，直接使用不透明
                        dest[0] = _cursorRgba[srcIdx + 2]; // B
                        dest[1] = _cursorRgba[srcIdx + 1]; // G
                        dest[2] = _cursorRgba[srcIdx];     // R
                        dest[3] = 255;                     // A (Windows cursor has no alpha)
                    }
                    else
                    {
                        float alpha = a / 255f;
                        dest[0] = (byte)(_cursorRgba[srcIdx + 2] * alpha + dest[0] * (1 - alpha));
                        dest[1] = (byte)(_cursorRgba[srcIdx + 1] * alpha + dest[1] * (1 - alpha));
                        dest[2] = (byte)(_cursorRgba[srcIdx] * alpha + dest[2] * (1 - alpha));
                        dest[3] = 255;
                    }
                }
            }
        }
        catch { /* 光标绘制失败不应影响帧渲染 */ }
    }
}
