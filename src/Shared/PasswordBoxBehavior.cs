#nullable disable
using System.Windows;
using System.Windows.Controls;

namespace EasyRDP.Shared
{
    /// <summary>
    /// PasswordBox 附加行为（attached behavior）：PasswordBox.Password 是 SecureString 派生值，
    /// WPF 出于安全设计不开放直接绑定。本行为把 PasswordBox.Password 双向桥接到 ViewModel 的 string 属性，
    /// 让密码输入也走标准数据绑定，避免在 View code-behind 里写 PasswordChanged 事件。
    /// 用法：
    /// &lt;PasswordBox behaviors:PasswordBoxBehavior.BindPassword="True"
    ///              behaviors:PasswordBoxBehavior.Password="{Binding Password, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"/&gt;
    /// </summary>
    public static class PasswordBoxBehavior
    {
        /// <summary>是否启用密码绑定（true 时订阅 PasswordChanged 事件）。</summary>
        public static readonly DependencyProperty BindPasswordProperty =
            DependencyProperty.RegisterAttached("BindPassword", typeof(bool), typeof(PasswordBoxBehavior),
                new PropertyMetadata(false, OnBindPasswordChanged));

        /// <summary>绑定的密码明文（ViewModel 的同步目标）。</summary>
        public static readonly DependencyProperty PasswordProperty =
            DependencyProperty.RegisterAttached("Password", typeof(string), typeof(PasswordBoxBehavior),
                new PropertyMetadata(string.Empty, OnPasswordChanged));

        public static bool GetBindPassword(DependencyObject obj)
        {
            return (bool)obj.GetValue(BindPasswordProperty);
        }

        public static void SetBindPassword(DependencyObject obj, bool value)
        {
            obj.SetValue(BindPasswordProperty, value);
        }

        public static string GetPassword(DependencyObject obj)
        {
            return (string)obj.GetValue(PasswordProperty);
        }

        public static void SetPassword(DependencyObject obj, string value)
        {
            obj.SetValue(PasswordProperty, value);
        }

        private static void OnBindPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var box = d as PasswordBox;
            if (box == null) return;

            if ((bool)e.NewValue)
            {
                box.PasswordChanged += OnPasswordBoxChanged;
                // 建立绑定时把 ViewModel 当前值同步进 PasswordBox（如编辑会话时回填旧密码）
                string current = GetPassword(box);
                if (box.Password != current)
                    box.Password = current ?? string.Empty;
            }
            else
            {
                box.PasswordChanged -= OnPasswordBoxChanged;
            }
        }

        private static void OnPasswordBoxChanged(object sender, RoutedEventArgs e)
        {
            var box = sender as PasswordBox;
            if (box == null) return;
            // 用户输入：把 PasswordBox 最新值写回附加属性（触发 TwoWay 绑定回写 ViewModel）
            SetPassword(box, box.Password);
        }

        private static void OnPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var box = d as PasswordBox;
            if (box == null) return;
            // ViewModel 值变化：同步进 PasswordBox。临时退订事件，避免回写触发递归。
            box.PasswordChanged -= OnPasswordBoxChanged;
            string newValue = e.NewValue as string;
            if (box.Password != newValue)
                box.Password = newValue ?? string.Empty;
            box.PasswordChanged += OnPasswordBoxChanged;
        }
    }
}
