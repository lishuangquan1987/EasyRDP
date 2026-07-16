using System;
using System.ComponentModel;

namespace EasyRDP.Server.Wpf.Models
{
    /// <summary>
    /// 客户端会话信息（绑定到 ListView）。
    /// </summary>
    public class ClientSessionModel : INotifyPropertyChanged
    {
        private uint _sessionId;
        private string _remoteEndPoint;
        private DateTime _connectedAt;
        private bool _isAuthenticated;
        private int _frameCount;
        private DateTime _lastFrameAt;

        public uint SessionId
        {
            get { return _sessionId; }
            set { _sessionId = value; OnPropertyChanged("SessionId"); }
        }

        public string RemoteEndPoint
        {
            get { return _remoteEndPoint; }
            set { _remoteEndPoint = value; OnPropertyChanged("RemoteEndPoint"); }
        }

        public DateTime ConnectedAt
        {
            get { return _connectedAt; }
            set { _connectedAt = value; OnPropertyChanged("ConnectedAt"); }
        }

        public bool IsAuthenticated
        {
            get { return _isAuthenticated; }
            set { _isAuthenticated = value; OnPropertyChanged("IsAuthenticated"); OnPropertyChanged("DisplayStatus"); }
        }

        public int FrameCount
        {
            get { return _frameCount; }
            set { _frameCount = value; OnPropertyChanged("FrameCount"); }
        }

        public DateTime LastFrameAt
        {
            get { return _lastFrameAt; }
            set { _lastFrameAt = value; OnPropertyChanged("LastFrameAt"); }
        }

        /// <summary>显示用状态文本。</summary>
        public string DisplayStatus
        {
            get { return _isAuthenticated ? "已认证" : "握手中"; }
        }

        /// <summary>连接时间显示。</summary>
        public string ConnectedAtDisplay
        {
            get { return _connectedAt.ToString("HH:mm:ss"); }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged(string name)
        {
            var handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(name));
        }
    }
}
