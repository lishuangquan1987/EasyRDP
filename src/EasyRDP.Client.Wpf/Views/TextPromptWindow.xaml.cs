using System.Windows;
using System.Windows.Input;

namespace EasyRDP.Client.Wpf.Views
{
    /// <summary>通用单行文本输入对话框（新建/重命名分组等）。</summary>
    public partial class TextPromptWindow : Window
    {
        /// <summary>创建输入框并预填初始值。</summary>
        public TextPromptWindow(string title, string label, string initialValue)
        {
            InitializeComponent();
            Title = title;
            LabelText.Text = label;
            InputBox.Text = initialValue ?? "";
            Loaded += (s, e) =>
            {
                InputBox.Focus();
                InputBox.SelectAll();
            };
        }

        /// <summary>用户输入的值（确认时有效）。</summary>
        public string Value
        {
            get { return (InputBox.Text ?? "").Trim(); }
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        private void InputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                DialogResult = true;
                e.Handled = true;
            }
        }
    }
}
