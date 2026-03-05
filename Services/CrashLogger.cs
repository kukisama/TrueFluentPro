using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TrueFluentPro.Services;

public static class CrashLogger
{
    private static int _initialized;
    private static string? _logDir;
    private static readonly object _diagnosticsLock = new();
    private static Func<string>? _contextProvider;
    private static readonly Queue<string> _breadcrumbs = new();
    private const int MaxBreadcrumbs = 200;

    public static string? LogDirectory => _logDir;

    public static void Init()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 1)
        {
            return;
        }

        _logDir = TryCreateLogDir(Path.Combine(AppContext.BaseDirectory, "logs"))
            ?? TryCreateLogDir(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TrueFluentPro",
                "logs"));

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Write(
                source: "AppDomain.CurrentDomain.UnhandledException",
                exception: e.ExceptionObject as Exception,
                isTerminating: e.IsTerminating);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write(
                source: "TaskScheduler.UnobservedTaskException",
                exception: e.Exception,
                isTerminating: false);

            e.SetObserved();
        };

        try
        {
            Trace.AutoFlush = true;

            if (_logDir != null)
            {
                var tracePath = Path.Combine(_logDir, "trace.log");
                Trace.Listeners.Add(new TextWriterTraceListener(tracePath));
            }
        }
        catch
        {
            // ignore
        }
    }

    public static void WriteMessage(string source, string message)
    {
        Write(source, new Exception(message), isTerminating: false);
    }

    [Conditional("DEBUG")]
    public static void SetContextProvider(Func<string>? provider)
    {
        lock (_diagnosticsLock)
        {
            _contextProvider = provider;
        }
    }

    [Conditional("DEBUG")]
    public static void AddBreadcrumb(string message)
    {
        try
        {
            var line = $"{DateTimeOffset.Now:HH:mm:ss.fff} {message}";
            try
            {
                Trace.WriteLine($"[Breadcrumb] {line}");
            }
            catch
            {
                // ignore
            }

            lock (_diagnosticsLock)
            {
                _breadcrumbs.Enqueue(line);
                while (_breadcrumbs.Count > MaxBreadcrumbs)
                {
                    _breadcrumbs.Dequeue();
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    public static void HookAvaloniaUiThread()
    {
        try
        {
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                Write(
                    source: "Avalonia.Dispatcher.UIThread.UnhandledException",
                    exception: e.Exception,
                    isTerminating: false);

                // Keep default behavior (crash) unless we explicitly decide otherwise.
                e.Handled = false;
            };
        }
        catch
        {
            // ignore
        }
    }

    public static void Write(string source, Exception? exception, bool isTerminating)
    {
        try
        {
            if (_initialized == 0)
            {
                Init();
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Timestamp: {DateTimeOffset.Now:O}");
            sb.AppendLine($"Source: {source}");
            sb.AppendLine($"IsTerminating: {isTerminating}");
            sb.AppendLine($"Process: {Environment.ProcessPath}");
            sb.AppendLine($"BaseDirectory: {AppContext.BaseDirectory}");
            sb.AppendLine($"OS: {Environment.OSVersion}");
            sb.AppendLine($"Framework: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
            AppendRuntimeDiagnostics(sb);
            AppendContextSnapshot(sb);
            AppendBreadcrumbs(sb);
            sb.AppendLine();

            if (exception != null)
            {
                sb.AppendLine(exception.ToString());
            }
            else
            {
                sb.AppendLine("(no Exception object)");
            }

            if (_logDir != null)
            {
                var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss.fff");
                var crashPath = Path.Combine(_logDir, $"crash-{stamp}.log");
                File.WriteAllText(crashPath, sb.ToString(), Encoding.UTF8);

                try
                {
                    var lastPath = Path.Combine(_logDir, "last-crash.log");
                    File.Copy(crashPath, lastPath, overwrite: true);
                }
                catch
                {
                    // ignore
                }

                TryWriteToStderr($"[CrashLogger] wrote: {crashPath}");
            }
            else
            {
                TryWriteToStderr("[CrashLogger] log dir unavailable");
            }

            TryWriteToStderr(sb.ToString());
            Debug.WriteLine(sb.ToString());
        }
        catch
        {
            // Absolutely never throw from crash logging.
        }
    }

    private static void TryWriteToStderr(string text)
    {
        try
        {
            Console.Error.WriteLine(text);
        }
        catch
        {
            // ignore
        }
    }

    private static void AppendRuntimeDiagnostics(StringBuilder sb)
    {
        try
        {
            var process = Process.GetCurrentProcess();
            ThreadPool.GetAvailableThreads(out var workerAvailable, out var ioAvailable);
            ThreadPool.GetMaxThreads(out var workerMax, out var ioMax);
            ThreadPool.GetMinThreads(out var workerMin, out var ioMin);
            var gcInfo = GC.GetGCMemoryInfo();

            sb.AppendLine();
            sb.AppendLine("RuntimeDiagnostics:");
            sb.AppendLine($"ManagedThreadId: {Environment.CurrentManagedThreadId}");
            sb.AppendLine($"ProcessId: {Environment.ProcessId}");
            sb.AppendLine($"Uptime: {(DateTimeOffset.Now - process.StartTime):g}");
            sb.AppendLine($"WorkingSetMB: {process.WorkingSet64 / 1024d / 1024d:F1}");
            sb.AppendLine($"PrivateMemoryMB: {process.PrivateMemorySize64 / 1024d / 1024d:F1}");
            sb.AppendLine($"Threads: {process.Threads.Count}");
            sb.AppendLine($"Handles: {process.HandleCount}");
            sb.AppendLine($"GC.TotalMemoryMB: {GC.GetTotalMemory(false) / 1024d / 1024d:F1}");
            sb.AppendLine($"GC.HeapSizeMB: {gcInfo.HeapSizeBytes / 1024d / 1024d:F1}");
            sb.AppendLine($"GC.FragmentedMB: {gcInfo.FragmentedBytes / 1024d / 1024d:F1}");
            sb.AppendLine($"GC.MemoryLoadBytes: {gcInfo.MemoryLoadBytes}");
            sb.AppendLine($"ThreadPool.Worker: available={workerAvailable}, min={workerMin}, max={workerMax}");
            sb.AppendLine($"ThreadPool.IO: available={ioAvailable}, min={ioMin}, max={ioMax}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"RuntimeDiagnosticsError: {ex.Message}");
        }
    }

    private static void AppendContextSnapshot(StringBuilder sb)
    {
        Func<string>? provider;
        lock (_diagnosticsLock)
        {
            provider = _contextProvider;
        }

        if (provider == null)
        {
            return;
        }

        try
        {
            var snapshot = provider();
            if (!string.IsNullOrWhiteSpace(snapshot))
            {
                sb.AppendLine();
                sb.AppendLine("AppContextSnapshot:");
                sb.AppendLine(snapshot.TrimEnd());
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine();
            sb.AppendLine($"AppContextSnapshotError: {ex.Message}");
        }
    }

    private static void AppendBreadcrumbs(StringBuilder sb)
    {
        string[] lines;
        lock (_diagnosticsLock)
        {
            if (_breadcrumbs.Count == 0)
            {
                return;
            }

            lines = _breadcrumbs.ToArray();
        }

        sb.AppendLine();
        sb.AppendLine("Breadcrumbs:");
        foreach (var line in lines)
        {
            sb.AppendLine(line);
        }
    }

    private static string? TryCreateLogDir(string path)
    {
        try
        {
            Directory.CreateDirectory(path);

            // Touch test to ensure it's writable.
            var testFile = Path.Combine(path, ".write-test");
            File.WriteAllText(testFile, DateTimeOffset.Now.ToString("O"), Encoding.UTF8);
            File.Delete(testFile);

            return path;
        }
        catch
        {
            return null;
        }
    }
}
