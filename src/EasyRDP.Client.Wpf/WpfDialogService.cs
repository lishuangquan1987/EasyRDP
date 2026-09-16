using System.Windows;
using System.Windows.Controls;

namespace EasyRDP.Client.Wpf
{
    /// <summary>
    /// IDialogService 的 WPF 实现：以轻量代码构建模态对话框（Edit / Rename）。
    /// 隔离在 View 层，ViewModel 通过接口调用，不直接接触 Window/控件。
    /// </summary>
    public sealed class WpfDialogService : IDialogService
    {
        private readonly Window _owner;

        public WpfDialogService(Window owner)
        {
            _owner = owner;
        }

        /// <summary>编辑连接：构建 5 字段编辑表单，返回新值；取消或地址为空返回 null。</summary>
        public ConnectionEditResult? EditConnection(RecentConnection current)
        {
            var dlg = CreateDialog("Edit - " + (current?.Host ?? ""), 400, 430);
            var panel = new StackPanel { Margin = new Thickness(14) };

            panel.Children.Add(new TextBlock { Text = "显示名称：", Margin = new Thickness(0, 0, 0, 4) });
            var nameBox = new TextBox { Text = current?.DisplayName ?? "", Height = 26, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) };
            panel.Children.Add(nameBox);

            panel.Children.Add(new TextBlock { Text = "连接地址（IP 或 hostname）：", Margin = new Thickness(0, 0, 0, 4) });
            var hostBox = new TextBox { Text = current?.Host ?? "", Height = 26, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) };
            panel.Children.Add(hostBox);

            panel.Children.Add(new TextBlock { Text = "端口：", Margin = new Thickness(0, 0, 0, 4) });
            var portBox = new TextBox { Text = string.IsNullOrWhiteSpace(current?.Port) ? "2000" : current.Port, Height = 26, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) };
            panel.Children.Add(portBox);

            panel.Children.Add(new TextBlock { Text = "用户名（可选）：", Margin = new Thickness(0, 0, 0, 4) });
            var userBox = new TextBox { Text = current?.Username ?? "", Height = 26, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) };
            panel.Children.Add(userBox);

            panel.Children.Add(new TextBlock { Text = "密码（可选）：", Margin = new Thickness(0, 0, 0, 4) });
            var passBox = new PasswordBox { Height = 26, Margin = new Thickness(0, 0, 0, 8) };
            passBox.Password = current?.Password ?? "";
            panel.Children.Add(passBox);

            panel.Children.Add(BuildButtons(dlg));
            dlg.Content = panel;
            dlg.Loaded += (s2, e2) => { nameBox.Focus(); };

            if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(hostBox.Text))
                return null;

            return new ConnectionEditResult
            {
                Host = hostBox.Text.Trim(),
                DisplayName = string.IsNullOrWhiteSpace(nameBox.Text) ? null : nameBox.Text.Trim(),
                Port = string.IsNullOrWhiteSpace(portBox.Text) ? "2000" : portBox.Text.Trim(),
                Username = string.IsNullOrWhiteSpace(userBox.Text) ? null : userBox.Text.Trim(),
                Password = passBox.Password.Length > 0 ? passBox.Password : null
            };
        }

        /// <summary>重命名连接：构建单输入框，返回新的显示名；取消返回 null。</summary>
        public string? RenameConnection(RecentConnection current)
        {
            var dlg = CreateDialog("Rename - " + (current?.Host ?? ""), 360, 150);
            var panel = new StackPanel { Margin = new Thickness(14) };
            var label = new TextBlock { Text = "显示名称（留空则恢复为 IP）：", Margin = new Thickness(0, 0, 0, 6) };
            var input = new TextBox { Text = current?.DisplayName ?? "", Height = 26, VerticalContentAlignment = VerticalAlignment.Center };
            panel.Children.Add(label);
            panel.Children.Add(input);
            panel.Children.Add(BuildButtons(dlg));
            dlg.Content = panel;
            dlg.Loaded += (s2, e2) => { input.Focus(); input.SelectAll(); };

            return dlg.ShowDialog() == true ? input.Text : null;
        }

        /// <summary>创建统一的模态工具窗口（无系统任务栏、不可缩放、居中于宿主）。</summary>
        private Window CreateDialog(string title, double width, double height)
        {
            return new Window
            {
                Title = title,
                Width = width,
                Height = height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = _owner,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow,
                ShowInTaskbar = false
            };
        }

        /// <summary>构建 OK / Cancel 按钮行（OK 设为默认钮，Cancel 关闭对话框）。</summary>
        private static StackPanel BuildButtons(Window dlg)
        {
            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var ok = new Button { Content = "OK", Width = 72, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var cancel = new Button { Content = "Cancel", Width = 72, IsCancel = true };
            ok.Click += (s2, e2) => { dlg.DialogResult = true; };
            btns.Children.Add(ok);
            btns.Children.Add(cancel);
            return btns;
        }
    }
}
