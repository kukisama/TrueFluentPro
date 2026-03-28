using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using TrueFluentPro.Services;

namespace TrueFluentPro.Controls
{
    public class FilePathToBitmapConverter : IValueConverter
    {
        // 强引用 LRU 缓存：虚拟化回收 Item 后 Bitmap 不会被 GC
        private static readonly LinkedList<(string Key, Bitmap Bmp)> _lruList = new();
        private static readonly Dictionary<string, LinkedListNode<(string Key, Bitmap Bmp)>> _lruMap = new();
        private const int MaxCacheEntries = 200;

        // 尺寸永久缓存：bitmap 被 LRU 淘汰后尺寸仍保留，防止虚拟化回收时高度塌缩
        private static readonly Dictionary<string, PixelSize> _dimensionCache = new();

        private static int _hitCount;
        private static int _missCount;

        /// <summary>缩略图解码的最大边长（px）。XAML 中 MaxWidth/MaxHeight 均为 200。</summary>
        private const int ThumbnailMaxPixels = 400; // 2x 保留清晰度

        private static void AuditLog(string message)
        {
            if (!AppLogService.IsInitialized) return;
            try
            {
                AppLogService.Instance.LogAudit("ImageCache", message);
            }
            catch { }
        }

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return value is string filePath ? TryGetBitmap(filePath) : null;
        }

        public static Bitmap? TryGetBitmap(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return null;

            if (!File.Exists(filePath))
                return null;

            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif"))
                return null;

            lock (_lruList)
            {
                if (_lruMap.TryGetValue(filePath, out var node))
                {
                    _hitCount++;
                    // 移到链表头部（最近使用）
                    _lruList.Remove(node);
                    _lruList.AddFirst(node);
                    return node.Value.Bmp;
                }

                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var bmp = DecodeThumbnail(filePath);
                    sw.Stop();

                    if (bmp == null) return null;

                    // 缓存尺寸（永久保留）
                    _dimensionCache[filePath] = bmp.PixelSize;

                    // 插入到 LRU 头部
                    var entry = _lruList.AddFirst((filePath, bmp));
                    _lruMap[filePath] = entry;

                    // 超出上限时淘汰最旧的
                    while (_lruList.Count > MaxCacheEntries)
                    {
                        var last = _lruList.Last!;
                        _lruMap.Remove(last.Value.Key);
                        last.Value.Bmp.Dispose();
                        _lruList.RemoveLast();
                    }

                    _missCount++;
                    if (sw.ElapsedMilliseconds > 10)
                        Helpers.ScrollDiagLog.Log($"[BitmapLoad] 慢加载 {sw.ElapsedMilliseconds}ms {Path.GetFileName(filePath)} {bmp.PixelSize.Width}x{bmp.PixelSize.Height}");
                    AuditLog($"新建加载 路径={Path.GetFileName(filePath)} 命中={_hitCount} 未命中={_missCount} 缓存数={_lruMap.Count}");
                    return bmp;
                }
                catch
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// 获取已缓存的像素尺寸，若无缓存则尝试读取 PNG 文件头（微秒级）。
        /// </summary>
        public static PixelSize? TryGetPixelSize(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return null;

            lock (_lruList)
            {
                if (_dimensionCache.TryGetValue(filePath, out var cached))
                    return cached;
            }

            // 尝试从 PNG 文件头读取（只读 24 字节）
            var dims = ReadPngHeaderDimensions(filePath);
            if (dims.HasValue)
            {
                lock (_lruList)
                {
                    _dimensionCache[filePath] = dims.Value;
                }
            }
            return dims;
        }

        /// <summary>
        /// 根据像素尺寸和 maxW/maxH 约束，计算 Uniform 缩放后的显示尺寸。
        /// </summary>
        public static Size CalculateDisplaySize(PixelSize pixel, double maxW, double maxH)
        {
            if (pixel.Width <= 0 || pixel.Height <= 0)
                return new Size(maxW, maxH);

            double scale = Math.Min(maxW / pixel.Width, maxH / pixel.Height);
            scale = Math.Min(scale, 1.0); // 不放大
            return new Size(Math.Round(pixel.Width * scale), Math.Round(pixel.Height * scale));
        }

        /// <summary>
        /// 从 PNG 文件头部（24 字节）零成本读取图片像素尺寸。
        /// </summary>
        private static PixelSize? ReadPngHeaderDimensions(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return null;
                var ext = Path.GetExtension(filePath).ToLowerInvariant();
                if (ext != ".png") return null;

                Span<byte> buf = stackalloc byte[24];
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (fs.Read(buf) < 24) return null;
                // PNG 签名: 137 80 78 71 13 10 26 10
                if (buf[0] != 137 || buf[1] != 80 || buf[2] != 78 || buf[3] != 71) return null;
                // IHDR: width@16-19, height@20-23 (big-endian)
                int w = (buf[16] << 24) | (buf[17] << 16) | (buf[18] << 8) | buf[19];
                int h = (buf[20] << 24) | (buf[21] << 16) | (buf[22] << 8) | buf[23];
                if (w <= 0 || h <= 0 || w > 32768 || h > 32768) return null;
                return new PixelSize(w, h);
            }
            catch { return null; }
        }

        /// <summary>
        /// 以缩小分辨率解码图片。对于大图只解码缩略图，节省内存和 CPU。
        /// </summary>
        private static Bitmap? DecodeThumbnail(string filePath)
        {
            // DecodeToWidth 内部会只解码到目标尺寸，不加载全量像素
            using var stream = File.OpenRead(filePath);
            return Bitmap.DecodeToWidth(stream, ThumbnailMaxPixels);
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return null;
        }
    }
}
