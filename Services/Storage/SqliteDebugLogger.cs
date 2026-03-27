using System;
using System.Diagnostics;
using System.IO;

namespace TrueFluentPro.Services.Storage
{
    /// <summary>
    /// SQLite 调试日志。所有方法均标记 [Conditional("DEBUG")]，Release 构建中调用点被完全消除。
    /// 开发环境日志写入仓库 Docs/SqliteLogs/（与 CrashLogger 同策略）；
    /// 发布环境 fallback 到 bin 旁 logs 或 %LOCALAPPDATA%。
    /// </summary>
    internal static class SqliteDebugLogger
    {
        private static readonly object _lock = new();
        private static string? _logDir;
        private static bool _logDirResolved;

        [Conditional("DEBUG")]
        public static void LogLifecycle(string message)
            => Write("LIFECYCLE", message);

        [Conditional("DEBUG")]
        public static void LogWrite(string table, string key, string action)
            => Write("WRITE", $"[{table}] {action} key={key}");

        [Conditional("DEBUG")]
        public static void LogRead(string table, string condition, int resultCount)
            => Write("READ", $"[{table}] {condition} → {resultCount} 行");

        [Conditional("DEBUG")]
        public static void LogPathResolve(string original, string resolved)
            => Write("PATH", $"{original} → {resolved}");

        [Conditional("DEBUG")]
        public static void LogImport(string message)
            => Write("IMPORT", message);

        [Conditional("DEBUG")]
        public static void LogLazyLoad(string sessionId, int windowStart, int count)
            => Write("LAZY", $"session={sessionId} window=[{windowStart}, +{count}]");

        private static string? ResolveLogDir()
        {
            if (_logDirResolved) return _logDir;
            _logDirResolved = true;

            // 优先：沿 BaseDirectory 向上找 .sln，写到仓库 Docs/SqliteLogs/
            try
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                for (var i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
                {
                    if (dir.GetFiles("*.sln").Length > 0)
                    {
                        var candidate = Path.Combine(dir.FullName, "Docs", "SqliteLogs");
                        if (TryEnsureWritable(candidate))
                        {
                            _logDir = candidate;
                            return _logDir;
                        }
                    }
                }
            }
            catch { }

            // Fallback: PathManager.Instance.LogsPath
            try
            {
                var fallback = PathManager.Instance.LogsPath;
                if (TryEnsureWritable(fallback))
                {
                    _logDir = fallback;
                    return _logDir;
                }
            }
            catch { }

            return null;
        }

        private static bool TryEnsureWritable(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                var test = Path.Combine(path, ".write-test");
                File.WriteAllText(test, "ok");
                File.Delete(test);
                return true;
            }
            catch { return false; }
        }

        private static void Write(string category, string message)
        {
            try
            {
                var logDir = ResolveLogDir();
                if (logDir == null) return;

                var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{category,-10}] {message}";
                var logPath = Path.Combine(logDir, "sqlite-debug.log");
                lock (_lock)
                {
                    File.AppendAllText(logPath, line + Environment.NewLine);
                }
            }
            catch
            {
                // 调试日志绝不能导致应用崩溃
            }
        }
    }
}
