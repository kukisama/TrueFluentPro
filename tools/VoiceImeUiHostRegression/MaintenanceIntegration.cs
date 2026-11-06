using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using TrueFluentPro.ViewModels.Settings;
using TrueFluentPro.Views;

// 仅此源码链接工具的入口/路径/替身；绝不进入生产 CLI。
internal static class MaintenanceIntegration
{
    internal const string RootVariable = "TFP_UI_MAINTENANCE_FIXTURE";
    private static string Root => Environment.GetEnvironmentVariable(RootVariable) ?? throw new Exception("missing fixture root");
    private static string FileAt(string name) => Path.Combine(Root, name);
    private static readonly List<string> Evidence = new();

    internal static int Run(string[] args)
    {
        try
        {
            if (args[0] == VoiceImeUiProtocol.Switch)
                return VoiceImeUiHost.RunCore(args, CreateSettings, FileAt("maintenance.lock"));
            if (args[0] == "--integration-owner") return OwnerAsync(args[1]).GetAwaiter().GetResult();
            if (args[0] == "--integration-audio") return AudioAsync().GetAwaiter().GetResult();
            if (args[0] == "--integration-wrong-client") return WrongClientAsync(args[1]).GetAwaiter().GetResult();
            if (args[0] != "--integration-test") return 2;
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../artifacts/",
                DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-maintenance"));
            Directory.CreateDirectory(root);
            Console.WriteLine("ARTIFACTS=" + root);
            TestAsync(root).GetAwaiter().GetResult();
            PublishSignal(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new
            {
                status = "PASS", evidence = Evidence,
                boundary = "linked production UI/VM/lifecycle/wait/deployment; fake maintenance/authorization/status only; no main build/config/DB/install/UAC/audio/cloud; user processes untouched"
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("PASS maintenance integration; result=" + Path.Combine(root, "result.json"));
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); Program.CleanupFailedFixtures(); return 1; }
    }

    internal static string PrepareUi(string source)
    {
        // 真生产差异复制器处理所有 DLL/依赖，再补此工具自己的 apphost/依赖清单。
        var executable = VoiceImeUiProtocol.Prepare(source);
        var destination = Path.GetDirectoryName(executable)!;
        Copy(source, executable);
        foreach (var name in new[] { "VoiceImeUiHostRegression.deps.json", "VoiceImeUiHostRegression.runtimeconfig.json" })
            Copy(Path.Combine(Path.GetDirectoryName(source)!, name), Path.Combine(destination, name));
        return executable;
    }

    private static async Task TestAsync(string root)
    {
        var solution = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../TrueFluentPro.sln"));
        var solutionHash = Hash(solution);
        var package = Path.Combine(root, "package", "host");
        Directory.CreateDirectory(package);
        // 改名的是本工具 apphost，内嵌入口仍为本工具 DLL，绝不是生产 TrueFluentPro.exe。
        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory))
            if (Path.GetExtension(file) == ".dll" || file.EndsWith(".deps.json") || file.EndsWith(".runtimeconfig.json"))
                Copy(file, Path.Combine(package, Path.GetFileName(file)));
        var executable = Path.Combine(package, "TrueFluentPro.exe");
        Copy(Environment.ProcessPath!, executable);

        foreach (var scenario in new[] { "install-dispose", "uninstall-parent-exit" })
        {
            var folder = Path.Combine(root, scenario);
            Directory.CreateDirectory(folder);
            using var owner = Start(executable, folder, "--integration-owner", scenario);
            await ObserveAsync(folder, "ui-pid");
            using var ui = Process.GetProcessById(int.Parse(ReadSignal(Path.Combine(folder, "ui-pid"))));
            Program.TrackFixture(ui);
            await ObserveAsync(folder, "maintenance-begun");
            Check(!owner.HasExited && !ui.HasExited, scenario + ": real settings button entered one fake authorization flow");
            PublishSignal(Path.Combine(folder, "maintenance.lock"), "fixture gate");
            await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            Check(owner.ExitCode == 0, scenario + ": audio owner exited without UI wait/kill");
            await ObserveAsync(folder, "audio-wait-passed");
            Check(!ui.HasExited && FindHosts(executable).Length == 0, scenario + ": strict WaitForHost passed while UI survives; exact-path audio discovery is empty");
            // 明确跨过旧 Dispose 的 2 秒强杀窗口，不用延迟充当 ready/渲染证据。
            await Task.Delay(TimeSpan.FromMilliseconds(2300));
            Check(!ui.HasExited, scenario + ": original settings survives beyond former 2-second kill deadline");
            PublishSignal(Path.Combine(folder, "complete-maintenance"), "return original result");
            await ObserveAsync(folder, "result-visible.json");
            Console.WriteLine("VISIBLE=" + ReadSignal(Path.Combine(folder, "result-visible.json")));
            Check(!ui.HasExited, scenario + ": completed result remains visible in same settings window");
            Check(!Directory.EnumerateFiles(folder, "host-*.json").Any(), scenario + ": UI has no audio receipt");

            using var replacement = Start(executable, folder, "--integration-audio");
            await ObserveAsync(folder, "replacement-ready");
            var found = FindHosts(executable);
            Check(found.SequenceEqual(new[] { replacement.Id }), scenario + ": replacement audio owner discovered alone with UI still open");
            // 不放宽 WaitForHost：释放麦克风但保持同 PID/start/exe 的录音进程存活，必须继续等待。
            PublishSignal(Path.Combine(folder, "release-microphone"), "fixture");
            await ObserveAsync(folder, "microphone-released");
            var wait = VoiceImeMaintenance.WaitForHostAsync(folder);
            await Task.Delay(180);
            Check(!wait.IsCompleted, scenario + ": released microphone does not bypass live audio receipt");
            PublishSignal(Path.Combine(folder, "exit-audio"), "fixture");
            await replacement.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            using (await wait.WaitAsync(TimeSpan.FromSeconds(5))) { }
            Check(replacement.ExitCode == 0 && !ui.HasExited, scenario + ": audio wait completes only after true process exit; UI result retained");
            PublishSignal(Path.Combine(folder, "close-settings"), "user close");
            await ui.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Check(ui.ExitCode == 0, scenario + ": last settings Close exits detached UI");
        }
        await RejectWrongClientAsync(executable, Path.Combine(root, "wrong-client"));
        Check(Hash(solution) == solutionHash, "solution byte identity unchanged");
    }

    private static async Task<int> OwnerAsync(string scenario)
    {
        using var ownerLock = new FileStream(FileAt("owner.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var microphone = new FileStream(FileAt("microphone.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        VoiceImeMaintenance.RecordHost(Root);
        using var lifetime = new CancellationTokenSource();
        var monitor = VoiceImeMaintenance.MonitorAsync(FileAt("maintenance.lock"), lifetime, CancellationToken.None);
        var client = new VoiceImeUiClient(lifetime.Token, PrepareUi);
        client.Request(VoiceImeUiCommand.Warm);
        var ui = await client.Ready.WaitAsync(TimeSpan.FromSeconds(20));
        PublishSignal(FileAt("ui-pid"), ui.ToString());
        client.Request(VoiceImeUiCommand.Settings);
        await monitor;
        if (scenario == "install-dispose") await client.DisposeAsync();
        else GC.KeepAlive(client); // 真父进程退出，不发关闭命令、不 Dispose 代理。
        return 0;
    }

    private static async Task<int> AudioAsync()
    {
        using var owner = new FileStream(FileAt("owner.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using (var microphone = new FileStream(FileAt("microphone.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            VoiceImeMaintenance.RecordHost(Root);
            PublishSignal(FileAt("replacement-ready"), Environment.ProcessId.ToString());
            await ObserveAsync(Root, "release-microphone");
        }
        PublishSignal(FileAt("microphone-released"), "still alive");
        await ObserveAsync(Root, "exit-audio");
        return 0;
    }

    private static Window CreateSettings(VoiceImeUiCommand kind)
    {
        if (kind != VoiceImeUiCommand.Settings) throw new Exception("unexpected business window");
        var install = Path.GetFileName(Root) == "install-dispose";
        var expected = install ? "隔离安装/修复完成；原窗口结果保留。" : "隔离卸载完成；原窗口结果保留。";
        var manages = 0; var confirmations = 0; var elevations = 0; var refreshes = 0;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new VoiceImeSectionVM(new VoiceImeRegistrationService(),
            value => VoiceImeRegistrationService.ManageFromUiAsync(value,
                flag => { manages++; Check(flag == install, "button install/uninstall mapping"); return Task.FromResult(new VoiceImeRegistrationService.Result(false, "fixture permission") { RequiresAdministrator = true }); },
                _ => { confirmations++; return Task.FromResult(true); },
                async _ =>
                {
                    elevations++;
                    PublishSignal(FileAt("maintenance-begun"), "fixture only; no UAC");
                    await ObserveAsync(Root, "maintenance.lock");
                    using (await VoiceImeMaintenance.WaitForHostAsync(Root)) { }
                    PublishSignal(FileAt("audio-wait-passed"), "strict production wait");
                    await ObserveAsync(Root, "complete-maintenance");
                    return new VoiceImeRegistrationService.Result(true, expected);
                }),
            () => { refreshes++; return Task.FromResult(new VoiceImeRegistrationService.Result(true, "fake host refresh")); },
            () => Task.FromResult(new VoiceImeRegistrationService.Result(true, "隔离状态已刷新。")));
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.IsRegistrationBusy) && !vm.IsRegistrationBusy) completed.TrySetResult(); };
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new VoiceImeSettingsWindow(() => Task.FromResult(new AzureSpeechConfig()),
            _ => throw new Exception("unexpected real configuration save"), _ => loaded.TrySetResult(), vm);
        window.Opened += async (_, _) =>
        {
            try
            {
                await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await VoiceImeUiWindows.RenderedAsync(window);
                var command = install ? vm.RegisterCommand : vm.UnregisterCommand;
                var button = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, command));
                button.BringIntoView();
                Check(button.IsEffectivelyEnabled && ReferenceEquals(VoiceImeSettingsWindow.Current, window), "actual settings button bound to source-linked VM");
                // Avalonia Button.OnKeyDown(Enter) 调用 OnClick -> Command.Execute；不是直接调用 VM 私有方法。
                button.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = button });
                Check(vm.IsRegistrationBusy && !command.CanExecute(null), "maintenance busy gate prevents duplicate action");
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
                Check(vm.OperationMessage == expected && vm.RegistrationStatus == "隔离状态已刷新。"
                    && manages == 1 && confirmations == 1 && elevations == 1 && refreshes == (install ? 1 : 0), "original result/status and single authorization retained");
                Check(window.IsVisible && ReferenceEquals(VoiceImeSettingsWindow.Current, window), "same settings instance and elevation owner survive");
                var text = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == expected);
                text.BringIntoView();
                await VoiceImeUiWindows.RenderedAsync(window);
                var point = text.TranslatePoint(default, window) ?? throw new Exception("result not attached");
                Check(text.IsEffectivelyVisible && text.Bounds.Height > 0 && point.Y >= 0
                    && point.Y + text.Bounds.Height <= window.ClientSize.Height, "original operation result is laid out in the visible viewport");
                PublishSignal(FileAt("result-visible.json"), JsonSerializer.Serialize(new { pid = Environment.ProcessId,
                    hwnd = window.TryGetPlatformHandle()!.Handle.ToInt64(), sameWindow = true, text = text.Text,
                    y = point.Y, height = text.Bounds.Height, clientHeight = window.ClientSize.Height, manages, confirmations, elevations, refreshes }));
                await ObserveAsync(Root, "close-settings");
                window.Close();
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); ((IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!).Shutdown(1); }
        };
        return window;
    }

    private static async Task RejectWrongClientAsync(string executable, string folder)
    {
        Directory.CreateDirectory(folder);
        using var parent = Process.GetCurrentProcess();
        // 此子模式的父是本工具原 apphost，因此用严格配对的旁路 apphost，不用另一目录的克隆。
        using var ui = Start(PrepareUi(Environment.ProcessPath!), folder, VoiceImeUiProtocol.Switch,
            parent.Id.ToString(), parent.StartTime.ToUniversalTime().Ticks.ToString());
        var uiTicks = ui.StartTime.ToUniversalTime().Ticks;
        var uiExecutable = ui.MainModule!.FileName;
        using (var wrong = Start(executable, folder, "--integration-wrong-client", VoiceImeUiProtocol.Name(parent)))
        {
            await wrong.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Console.WriteLine($"WRONG_CLIENT_PID={wrong.Id};EXIT={wrong.ExitCode};UI_PID={ui.Id};UI_EXIT={(ui.HasExited ? ui.ExitCode.ToString() : "running")}");
            var observation = JsonSerializer.Deserialize<JsonElement>(ReadSignal(Path.Combine(folder, "wrong-client-result.json")));
            Console.WriteLine("WRONG_CLIENT=" + observation.GetRawText());
            using var identity = WindowsIdentity.GetCurrent();
            Check(wrong.ExitCode == 0 && observation.GetProperty("pid").GetInt32() == wrong.Id && wrong.Id != parent.Id
                && observation.GetProperty("sid").GetString() == identity.User!.Value
                && observation.GetProperty("session").GetInt32() == parent.SessionId
                && observation.GetProperty("rejected").GetBoolean()
                && VoiceImeUiProtocol.Matches(ui, ui.Id, uiTicks, parent.SessionId, uiExecutable),
                "wrong same-user client receives no ready and does not stop listener");
        }
        using (var pipe = new NamedPipeClientStream(".", VoiceImeUiProtocol.Name(parent), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            await pipe.ConnectAsync(timeout.Token);
            Check(VoiceImeUiProtocol.GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var serverPid)
                && serverPid == (uint)ui.Id && VoiceImeUiProtocol.Matches(ui, ui.Id, uiTicks, parent.SessionId, uiExecutable),
                "legitimate reconnect uses original listener PID/start/exe");
            Check(await VoiceImeUiProtocol.ReadAsync(pipe, timeout.Token) == VoiceImeUiProtocol.Ready, "legitimate owner connects after rejected PID");
            await VoiceImeUiProtocol.WriteAsync(pipe, 0, timeout.Token);
            Check(await VoiceImeUiProtocol.ReadAsync(pipe, timeout.Token) == VoiceImeUiProtocol.Accepted, "legitimate owner warm command ACK");
        }
        await ui.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Console.WriteLine($"RECONNECTED_UI_PID={ui.Id};EXIT={ui.ExitCode}");
        Check(ui.ExitCode == 0, "warm UI without windows exits on lease EOF");
    }

    private static async Task<int> WrongClientAsync(string name)
    {
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var stage = "connect";
        Exception? rejection = null;
        var rejected = false;
        try
        {
            await pipe.ConnectAsync(timeout.Token);
            stage = "read-first-byte";
            // 收到任何字节（包括不完整 Ready/畸形帧）均失败，不能被 IOException 当作拒绝成功。
            rejected = await pipe.ReadAsync(new byte[1], timeout.Token) == 0;
        }
        catch (IOException ex) when ((uint)ex.HResult is 0x8007006D or 0x800700E9 or 0x800700E8)
        { rejection = ex; rejected = true; } // Windows broken pipe / pipe disconnected / no data。
        catch (InvalidOperationException ex) when (stage == "connect" && pipe.IsConnected
            && ex.StackTrace?.Contains("System.IO.Pipes.NamedPipeClientStream.ValidateRemotePipeUser()", StringComparison.Ordinal) == true)
        {
            // .NET TryConnect 先设置 Connected，再由 CurrentUserOnly 查询 ACL；服务端按 PID 拒绝可抢先 Disconnect。
            // 仅此已核验栈被识别；权限错误、连接超时及其它 InvalidOperationException 仍直接失败。
            rejection = ex; rejected = true;
        }
        using var identity = WindowsIdentity.GetCurrent();
        using var current = Process.GetCurrentProcess();
        PublishSignal(FileAt("wrong-client-result.json"), JsonSerializer.Serialize(new { pid = Environment.ProcessId,
            session = current.SessionId, sid = identity.User!.Value, stage, rejected,
            exception = rejection?.GetType().FullName, hresult = rejection?.HResult, stack = rejection?.StackTrace }));
        return rejected ? 0 : 1;
    }

    private static int[] FindHosts(string executable)
    {
        var method = typeof(VoiceImeRegistrationService).GetMethod("FindStandaloneHostProcesses", BindingFlags.NonPublic | BindingFlags.Static)!;
        var processes = (Process[])method.Invoke(null, new object[] { executable })!;
        try { return processes.Select(p => p.Id).OrderBy(id => id).ToArray(); }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private static Process Start(string executable, string folder, params string[] arguments)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
        info.Environment[RootVariable] = folder;
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        var child = Process.Start(info) ?? throw new Exception("fixture process failed to start");
        Program.TrackFixture(child);
        return child;
    }

    private static async Task ObserveAsync(string folder, string name)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(folder, name);
        watcher.Created += (_, _) => tcs.TrySetResult();
        watcher.Renamed += (_, _) => tcs.TrySetResult();
        watcher.EnableRaisingEvents = true;
        if (File.Exists(Path.Combine(folder, name))) return;
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    internal static void PublishSignal(string path, string content)
    {
        // Created 可能早于写入完成；同目录唯一临时文件关闭后才发布最终名，供 Created/Renamed 观察者读取。
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, content);
        File.Move(temporary, path, overwrite: true);
    }

    internal static string ReadSignal(string path)
    {
        // Renamed 可早于 MoveFileEx 关闭 DELETE 句柄；允许重命名共享，但仍不允许并发写入。
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var reader = new StreamReader(file);
        return reader.ReadToEnd();
    }

    private static void Copy(string source, string destination)
    {
        if (!File.Exists(destination) || Hash(source) != Hash(destination)) File.Copy(source, destination, true);
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Check(bool success, string message)
    {
        if (!success) throw new Exception(message);
        Evidence.Add(message);
        Console.WriteLine("PASS " + message);
    }
}