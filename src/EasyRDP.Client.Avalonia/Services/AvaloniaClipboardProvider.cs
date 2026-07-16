using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using EasyRDP.Client.Common;

namespace EasyRDP.Client.Avalonia.Services;

/// <summary>
/// Avalonia 剪贴板实现。
/// </summary>
public class AvaloniaClipboardProvider : IClipboardProvider
{
    public string GetText()
    {
        // Avalonia 12 clipboard read not reliably available across platforms.
        return string.Empty;
    }

    public void SetText(string text)
    {
        try
        {
            var lifetime = Application.Current?.ApplicationLifetime;
            if (lifetime is global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                var topLevel = TopLevel.GetTopLevel(desktop.MainWindow);
                if (topLevel?.Clipboard != null)
                    Dispatcher.UIThread.Post(
                        async () => { try { await topLevel.Clipboard.SetTextAsync(text); } catch { } });
            }
        }
        catch { }
    }
}
