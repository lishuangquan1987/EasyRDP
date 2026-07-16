using System.ComponentModel;

namespace EasyRDP.Server.Wpf.Models
{
    /// <summary>
    /// 服务端配置模型。
    /// </summary>
    public class ServerConfigModel : INotifyPropertyChanged
    {
        private int _port = 8750;
        private string _authToken = "easyrdp-demo";
        private string _compressType = "Zlib";
        private int _frameRate = 15;
        private int _maxClients = 0;

        public int Port
        {
            get { return _port; }
            set { if (_port != value) { _port = value; OnPropertyChanged("Port"); } }
        }

        public string AuthToken
        {
            get { return _authToken; }
            set { if (_authToken != value) { _authToken = value; OnPropertyChanged("AuthToken"); } }
        }

        public string CompressType
        {
            get { return _compressType; }
            set { if (_compressType != value) { _compressType = value; OnPropertyChanged("CompressType"); } }
        }

        public int FrameRate
        {
            get { return _frameRate; }
            set { if (_frameRate != value) { _frameRate = value; OnPropertyChanged("FrameRate"); } }
        }

        public int MaxClients
        {
            get { return _maxClients; }
            set { if (_maxClients != value) { _maxClients = value; OnPropertyChanged("MaxClients"); } }
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
