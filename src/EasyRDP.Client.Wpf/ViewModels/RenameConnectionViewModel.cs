using System;
using EasyRDP.Client.Wpf.Models;
using EasyRDP.Shared;

namespace EasyRDP.Client.Wpf.ViewModels
{
    /// <summary>
    /// 重命名连接对话框 ViewModel。承载单个显示名称输入字段，
    /// 通过 OkCommand 请求关闭并返回结果（空输入表示恢复为 IP）。
    /// </summary>
    public class RenameConnectionViewModel
    {
        /// <summary>显示名称（空表示恢复为 IP）。</summary>
        public string DisplayName { get; set; }

        /// <summary>确定：请求以成功结果关闭对话框。</summary>
        public RelayCommand OkCommand { get; }

        /// <summary>请求关闭对话框（参数 true=确认，false=取消）。</summary>
        public event Action<bool>? CloseRequested;

        /// <summary>以已有连接项初始化输入字段。</summary>
        public RenameConnectionViewModel(RecentConnection current)
        {
            DisplayName = current?.DisplayName ?? "";
            OkCommand = new RelayCommand(() => CloseRequested?.Invoke(true));
        }

        /// <summary>返回归一化后的显示名称（空/空白字符串归为 null，表示恢复为 IP）。</summary>
        public string? ResolvedDisplayName()
        {
            return string.IsNullOrWhiteSpace(DisplayName) ? null : DisplayName.Trim();
        }
    }
}
