using System.Windows;

namespace EasyRDP.Client.Wpf.Dialogs
{
    /// <summary>编辑连接对话框（View 层，纯 XAML 表单 + 数据绑定，无业务逻辑）。</summary>
    public partial class EditConnectionWindow : Window
    {
        public EditConnectionWindow()
        {
            InitializeComponent();
        }
    }
}
