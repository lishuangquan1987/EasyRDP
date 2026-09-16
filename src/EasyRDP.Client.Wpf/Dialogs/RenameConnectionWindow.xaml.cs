using System.Windows;

namespace EasyRDP.Client.Wpf.Dialogs
{
    /// <summary>重命名连接对话框（View 层，纯 XAML 表单 + 数据绑定，无业务逻辑）。</summary>
    public partial class RenameConnectionWindow : Window
    {
        public RenameConnectionWindow()
        {
            InitializeComponent();
        }
    }
}
