#nullable disable
using System.ComponentModel;

namespace EasyRDP.Server.Wpf
{
    /// <summary>
    /// Session list item for UI binding.
    /// </summary>
    public class SessionItem : INotifyPropertyChanged
    {
        /// <summary>Session ID（用于去重匹配）。</summary>
        public uint IdValue { get; set; }
        private string _id = "";
        private string _remote = "";
        private string _codec = "";
        private string _resolution = "";
        private int _frames;

        public string Id { get { return _id; } set { _id = value; OnChanged(nameof(Id)); } }
        public string Remote { get { return _remote; } set { _remote = value; OnChanged(nameof(Remote)); } }
        public string Codec { get { return _codec; } set { _codec = value; OnChanged(nameof(Codec)); } }
        public string Resolution { get { return _resolution; } set { _resolution = value; OnChanged(nameof(Resolution)); } }
        public int Frames { get { return _frames; } set { _frames = value; OnChanged(nameof(Frames)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
