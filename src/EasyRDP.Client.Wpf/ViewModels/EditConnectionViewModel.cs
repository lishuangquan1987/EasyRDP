using System;
using EasyRDP.Client.Wpf.Models;
using EasyRDP.Shared;

namespace EasyRDP.Client.Wpf.ViewModels
{
    /// <summary>
    /// 编辑连接对话框 ViewModel。承载五个输入字段（显示名/地址/端口/用户名/密码），
    /// 通过 OkCommand 请求关闭并返回结果；字段与 View 的 TextBox/PasswordBox 做 TwoWay 绑定。
    /// </summary>
    public class EditConnectionViewModel
    {
        /// <summary>显示名称（可空，空则回退到 Host）。</summary>
        public string DisplayName { get; set; }

        /// <summary>连接地址（IP 或主机名）。</summary>
        public string Host { get; set; }

        /// <summary>连接端口。</summary>
        public string Port { get; set; }

        /// <summary>登录用户名（可空）。</summary>
        public string Username { get; set; }

        /// <summary>登录密码（可空）。</summary>
        public string Password { get; set; }

        /// <summary>确定：请求以成功结果关闭对话框。</summary>
        public RelayCommand OkCommand { get; }

        /// <summary>请求关闭对话框（参数 true=确认，false=取消；由 DialogService 转为窗口 DialogResult）。</summary>
        public event Action<bool>? CloseRequested;

        /// <summary>以已有连接项初始化各输入字段。</summary>
        public EditConnectionViewModel(RecentConnection current)
        {
            DisplayName = current?.DisplayName ?? "";
            Host = current?.Host ?? "";
            Port = string.IsNullOrWhiteSpace(current?.Port) ? "2000" : current.Port;
            Username = current?.Username ?? "";
            Password = current?.Password ?? "";

            OkCommand = new RelayCommand(() => CloseRequested?.Invoke(true));
        }

        /// <summary>把输入字段组装为可直接落盘的编辑结果（空字段按业务规则归一化）。</summary>
        public ConnectionEditResult ToResult()
        {
            return new ConnectionEditResult
            {
                Host = (Host ?? "").Trim(),
                DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? null : DisplayName.Trim(),
                Port = string.IsNullOrWhiteSpace(Port) ? "2000" : Port.Trim(),
                Username = string.IsNullOrWhiteSpace(Username) ? null : Username.Trim(),
                Password = string.IsNullOrEmpty(Password) ? null : Password
            };
        }
    }
}
