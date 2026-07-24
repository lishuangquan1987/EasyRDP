using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using EasyDesk.Core;
using EasyDesk.Windows;

namespace EasyRDP.Server.Wpf;

/// <summary>
/// 服务端主窗口。专业仪表盘界面。
/// </summary>
public partial class MainWindow : Window
{
    private CaptureService? _captureService;
    private TcpTransportServer? _transportServer;
    private TransportHost? _transportHost;
    private readonly ObservableCollection<SessionItem> _sessions = new ObservableCollection<SessionItem>();
    private readonly ObservableCollection<string> _logEntries = new ObservableCollection<string>();
    private DateTime _startTime;
    private DispatcherTimer? _uptimeTimer;

    public MainWindow()
    {
        InitializeComponent();
        SessionList.ItemsSource = _sessions;
        LogList.ItemsSource = _logEntries;
    }

    private void StartBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortBox.Text, out int port))
            port = 2000;

        try
        {
            var factory = new WindowsDesktopFactory();
            var capturer = factory.CreateScreenCapturer();
            var inputSim = factory.CreateInputSimulator();

            _captureService = new CaptureService(capturer);
            _captureService.Start();

            _transportServer = new TcpTransportServer();
            _transportServer.OnLog = (msg) => Dispatcher.Invoke(() => AddLog(msg));

            _transportHost = new TransportHost(_captureService, _transportServer, inputSim);
            _transportHost.Start(port);

            _startTime = DateTime.Now;
            _uptimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _uptimeTimer.Tick += (s, ev) => UpdateUptime();
            _uptimeTimer.Start();

            StopBtn.IsEnabled = true;
            PortBox.IsEnabled = false;
            StatusLabel.Text = "Running";
            PortLabel.Text = port.ToString();
            SessionCountLabel.Text = "0";
            AddLog("Server started on port " + port);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to start: " + ex.Message, "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StopBtn_Click(object sender, RoutedEventArgs e)
    {
        _uptimeTimer?.Stop();
        _transportHost?.Stop();
        _transportHost = null;
        _transportServer?.Dispose();
        _transportServer = null;
        _captureService?.Stop();
        _captureService = null;

        _sessions.Clear();
        StopBtn.IsEnabled = false;
        PortBox.IsEnabled = true;
        StatusLabel.Text = "Stopped";
        PortLabel.Text = "—";
        SessionCountLabel.Text = "0";
        UptimeLabel.Text = "—";
        AddLog("Server stopped");
    }

    private void UpdateUptime()
    {
        var elapsed = DateTime.Now - _startTime;
        UptimeLabel.Text = string.Format("{0:D2}:{1:D2}:{2:D2}",
            elapsed.Hours, elapsed.Minutes, elapsed.Seconds);
    }

    private void AddLog(string message)
    {
        var entry = string.Format("[{0:HH:mm:ss}] {1}", DateTime.Now, message);
        _logEntries.Insert(0, entry);
        if (_logEntries.Count > 200)
            _logEntries.RemoveAt(_logEntries.Count - 1);
        LogLine.Text = message;
    }
}

/// <summary>
/// Session list item for UI binding.
/// </summary>
public class SessionItem : INotifyPropertyChanged
{
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

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string name)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
