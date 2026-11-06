using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using TrueFluentPro.Views;

namespace TrueFluentPro.Services;

internal static class VoiceImeUiHost
{
    internal static int Run(string[] args) => RunCore(args, CreateWindow, VoiceImeMaintenance.GatePath);

    // 仅源码链接工具使用；生产 CLI 不接受路径、工厂、环境变量或其它测试开关。
    internal static int RunCore(string[] args, Func<VoiceImeUiCommand, Window> factory, string gate)
    {
        if (!OperatingSystem.IsWindows() || args.Length != 3 || args[0] != VoiceImeUiProtocol.Switch
            || !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parentId) || parentId <= 0
            || !long.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) || ticks <= 0) return 2;
        try
        {
            using var self = Process.GetCurrentProcess();
            using var parent = Process.GetProcessById(parentId);
            var executable = self.MainModule!.FileName;
            var source = VoiceImeUiProtocol.SourceExecutable(executable);
            if (!VoiceImeUiProtocol.Matches(parent, parentId, ticks, self.SessionId, source)) return 2;
            VoiceImeUiProtocol.VerifyPackage(source, executable);
            // 内核命名对象与 pipe 均按 SID/session/parent/starttime 隔离。生命周期内不释放单飞权。
            using var mutex = new Mutex(false, @"Local\" + VoiceImeUiProtocol.Name(parent));
            bool held;
            try { held = mutex.WaitOne(0); } catch (AbandonedMutexException) { held = true; }
            if (!held) return 0;
            try
            {
                if (VoiceImeMaintenance.Exists(gate)) return 0;
                WindowsDpiAwareness.TryEnablePerMonitorV2();
                return AppBuilder.Configure(() => new UiApplication(parent, gate, factory))
                    .UsePlatformDetect().WithInterFont().LogToTrace()
                    .StartWithClassicDesktopLifetime(Array.Empty<string>(), ShutdownMode.OnExplicitShutdown);
            }
            finally { mutex.ReleaseMutex(); }
        }
        catch { return 1; }
    }

    private static Window CreateWindow(VoiceImeUiCommand kind) => kind switch
    {
        VoiceImeUiCommand.Settings => new VoiceImeSettingsWindow(),
        VoiceImeUiCommand.Usage => new VoiceImeUsageWindow(),
        VoiceImeUiCommand.Language => new VoiceImeLanguageWindow(),
        _ => throw new InvalidDataException()
    };

    private sealed class UiApplication(Process parent, string gate, Func<VoiceImeUiCommand, Window> factory) : Application
    {
        public override void Initialize() => VoiceImeUiTheme.Apply(this);
        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                // 不设置 MainWindow、不构造窗口。dispatcher 已运行才宣布 ready。
                Dispatcher.UIThread.Post(async () =>
                {
                    using var stop = new CancellationTokenSource();
                    var windows = new VoiceImeUiWindows(factory);
                    var monitor = MonitorAsync(parent, gate, stop);
                    try { await ServeAsync(parent, windows, stop.Token); }
                    catch { }
                    finally
                    {
                        stop.Cancel();
                        await monitor;
                        // lease/父进程/维护只停止接收新命令；已经打开的窗口及其原权限维护等待不能丢失。
                        // 无窗预热立即退出；末窗由用户关闭后退出，不向新 owner 转移任务。
                        await windows.WaitForCloseAsync();
                        desktop.Shutdown();
                    }
                });
            }
            base.OnFrameworkInitializationCompleted();
        }
    }

    private static async Task MonitorAsync(Process parent, string gate, CancellationTokenSource stop)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            do
            {
                if (parent.HasExited || VoiceImeMaintenance.Exists(gate)) { stop.Cancel(); return; }
            } while (await timer.WaitForNextTickAsync(stop.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch { stop.Cancel(); }
    }

    private static async Task ServeAsync(Process parent, VoiceImeUiWindows windows, CancellationToken token)
    {
        using var pipe = new NamedPipeServerStream(VoiceImeUiProtocol.Name(parent), PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
        using (var ready = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            ready.CancelAfter(VoiceImeUiProtocol.ReadyTimeout);
            while (true)
            {
                await pipe.WaitForConnectionAsync(ready.Token);
                if (parent.HasExited) return;
                if (VoiceImeUiProtocol.GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var pid)
                    && pid == (uint)parent.Id) break;
                // 错 PID 不读帧/不发 ready；复用同一 server，期限不重置，拒绝后仍等唯一父进程。
                pipe.Disconnect();
            }
            var source = parent.MainModule?.FileName ?? throw new IOException();
            VoiceImeUiProtocol.VerifyPackage(source, Environment.ProcessPath ?? throw new IOException());
            await VoiceImeUiProtocol.WriteAsync(pipe, VoiceImeUiProtocol.Ready, ready.Token);
        }
        while (!token.IsCancellationRequested)
        {
            var value = await VoiceImeUiProtocol.ReadAsync(pipe, token);
            if (value > (byte)VoiceImeUiCommand.Language) return;
            // 单个连接逐项派发，最多三个存活窗口；协议 ACK 只代表接收，绝不冒充渲染完成。
            if (value != 0) windows.Open((VoiceImeUiCommand)value);
            using var write = CancellationTokenSource.CreateLinkedTokenSource(token);
            write.CancelAfter(VoiceImeUiProtocol.FrameTimeout);
            await VoiceImeUiProtocol.WriteAsync(pipe, VoiceImeUiProtocol.Accepted, write.Token);
        }
    }
}

internal sealed class VoiceImeUiWindows(Func<VoiceImeUiCommand, Window> factory)
{
    private readonly Dictionary<VoiceImeUiCommand, Window> _windows = new();
    private TaskCompletionSource? _closed;
    internal int Count => _windows.Count;
    internal Task WaitForCloseAsync() => Count == 0 ? Task.CompletedTask
        : (_closed ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
    internal Window Open(VoiceImeUiCommand kind)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (kind is < VoiceImeUiCommand.Settings or > VoiceImeUiCommand.Language) throw new InvalidDataException();
        if (!_windows.TryGetValue(kind, out var window))
        {
            window = factory(kind);
            _windows.Add(kind, window);
            var captured = window;
            window.Closed += (_, _) =>
            {
                if (_windows.TryGetValue(kind, out var current) && ReferenceEquals(current, captured)) _windows.Remove(kind);
                if (_windows.Count == 0) _closed?.TrySetResult();
            };
        }
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        if (!window.IsVisible) window.Show();
        window.Activate(); // 只有用户窗口命令才能到达此处，Warm 无窗口/无激活。
        return window;
    }

    internal static async Task RenderedAsync(Window window, CancellationToken token = default)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        token.ThrowIfCancellationRequested();
        window.UpdateLayout();
        var visual = ElementComposition.GetElementVisual(window) ?? throw new InvalidOperationException("窗口尚未连接合成器。");
        await Dispatcher.UIThread.AwaitWithPriority(visual.Compositor.RequestCompositionBatchCommitAsync().Rendered
            .WaitAsync(TimeSpan.FromSeconds(8), token), DispatcherPriority.Background);
    }
}