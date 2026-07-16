namespace EasyRDP.Client.Common
{
    /// <summary>
    /// 剪贴板操作抽象。
    /// WPF 和 Avalonia 各自实现（同步接口，内部可用异步桥接）。
    /// </summary>
    public interface IClipboardProvider
    {
        /// <summary>获取剪贴板文本。</summary>
        string GetText();

        /// <summary>设置剪贴板文本。</summary>
        void SetText(string text);
    }
}
