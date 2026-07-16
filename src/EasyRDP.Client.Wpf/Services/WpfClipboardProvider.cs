using EasyRDP.Client.Common;

namespace EasyRDP.Client.Wpf.Services
{
    /// <summary>
    /// WPF 剪贴板实现。
    /// </summary>
    public class WpfClipboardProvider : IClipboardProvider
    {
        public string GetText()
        {
            try { return System.Windows.Clipboard.GetText(); }
            catch { return string.Empty; }
        }

        public void SetText(string text)
        {
            try { System.Windows.Clipboard.SetText(text); }
            catch { }
        }
    }
}
