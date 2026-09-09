using System;
using System.ComponentModel;
using System.Windows.Media.Imaging;

namespace EasyRDP.Client.Wpf
{
    /// <summary>
    /// 最近连接项（浏览窗口缩略图网格数据）。
    /// 与 ServerProfile 解耦：ServerProfile 是用户保存的“配置”，
    /// RecentConnection 是用户实际连接过的“历史”，可独立持久化。
    /// </summary>
    public class RecentConnection : INotifyPropertyChanged
    {
        private bool _isSelected;
        private bool _isActive;
        private BitmapImage _thumbnailSource;

        /// <summary>目标主机地址（IP 或主机名）。</summary>
        public string Host { get; set; }

        /// <summary>连接端口（默认 2000）。</summary>
        public string Port { get; set; }

        /// <summary>登录用户名（可选）。</summary>
        public string Username { get; set; }

        /// <summary>登录密码（可选；本地明文存储，同 ServerProfile 策略）。</summary>
        public string Password { get; set; }

        /// <summary>显示名称（可选，为空时 UI 回退到 Host）。</summary>
        public string DisplayName { get; set; }

        /// <summary>最近一次成功连接的 UTC 时间。</summary>
        public DateTime LastConnectedUtc { get; set; }

        /// <summary>本地缩略图缓存文件路径（相对或绝对）。</summary>
        public string ThumbnailPath { get; set; }

        /// <summary>缩略图图像源（运行时加载，不序列化）。</summary>
        public BitmapImage ThumbnailSource
        {
            get { return _thumbnailSource; }
            set { _thumbnailSource = value; OnPropertyChanged(nameof(ThumbnailSource)); }
        }

        /// <summary>卡片选中态（灰色高亮）。</summary>
        public bool IsSelected
        {
            get { return _isSelected; }
            set { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); }
        }

        /// <summary>活动状态（卡片左上角红色圆点，表示正在连接/在线）。</summary>
        public bool IsActive
        {
            get { return _isActive; }
            set { _isActive = value; OnPropertyChanged(nameof(IsActive)); }
        }

        /// <summary>卡片显示文本：重命名后显示 DisplayName，否则显示 Host。</summary>
        public string DisplayText
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(DisplayName)
                    && !string.Equals(DisplayName.Trim(), Host, StringComparison.OrdinalIgnoreCase))
                    return DisplayName.Trim();
                return Host;
            }
        }

        /// <summary>最后连接时间本地化文本（UI 显示）。</summary>
        public string LastConnectedText
        {
            get
            {
                if (LastConnectedUtc == default(DateTime)) return "未知";
                DateTime local = LastConnectedUtc.ToLocalTime();
                if (local.Date == DateTime.Today) return local.ToString("HH:mm");
                return local.ToString("MM-dd HH:mm");
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged(string name)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }
    }
}
