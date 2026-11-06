using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace TrueFluentPro.Services;

// 专用协议：没有字符串、文件名、编辑目标或可执行命令。每帧只能是长度 1 + 一个固定枚举。
internal enum VoiceImeUiCommand : byte { Warm = 0, Settings = 1, Usage = 2, Language = 3 }

internal static class VoiceImeUiProtocol
{
    internal const string Switch = "--voice-ime-ui-host";
    internal const byte Ready = 0x52, Accepted = 0x41;
    internal static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(5);
    private const string UiDirectorySuffix = ".voice-ime-ui";

    // 新 UI 包为 source目录.voice-ime-ui/<内容ID>/EXE；旧直接 suffix 路径仅保留 source 回映。
    internal static string SourceExecutable(string executable)
    {
        VoiceImeMaintenance.GuardPath(executable);
        var directory = Path.GetDirectoryName(Path.GetFullPath(executable))!;
        var parent = Path.GetDirectoryName(directory);
        if (parent != null && parent.EndsWith(UiDirectorySuffix, StringComparison.OrdinalIgnoreCase))
        {
            var contentId = Path.GetFileName(directory);
            if (contentId.Length != 64 || !System.Linq.Enumerable.All(contentId,
                c => c is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f'))
                throw new IOException("工具 UI 包内容身份格式无效。");
            directory = parent[..^UiDirectorySuffix.Length];
        }
        else if (directory.EndsWith(UiDirectorySuffix, StringComparison.OrdinalIgnoreCase))
            directory = directory[..^UiDirectorySuffix.Length];
        var source = Path.Combine(directory, Path.GetFileName(executable));
        VoiceImeMaintenance.GuardPath(source);
        return source;
    }

    internal static string UiExecutable(string source)
    {
        VoiceImeMaintenance.GuardPath(source);
        source = Path.GetFullPath(source);
        if (!string.Equals(SourceExecutable(source), source, StringComparison.OrdinalIgnoreCase))
            throw new IOException("不能从工具 UI 再启动录音 owner 的 UI 代理。");
        var directory = Path.GetDirectoryName(source)!;
        return UiExecutable(source, VoiceImeHostDeployment.Create(directory, directory + UiDirectorySuffix).ContentId);
    }

    private static string UiExecutable(string source, string contentId)
        => Path.Combine(Path.GetDirectoryName(source)! + UiDirectorySuffix, contentId, Path.GetFileName(source));

    internal static void VerifyPackage(string source, string executable)
    {
        VoiceImeMaintenance.GuardPath(source);
        VoiceImeMaintenance.GuardPath(executable);
        var plan = VoiceImeHostDeployment.Create(Path.GetDirectoryName(source)!, Path.GetDirectoryName(executable)!);
        if (!string.Equals(UiExecutable(source, plan.ContentId), executable, StringComparison.OrdinalIgnoreCase)
            || plan.Changes.Count != 0)
            throw new IOException("工具 UI 源包或交付内容已变化，请重新准备后再启动。");
    }

    private static string PrepareExecutable(string source)
    {
        VoiceImeMaintenance.GuardPath(source);
        source = Path.GetFullPath(source);
        var executable = UiExecutable(source);
        using var identity = WindowsIdentity.GetCurrent();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(executable.ToUpperInvariant())));
        using var mutex = new Mutex(false, $@"Local\TrueFluentPro.VoiceIme.UI.Deploy.{identity.User?.Value}.{key}");
        bool held;
        try { held = mutex.WaitOne(TimeSpan.FromSeconds(5)); } catch (AbandonedMutexException) { held = true; }
        if (!held) throw new IOException("工具 UI 正在准备，请稍后重试。");
        try
        {
            // 不变包仍差异同步；变包隔离，不覆盖/清理旧 UI，不改维护组件与原复制语义。
            var plan = VoiceImeHostDeployment.Create(Path.GetDirectoryName(source)!, Path.GetDirectoryName(executable)!);
            if (!string.Equals(UiExecutable(source, plan.ContentId), executable, StringComparison.OrdinalIgnoreCase))
                throw new IOException("工具 UI 源包在准备时发生变化，未继续同步。");
            plan.Synchronize();
            VerifyPackage(source, executable); // 全清单复核也捕获无差异文件变化、增删和同步中变化。
            return executable;
        }
        finally { mutex.ReleaseMutex(); }
    }

    internal static string Prepare(string source) => PrepareExecutable(source);

    internal static string Name(Process parent)
    {
        using var identity = WindowsIdentity.GetCurrent();
        return $"TrueFluentPro.VoiceIme.UI.{parent.SessionId}.{identity.User?.Value ?? throw new IOException()}.{parent.Id}.{parent.StartTime.ToUniversalTime().Ticks}";
    }

    internal static bool Matches(Process process, int pid, long ticks, int session, string exe)
    {
        try
        {
            return process.Id == pid && !process.HasExited && process.SessionId == session
                && process.StartTime.ToUniversalTime().Ticks == ticks
                && string.Equals(process.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    internal static async Task<byte> ReadAsync(Stream pipe, CancellationToken token)
    {
        var frame = new byte[5];
        // 空闲连接不超时；首字节之后必须在五秒内完成，长度验证前也只分配固定五字节。
        await pipe.ReadExactlyAsync(frame.AsMemory(0, 1), token).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(FrameTimeout);
        await pipe.ReadExactlyAsync(frame.AsMemory(1), deadline.Token).ConfigureAwait(false);
        if (BinaryPrimitives.ReadInt32LittleEndian(frame) != 1) throw new InvalidDataException();
        return frame[4];
    }

    internal static async Task WriteAsync(Stream pipe, byte command, CancellationToken token)
    {
        var frame = new byte[] { 1, 0, 0, 0, command };
        await pipe.WriteAsync(frame, token).ConfigureAwait(false);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint pid);
}

/// <summary>语音 owner 的无 Avalonia 依赖代理。actor 只 TryWrite，启动、ready、IPC 全在单个后台消费者。</summary>
internal sealed class VoiceImeUiClient : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop;
    private readonly Channel<VoiceImeUiCommand> _requests = Channel.CreateBounded<VoiceImeUiCommand>(
        new BoundedChannelOptions(16) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task _worker;
    private readonly Func<string, string> _prepare;
    private readonly TaskCompletionSource<int> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task<int> Ready => _ready.Task; // 诊断/fixture 可等待；owner actor 从不等待。
    private Process? _child;
    private NamedPipeClientStream? _pipe;
    private int _disposed;

    internal VoiceImeUiClient(CancellationToken token) : this(token, VoiceImeUiProtocol.Prepare) { }

    // 源码链接 fixture 可复制自身 apphost；生产入口没有路径/环境变量测试开关。
    internal VoiceImeUiClient(CancellationToken token, Func<string, string> prepare)
    {
        _prepare = prepare;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        _worker = Task.Run(RunAsync);
    }

    internal void Request(VoiceImeUiCommand command)
    {
        if ((byte)command <= 3 && Volatile.Read(ref _disposed) == 0)
            _requests.Writer.TryWrite(command); // 满队列只丢重复 UI 请求，不阻塞/终止语音 actor。
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (var request in _requests.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                try
                {
                    await EnsureReadyAsync().ConfigureAwait(false);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    deadline.CancelAfter(VoiceImeUiProtocol.FrameTimeout);
                    if (request != VoiceImeUiCommand.Warm && _child != null)
                        AllowSetForegroundWindow((uint)_child.Id);
                    await VoiceImeUiProtocol.WriteAsync(_pipe!, (byte)request, deadline.Token).ConfigureAwait(false);
                    if (await VoiceImeUiProtocol.ReadAsync(_pipe!, deadline.Token).ConfigureAwait(false) != VoiceImeUiProtocol.Accepted)
                        throw new InvalidDataException();
                    _ready.TrySetResult(_child!.Id);
                }
                catch when (!_stop.IsCancellationRequested)
                {
                    _pipe?.Dispose();
                    _pipe = null;
                    // 不盲目再启动：仍活着的同一 child 必须先实际退出；下一次点击才重试。
                    Trace.WriteLine("输入法工具窗口暂未就绪，请稍后重新点击。");
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch { Trace.WriteLine("输入法工具窗口代理已停止。"); }
        finally
        {
            _pipe?.Dispose(); // 仅释放 lease；已有窗口（尤其正在等待维护结果的设置页）自行存活到关闭。
            _child?.Dispose(); // 释放进程句柄，不等待/强杀 UI，不阻塞真实录音 owner 的退出。
        }
    }

    private async Task EnsureReadyAsync()
    {
        if (_pipe is { IsConnected: true } && _child is { HasExited: false }) return;
        _pipe?.Dispose();
        _pipe = null;
        using var parent = Process.GetCurrentProcess();
        var source = parent.MainModule?.FileName ?? throw new IOException();
        var exe = VoiceImeUiProtocol.UiExecutable(source);
        if (_child == null || _child.HasExited)
        {
            _child?.Dispose();
            if (!string.Equals(_prepare(source), exe, StringComparison.OrdinalIgnoreCase)) throw new IOException();
            VoiceImeUiProtocol.VerifyPackage(source, exe);
            _stop.Token.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo(exe)
            {
                UseShellExecute = false, CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory
            };
            start.ArgumentList.Add(VoiceImeUiProtocol.Switch);
            start.ArgumentList.Add(parent.Id.ToString(CultureInfo.InvariantCulture));
            start.ArgumentList.Add(parent.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
            _child = Process.Start(start) ?? throw new IOException();
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        deadline.CancelAfter(VoiceImeUiProtocol.ReadyTimeout);
        var pipe = new NamedPipeClientStream(".", VoiceImeUiProtocol.Name(parent), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            if (!VoiceImeUiProtocol.GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var pid)
                || !VoiceImeUiProtocol.Matches(_child, (int)pid, _child.StartTime.ToUniversalTime().Ticks, parent.SessionId, exe)
                || await VoiceImeUiProtocol.ReadAsync(pipe, deadline.Token).ConfigureAwait(false) != VoiceImeUiProtocol.Ready)
                throw new IOException();
            VoiceImeUiProtocol.VerifyPackage(source, exe);
            _pipe = pipe;
        }
        catch { pipe.Dispose(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _requests.Writer.TryComplete();
        _stop.Cancel();
        await _worker.ConfigureAwait(false);
        _stop.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);
}