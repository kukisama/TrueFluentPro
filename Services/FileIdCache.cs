using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;

namespace TrueFluentPro.Services
{
    /// <summary>
    /// 本地文件 SHA256 → 远端 file_id 映射缓存。
    /// 避免同一图片反复上传到 /v1/files。
    /// 按 endpoint base URL 隔离（不同 endpoint 的 file_id 不可混用）。
    /// </summary>
    public sealed class FileIdCache
    {
        private static readonly TimeSpan Ttl = TimeSpan.FromHours(12);

        /// <summary>
        /// Key = "{endpointHash}_{fileContentHash}"
        /// Value = (file_id, upload_time)
        /// </summary>
        private readonly ConcurrentDictionary<string, (string FileId, DateTime UploadedAt)> _cache = new();

        /// <summary>
        /// 尝试从缓存获取 file_id。
        /// </summary>
        public string? TryGet(string endpointBaseUrl, string filePath)
        {
            var key = BuildKey(endpointBaseUrl, filePath);
            if (key == null) return null;

            if (_cache.TryGetValue(key, out var entry) && DateTime.UtcNow - entry.UploadedAt < Ttl)
                return entry.FileId;

            _cache.TryRemove(key, out _);
            return null;
        }

        /// <summary>
        /// 写入缓存。
        /// </summary>
        public void Set(string endpointBaseUrl, string filePath, string fileId)
        {
            var key = BuildKey(endpointBaseUrl, filePath);
            if (key == null) return;
            _cache[key] = (fileId, DateTime.UtcNow);
        }

        /// <summary>
        /// 标记某个 file_id 失效（API 返回 file_id 相关错误时调用）。
        /// </summary>
        public void Invalidate(string endpointBaseUrl, string filePath)
        {
            var key = BuildKey(endpointBaseUrl, filePath);
            if (key != null) _cache.TryRemove(key, out _);
        }

        /// <summary>
        /// 清空所有缓存。
        /// </summary>
        public void Clear() => _cache.Clear();

        private static string? BuildKey(string endpointBaseUrl, string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return null;

            var epHash = ComputeShortHash(endpointBaseUrl);
            var fileHash = ComputeFileHash(filePath);
            return fileHash == null ? null : $"{epHash}_{fileHash}";
        }

        private static string ComputeShortHash(string input)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(input);
            var hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
        }

        private static string? ComputeFileHash(string filePath)
        {
            try
            {
                using var stream = File.OpenRead(filePath);
                var hash = SHA256.HashData(stream);
                return Convert.ToHexString(hash).ToLowerInvariant();
            }
            catch
            {
                return null;
            }
        }
    }
}
