using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;
using EasyRDP.Client.Wpf.Services;

namespace EasyRDP.Client.Wpf.Models
{
    /// <summary>
    /// 浏览窗口树节点：分组节点（IsGroup=true，Children=连接）或连接节点（Profile 非空）。
    /// 分组名 "Recent" 为虚拟分组（最近使用，不可拖入/不可重命名）。
    /// </summary>
    public class ConnectionTreeNode : INotifyPropertyChanged
    {
        private ServerAvailability _availability = ServerAvailability.Unknown;

        /// <summary>是否分组节点。</summary>
        public bool IsGroup { get; set; }

        /// <summary>分组名（连接节点为所属分组，空=未分组）。</summary>
        public string GroupName { get; set; } = "";

        /// <summary>显示名称（分组名 / 连接名）。</summary>
        public string DisplayName { get; set; } = "";

        /// <summary>副标题（连接节点为 host:port，分组节点为连接数）。</summary>
        public string Detail { get; set; } = "";

        /// <summary>缩略图（连接节点；无缩略图时为 null）。</summary>
        public ImageSource ThumbnailSource { get; set; }

        /// <summary>连接配置（连接节点；分组节点为 null）。</summary>
        public ServerProfile Profile { get; set; }

        /// <summary>最近连接条目（Recent 虚拟分组下的节点使用；其它节点为 null）。</summary>
        public RecentConnection Recent { get; set; }

        /// <summary>子节点（仅分组节点使用）。</summary>
        public ObservableCollection<ConnectionTreeNode> Children { get; } =
            new ObservableCollection<ConnectionTreeNode>();

        /// <summary>是否展开（分组节点）。</summary>
        public bool IsExpanded { get; set; } = true;

        /// <summary>虚拟分组：最近使用（不可拖入、不可重命名/删除）。</summary>
        public bool IsRecentGroup { get; set; }

        /// <summary>未分组虚拟节点（可拖入，但不可重命名/删除）。</summary>
        public bool IsUngrouped { get; set; }

        /// <summary>服务端在线状态（连接节点；探测更新时通知界面）。</summary>
        public ServerAvailability Availability
        {
            get { return _availability; }
            set
            {
                if (_availability == value) return;
                _availability = value;
                var handler = PropertyChanged;
                if (handler == null) return;
                handler(this, new PropertyChangedEventArgs(nameof(Availability)));
                handler(this, new PropertyChangedEventArgs(nameof(AvailabilityText)));
            }
        }

        /// <summary>在线状态文本（详情面板显示）。</summary>
        public string AvailabilityText
        {
            get
            {
                switch (_availability)
                {
                    case ServerAvailability.Online: return "Online";
                    case ServerAvailability.Offline: return "Offline";
                    default: return "Checking…";
                }
            }
        }

        /// <summary>显示副标题。</summary>
        public string Subtitle { get { return Detail; } }

        /// <summary>属性变化通知（Availability 更新用）。</summary>
        public event PropertyChangedEventHandler PropertyChanged;
    }
}
