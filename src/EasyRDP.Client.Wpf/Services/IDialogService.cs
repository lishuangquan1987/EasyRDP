using EasyRDP.Client.Wpf.Models;

namespace EasyRDP.Client.Wpf.Services
{
    /// <summary>
    /// 浏览窗口对话框服务：把需要收集用户输入的弹窗（Edit / Rename）隔离到 View 层。
    /// ViewModel 只依赖本接口，不直接创建 Window/控件（严格 MVVM）。
    /// </summary>
    public interface IDialogService
    {
        /// <summary>编辑连接：返回新值；取消或地址无效返回 null。</summary>
        ConnectionEditResult? EditConnection(RecentConnection current);

        /// <summary>重命名连接：返回新的显示名（可能为空串表示恢复 IP）；取消返回 null。</summary>
        string? RenameConnection(RecentConnection current);
    }
}
