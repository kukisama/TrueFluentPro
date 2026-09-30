using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GptImageCli;

internal static class QueueWorkerHost
{
    public static async Task<bool> EnsureStartedAsync(QueuePaths paths)
    {
        using var gate = await paths.LockAsync("lifecycle");
        return await EnsureStartedUnderLockAsync(paths);
    }

    internal static async Task<bool> EnsureStartedUnderLockAsync(QueuePaths paths)
    {
        var binaryHash = BinaryHash();
        var store = new QueueStore(paths);
        if (paths.WorkerRunning())
        {
            var untilReady = Environment.TickCount64 + 10000;
            while (Environment.TickCount64 < untilReady)
            {
                if (IsReady(paths, store, binaryHash)) return true;
                if (!paths.WorkerRunning()) break;
                await Task.Delay(50);
            }
            if (paths.WorkerRunning()) throw new CliException("已有执行器未完成就绪握手；请检查 queue worker，不要重复提交。");
        }
        var executable = Environment.ProcessPath ?? throw new CliException("无法确定当前程序路径，后台未启动。");
        var arguments = new List<string>();
        // Supports managed development builds as well as the self-contained AOT executable.
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            arguments.Add(Path.Combine(AppContext.BaseDirectory, "gpt-image.dll"));
        arguments.AddRange(["queue", "worker", "--queue-dir", paths.Root]);
        var command = new StringBuilder(Quote(executable) + " " + string.Join(' ', arguments.Select(Quote)));
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        const uint noWindow = 0x08000000, breakaway = 0x01000000;
        // Do not inherit console/std handles. Break away from a host job only if Windows permits it.
        if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, false, noWindow | breakaway,
            IntPtr.Zero, AppContext.BaseDirectory, ref startup, out var process))
            throw new CliException($"任务已入队，但后台启动失败（Windows 错误 {Marshal.GetLastWin32Error()}）。请在独立终端执行 gpt-image queue start；受限宿主可能禁止后台独立运行。");
        CloseHandle(process.Thread);
        try
        {
            var until = Environment.TickCount64 + 10000;
            while (Environment.TickCount64 < until)
            {
                if (IsReady(paths, store, binaryHash, (int)process.ProcessId)) return true;
                if (WaitForSingleObject(process.Process, 0) == 0)
                    throw new CliException("任务已入队，但后台执行器启动后退出；请运行 queue worker 查看安全诊断。");
                await Task.Delay(50);
            }
            throw new CliException("任务已入队，后台启动状态尚未确认；请用 queue list 查询，不要重复提交。");
        }
        finally { CloseHandle(process.Process); }
    }

    private static string BinaryHash()
    {
        var executable = Environment.ProcessPath ?? throw new CliException("无法确定程序路径。");
        var assembly = Path.Combine(AppContext.BaseDirectory, "gpt-image.dll");
        var path = RuntimeFeature.IsDynamicCodeSupported && File.Exists(assembly) ? assembly : executable;
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    internal static void WriteReady(QueuePaths paths, QueueStore store)
    {
        using var process = Process.GetCurrentProcess();
        var info = new QueueWorkerInfo(1, process.Id, process.StartTime.ToUniversalTime().Ticks, BinaryHash());
        store.WriteWorkerInfo(info);
        // Called only with the exclusive worker lease; an older worker can no longer be writing these files.
        try
        {
            File.Delete(Path.Combine(paths.Root, "worker-info.json"));
            File.Delete(Path.Combine(paths.Root, "worker-info.json.tmp"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { store.Log("legacy_worker_info_cleanup", "执行器就绪信息已入库；旧版就绪文件无法删除，请检查目录权限。"); }
    }

    private static bool IsReady(QueuePaths paths, QueueStore store, string binaryHash, int? expectedPid = null)
    {
        QueueWorkerInfo? info;
        try
        {
            info = store.GetWorkerInfo();
            if (!IsLiveProcess(info, expectedPid))
            {
                // Read-only compatibility with a still-running older binary; never create a JSON sidecar.
                info = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(paths.Root, "worker-info.json")),
                    QueueJsonContext.Default.QueueWorkerInfo);
                if (!IsLiveProcess(info, expectedPid)) return false;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or Microsoft.Data.Sqlite.SqliteException)
        { return false; }
        if (!paths.WorkerRunning()) return false;
        if (info!.ProtocolVersion != 1 || info.BinaryHash != binaryHash)
            throw new CliException("另一个版本的执行器仍在处理此队列；请等其完成后再用当前版本提交，或使用相同版本。未切换正在运行的程序。");
        return true;
    }

    private static bool IsLiveProcess(QueueWorkerInfo? info, int? expectedPid)
    {
        if (info is null || (expectedPid is not null && info.ProcessId != expectedPid)) return false;
        try
        {
            using var process = Process.GetProcessById(info.ProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == info.ProcessStartedAt;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return false; }
    }

    internal static string Quote(string argument)
    {
        var result = new StringBuilder("\""); var slashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { result.Append('\\', slashes * 2 + 1); result.Append(c); }
            else { result.Append('\\', slashes); result.Append(c); }
            slashes = 0;
        }
        result.Append('\\', slashes * 2); return result.Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2Size;
        public IntPtr Reserved2, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string applicationName, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags,
        IntPtr environment, string currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
}