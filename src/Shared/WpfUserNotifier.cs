using System.Windows;

namespace EasyRDP.Shared
{
    /// <summary>
    /// IUserNotifier 的 WPF 默认实现：基于 MessageBox 的模态提示。
    /// 客户端与服务端均已链接本文件（See csproj 的 Compile/Link）。
    /// </summary>
    public sealed class WpfUserNotifier : IUserNotifier
    {
        /// <summary>显示错误提示。</summary>
        public void ShowError(string message, string title)
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }

        /// <summary>显示警告提示。</summary>
        public void ShowWarning(string message, string title)
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        /// <summary>显示信息提示。</summary>
        public void ShowInfo(string message, string title)
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
