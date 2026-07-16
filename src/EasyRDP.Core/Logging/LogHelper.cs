using System;

namespace EasyRDP.Core.Logging
{
    /// <summary>
    /// 静态日志门面 — 封装 NLog，日志文件写入程序目录下 Log/ 文件夹。
    /// 静态构造函数中自动完成 NLog 程序化配置，无需 NLog.config 文件。
    /// </summary>
    public static class LogHelper
    {
        private static readonly NLog.Logger Logger;

        static LogHelper()
        {
            // 仅在尚未配置时初始化（避免覆盖用户自带的 NLog.config）
            if (NLog.LogManager.Configuration == null)
            {
                var config = new NLog.Config.LoggingConfiguration();

                var fileTarget = new NLog.Targets.FileTarget("file")
                {
                    FileName = AppDomain.CurrentDomain.BaseDirectory + @"Log\${shortdate}.log",
                    Layout = "${longdate} [${level:uppercase=true}] ${message} ${exception:format=tostring}",
                    MaxArchiveFiles = 7,
                    ArchiveEvery = NLog.Targets.FileArchivePeriod.Day,
                    ArchiveNumbering = NLog.Targets.ArchiveNumberingMode.Date,
                    Encoding = System.Text.Encoding.UTF8
                };

                config.AddTarget(fileTarget);
                config.AddRule(NLog.LogLevel.Debug, NLog.LogLevel.Fatal, fileTarget);

                NLog.LogManager.Configuration = config;
            }

            Logger = NLog.LogManager.GetLogger("EasyRDP");
        }

        /// <summary>调试信息（仅开发排查用）。</summary>
        public static void Debug(string message)
        {
            Logger.Debug(message);
        }

        /// <summary>一般信息（运行状态、关键节点）。</summary>
        public static void Info(string message)
        {
            Logger.Info(message);
        }

        /// <summary>警告（非致命异常、可恢复错误）。</summary>
        public static void Warn(string message)
        {
            Logger.Warn(message);
        }

        /// <summary>错误（需要关注的异常）。</summary>
        public static void Error(string message)
        {
            Logger.Error(message);
        }

        /// <summary>错误（附带异常详情）。</summary>
        public static void Error(Exception ex, string message)
        {
            Logger.Error(ex, message);
        }
    }
}
