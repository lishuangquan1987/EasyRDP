#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using EasyRDP.Shared;

namespace EasyRDP.Client.Wpf
{
    /// <summary>一条已保存的服务器连接配置。</summary>
    public class ServerProfile
    {
        /// <summary>配置名称（唯一标识，用于替换/删除）。</summary>
        public string Name { get; set; } = "";
        /// <summary>服务器地址（IP 或主机名）。</summary>
        public string Host { get; set; } = "";
        /// <summary>端口号。</summary>
        public string Port { get; set; } = "2000";
        /// <summary>登录用户名。</summary>
        public string Username { get; set; } = "";
        /// <summary>登录密码（写入磁盘前经 Windows DPAPI 加密，仅当前用户可解密）。</summary>
        public string Password { get; set; } = "";

        /// <summary>复制一份（避免列表项被外部修改）。</summary>
        public ServerProfile Clone()
        {
            return new ServerProfile
            {
                Name = Name,
                Host = Host,
                Port = Port,
                Username = Username,
                Password = Password
            };
        }
    }

    /// <summary>
    /// 客户端连接配置持久化：%AppData%\EasyRDP\client\connections.json。
    /// 保存多个服务器配置 + 最后使用的配置名，下次启动自动恢复。
    /// </summary>
    public class ConnectionProfileStore
    {
        private readonly string _filePath;

        public ConnectionProfileStore() : this(DefaultPath())
        {
        }

        public ConnectionProfileStore(string filePath)
        {
            _filePath = filePath;
        }

        /// <summary>默认配置文件路径。</summary>
        public static string DefaultPath()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "EasyRDP", "client");
            return Path.Combine(dir, "connections.json");
        }

        /// <summary>读取全部配置；文件不存在或损坏时返回空列表。</summary>
        public List<ServerProfile> Load(out string lastProfileName)
        {
            lastProfileName = "";
            try
            {
                if (!File.Exists(_filePath))
                    return new List<ServerProfile>();

                string json = File.ReadAllText(_filePath, Encoding.UTF8);
                using (var doc = JsonDocument.Parse(json))
                {
                    var list = new List<ServerProfile>();
                    if (doc.RootElement.TryGetProperty("profiles", out JsonElement arr))
                    {
                        foreach (JsonElement e in arr.EnumerateArray())
                        {
                            list.Add(new ServerProfile
                            {
                                Name = GetString(e, "name"),
                                Host = GetString(e, "host"),
                                Port = GetString(e, "port"),
                                Username = GetString(e, "username"),
                                Password = SecretProtector.Unprotect(GetString(e, "password")) ?? ""
                            });
                        }
                    }
                    if (doc.RootElement.TryGetProperty("lastProfile", out JsonElement lp))
                        lastProfileName = lp.GetString() ?? "";
                    return list;
                }
            }
            catch (Exception ex)
            {
                NLog.LogManager.GetCurrentClassLogger().Warn(ex, "Load profiles failed, using empty list");
                return new List<ServerProfile>();
            }
        }

        /// <summary>持久化配置列表与最后使用的配置名（原子写入：先写唯一临时文件再替换）。</summary>
        /// <returns>true 表示保存成功；false 表示失败（调用方可用状态栏提示）。</returns>
        public bool Save(List<ServerProfile> profiles, string lastProfileName)
        {
            try
            {
                string dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                using (var ms = new MemoryStream())
                {
                    using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
                    {
                        w.WriteStartObject();
                        w.WriteStartArray("profiles");
                        foreach (var p in profiles)
                        {
                            // 密码加密失败时中止保存，避免把密码静默降级为空
                            string encryptedPassword = SecretProtector.Protect(p.Password);
                            if (!string.IsNullOrEmpty(p.Password) && encryptedPassword == null)
                            {
                                NLog.LogManager.GetCurrentClassLogger().Warn(
                                    "Protect password failed for profile '{0}', aborting save", p.Name);
                                return false;
                            }
                            w.WriteStartObject();
                            w.WriteString("name", p.Name ?? "");
                            w.WriteString("host", p.Host ?? "");
                            w.WriteString("port", p.Port ?? "");
                            w.WriteString("username", p.Username ?? "");
                            w.WriteString("password", encryptedPassword ?? "");
                            w.WriteEndObject();
                        }
                        w.WriteEndArray();
                        w.WriteString("lastProfile", lastProfileName ?? "");
                        w.WriteEndObject();
                        w.Flush();
                    }
                    string tmp = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        File.WriteAllBytes(tmp, ms.ToArray());
                        if (File.Exists(_filePath))
                            File.Replace(tmp, _filePath, null);
                        else
                            File.Move(tmp, _filePath);
                    }
                    finally
                    {
                        // 失败时清理残留临时文件
                        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                NLog.LogManager.GetCurrentClassLogger().Warn(ex, "Save profiles failed");
                return false;
            }
        }

        private static string GetString(JsonElement e, string propertyName)
        {
            if (e.TryGetProperty(propertyName, out JsonElement v))
                return v.GetString() ?? "";
            return "";
        }
    }
}
