using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using EasyRDP.Client.Wpf.Models;
using EasyRDP.Shared;
using NLog;

namespace EasyRDP.Client.Wpf.Services
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
        /// 持久化 DTO：与 RecentConnection 分离，避免把运行时属性（缩略图 ImageSource、
        /// 选中态）写进 JSON，同时承载"密码是否已加密"标记以兼容旧明文文件。
        /// </summary>
        private sealed class PersistedRecent
        {
            public string Host { get; set; } = string.Empty;
            public string Port { get; set; } = "2000";
            public string? Username { get; set; }
            public string? Password { get; set; }
            /// <summary>Password 是否为 DPAPI 密文（旧文件缺省 false = 明文，下次保存时自动加密）。</summary>
            public bool PasswordEncrypted { get; set; }
            /// <summary>是否记住密码（旧文件缺少该键时保持 true，避免升级后丢失已保存凭据）。</summary>
            public bool SavePassword { get; set; } = true;
            public string? DisplayName { get; set; }
            public DateTime LastConnectedUtc { get; set; }
            public string? ThumbnailPath { get; set; }
        }

        /// <summary>
        /// 读取最近连接列表；文件不存在或损坏时返回空列表。密码经 DPAPI 解密。
        /// </summary>
        public List<RecentConnection> Load()
        {
            try
            {
                if (!File.Exists(_filePath))
                    return new List<RecentConnection>();

                string json = File.ReadAllText(_filePath, Encoding.UTF8);
                var stored = JsonSerializer.Deserialize<List<PersistedRecent>>(json);
                if (stored != null)
                {
                    var list = new List<RecentConnection>(stored.Count);
                    foreach (var item in stored)
                    {
                        if (item == null || string.IsNullOrWhiteSpace(item.Host))
                            continue;
                        string? password = item.Password;
                        if (item.PasswordEncrypted && !string.IsNullOrEmpty(password))
                        {
                            // 解密失败（换用户/换机器）时清空，避免把密文当口令发送
                            password = SecretProtector.Unprotect(password);
                            if (password == null)
                            {
                                Logger.Warn("Unprotect recent password failed for host {0}", item.Host);
                                password = null;
                            }
                        }
                        list.Add(new RecentConnection
                        {
                            Host = item.Host.Trim(),
                            Port = string.IsNullOrWhiteSpace(item.Port) ? "2000" : item.Port,
                            Username = item.Username,
                            Password = password,
                            SavePassword = item.SavePassword,
                            DisplayName = item.DisplayName,
                            LastConnectedUtc = item.LastConnectedUtc,
                            ThumbnailPath = item.ThumbnailPath
                        });
                    }
                    return list;
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
        /// 密码写入前用 DPAPI 加密（与 connections.json 策略一致）。
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

                var stored = new List<PersistedRecent>(normalized.Count);
                foreach (var r in normalized)
                {
                    string? encrypted = null;
                    if (r.SavePassword && !string.IsNullOrEmpty(r.Password))
                    {
                        encrypted = SecretProtector.Protect(r.Password);
                        if (encrypted == null)
                        {
                            // 加密失败时不落盘密码（宁可下次要求重输，也不写明文）
                            Logger.Warn("Protect recent password failed for host {0}; password not saved", r.Host);
                        }
                    }
                    stored.Add(new PersistedRecent
                    {
                        Host = r.Host,
                        Port = r.Port,
                        Username = r.Username,
                        Password = encrypted,
                        PasswordEncrypted = !string.IsNullOrEmpty(encrypted),
                        SavePassword = r.SavePassword,
                        DisplayName = r.DisplayName,
                        LastConnectedUtc = r.LastConnectedUtc,
                        ThumbnailPath = r.ThumbnailPath
                    });
                }

                string dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string json = JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_filePath, json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Save recent connections failed");
            }
        }

        /// <summary>
        /// 记录一次成功连接：置顶或新建条目，更新时间戳，并持久化本次连接使用的
        /// 端口/用户名/密码（新建条目完整落盘；已存在条目补齐空字段并刷新时间戳）。
        /// </summary>
        public void Touch(string host, string? displayName, string? port = null,
            string? username = null, string? password = null)
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
                // 仅补齐空字段：卡片编辑（右键 Edit）保存的凭据是用户显式配置，
                // 不应被本次连接使用的值覆盖；地址栏直连（无凭据）时此处补上空值无害。
                if (!string.IsNullOrWhiteSpace(port))
                    existing.Port = port.Trim();
                if (!string.IsNullOrWhiteSpace(username))
                    existing.Username = username.Trim();
                // 用户在该条目上取消了"记住密码"时不回填，避免下次启动又把口令写盘
                if (existing.SavePassword && !string.IsNullOrEmpty(password))
                    existing.Password = password;
            }
            else
            {
                list.Add(new RecentConnection
                {
                    Host = h,
                    DisplayName = !string.IsNullOrWhiteSpace(displayName) ? displayName.Trim() : h,
                    Port = string.IsNullOrWhiteSpace(port) ? "2000" : port.Trim(),
                    Username = !string.IsNullOrWhiteSpace(username) ? username.Trim() : null,
                    Password = !string.IsNullOrEmpty(password) ? password : null,
                    LastConnectedUtc = DateTime.UtcNow
                });
            }
            Save(list);
        }

        /// <summary>
        /// 按 Host 移除一条最近连接记录（用于右键删除）。
        /// </summary>
        public void Remove(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return;
            var list = Load();
            string h = host.Trim();
            var filtered = list.Where(r =>
                !string.Equals(r.Host, h, StringComparison.OrdinalIgnoreCase)).ToList();
            if (filtered.Count != list.Count)
                Save(filtered);
        }
    }
}
