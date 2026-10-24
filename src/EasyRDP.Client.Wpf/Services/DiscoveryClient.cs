#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using EasyRDP.Core.Discovery;
using NLog;

namespace EasyRDP.Client.Wpf.Services
{
    /// <summary>
    /// 局域网服务端发现：向广播地址（含每块网卡的子网广播地址）发送 UDP 探测，
    /// 在超时窗口内收集应答，按 ip:tcpPort 去重。不改变任何连接状态。
    /// </summary>
    public static class DiscoveryClient
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        /// <summary>默认扫描窗口（毫秒）。</summary>
        public const int DefaultTimeoutMs = 1500;

        /// <summary>
        /// 扫描局域网内的 EasyRDP 服务端。回调逐个返回结果（后台线程），
        /// 调用方可据此增量刷新 UI；扫描结束返回完整列表。
        /// </summary>
        public static Task<List<DiscoveredServer>> ScanAsync(int timeoutMs,
            Action<DiscoveredServer> onFound, CancellationToken cancellationToken)
        {
            if (timeoutMs <= 0) timeoutMs = DefaultTimeoutMs;

            return Task.Run(() =>
            {
                var found = new List<DiscoveredServer>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                UdpClient udp = null;
                try
                {
                    udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
                    udp.EnableBroadcast = true;
                    udp.Client.ReceiveTimeout = 200;

                    byte[] request = DiscoveryProtocol.BuildRequest(out byte[] nonce);
                    var targets = BroadcastTargets().Distinct().ToList();
                    foreach (IPAddress target in targets)
                    {
                        try
                        {
                            udp.Send(request, request.Length,
                                new IPEndPoint(target, DiscoveryConstants.UdpPort));
                        }
                        catch (Exception ex)
                        {
                            Logger.Debug("Discovery send to {0} failed: {1}", target, ex.Message);
                        }
                    }
                    Logger.Debug("Discovery scan: {0} broadcast target(s), timeout={1}ms",
                        targets.Count, timeoutMs);

                    long deadline = Environment.TickCount64 + timeoutMs;
                    while (Environment.TickCount64 < deadline)
                    {
                        if (cancellationToken.IsCancellationRequested) break;
                        try
                        {
                            var remote = new IPEndPoint(IPAddress.Any, 0);
                            byte[] data = udp.Receive(ref remote);
                            DiscoveredServer server;
                            if (!DiscoveryProtocol.TryParseResponse(data, nonce, remote.Address, out server))
                                continue;
                            if (!seen.Add(server.Key))
                                continue;
                            found.Add(server);
                            try { onFound?.Invoke(server); } catch { }
                        }
                        catch (SocketException)
                        {
                            // 200ms 无数据：继续等待剩余窗口
                        }
                        catch (ObjectDisposedException)
                        {
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "Discovery scan failed");
                }
                finally
                {
                    try { if (udp != null) udp.Close(); } catch { }
                }
                return found;
            }, cancellationToken);
        }

        /// <summary>
        /// 计算广播目标：255.255.255.255 + 每块启用网卡的子网广播地址。
        /// 只发 255.255.255.255 在部分交换机/多网卡环境下收不到应答。
        /// </summary>
        public static IEnumerable<IPAddress> BroadcastTargets()
        {
            yield return IPAddress.Broadcast;

            NetworkInterface[] interfaces;
            try { interfaces = NetworkInterface.GetAllNetworkInterfaces(); }
            catch { yield break; }

            foreach (NetworkInterface ni in interfaces)
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                IPInterfaceProperties props;
                try { props = ni.GetIPProperties(); }
                catch { continue; }

                foreach (UnicastIPAddressInformation ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (ua.IPv4Mask == null) continue;
                    IPAddress broadcast = CalcBroadcast(ua.Address, ua.IPv4Mask);
                    if (broadcast != null) yield return broadcast;
                }
            }
        }

        /// <summary>按掩码计算子网广播地址（addr | ~mask）。</summary>
        private static IPAddress CalcBroadcast(IPAddress address, IPAddress mask)
        {
            if (address == null || mask == null) return null;
            byte[] ip = address.GetAddressBytes();
            byte[] mk = mask.GetAddressBytes();
            if (ip.Length != 4 || mk.Length != 4) return null;

            byte[] result = new byte[4];
            for (int i = 0; i < 4; i++)
                result[i] = (byte)(ip[i] | (byte)~mk[i]);
            return new IPAddress(result);
        }
    }
}
