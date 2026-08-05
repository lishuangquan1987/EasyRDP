#nullable disable
using System;
using System.IO;
using System.Text;
using EasyRDP.Core.Protocol;
using EasyRDP.Shared;

namespace EasyRDP.Server.Wpf
{
    /// <summary>服务端可持久化设置。</summary>
    public class ServerSettings
    {
        /// <summary>监听端口。</summary>
        public string Port { get; set; }

        /// <summary>认证用户名。</summary>
        public string Username { get; set; }

        /// <summary>认证密码（写入磁盘前经 Windows DPAPI 加密，仅当前用户可解密）。</summary>
        public string Password { get; set; }

        /// <summary>
        /// 帧变化检测模式。控制 ServerStreamSession 在编码前如何判断画面是否变化。
        /// FullFrameMemcmp=原始方式（全帧 memcmp），BlockHashDirtyRect=改进方式（32×32 块哈希）。
        /// 切换在下次会话建立时生效。
        /// </summary>
        public ChangeDetectionMode ChangeDetectionMode { get; set; }

        public ServerSettings()
        {
            Port = "2000";
            Username = "";
            Password = "";
            // 默认原始方式：保持与历史版本完全一致的行为，避免引入潜在回归
            ChangeDetectionMode = ChangeDetectionMode.FullFrameMemcmp;
        }
    }

    /// <summary>
    /// 服务端设置持久化：%AppData%\EasyRDP\server\settings.json。
    /// 使用内置的迷你 JSON 读写（设置结构固定为三个字符串字段），
    /// 避免 net40 目标引入第三方 JSON 依赖或 System.Web 引用破坏 XAML 编译。
    /// </summary>
    public class ServerSettingsStore
    {
        private readonly string _filePath;

        public ServerSettingsStore() : this(DefaultPath())
        {
        }

        public ServerSettingsStore(string filePath)
        {
            _filePath = filePath;
        }

        /// <summary>默认配置文件路径。</summary>
        public static string DefaultPath()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "EasyRDP", "server");
            return Path.Combine(dir, "settings.json");
        }

        /// <summary>读取设置；文件不存在或损坏时返回默认值。</summary>
        public ServerSettings Load()
        {
            try
            {
                if (!File.Exists(_filePath))
                    return new ServerSettings();
                string json = File.ReadAllText(_filePath, Encoding.UTF8);
                return ParseJson(json);
            }
            catch (Exception ex)
            {
                NLog.LogManager.GetCurrentClassLogger().Warn(ex, "Load server settings failed, using defaults");
                return new ServerSettings();
            }
        }

        /// <summary>保存设置（原子写入：先写唯一临时文件再替换）。</summary>
        /// <returns>true 表示保存成功；false 表示失败。</returns>
        public bool Save(ServerSettings settings)
        {
            try
            {
                string dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                // 密码加密失败时中止保存，避免把密码静默降级为空
                string encryptedPassword = SecretProtector.Protect(settings.Password);
                if (!string.IsNullOrEmpty(settings.Password) && encryptedPassword == null)
                {
                    NLog.LogManager.GetCurrentClassLogger().Warn("Protect password failed, aborting settings save");
                    return false;
                }

                string json = SerializeJson(settings, encryptedPassword);

                string tmp = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllBytes(tmp, Encoding.UTF8.GetBytes(json));
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
                return true;
            }
            catch (Exception ex)
            {
                NLog.LogManager.GetCurrentClassLogger().Warn(ex, "Save server settings failed");
                return false;
            }
        }

        /// <summary>序列化为 JSON 对象（端口/用户名/密码 + 变化检测模式）。</summary>
        private static string SerializeJson(ServerSettings s, string encryptedPassword)
        {
            var sb = new StringBuilder(192);
            sb.Append('{');
            sb.Append("\"Port\":\"").Append(Escape(s.Port)).Append("\",");
            sb.Append("\"Username\":\"").Append(Escape(s.Username)).Append("\",");
            sb.Append("\"Password\":\"").Append(Escape(encryptedPassword ?? "")).Append("\",");
            // 整型枚举以数字形式序列化，老版本读取时会被忽略（解析时仅识别已知键）
            sb.Append("\"ChangeDetectionMode\":").Append((int)s.ChangeDetectionMode);
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>从 JSON 解析字段（容忍格式差异，字段缺失保持默认值）。</summary>
        private static ServerSettings ParseJson(string json)
        {
            var s = new ServerSettings();
            if (string.IsNullOrEmpty(json)) return s;

            string key = null;
            bool expectValue = false;
            int i = 0;
            while (i < json.Length)
            {
                char c = json[i];
                if (c == '"')
                {
                    int j = i + 1;
                    var sb = new StringBuilder();
                    while (j < json.Length && json[j] != '"')
                    {
                        if (json[j] == '\\' && j + 1 < json.Length)
                        {
                            char esc = json[j + 1];
                            if (esc == 'u' && j + 5 < json.Length)
                            {
                                // \uXXXX 十六进制转义
                                string hex = json.Substring(j + 2, 4);
                                sb.Append((char)Convert.ToInt32(hex, 16));
                                j += 6;
                            }
                            else
                            {
                                sb.Append(esc);
                                j += 2;
                            }
                            continue;
                        }
                        sb.Append(json[j]);
                        j++;
                    }
                    string token = sb.ToString();
                    if (expectValue && key != null)
                    {
                        if (key == "Port") s.Port = token;
                        else if (key == "Username") s.Username = token;
                        else if (key == "Password") s.Password = SecretProtector.Unprotect(token) ?? "";
                        key = null;
                        expectValue = false;
                    }
                    else
                    {
                        key = token;
                    }
                    i = j + 1; // 跳过结束引号，避免同一字符串被重复解析（曾导致死循环）
                }
                else if (c == ':')
                {
                    expectValue = true;
                    i++;
                }
                else if (expectValue && key != null && (char.IsDigit(c) || c == '-'))
                {
                    // 非引号值（整数）：解析到 ',' 或 '}' 为止
                    int j = i;
                    var numSb = new StringBuilder(8);
                    while (j < json.Length && json[j] != ',' && json[j] != '}' && !char.IsWhiteSpace(json[j]))
                    {
                        numSb.Append(json[j]);
                        j++;
                    }
                    int num;
                    if (int.TryParse(numSb.ToString(), out num))
                    {
                        if (key == "ChangeDetectionMode")
                        {
                            // 枚举值范围校验：未知值回退到默认（FullFrameMemcmp）
                            if (num >= 0 && num <= (int)ChangeDetectionMode.BlockHashDirtyRect)
                                s.ChangeDetectionMode = (ChangeDetectionMode)num;
                        }
                    }
                    key = null;
                    expectValue = false;
                    i = j;
                }
                else
                {
                    i++;
                }
            }
            return s;
        }

        /// <summary>JSON 字符串转义。</summary>
        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var sb = new StringBuilder(value.Length + 8);
            foreach (char c in value)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }
}
