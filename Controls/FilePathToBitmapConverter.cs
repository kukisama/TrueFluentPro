using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using TrueFluentPro.Services;

namespace TrueFluentPro.Controls
{
    public class FilePathToBitmapConverter : IValueConverter
    {
        private static readonly Dictionary<string, WeakReference<Bitmap>> _cache = new();
        private static int _hitCount;
        private static int _missCount;

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

            lock (_cache)
            {
                if (_cache.TryGetValue(filePath, out var weakRef) && weakRef.TryGetTarget(out var cached))
                {
                    _hitCount++;
                    AuditLog($"命中缓存 路径={Path.GetFileName(filePath)} 命中={_hitCount} 未命中={_missCount} 缓存数={_cache.Count}");
                    return cached;
                }

                try
                {
                    var bmp = new Bitmap(filePath);
                    _cache[filePath] = new WeakReference<Bitmap>(bmp);
                    _missCount++;
                    AuditLog($"新建加载 路径={Path.GetFileName(filePath)} 命中={_hitCount} 未命中={_missCount} 缓存数={_cache.Count}");
                    return bmp;
                }
                catch
                {
                    return null;
                }
            }
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return null;
        }
    }
}
