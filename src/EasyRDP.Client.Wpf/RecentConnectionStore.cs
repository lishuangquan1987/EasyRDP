using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using NLog;

namespace EasyRDP.Client.Wpf
{
    /// <summary>
    /// 最近连接历史持久化：%AppData%\EasyRDP\client\recent.json。
    /// 轻量 JSON 读写，失败时降级为内存列表。
    /// </summary>
    public class RecentConnectionStore
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        /// <summary>最大保留条数。</summary>
        public const int MaxEntries = 50;

        /// <summary>默认配置文件路径。</summary>
        public static string DefaultPath()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "EasyRDP", "client");
            return Path.Combine(dir, "recent.json");
        }

        private readonly string _filePath;

        public RecentConnectionStore() : this(DefaultPath())
        {
        }

        public RecentConnectionStore(string filePath)
        {
            _filePath = filePath ?? throw new ArgumentNullException("filePath");
        }

        /// <summary>
        /// 读取最近连接列表；文件不存在或损坏时返回空列表。
        /// </summary>
        public List<RecentConnection> Load()
        {
            try
            {
                if (!File.Exists(_filePath))
                    return new List<RecentConnection>();

                string json = File.ReadAllText(_filePath, Encoding.UTF8);
                var list = JsonSerializer.Deserialize<List<RecentConnection>>(json);
                if (list != null)
                {
                    // 防御脏数据
                    foreach (var item in list)
                    {
                        if (item.Host != null)
                            item.Host = item.Host.Trim();
                    }
                    return list.Where(r => !string.IsNullOrWhiteSpace(r.Host)).ToList();
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Load recent connections failed, using empty list");
            }
            return new List<RecentConnection>();
        }

        /// <summary>
        /// 保存最近连接列表；自动去重、按最后连接时间倒序、截断到 MaxEntries。
        /// </summary>
        public void Save(List<RecentConnection> list)
        {
            try
            {
                if (list == null) list = new List<RecentConnection>();

                var normalized = list
                    .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Host))
                    .Select(r =>
                    {
                        r.Host = r.Host.Trim();
                        if (string.IsNullOrWhiteSpace(r.DisplayName))
                            r.DisplayName = r.Host;
                        return r;
                    })
                    .GroupBy(r => r.Host, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderByDescending(r => r.LastConnectedUtc).First())
                    .OrderByDescending(r => r.LastConnectedUtc)
                    .Take(MaxEntries)
                    .ToList();

                string dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string json = JsonSerializer.Serialize(normalized, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_filePath, json, Encoding.UTF8);
            }
            catch (Exception ex)
                
            {
                Logger.Warn(ex, "Save recent connections failed");
            }
        }

        /// <summary>
        /// 记录一次成功连接：置顶或新建条目，更新时间戳。
        /// </summary>
        public void Touch(string host, string displayName)
        {
            if (string.IsNullOrWhiteSpace(host)) return;
            var list = Load();
            string h = host.Trim();
            var existing = list.FirstOrDefault(r =>
                string.Equals(r.Host, h, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.LastConnectedUtc = DateTime.UtcNow;
                if (!string.IsNullOrWhiteSpace(displayName))
                    existing.DisplayName = displayName.Trim();
            }
            else
            {
                list.Add(new RecentConnection
                {
                    Host = h,
                    DisplayName = !string.IsNullOrWhiteSpace(displayName) ? displayName.Trim() : h,
                    LastConnectedUtc = DateTime.UtcNow
                });
            }
            Save(list);
        }
    }
}
