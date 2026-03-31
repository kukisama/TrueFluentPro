using System;
using System.IO;

namespace TrueFluentPro.Services.Storage
{
    public sealed class StoragePathResolver : IStoragePathResolver
    {
        public string WorkspaceRoot { get; }

        public StoragePathResolver()
        {
            WorkspaceRoot = PathManager.Instance.SessionsPath;
        }

        public string ToRelativePath(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath)) return "";

            var fullRoot = Path.GetFullPath(WorkspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullPath = Path.GetFullPath(absolutePath);

            if (fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                var relative = Path.GetRelativePath(fullRoot, fullPath).Replace('\\', '/');
                SqliteDebugLogger.LogPathResolve(absolutePath, relative);
                return relative;
            }

            SqliteDebugLogger.LogPathResolve(absolutePath, $"[外部路径] {absolutePath}");
            return absolutePath;
        }

        public string ToAbsolutePath(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) return "";
            if (Path.IsPathRooted(relativePath)) return relativePath;
            return Path.GetFullPath(Path.Combine(WorkspaceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        }

        public string GetNewResourcePath(string mediaType, string extension)
        {
            var now = DateTime.Now;
            var dir = Path.Combine("library", mediaType, now.ToString("yyyy"), now.ToString("MM"));
            var uid = Guid.NewGuid().ToString("N")[..8];
            var prefix = mediaType.Length >= 3 ? mediaType[..3] : mediaType;
            var fileName = $"{prefix}_{now:yyyyMMdd_HHmmss}_{uid}{extension}";

            var fullDir = Path.Combine(WorkspaceRoot, dir);
            Directory.CreateDirectory(fullDir);

            var relative = $"{dir}/{fileName}".Replace('\\', '/');
            SqliteDebugLogger.LogPathResolve($"[新资源] {mediaType}{extension}", relative);
            return relative;
        }

        public string GetNewResourceDirectory(string mediaType)
        {
            var now = DateTime.Now;
            var fullDir = Path.Combine(WorkspaceRoot, "library", mediaType, now.ToString("yyyy"), now.ToString("MM"));
            Directory.CreateDirectory(fullDir);
            return fullDir;
        }
    }
}
