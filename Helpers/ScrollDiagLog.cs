using System;
using System.Diagnostics;
using System.IO;

namespace TrueFluentPro.Helpers;

/// <summary>
/// 临时诊断日志，写到 exe 同目录的 scroll_diag.log。
/// 用完后整个文件删除即可。
/// </summary>
internal static class ScrollDiagLog
{
    private static readonly string s_path = Path.Combine(
        AppContext.BaseDirectory, "scroll_diag.log");

    private static readonly object s_lock = new();

    [Conditional("DEBUG")]
    internal static void Log(string message)
    {
        try
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff}  {message}";
            lock (s_lock)
            {
                File.AppendAllText(s_path, line + Environment.NewLine);
            }
        }
        catch
        {
            // 诊断代码不应崩溃
        }
    }

    /// <summary>启动时清空旧日志</summary>
    [Conditional("DEBUG")]
    internal static void Reset()
    {
        try { File.Delete(s_path); } catch { }
    }
}
