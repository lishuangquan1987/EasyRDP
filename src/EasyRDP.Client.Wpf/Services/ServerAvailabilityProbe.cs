#nullable disable
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NLog;

namespace EasyRDP.Client.Wpf.Services
{
    /// <summary>服务端在线状态。</summary>
    public enum ServerAvailability
    {
        /// <summary>尚未探测/探测中。</summary>
        Unknown = 0,
        /// <summary>TCP 端口可连接。</summary>
        Online = 1,
        /// <summary>连接被拒或超时。</summary>
        Offline = 2
    }

    /// <summary>
    /// 服务端在线探测：对 host:port 做一次 TCP 连接测试。
    /// 不发送任何协议数据、不占用服务端会话名额；端口可连接即视为在线。
    /// </summary>
    public static class ServerAvailabilityProbe
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        /// <summary>探测单个端点（阻塞，超时返回 false）。</summary>
        public static bool Probe(string host, int port, int timeoutMs)
        {
            if (string.IsNullOrWhiteSpace(host) || port <= 0 || port > 65535)
                return false;

            TcpClient client = null;
            try
            {
                client = new TcpClient();
                var result = client.BeginConnect(host, port, null, null);
                if (!result.AsyncWaitHandle.WaitOne(timeoutMs))
                {
                    try { client.Close(); } catch { }
                    // 关闭会中止挂起的连接，异步操作随后以异常完成；
                    // 必须在后台调用一次 EndConnect「观察」该异常，否则会触发进程级未观察异常
                    var aborted = client;
                    ThreadPool.QueueUserWorkItem(state =>
                    {
                        try { aborted.EndConnect(result); } catch { }
                        try { aborted.Close(); } catch { }
                    });
                    return false;
                }

                client.EndConnect(result);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Debug("Availability probe failed {0}:{1} — {2}", host, port, ex.Message);
                return false;
            }
            finally
            {
                try { if (client != null) client.Close(); } catch { }
            }
        }

        /// <summary>
        /// 批量探测（并发上限 <paramref name="maxConcurrency"/>）。
        /// 每个端点完成后回调一次（回调在后台线程，调用方自行切回 UI 线程）。
        /// </summary>
        public static void ProbeMany(IList<KeyValuePair<string, int>> targets,
            int timeoutMs, int maxConcurrency, Action<string, int, bool> onResult)
        {
            if (targets == null || targets.Count == 0) return;

            Task.Run(() =>
            {
                try
                {
                    var options = new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Math.Max(1, Math.Min(maxConcurrency, targets.Count))
                    };
                    Parallel.ForEach(targets, options, target =>
                    {
                        bool online;
                        try { online = Probe(target.Key, target.Value, timeoutMs); }
                        catch (Exception ex)
                        {
                            Logger.Warn(ex, "Availability probe threw for {0}:{1}", target.Key, target.Value);
                            online = false;
                        }
                        try { onResult(target.Key, target.Value, online); }
                        catch (Exception ex) { Logger.Warn(ex, "Availability callback failed"); }
                    });
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "ProbeMany failed");
                }
            });
        }
    }
}
