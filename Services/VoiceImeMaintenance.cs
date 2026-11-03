using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace TrueFluentPro.Services;

/// <summary>仅本输入法使用的固定维护门禁；不接受配置键或命令行路径。</summary>
internal static class VoiceImeMaintenance
{
    internal static string InstallRoot
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return Path.Combine(AppContext.BaseDirectory, "voice-ime-maintenance");
            // 与 x86/x64 native 使用同一原生 ProgramFilesDir，不能使用进程位数相关的 SpecialFolder。
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,
                Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32);
            using var version = machine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion");
            var root = version?.GetValue("ProgramFilesDir") as string;
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
                throw new IOException("无法确定原生 Program Files，已停止输入法组件操作。");
            return Path.Combine(root, "TrueFluentPro", "VoiceIme");
        }
    }

    internal static string GatePath => Path.Combine(InstallRoot, "maintenance.lock");

    internal static void GuardPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.IndexOf(':', 2) >= 0) throw new IOException("输入法路径不是本机绝对路径。");
        // 包括父目录和文件本身；不跟随 junction/symlink，也不把权限失败当作不存在。
        for (string? part = Path.GetFullPath(path); part != null; part = Path.GetDirectoryName(part))
        {
            try
            {
                if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("输入法路径包含重解析点，已拒绝操作。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    internal static void Check(string gate, CancellationTokenSource lifetime)
    {
        if (Exists(gate)) lifetime.Cancel();
        lifetime.Token.ThrowIfCancellationRequested();
    }

    internal static async Task MonitorAsync(string gate, CancellationTokenSource lifetime, CancellationToken stop)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            do
            {
                if (Exists(gate)) { lifetime.Cancel(); return; }
            } while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch { lifetime.Cancel(); } // 无法确认门禁时 fail closed，不继续采音。
    }

    internal static void RecordHost(string directory)
    {
        using var process = Process.GetCurrentProcess();
        var host = process.MainModule?.FileName ?? throw new IOException("无法确认输入法后台身份。");
        var receipt = new HostReceipt { Pid = process.Id, StartTime = process.StartTime.ToUniversalTime().Ticks,
            HostExe = Path.GetFullPath(host) };
        var path = Path.Combine(directory, $"host-{receipt.Pid}-{receipt.StartTime}.json");
        GuardPath(path);
        // 保留至后续维护确认 PID+启动时间+exe 已退出，不能只因麦克风锁释放就认为 Host 已退出。
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(file, receipt);
        file.Flush(true);
    }

    internal static async Task<FileStream> WaitForHostAsync(string directory)
    {
        var watch = Stopwatch.StartNew();
        var microphone = Path.Combine(directory, "microphone.lock");
        GuardPath(microphone);
        while (true)
        {
            FileStream? held = null;
            var handedOff = false;
            try
            {
                held = new FileStream(microphone, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    1, FileOptions.DeleteOnClose);
                var alive = false;
                var receipts = Directory.EnumerateFiles(directory, "host-*.json").Take(65).ToList();
                if (receipts.Count > 64) throw new IOException("输入法后台记录过多，无法确认已停止。");
                foreach (var path in receipts)
                {
                    var itemAlive = false;
                    GuardPath(path);
                    if (new FileInfo(path).Length > 4096) throw new IOException("输入法后台记录异常。");
                    var receipt = JsonSerializer.Deserialize<HostReceipt>(File.ReadAllText(path))
                        ?? throw new IOException("输入法后台记录异常。");
                    if (receipt.Pid <= 0 || receipt.StartTime <= 0 || !Path.IsPathFullyQualified(receipt.HostExe)
                        || Path.GetFileName(receipt.HostExe) != "TrueFluentPro.exe"
                        || Path.GetFileName(path) != $"host-{receipt.Pid}-{receipt.StartTime}.json")
                        throw new IOException("输入法后台记录身份不匹配，未结束任何进程。");
                    try
                    {
                        using var process = Process.GetProcessById(receipt.Pid);
                        if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == receipt.StartTime)
                        {
                            var actual = process.MainModule?.FileName;
                            // 无权限确认映像时不得当成已退出，更不能杀此 PID。
                            if (actual == null) throw new IOException("无法确认输入法后台是否退出。");
                            if (string.Equals(Path.GetFullPath(actual), receipt.HostExe, StringComparison.OrdinalIgnoreCase))
                                itemAlive = true;
                        }
                    }
                    catch (ArgumentException) { }
                    alive |= itemAlive;
                    if (!itemAlive) File.Delete(path);
                }
                if (!alive) { handedOff = true; return held; }
            }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33) { }
            finally { if (!handedOff) held?.Dispose(); }
            if (watch.Elapsed >= TimeSpan.FromSeconds(5))
                throw new IOException("输入法后台仍存活或麦克风尚未释放；维护门禁已保留，请稍后重试，不会结束目标应用。");
            await Task.Delay(100).ConfigureAwait(false);
        }
    }

    private sealed class HostReceipt
    {
        public HostReceipt() { }
        public int Pid { get; set; }
        public long StartTime { get; set; }
        public string HostExe { get; set; } = "";
    }
}