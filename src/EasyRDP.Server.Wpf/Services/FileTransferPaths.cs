#nullable disable
using System;
using System.IO;

namespace EasyRDP.Server.Wpf.Services
{
    /// <summary>
    /// 服务端文件接收目录解析。首次需要时创建（延迟创建，避免未用文件传输出现在文档目录里）。
    /// </summary>
    internal static class FileTransferPaths
    {
        /// <summary>
        /// 客户端发来的文件保存目录：我的文档\EasyRDP Received。
        /// 我的文档不可用（精简系统/服务账户）时回退到程序目录下的 Received。
        /// </summary>
        public static string ReceivedDirectory()
        {
            string documents = "";
            try
            {
                documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            }
            catch
            {
            }
            if (string.IsNullOrEmpty(documents))
                documents = AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(documents, "EasyRDP Received");
        }
    }
}
