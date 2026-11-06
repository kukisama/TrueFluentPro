using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TrueFluentPro.Services;

// 复用原设置页维护 fixture；仅克隆工具 apphost/依赖，不运行产品 EXE 或系统维护。
internal static class ContentAddressedDeployment
{
    private static readonly List<object> Evidence = new();

    internal static int Run(string[] args)
    {
        try
        {
            if (args[0] == "--content-warm-owner") return WarmAsync().GetAwaiter().GetResult();
            if (args[0] == "--content-stale-owner") return StaleAsync().GetAwaiter().GetResult();
            if (args[0] != "--content-test") return 2;
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../artifacts/",
                DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-content"));
            Directory.CreateDirectory(root);
            Console.WriteLine("ARTIFACTS=" + root);
            TestAsync(root).GetAwaiter().GetResult();
            MaintenanceIntegration.PublishSignal(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new
            {
                status = "PASS", evidence = Evidence,
                boundary = "linked production sources; cloned fixture; detached real Settings + new owner ready/ACK; no root build/Sync/config DB/microphone/cloud/registration/UAC/user process termination"
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("RESULT=" + Path.Combine(root, "result.json"));
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static async Task TestAsync(string root)
    {
        ManifestContracts(Path.Combine(root, "manifest"));
        var package = Path.Combine(root, "package", "host");
        Directory.CreateDirectory(package);
        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory))
            if (Path.GetExtension(file).Equals(".dll", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".deps.json") || file.EndsWith(".runtimeconfig.json"))
                File.Copy(file, Path.Combine(package, Path.GetFileName(file)));
        var source = Path.Combine(package, "TrueFluentPro.exe");
        File.Copy(Environment.ProcessPath!, source);
        File.Copy(Path.Combine(package, "VoiceImeUiHostRegression.runtimeconfig.json"), Path.Combine(package, "TrueFluentPro.runtimeconfig.json"));
        var folder = Path.Combine(root, "install-dispose");
        Directory.CreateDirectory(folder);
        using var owner = Start(source, folder, "--integration-owner", "install-dispose");
        await ObserveAsync(folder, "ui-pid");
        using var oldUi = Process.GetProcessById(int.Parse(Read(folder, "ui-pid")));
        var oldExecutable = oldUi.MainModule!.FileName;
        var oldDirectory = Path.GetDirectoryName(oldExecutable)!;
        var oldDll = Path.Combine(oldDirectory, "VoiceImeUiHostRegression.dll");
        try
        {
            await ObserveAsync(folder, "maintenance-begun");
            Publish(folder, "maintenance.lock", "fixture gate");
            await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            Check(owner.ExitCode == 0, "old owner exited without waiting for or killing Settings");
            await ObserveAsync(folder, "audio-wait-passed");
            Publish(folder, "complete-maintenance", "original result");
            await ObserveAsync(folder, "result-visible.json");
            var visible = Read(folder, "result-visible.json");
            Check(!oldUi.HasExited && oldUi.Modules.Cast<ProcessModule>().Any(m => m.FileName.Equals(oldDll, StringComparison.OrdinalIgnoreCase)),
                "detached Settings is alive with actual copied DLL mapped");
            Check(VoiceImeUiProtocol.SourceExecutable(oldExecutable) == source, "content directory maps to original owner executable");
            var baseline = Snapshot(package, oldDirectory);
            var same = VoiceImeHostDeployment.Create(package, oldDirectory);
            Check(same.Changes.Count == 0 && same.Synchronize() == 0 && MaintenanceIntegration.PrepareUi(source) == oldExecutable,
                "same complete package reuses directory with zero planned/copied files");
            Evidence.Add(new { @case = "old-settings", ownerPid = owner.Id, ownerExit = owner.ExitCode,
                uiPid = oldUi.Id, executable = oldExecutable, same.ContentId, visible = JsonSerializer.Deserialize<JsonElement>(visible) });
            await NewOwnerAsync("same-new-owner", oldExecutable);

            // 未加载文件与已加载 DLL 分别改变；产品/程序集版本仍相同，内容身份必须分别变化。
            File.AppendAllText(Path.Combine(package, "TrueFluentPro.runtimeconfig.json"), "\n ");
            var configExecutable = MaintenanceIntegration.PrepareUi(source);
            Check(configExecutable != oldExecutable, "runtimeconfig-only change gets a separate full package");
            await NewOwnerAsync("config-new-owner", configExecutable);
            var sourceDll = Path.Combine(package, "VoiceImeUiHostRegression.dll");
            var version = FileVersionInfo.GetVersionInfo(sourceDll).FileVersion;
            using (var file = new FileStream(sourceDll, FileMode.Append, FileAccess.Write, FileShare.Read))
                file.Write(Encoding.UTF8.GetBytes("CONTENT_VARIANT_SAME_VERSION"));
            var changedExecutable = MaintenanceIntegration.PrepareUi(source);
            Check(changedExecutable != oldExecutable && changedExecutable != configExecutable
                && FileVersionInfo.GetVersionInfo(sourceDll).FileVersion == version, "same-version loaded-DLL change gets another directory");
            await NewOwnerAsync("dll-new-owner", changedExecutable);
            Check(baseline == Snapshot(package, oldDirectory) && !oldUi.HasExited,
                "all original UI package hashes/mtimes unchanged; original Settings remains alive");
            Check(MaintenanceIntegration.PrepareUi(source) == changedExecutable
                && VoiceImeHostDeployment.Create(package, Path.GetDirectoryName(changedExecutable)!).Changes.Count == 0,
                "changed package also reuses its own complete content directory");
            using var stale = Start(source, MakeFolder(root, "stale-owner"), "--content-stale-owner");
            await stale.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Check(stale.ExitCode == 0 && !oldUi.HasExited, "startup source change fails closed without affecting original Settings");
            Evidence.Add(new { @case = "stale-source", ownerExit = stale.ExitCode,
                result = JsonSerializer.Deserialize<JsonElement>(Read(Path.Combine(root, "stale-owner"), "stale.json")) });
        }
        finally
        {
            // 只关闭本轮创建且已核验的 fixture 设置页；不强杀任何窗口/应用，也不删缓存。
            Publish(folder, "close-settings", "fixture user close");
            await oldUi.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
        }
        Check(oldUi.ExitCode == 0, "fixture Settings closed normally; no process termination or cache cleanup");
        Evidence.Add(new { @case = "fixture-close", uiExit = oldUi.ExitCode, remainingOwnedProcesses = Array.Empty<int>() });

        async Task NewOwnerAsync(string name, string expected)
        {
            var next = MakeFolder(root, name);
            using var process = Start(source, next, "--content-warm-owner");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Check(process.ExitCode == 0 && !oldUi.HasExited, name + ": new owner ready/ACK; old Settings still alive");
            var result = JsonSerializer.Deserialize<JsonElement>(Read(next, "ready.json"));
            Check(result.GetProperty("executable").GetString() == expected && result.GetProperty("source").GetString() == source,
                name + ": exact expected UI path and source mapping");
            Evidence.Add(new { @case = name, ownerPid = process.Id, ownerExit = process.ExitCode, oldUiAlive = !oldUi.HasExited, result });
        }
    }

    private static async Task<int> WarmAsync()
    {
        var folder = Environment.GetEnvironmentVariable(MaintenanceIntegration.RootVariable)!;
        await using var client = new VoiceImeUiClient(CancellationToken.None, MaintenanceIntegration.PrepareUi);
        client.Request(VoiceImeUiCommand.Warm);
        using var ui = Process.GetProcessById(await client.Ready.WaitAsync(TimeSpan.FromSeconds(15)));
        var executable = ui.MainModule!.FileName;
        var source = VoiceImeUiProtocol.SourceExecutable(executable);
        Check(source == Environment.ProcessPath && executable == VoiceImeUiProtocol.UiExecutable(source), "ready path and parent identity paired");
        await client.DisposeAsync();
        await ui.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Check(ui.ExitCode == 0, "new warm UI exits normally on lease close");
        Publish(folder, "ready.json", JsonSerializer.Serialize(new { uiPid = ui.Id, uiExit = ui.ExitCode, readyAck = true, executable, source }));
        return 0;
    }

    private static async Task<int> StaleAsync()
    {
        var folder = Environment.GetEnvironmentVariable(MaintenanceIntegration.RootVariable)!;
        var source = Environment.ProcessPath!;
        var config = Path.Combine(Path.GetDirectoryName(source)!, "TrueFluentPro.runtimeconfig.json");
        var original = File.ReadAllBytes(config);
        string? prepared = null;
        try
        {
            await using var client = new VoiceImeUiClient(CancellationToken.None, exe =>
            {
                prepared = MaintenanceIntegration.PrepareUi(exe);
                File.AppendAllText(config, "\n  "); // 模拟 Prepare 返回前源文件变化，不增加生产测试开关。
                return prepared;
            });
            var ensure = typeof(VoiceImeUiClient).GetMethod("EnsureReadyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Exception? rejected = null;
            try { await ((Task)ensure.Invoke(client, null)!).WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (IOException ex) { rejected = ex; }
            Check(rejected != null && typeof(VoiceImeUiClient).GetField("_child", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client) == null,
                "client rejects changed source before spawning UI");
            using var self = Process.GetCurrentProcess();
            using var host = Start(prepared!, folder, VoiceImeUiProtocol.Switch, self.Id.ToString(), self.StartTime.ToUniversalTime().Ticks.ToString());
            await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Check(host.ExitCode == 1, "RunCore rejects stale content directory before framework/ready");
            Publish(folder, "stale.json", JsonSerializer.Serialize(new { clientRejected = rejected!.GetType().FullName,
                spawnedByClient = false, hostExit = host.ExitCode, staleExecutable = prepared }));
            return 0;
        }
        finally { File.WriteAllBytes(config, original); }
    }

    private static void ManifestContracts(string root)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TrueFluentPro.exe"] = "apphost", ["TrueFluentPro.deps.json"] = "deps", ["TrueFluentPro.runtimeconfig.json"] = "runtime",
            ["TrueFluentPro.dll"] = "managed", ["Dependency.dll"] = "dependency", ["runtimes/win-x64/native/native.dll"] = "native runtime"
        };
        foreach (var arch in new[] { "win-x64", "win-x86", "win-arm64" })
        {
            files["voice-ime/" + arch + "/TrueFluentVoiceIme.dll"] = "IME " + arch;
            files["voice-ime/" + arch + "/VoiceImeRegister.exe"] = "helper " + arch;
        }
        foreach (var file in files) Write(root, file.Key, file.Value);
        Write(root, "Updater.dll", "excluded"); Write(root, "README.md", "excluded fixture marker");
        var expected = ManifestId(files);
        Check(Id(root) == expected && VoiceImeHostDeployment.Create(root, root).ContentId == expected,
            "independent sorted full-manifest digest includes paths and all delivery files even with zero changes");
        var reverse = root + "-reverse";
        foreach (var file in files.Reverse()) Write(reverse, file.Key, file.Value);
        Check(Id(reverse) == expected, "content ID independent of enumeration order and absolute source directory");
        foreach (var file in files)
        {
            Write(root, file.Key, file.Value + " changed");
            Check(Id(root) != expected, "content ID covers " + file.Key);
            Write(root, file.Key, file.Value);
        }
        var runtime = Path.Combine(root, "runtimes", "win-x64", "native", "native.dll");
        File.Move(runtime, runtime + ".renamed");
        Check(Id(root) != expected, "same bytes under a different relative path change content ID");
        File.Move(runtime + ".renamed", runtime);
        Write(root, "Updater.dll", "ignored change");
        Check(Id(root) == expected, "original exclusions and copy/maintenance file selection unchanged");
        var source = Path.Combine(root, "TrueFluentPro.exe");
        Check(VoiceImeUiProtocol.SourceExecutable(root + ".voice-ime-ui\\TrueFluentPro.exe") == source, "legacy suffix source mapping preserved");
        foreach (var invalid in new[] { "123", new string('G', 64), new string('Ａ', 64) })
            Reject(() => VoiceImeUiProtocol.SourceExecutable(Path.Combine(root + ".voice-ime-ui", invalid, "TrueFluentPro.exe")), "invalid hash directory rejected");
        Reject(() => VoiceImeUiProtocol.UiExecutable(root + ".voice-ime-ui\\TrueFluentPro.exe"), "legacy UI cannot start owner proxy");
        Reject(() => VoiceImeUiProtocol.UiExecutable(VoiceImeUiProtocol.UiExecutable(source)), "content UI cannot start owner proxy");
        Reject(() => VoiceImeUiProtocol.Prepare("relative.exe"), "Prepare retains absolute-path guard before normalization");
        Evidence.Add(new { @case = "manifest-contract", contentId = expected, files = files.Keys.ToArray() });
    }

    private static string ManifestId(Dictionary<string, string> files)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            foreach (var file in files.OrderBy(f => f.Key, StringComparer.Ordinal))
            { writer.Write(file.Key); writer.Write(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file.Value)))); }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
    private static string Id(string root) => VoiceImeHostDeployment.Create(root, root + ".voice-ime-ui").ContentId;
    private static void Write(string root, string relative, string value)
    { var path = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, value, new UTF8Encoding(false)); }
    private static string Snapshot(string source, string destination) => JsonSerializer.Serialize(VoiceImeHostDeployment.Create(source, destination)
        .DestinationPaths.Where(File.Exists).Select(p => new { path = p, hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))), ticks = File.GetLastWriteTimeUtc(p).Ticks }));
    private static string MakeFolder(string root, string name) { var folder = Path.Combine(root, name); Directory.CreateDirectory(folder); return folder; }
    private static void Publish(string root, string name, string value) => MaintenanceIntegration.PublishSignal(Path.Combine(root, name), value);
    private static string Read(string root, string name) => MaintenanceIntegration.ReadSignal(Path.Combine(root, name));
    private static async Task ObserveAsync(string root, string name)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(root, name);
        watcher.Created += (_, _) => signal.TrySetResult(); watcher.Renamed += (_, _) => signal.TrySetResult(); watcher.EnableRaisingEvents = true;
        if (!File.Exists(Path.Combine(root, name))) await signal.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
    private static Process Start(string executable, string root, params string[] args)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
        start.Environment[MaintenanceIntegration.RootVariable] = root;
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return Process.Start(start) ?? throw new Exception("fixture start failed");
    }
    private static void Reject(Action action, string message)
    { try { action(); } catch (IOException) { Check(true, message); return; } throw new Exception(message); }
    private static void Check(bool success, string message)
    { if (!success) throw new Exception(message); Console.WriteLine("PASS " + message); }
}