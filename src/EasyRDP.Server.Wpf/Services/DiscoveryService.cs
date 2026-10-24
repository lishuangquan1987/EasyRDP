#nullable disable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using EasyRDP.Core.Discovery;
using NLog;

namespace EasyRDP.Server.Wpf.Services
{
    /// <summary>
    /// 局域网发现服务：监听 UDP 2001，收到探测请求后单播应答（TCP 端口、认证模式、忙、计算机名）。
    /// 只应答私网/回环来源，速率限制每 IP 每秒 10 次，报文不含任何凭据。
    /// 绑定失败（端口被占用/防火墙）只记日志，不影响 TCP 主功能。
    /// </summary>
    public class DiscoveryService : IDisposable
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private readonly Func<int> _tcpPort;
        private readonly Func<byte> _authMode;
        private readonly Func<bool> _busy;
        private readonly string _displayName;
        private readonly object _lock = new object();
        private readonly Dictionary<string, RateWindow> _rateLimits =
            new Dictionary<string, RateWindow>(StringComparer.OrdinalIgnoreCase);

        private Thread _thread;
        private volatile bool _running;
        private UdpClient _udp;

        private class RateWindow
        {
            public long WindowStartTicks;
            public int Count;
        }

        /// <summary>创建发现服务。</summary>
        /// <param name="tcpPort">TCP 业务端口（应答里告诉客户端）。</param>
        /// <param name="authMode">当前认证模式（0=口令，1=审批）。</param>
        /// <param name="busy">是否已达会话上限。</param>
        /// <param name="displayName">对外显示名（默认计算机名）。</param>
        public DiscoveryService(Func<int> tcpPort, Func<byte> authMode, Func<bool> busy, string displayName)
        {
            _tcpPort = tcpPort;
            _authMode = authMode;
            _busy = busy;
            _displayName = string.IsNullOrWhiteSpace(displayName) ? Environment.MachineName : displayName;
        }

        /// <summary>是否正在监听。</summary>
        public bool IsRunning
        {
            get { return _running; }
        }

        /// <summary>启动监听（幂等）。</summary>
        public void Start()
        {
            lock (_lock)
            {
                if (_running) return;
                _running = true;

                _thread = new Thread(ListenLoop);
                _thread.IsBackground = true;
                _thread.Name = "EasyRDP-Discovery";
                _thread.Start();
            }
        }

        /// <summary>停止监听（幂等）。</summary>
        public void Stop()
        {
            Thread thread;
            lock (_lock)
            {
                if (!_running) return;
                _running = false;
                thread = _thread;
                _thread = null;
                try { if (_udp != null) _udp.Close(); } catch { }
            }
            try { if (thread != null && thread.IsAlive) thread.Join(1500); } catch { }
        }

        /// <summary>释放资源。</summary>
        public void Dispose()
        {
            Stop();
        }

        private void ListenLoop()
        {
            try
            {
                var udp = new UdpClient();
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryConstants.UdpPort));
                udp.Client.ReceiveTimeout = 1000;
                _udp = udp;
                Logger.Info("Discovery listening on UDP {0} as '{1}'", DiscoveryConstants.UdpPort, _displayName);

                while (_running)
                {
                    try
                    {
                        IPEndPoint remote = null;
                        byte[] data = udp.Receive(ref remote);
                        byte[] nonce;
                        if (!DiscoveryProtocol.TryParseRequest(data, out nonce))
                            continue;
                        if (remote == null || !DiscoveryProtocol.IsPrivateOrLoopback(remote.Address))
                            continue;
                        if (!AllowRate(remote.Address))
                        {
                            Logger.Debug("Discovery request rate-limited from {0}", remote.Address);
                            continue;
                        }

                        int port = 0;
                        byte authMode = 0;
                        bool busy = false;
                        try
                        {
                            port = _tcpPort != null ? _tcpPort() : 0;
                            authMode = _authMode != null ? _authMode() : (byte)0;
                            busy = _busy != null && _busy();
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn(ex, "Discovery: read server state failed");
                        }
                        if (port <= 0 || port > 65535)
                            continue;

                        byte[] resp = DiscoveryProtocol.BuildResponse(nonce, port, authMode, busy, _displayName);
                        udp.Send(resp, resp.Length, remote);
                    }
                    catch (SocketException)
                    {
                        // ReceiveTimeout 或套接字关闭：继续下一轮（_running 控制退出）
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, "Discovery loop error");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Discovery listener failed to start (UDP {0}): {1}",
                    DiscoveryConstants.UdpPort, ex.Message);
            }
            finally
            {
                try { if (_udp != null) _udp.Close(); } catch { }
                _udp = null;
                Logger.Info("Discovery listener stopped");
            }
        }

        /// <summary>每 IP 每秒最多 10 次应答，避免被扫描放大。</summary>
        private bool AllowRate(IPAddress address)
        {
            string key = address.ToString();
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            lock (_rateLimits)
            {
                RateWindow window;
                if (!_rateLimits.TryGetValue(key, out window))
                {
                    window = new RateWindow { WindowStartTicks = now, Count = 0 };
                    _rateLimits[key] = window;
                }
                if ((now - window.WindowStartTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency >= 1000)
                {
                    window.WindowStartTicks = now;
                    window.Count = 0;
                }
                window.Count++;
                if (_rateLimits.Count > 256)
                {
                    // 简单的容量保护：太多来源时清空（发现是低频操作）
                    _rateLimits.Clear();
                }
                return window.Count <= 10;
            }
        }
    }
}
