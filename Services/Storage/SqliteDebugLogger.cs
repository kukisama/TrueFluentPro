using System;
using System.Diagnostics;
using System.IO;

namespace TrueFluentPro.Services.Storage
{
    /// <summary>
    /// SQLite 调试日志。所有方法均标记 [Conditional("DEBUG")]，Release 构建中调用点被完全消除。
    /// 日志写入 {LogsPath}/sqlite-debug.log。
    /// </summary>
    internal static class SqliteDebugLogger
    {
        private static readonly object _lock = new();

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

        private static void Write(string category, string message)
        {
            try
            {
                var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{category,-10}] {message}";
                var logPath = Path.Combine(PathManager.Instance.LogsPath, "sqlite-debug.log");
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
