using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using TrueFluentPro.ViewModels.Settings;
using TrueFluentPro.Views;

internal static class Program
{
    private const string RootVariable = "TFP_UI_HOST_FIXTURE_ROOT";
    private static string Root = "";
    private static readonly List<object> Timings = new();
    private static readonly VoiceImeUiCommand[] Kinds = { VoiceImeUiCommand.Settings, VoiceImeUiCommand.Usage, VoiceImeUiCommand.Language };
    private static int Created, Loaded, Refreshed;
    private sealed record FixtureProcess(int Pid, long Ticks, int Session, string Executable);
    private static readonly List<FixtureProcess> FixtureProcesses = new();

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "--sheets")
                return SheetInteraction.Run();
            if (args.Length > 0 && args[0].StartsWith("--content-", StringComparison.Ordinal))
                return ContentAddressedDeployment.Run(args);
            if (args.Length > 0 && (args[0].StartsWith("--integration-", StringComparison.Ordinal)
                || args[0] == VoiceImeUiProtocol.Switch && Environment.GetEnvironmentVariable(MaintenanceIntegration.RootVariable) != null))
                return MaintenanceIntegration.Run(args);
            if (args.Length > 0 && args[0] == VoiceImeUiProtocol.Switch)
            {
                Root = Environment.GetEnvironmentVariable(RootVariable) ?? throw new Exception("fixture root missing");
                return VoiceImeUiHost.RunCore(args, Create, Path.Combine(Root, "maintenance.lock"));
            }
            if (args.Length > 0 && args[0] == "--cold")
            {
                Root = args[1];
                var kind = Enum.Parse<VoiceImeUiCommand>(args[2]);
                return AppBuilder.Configure(() => new ColdApplication(kind)).UsePlatformDetect().WithInterFont()
                    .StartWithClassicDesktopLifetime(Array.Empty<string>());
            }
            if (args.Length > 0 && args[0] == "--orphan-owner")
            {
                Root = args[1];
                return OrphanOwnerAsync().GetAwaiter().GetResult();
            }
            if (args.Length > 0 && args[0] == "--contracts")
            {
                Root = args[1];
                return AppBuilder.Configure<ContractsApplication>().UsePlatformDetect().WithInterFont()
                    .StartWithClassicDesktopLifetime(Array.Empty<string>(), ShutdownMode.OnExplicitShutdown);
            }
            Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../artifacts/", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")));
            Directory.CreateDirectory(Root);
            Console.WriteLine("ARTIFACTS=" + Root);
            RunAsync().GetAwaiter().GetResult();
            MaintenanceIntegration.PublishSignal(Path.Combine(Root, "result.json"), JsonSerializer.Serialize(new { status = "PASS", timings = Timings,
                boundary = "linked production sources + shipped managed dependencies; actual Show/CompositionBatch.Rendered; isolated config/missing DB; no main build, registration, audio, cloud or UAC" }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("PASS all; result=" + Path.Combine(Root, "result.json"));
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); CleanupFailedFixtures(); return 1; }
    }

    private static async Task RunAsync()
    {
        var solution = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../TrueFluentPro.sln"));
        var solutionHash = Hash(solution);
        // 每个窗口一次全新进程冷启动；使用与生产旧 CLI 相同的最小 Application + 真窗口构造。
        foreach (var kind in Kinds)
        {
            var folder = Prepare("cold-" + kind);
            Stamp(folder, kind);
            using var child = Start("--cold", folder, kind.ToString());
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));
            Check(child.ExitCode == 0, "cold process " + kind);
            Timings.Add(ReadMetric(folder, kind));
        }

        var shared = Prepare("shared");
        Environment.SetEnvironmentVariable(RootVariable, shared);
        await using (var client = new VoiceImeUiClient(CancellationToken.None, MaintenanceIntegration.PrepareUi))
        {
            var before = Foreground();
            var start = Stopwatch.StartNew();
            client.Request(VoiceImeUiCommand.Warm);
            var hostId = await client.Ready.WaitAsync(TimeSpan.FromSeconds(20));
            using var host = Process.GetProcessById(hostId);
            TrackFixture(host);
            Check(VisibleWindows(hostId) == 0 && !Directory.EnumerateFiles(shared, "created-*").Any(), "warm: zero visible windows / zero production window constructors");
            Check(Foreground() == before, "warm: did not activate a window");
            // CLR 静态引用加载时机不确定；本工具还含 GUI 分支，不能拿程序集加载冒充框架初始化。
            Check(Application.Current == null && TrueFluentPro.App.Services == null, "owner-side client has no Application / main DI initialization");
            Console.WriteLine($"READY_MS={start.Elapsed.TotalMilliseconds:F2};PID={hostId};WORKING_SET={host.WorkingSet64}");
            // 与 owner 同一固定模式的第二次启动不得产生第二套框架/窗口。
            using (var self = Process.GetCurrentProcess())
            using (var duplicate = Start(VoiceImeUiProtocol.Switch, self.Id.ToString(), self.StartTime.ToUniversalTime().Ticks.ToString()))
            {
                await duplicate.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Check(duplicate.ExitCode == 0 && !host.HasExited, "duplicate host rejected without replacing the live host");
            }
            foreach (var kind in Kinds)
            {
                Stamp(shared, kind);
                var metric = ObserveFileAsync(shared, "rendered-" + kind + ".json");
                client.Request(kind);
                await metric;
                Timings.Add(ReadMetric(shared, kind));
                CheckBusinessWindows(hostId, shared, kind);
            }
            var stop = Stopwatch.StartNew();
            await client.DisposeAsync();
            Check(!host.HasExited, "lease disposal preserves host PID");
            var handles = CheckBusinessWindows(hostId, shared, VoiceImeUiCommand.Language);
            Check(handles.Length == 3, "lease disposal preserves already opened windows");
            foreach (var handle in handles) PostMessage(new IntPtr(handle), 0x0010, IntPtr.Zero, IntPtr.Zero);
            await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Check(host.HasExited, "detached UI exits after user closes its windows");
            Console.WriteLine($"LEASE_EXIT_MS={stop.Elapsed.TotalMilliseconds:F2}");
        }

        // 无窗 maintenance 与父进程突然退出，均只动此工具的隔离门禁/子进程。
        var maintenance = Prepare("maintenance");
        Environment.SetEnvironmentVariable(RootVariable, maintenance);
        await using (var client = new VoiceImeUiClient(CancellationToken.None, MaintenanceIntegration.PrepareUi))
        {
            client.Request(VoiceImeUiCommand.Warm);
            using var host = Process.GetProcessById(await client.Ready.WaitAsync(TimeSpan.FromSeconds(20)));
            TrackFixture(host);
            var watch = Stopwatch.StartNew();
            MaintenanceIntegration.PublishSignal(Path.Combine(maintenance, "maintenance.lock"), "fixture");
            await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Check(host.ExitCode == 0, "maintenance closes UI");
            Console.WriteLine($"MAINTENANCE_EXIT_MS={watch.Elapsed.TotalMilliseconds:F2}");
        }
        var orphan = Prepare("orphan");
        Environment.SetEnvironmentVariable(RootVariable, orphan);
        using (var owner = Start("--orphan-owner", orphan))
        {
            await ObserveFileAsync(orphan, "orphan-host.txt");
            using var host = Process.GetProcessById(int.Parse(MaintenanceIntegration.ReadSignal(Path.Combine(orphan, "orphan-host.txt"))));
            TrackFixture(host);
            await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Check(host.HasExited, "parent exit terminates UI without an explicit shutdown command");
        }
        await RejectMalformedAsync(Prepare("malformed"));
        var contracts = Prepare("contracts");
        using (var child = Start("--contracts", contracts))
        {
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));
            Check(child.ExitCode == 0 && File.Exists(Path.Combine(contracts, "contracts-pass.txt")), "window lifecycle / current owner / deferred config loading contracts");
        }
        Check(Hash(solution) == solutionHash, "solution byte identity unchanged during execution");
    }

    private static async Task<int> OrphanOwnerAsync()
    {
        var client = new VoiceImeUiClient(CancellationToken.None, MaintenanceIntegration.PrepareUi);
        client.Request(VoiceImeUiCommand.Warm);
        var pid = await client.Ready.WaitAsync(TimeSpan.FromSeconds(20));
        MaintenanceIntegration.PublishSignal(Path.Combine(Root, "orphan-host.txt"), pid.ToString());
        // 不 dispose client，实际进程退出，验证 parent monitor/pipe EOF，而非发送 Shutdown。
        GC.KeepAlive(client);
        return 0;
    }

    private static async Task RejectMalformedAsync(string folder)
    {
        Environment.SetEnvironmentVariable(RootVariable, folder);
        using var self = Process.GetCurrentProcess();
        using var child = Start(VoiceImeUiProtocol.Switch, self.Id.ToString(), self.StartTime.ToUniversalTime().Ticks.ToString());
        using var pipe = new NamedPipeClientStream(".", VoiceImeUiProtocol.Name(self), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await pipe.ConnectAsync(deadline.Token);
        Check(await VoiceImeUiProtocol.ReadAsync(pipe, deadline.Token) == VoiceImeUiProtocol.Ready, "malformed fixture ready");
        await pipe.WriteAsync(new byte[] { 255, 255, 255, 127, 1 }, deadline.Token);
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Check(!Directory.EnumerateFiles(folder, "created-*").Any(), "oversized frame rejected before constructing a window");
        using var invalid = Start(VoiceImeUiProtocol.Switch, self.Id.ToString(), (self.StartTime.ToUniversalTime().Ticks + 1).ToString());
        await invalid.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Check(invalid.ExitCode == 2, "wrong parent start identity rejected before Avalonia");
    }

    private static Window Create(VoiceImeUiCommand kind)
    {
        Created++;
        MaintenanceIntegration.PublishSignal(Path.Combine(Root, "created-" + kind + "-" + Created), "fixture");
        Window window = kind switch
        {
            VoiceImeUiCommand.Settings => new VoiceImeSettingsWindow(() => { Interlocked.Increment(ref Loaded); return Task.FromResult(new AzureSpeechConfig()); },
                _ => throw new Exception("unexpected configuration save"), _ => Refreshed++),
            VoiceImeUiCommand.Language => new VoiceImeLanguageWindow(Path.Combine(Root, "config.json")),
            VoiceImeUiCommand.Usage => new VoiceImeUsageWindow((VoiceImeUsageReportService)Activator.CreateInstance(typeof(VoiceImeUsageReportService),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new object?[] { Path.Combine(Root, "missing-fixture.db"), null }, CultureInfo.InvariantCulture)!),
            _ => throw new Exception("invalid kind")
        };
        if (File.Exists(Path.Combine(Root, "start-" + kind)))
            window.Opened += async (_, _) =>
            {
                try
                {
                    await VoiceImeUiWindows.RenderedAsync(window);
                    Check(window.IsVisible && window.TryGetPlatformHandle()?.Handle != IntPtr.Zero && window.ClientSize.Width > 0, "real shown/rendered window " + kind);
                    var end = Stopwatch.GetTimestamp();
                    var start = long.Parse(MaintenanceIntegration.ReadSignal(Path.Combine(Root, "start-" + kind)));
                    var metric = new { kind = kind.ToString(), pid = Environment.ProcessId, elapsedMs = Stopwatch.GetElapsedTime(start, end).TotalMilliseconds,
                        boundary = "caller before Request/Process.Start -> actual window Show + CompositionBatch.Rendered", window.ClientSize, loaded = Loaded, refreshed = Refreshed,
                        hwnd = window.TryGetPlatformHandle()!.Handle.ToInt64(), windowType = window.GetType().FullName,
                        windows = CaptureWindows((IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!) };
                    var path = Path.Combine(Root, "rendered-" + kind + ".json");
                    MaintenanceIntegration.PublishSignal(path, JsonSerializer.Serialize(metric));
                    if (Application.Current is ColdApplication) window.Close();
                }
                catch (Exception ex) { Console.Error.WriteLine(ex); (Application.Current!.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)!.Shutdown(1); }
            };
        return window;
    }

    private sealed class ColdApplication(VoiceImeUiCommand kind) : Application
    {
        public override void Initialize() => VoiceImeUiTheme.Apply(this);
        public override void OnFrameworkInitializationCompleted()
        {
            var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.MainWindow = Create(kind);
            base.OnFrameworkInitializationCompleted();
        }
    }

    private sealed class ContractsApplication : Application
    {
        public override void Initialize() => VoiceImeUiTheme.Apply(this);
        public override void OnFrameworkInitializationCompleted()
        {
            var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    var manager = new VoiceImeUiWindows(Create);
                    Check(Created == 0 && Loaded == 0 && Refreshed == 0 && desktop.MainWindow == null, "framework warm has no business reads");
                    var settings = manager.Open(VoiceImeUiCommand.Settings);
                    Check(Loaded == 0 && Refreshed == 0, "Show returns before config/device/status work");
                    await VoiceImeUiWindows.RenderedAsync(settings);
                    Check(ReferenceEquals(VoiceImeElevationDialog.ResolveOwner(desktop), settings), "elevation owner is actual settings, not MainWindow");
                    // 真 tooltip 对照：PopupRoot 也是原生顶层 HWND，但不是第四个业务 Window。
                    var tip = new ToolTip { Content = "隔离窗口计数对照" };
                    ToolTip.SetTip(settings, tip);
                    try
                    {
                        ToolTip.SetIsOpen(settings, true);
                        await VoiceImeUiWindows.RenderedAsync(settings);
                        var popup = TopLevel.GetTopLevel(tip) as PopupRoot ?? throw new Exception("tooltip has no native PopupRoot");
                        var popupHandle = popup.TryGetPlatformHandle()!.Handle.ToInt64();
                        var native = NativeWindows(Environment.ProcessId);
                        var business = CaptureWindows(desktop);
                        Check(popup.IsVisible && native.Any(w => w.Hwnd == popupHandle && w.Visible)
                            && business.Length == 1 && business[0].Hwnd == settings.TryGetPlatformHandle()!.Handle.ToInt64()
                            && manager.Count == 1 && native.Count(w => w.Visible) > business.Length,
                            "visible tooltip PopupRoot HWND is distinct from the single production Window");
                        MaintenanceIntegration.PublishSignal(Path.Combine(Root, "popup-count.json"), JsonSerializer.Serialize(new
                        { pid = Environment.ProcessId, popupHandle, popupType = popup.GetType().FullName, business, native }));
                    }
                    finally { ToolTip.SetIsOpen(settings, false); }
                    settings.WindowState = WindowState.Minimized;
                    Check(ReferenceEquals(settings, manager.Open(VoiceImeUiCommand.Settings)) && settings.WindowState == WindowState.Normal && manager.Count == 1,
                        "repeat restores same window, no second constructor");
                    var language = manager.Open(VoiceImeUiCommand.Language);
                    var usage = manager.Open(VoiceImeUiCommand.Usage);
                    await VoiceImeUiWindows.RenderedAsync(language);
                    await VoiceImeUiWindows.RenderedAsync(usage);
                    Check(manager.Count == 3 && ReferenceEquals(VoiceImeElevationDialog.ResolveOwner(desktop), settings), "three windows coexist; settings remains explicit confirmation owner");
                    settings.Close();
                    Check(manager.Count == 2 && VoiceImeSettingsWindow.Current == null, "closed settings removed and weak owner cleared");
                    var second = manager.Open(VoiceImeUiCommand.Settings);
                    Check(!ReferenceEquals(settings, second) && ReferenceEquals(VoiceImeElevationDialog.ResolveOwner(desktop), second), "reopen creates new window and updates confirmation owner");
                    await VoiceImeUiWindows.RenderedAsync(second);
                    second.Close(); language.Close(); usage.Close();
                    Check(manager.Count == 0 && desktop.Windows.Count == 0 && desktop.ShutdownMode == ShutdownMode.OnExplicitShutdown, "last Close leaves framework alive without retained windows");
                    Check(!File.Exists(Path.Combine(Root, "missing-fixture.db")), "report did not create its isolated missing DB");
                    Check(TrueFluentPro.App.Services == null, "main App DI never started");
                    MaintenanceIntegration.PublishSignal(Path.Combine(Root, "contracts-pass.txt"), "PASS");
                    desktop.Shutdown();
                }
                catch (Exception ex) { Console.Error.WriteLine(ex); desktop.Shutdown(1); }
            });
            base.OnFrameworkInitializationCompleted();
        }
    }

    private static string Prepare(string name)
    {
        var folder = Path.Combine(Root, name);
        Directory.CreateDirectory(folder);
        MaintenanceIntegration.PublishSignal(Path.Combine(folder, "config.json"), "{\"VoiceIme\":{\"RecognitionLanguage\":\"auto\",\"TargetLanguage\":\"en-US\"}}");
        return folder;
    }
    private static void Stamp(string folder, VoiceImeUiCommand kind) => MaintenanceIntegration.PublishSignal(Path.Combine(folder, "start-" + kind), Stopwatch.GetTimestamp().ToString());
    private static JsonElement ReadMetric(string folder, VoiceImeUiCommand kind)
    {
        var text = MaintenanceIntegration.ReadSignal(Path.Combine(folder, "rendered-" + kind + ".json"));
        Console.WriteLine("RENDERED=" + text);
        return JsonSerializer.Deserialize<JsonElement>(text);
    }
    private static Process Start(params string[] args)
    {
        var executable = args.Length > 0 && args[0] == VoiceImeUiProtocol.Switch
            ? MaintenanceIntegration.PrepareUi(Environment.ProcessPath!) : Environment.ProcessPath!;
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        var child = Process.Start(start) ?? throw new Exception("child failed to start");
        TrackFixture(child);
        return child;
    }
    private static async Task ObserveFileAsync(string folder, string name)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(folder, name) { EnableRaisingEvents = true };
        watcher.Created += (_, _) => ready.TrySetResult();
        watcher.Renamed += (_, _) => ready.TrySetResult();
        if (File.Exists(Path.Combine(folder, name))) return;
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Check(bool success, string text) { if (!success) throw new Exception(text); Console.WriteLine("PASS " + text); }
    private sealed record BusinessWindow(long Hwnd, string? WindowType, string? Title, bool Visible);
    private sealed record NativeWindow(long Hwnd, string ClassName, string Title, bool Visible);
    private static BusinessWindow[] CaptureWindows(IClassicDesktopStyleApplicationLifetime desktop) => desktop.Windows.Select(w => new BusinessWindow(
        w.TryGetPlatformHandle()?.Handle.ToInt64() ?? 0, w.GetType().FullName, w.Title, w.IsVisible)).ToArray();
    private static long[] CheckBusinessWindows(int pid, string folder, VoiceImeUiCommand through)
    {
        var expectedKinds = Kinds.Take(Array.IndexOf(Kinds, through) + 1).ToArray();
        var metrics = expectedKinds.Select(kind => JsonSerializer.Deserialize<JsonElement>(MaintenanceIntegration.ReadSignal(
            Path.Combine(folder, "rendered-" + kind + ".json")))).ToArray();
        var business = metrics[^1].GetProperty("windows").Deserialize<BusinessWindow[]>()!;
        var native = NativeWindows(pid);
        var handles = metrics.Select(m => m.GetProperty("hwnd").GetInt64()).ToArray();
        Check(metrics.All(m => m.GetProperty("pid").GetInt32() == pid) && business.Length == expectedKinds.Length
            && handles.Distinct().Count() == handles.Length && handles.All(h => h != 0)
            && expectedKinds.Select((kind, index) => business.Count(w => w.Hwnd == handles[index] && w.Visible
                && w.WindowType == "TrueFluentPro.Views.VoiceIme" + kind + "Window") == 1).All(value => value)
            && handles.All(h => native.Any(w => w.Hwnd == h && w.Visible)), "three commands use the same host PID and exact Window/HWND identity: " + through);
        Console.WriteLine("WINDOW_IDENTITY=" + JsonSerializer.Serialize(new { pid, business, nativeVisible = native.Count(w => w.Visible),
            auxiliary = native.Where(w => w.Visible && !handles.Contains(w.Hwnd)).ToArray() }));
        return handles;
    }
    private static int VisibleWindows(int pid) => NativeWindows(pid).Count(w => w.Visible);
    private static List<NativeWindow> NativeWindows(int pid)
    {
        var result = new List<NativeWindow>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            if (owner == pid)
            {
                var className = new StringBuilder(256);
                var title = new StringBuilder(512);
                GetClassName(window, className, className.Capacity);
                GetWindowText(window, title, title.Capacity);
                var visible = IsWindowVisible(window);
                Console.WriteLine("NATIVE_WINDOW=" + JsonSerializer.Serialize(new { pid, hwnd = window.ToInt64(), className = className.ToString(), title = title.ToString(), visible }));
                result.Add(new(window.ToInt64(), className.ToString(), title.ToString(), visible));
            }
            return true;
        }, IntPtr.Zero);
        Console.WriteLine($"VISIBLE_WINDOWS_PID={pid};COUNT={result.Count(w => w.Visible)}");
        return result;
    }
    internal static void TrackFixture(Process process)
    {
        if (!process.HasExited)
            FixtureProcesses.Add(new(process.Id, process.StartTime.ToUniversalTime().Ticks, process.SessionId, process.MainModule!.FileName));
    }
    internal static void CleanupFailedFixtures()
    {
        foreach (var identity in FixtureProcesses.Distinct())
        {
            try
            {
                using var process = Process.GetProcessById(identity.Pid);
                if (!VoiceImeUiProtocol.Matches(process, identity.Pid, identity.Ticks, identity.Session, identity.Executable)) continue;
                process.Kill();
                Console.Error.WriteLine($"FIXTURE_CLEANUP_PID={identity.Pid};EXIT_OBSERVED={process.WaitForExit(5000)}");
            }
            catch (ArgumentException) { } // 已退出，PID 不再存在。
            catch (Exception ex) { Console.Error.WriteLine($"FIXTURE_CLEANUP_PID={identity.Pid};ERROR={ex.Message}"); }
        }
    }
    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int pid);
    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")] private static extern IntPtr Foreground();
}