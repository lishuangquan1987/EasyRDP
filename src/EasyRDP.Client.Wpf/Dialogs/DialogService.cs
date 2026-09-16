using System.Windows;
using EasyRDP.Client.Wpf.Models;
using EasyRDP.Client.Wpf.Services;
using EasyRDP.Client.Wpf.ViewModels;

namespace EasyRDP.Client.Wpf.Dialogs
{
    /// <summary>
    /// IDialogService 的 WPF 实现：以 XAML 窗口 + ViewModel 展示 Edit/Rename 对话框。
    /// 本服务属于 View 层（负责实例化窗口），接口契约在 Services 层，避免 ViewModel 依赖 Window。
    /// </summary>
    public sealed class DialogService : IDialogService
    {
        private readonly Window _owner;

        public DialogService(Window owner)
        {
            _owner = owner;
        }

        /// <summary>打开编辑连接对话框；取消返回 null。</summary>
        public ConnectionEditResult? EditConnection(RecentConnection current)
        {
            var vm = new EditConnectionViewModel(current);
            var window = new EditConnectionWindow
            {
                DataContext = vm,
                Owner = _owner,
                Title = "Edit - " + (current?.Host ?? "")
            };
            vm.CloseRequested += confirmed => window.DialogResult = confirmed;

            bool? dialogResult = window.ShowDialog();
            return dialogResult == true ? vm.ToResult() : null;
        }

        /// <summary>打开重命名对话框；取消返回 null，确认则返回原始输入（可空串）。</summary>
        public string? RenameConnection(RecentConnection current)
        {
            var vm = new RenameConnectionViewModel(current);
            var window = new RenameConnectionWindow
            {
                DataContext = vm,
                Owner = _owner,
                Title = "Rename - " + (current?.Host ?? "")
            };
            vm.CloseRequested += confirmed => window.DialogResult = confirmed;

            bool? dialogResult = window.ShowDialog();
            return dialogResult == true ? vm.DisplayName : null;
        }
    }
}
