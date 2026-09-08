using System;
using System.Windows.Media.Imaging;

namespace EasyRDP.Client.Wpf
{
    /// <summary>
    /// 最近连接项（主页缩略图网格数据）。
    /// 与 ServerProfile 解耦：ServerProfile 是用户保存的“配置”，
    /// RecentConnection 是用户实际连接过的“历史”，可独立持久化。
    /// </summary>
    public class RecentConnection
    {
        /// <summary>目标主机地址（IP 或主机名）。</summary>
        public string Host { get; set; }

        /// <summary>显示名称（可选，为空时 UI 回退到 Host）。</summary>
        public string DisplayName { get; set; }

        /// <summary>最近一次成功连接的 UTC 时间。</summary>
        public DateTime LastConnectedUtc { get; set; }

        /// <summary>本地缩略图缓存文件路径（相对或绝对）。</summary>
        public string ThumbnailPath { get; set; }

        /// <summary>缩略图图像源（运行时加载，不序列化）。</summary>
        public BitmapImage ThumbnailSource { get; set; }

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
    }
}
