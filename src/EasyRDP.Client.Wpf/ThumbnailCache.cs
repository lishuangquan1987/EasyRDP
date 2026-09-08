using System;
using System.IO;
using System.Windows.Media.Imaging;
using NLog;

namespace EasyRDP.Client.Wpf
{
    /// <summary>
    /// 最近连接缩略图本地缓存：%AppData%\EasyRDP\client\thumbs\{host-hash}.jpg。
    /// 把 BGRA 帧像素编码为 JPEG，并按 Host 命名存储；UI 通过 BitmapImage 加载。
    /// </summary>
    public static class ThumbnailCache
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        /// <summary>缩略图缓存目录。</summary>
        public static string ThumbnailDirectory()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "EasyRDP", "client", "thumbs");
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>缩略图文件路径。</summary>
        public static string GetThumbnailPath(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return null;
            string safe = host.Trim().Replace(':', '_').Replace('\\', '_').Replace('/', '_');
            return Path.Combine(ThumbnailDirectory(), safe + ".jpg");
        }

        /// <summary>
        /// 保存 BGRA 帧为 JPEG 缩略图。返回缓存文件路径，失败返回 null。
        /// </summary>
        public static string SaveThumbnail(byte[] bgra, int width, int height, string host, int maxEdge = 320)
        {
            if (bgra == null || width <= 0 || height <= 0 || string.IsNullOrWhiteSpace(host))
                return null;
            try
            {
                // 计算保持宽高比的缩略图尺寸
                int thumbW = width, thumbH = height;
                if (width > height && width > maxEdge)
                {
                    thumbW = maxEdge;
                    thumbH = (int)((long)height * maxEdge / width);
                }
                else if (height > maxEdge)
                {
                    thumbH = maxEdge;
                    thumbW = (int)((long)width * maxEdge / height);
                }

                // BGRA → BitmapSource
                var bitmap = BitmapSource.Create(width, height, 96, 96,
                    System.Windows.Media.PixelFormats.Bgra32, null, bgra, width * 4);

                // 等比缩放
                BitmapSource scaled = bitmap;
                if (thumbW != width || thumbH != height)
                {
                    scaled = new TransformedBitmap(bitmap, new System.Windows.Media.ScaleTransform(
                        (double)thumbW / width, (double)thumbH / height));
                }

                var encoder = new JpegBitmapEncoder { QualityLevel = 80 };
                encoder.Frames.Add(BitmapFrame.Create(scaled));

                string path = GetThumbnailPath(host);
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
                    encoder.Save(fs);

                Logger.Info("Thumbnail saved: {0} ({1}x{2} -> {3}x{4})", path, width, height, thumbW, thumbH);
                return path;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Save thumbnail failed for host={0}", host);
                return null;
            }
        }

        /// <summary>加载指定 host 的缩略图为 BitmapImage；不存在返回 null。</summary>
        public static BitmapImage LoadThumbnail(string host)
        {
            string path = GetThumbnailPath(host);
            if (path == null || !File.Exists(path)) return null;
            try
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.UriSource = new Uri(path, UriKind.Absolute);
                img.EndInit();
                img.Freeze();
                return img;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Load thumbnail failed: {0}", path);
                return null;
            }
        }

        /// <summary>删除指定 host 的缩略图缓存文件（用于移除最近连接）。</summary>
        public static void DeleteThumbnail(string host)
        {
            string path = GetThumbnailPath(host);
            if (path == null) return;
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Delete thumbnail failed: {0}", path);
            }
        }
    }
}
