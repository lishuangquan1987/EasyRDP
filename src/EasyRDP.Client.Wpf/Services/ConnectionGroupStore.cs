#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using NLog;

namespace EasyRDP.Client.Wpf.Services
{
    /// <summary>
    /// 连接分组名持久化：%AppData%\EasyRDP\client\groups.json。
    /// 分组名单独存储，允许存在空分组（连接本身的分组字段在 connections.json）。
    /// </summary>
    public class ConnectionGroupStore
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly string _filePath;

        /// <summary>默认路径（与 connections.json 同目录）。</summary>
        public ConnectionGroupStore() : this(DefaultPath())
        {
        }

        /// <summary>指定路径（测试用）。</summary>
        public ConnectionGroupStore(string filePath)
        {
            _filePath = filePath;
        }

        /// <summary>默认分组文件路径。</summary>
        public static string DefaultPath()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "EasyRDP", "client");
            return Path.Combine(dir, "groups.json");
        }

        /// <summary>读取分组名列表；文件不存在或损坏时返回空列表。</summary>
        public List<string> Load()
        {
            var result = new List<string>();
            try
            {
                if (!File.Exists(_filePath))
                    return result;
                string json = File.ReadAllText(_filePath, Encoding.UTF8);
                using (var doc = JsonDocument.Parse(json))
                {
                    if (doc.RootElement.TryGetProperty("groups", out JsonElement arr)
                        && arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement e in arr.EnumerateArray())
                        {
                            string name = e.GetString();
                            if (!string.IsNullOrWhiteSpace(name) && !result.Contains(name))
                                result.Add(name.Trim());
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Load connection groups failed, using empty list");
            }
            return result;
        }

        /// <summary>保存分组名列表（原子写入）。失败只记日志，不影响连接使用。</summary>
        public bool Save(List<string> groups)
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
                        w.WriteStartArray("groups");
                        if (groups != null)
                        {
                            foreach (string g in groups)
                            {
                                if (!string.IsNullOrWhiteSpace(g))
                                    w.WriteStringValue(g.Trim());
                            }
                        }
                        w.WriteEndArray();
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
                        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Save connection groups failed");
                return false;
            }
        }
    }
}
