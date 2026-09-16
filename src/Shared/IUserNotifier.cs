namespace EasyRDP.Shared
{
    /// <summary>
    /// 用户提示抽象：让 ViewModel 不直接依赖 MessageBox（System.Windows）。
    /// 由 View 层传入基于 MessageBox 的默认实现（WpfUserNotifier），保持 MVVM 分层清晰、便于单元测试。
    /// </summary>
    public interface IUserNotifier
    {
        /// <summary>显示错误提示（模态，含"确定"按钮）。</summary>
        void ShowError(string message, string title);

        /// <summary>显示警告提示（模态，含"确定"按钮）。</summary>
        void ShowWarning(string message, string title);

        /// <summary>显示信息提示（模态，含"确定"按钮）。</summary>
        void ShowInfo(string message, string title);
    }
}
