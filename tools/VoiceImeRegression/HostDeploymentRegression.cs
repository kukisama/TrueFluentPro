using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using TrueFluentPro.Services;
using static Program;

/// <summary>仅反射纯文件 helper；假文件保留在本轮 artifacts 内，不构造登记服务、不操作任何进程。</summary>
internal static class HostDeploymentRegression
{
    private static readonly Type Deployment = typeof(VoiceImeRegistrationService).Assembly
        .GetType("TrueFluentPro.Services.VoiceImeHostDeployment", throwOnError: true)!;
    private static readonly DateTime Stamp = new(2020, 1, 2, 3, 4, 6, DateTimeKind.Utc);

    internal static Task VerifyAsync()
    {
        var root = Path.Combine(Artifacts, "files");
        var source = Path.Combine(root, "source");
        var target = Path.Combine(root, "host");
        var selected = new[]
        {
            "TrueFluentPro.exe", "TrueFluentPro.dll", "TrueFluentPro.deps.json", "TrueFluentPro.runtimeconfig.json",
            "Runtime.Dependency.dll", "MixedCase.Dependency.DLL",
            "runtimes/win-x64/native/fake.dll", "runtimes/win-x86/native/runtime.json", "runtimes/win-arm64/native/fake.dll",
            "voice-ime/win-x64/TrueFluentVoiceIme.dll", "voice-ime/win-x64/VoiceImeRegister.exe",
            "voice-ime/win-x86/TrueFluentVoiceIme.dll", "voice-ime/win-x86/VoiceImeRegister.exe",
            "voice-ime/win-arm64/TrueFluentVoiceIme.dll", "voice-ime/win-arm64/VoiceImeRegister.exe"
        };
        var excluded = new[]
        {
            "Updater.exe", "Updater.dll", "Updater.deps.json", "Updater.runtimeconfig.json", "Updater.Settings.JSON",
            "other.exe", "app.json", "TrueFluentPro.pdb", "README.md", "versions.json", "ignored.txt",
            "Assets/fake.png", "Assets/versions.json",
            "voice-ime/source/Registration.cpp", "voice-ime/source/ThirdParty/libIME2/LICENSE.txt",
            "voice-ime/source/TrueFluentVoiceIme.dll", "voice-ime/win-x64/TrueFluentVoiceIme.pdb",
            "voice-ime/win-x64/test.exe", "voice-ime/win-x86/obj/TrueFluentVoiceIme.dll",
            "voice-ime/win-arm64/test.exe", "voice-ime/win-test/VoiceImeRegister.exe"
        };
        foreach (var relative in selected.Concat(excluded))
        {
            var path = Path.Combine(source, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "SYNTHETIC_NOT_EXECUTABLE:" + relative + ":v1");
        }

        var initial = Create(source, target);
        Check(Count(initial) == selected.Length && !Directory.Exists(target), "规划写入了目标或遗漏交付文件");
        Check(Sync(initial) == selected.Length && selected.All(p => Digest(Path.Combine(source, p)) == Digest(Path.Combine(target, p)))
            && excluded.All(p => !File.Exists(Path.Combine(target, p))), "运行文件未完整补齐或复制了排除项");
        Note("HOST_FILES runtime allowlist: app/dependencies/runtimes and x64/x86/arm64 DLL+helper retained; updater/assets/source/debug/tools excluded");

        // 模拟旧 Host 缓存：未知文件及已被新清单排除的文件既不覆盖，也不主动删除。
        var retained = new[] { "unknown.keep", "Updater.exe", "Assets/fake.png", "voice-ime/source/legacy.cpp" };
        foreach (var relative in retained)
        {
            var path = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "SYNTHETIC_OLD_CACHE:" + relative);
            File.SetLastWriteTimeUtc(path, Stamp);
        }
        var monitored = selected.Concat(retained).ToArray();
        foreach (var relative in excluded)
            File.AppendAllText(Path.Combine(source, relative), ":changed-but-excluded");

        // 目标和源刻意使用不同时间；相同内容必须不被重写，而不是仅按 mtime/长度跳过。
        foreach (var relative in selected)
        {
            File.SetLastWriteTimeUtc(Path.Combine(target, relative), Stamp);
            File.SetLastWriteTimeUtc(Path.Combine(source, relative), Stamp.AddDays(1));
        }
        var baseline = Snapshot(target, monitored);
        using (var readOnly = new FileStream(Path.Combine(target, selected[0]), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var unchanged = Create(source, target);
            Check(Count(unchanged) == 0 && Sync(unchanged) == 0 && Unchanged(target, baseline),
                "同内容产生写入、改变 mtime 或尝试覆盖已共享读取的 exe");
        }
        Note("HOST_FILES same content: planned=0 copied=0; all destination hashes/mtime unchanged");

        const string changedFile = "TrueFluentPro.runtimeconfig.json";
        var changed = Path.Combine(source, changedFile);
        var sourceTime = File.GetLastWriteTimeUtc(changed);
        File.WriteAllText(changed, File.ReadAllText(changed).Replace(":v1", ":v2", StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(changed, sourceTime); // 同长度、同 mtime，仍须由摘要发现。
        var one = Create(source, target);
        Check(Count(one) == 1 && Sync(one) == 1 && Unchanged(target, baseline, changedFile)
            && Digest(Path.Combine(target, changedFile)) == Digest(changed), "单文件变化未严格同步一个文件");
        Check(Sync(one) == 0, "同一计划再次同步仍重复写入");
        Note("HOST_FILES one same-length content change: planned=1 copied=1; other files unchanged; repeat copied=0");

        const string missingFile = "runtimes/win-x64/native/fake.dll";
        File.Delete(Path.Combine(target, missingFile)); // 仅本轮假文件；不接触安装目录。
        var missing = Create(source, target);
        Check(Count(missing) == 1 && Sync(missing) == 1
            && Digest(Path.Combine(target, missingFile)) == Digest(Path.Combine(source, missingFile)),
            "缺失单文件没有仅补齐一个");
        var sourceBefore = Snapshot(source, selected.Concat(excluded));
        var self = Create(source, Path.Combine(source, "."));
        Check(Count(self) == 0 && Sync(self) == 0 && Unchanged(source, sourceBefore), "同一路径发生自复制或修改 mtime");

        foreach (var destination in new[]
        {
            Path.Combine(source, "nested"), root, "relative-host", @"\\invalid\host",
            Path.Combine(root, "host:stream")
        })
            Reject(() => Create(source, destination), "未拒绝重叠/相对/UNC/ADS 目标路径");
        Reject(() => Create("relative-source", target), "未拒绝相对源路径");

        // 规划期间已得到摘要：执行前源内容变动应 fail closed，目标保持本轮快照。
        File.WriteAllText(changed, "SYNTHETIC_PLANNED_CONTENT");
        var stale = Create(source, target);
        var targetBefore = Snapshot(target, monitored);
        File.WriteAllText(changed, "SYNTHETIC_CHANGED_AFTER_PLAN");
        Reject(() => Sync(stale), "源文件变化后仍执行旧复制计划");
        Check(Unchanged(target, targetBefore), "拒绝过期计划前改写了目标文件");
        Check(excluded.Except(retained).All(p => !File.Exists(Path.Combine(target, p))), "后续同步重新复制了排除项");
        Note("HOST_FILES excluded-source changes ignored; old/unknown cache hashes/mtime retained without cleanup");
        Note("HOST_FILES missing/self/path guards/stale plan verified; real host/process/registry/helper calls=0");
        return Task.CompletedTask;
    }

    private static object Create(string source, string destination) => Call("Create", null, source, destination)!;
    private static int Sync(object plan) => (int)Call("Synchronize", plan)!;
    private static int Count(object plan) => ((IEnumerable)Deployment
        .GetProperty("Changes", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(plan)!).Cast<object>().Count();
    private static Dictionary<string, (string Hash, DateTime Time)> Snapshot(string root, IEnumerable<string> files)
        => files.ToDictionary(p => p, p => (Digest(Path.Combine(root, p)), File.GetLastWriteTimeUtc(Path.Combine(root, p))));
    private static bool Unchanged(string root, Dictionary<string, (string Hash, DateTime Time)> before, string? except = null)
        => before.Where(p => p.Key != except).All(p => Digest(Path.Combine(root, p.Key)) == p.Value.Hash
            && File.GetLastWriteTimeUtc(Path.Combine(root, p.Key)) == p.Value.Time);

    private static object? Call(string method, object? target, params object[] arguments)
    {
        try
        {
            return Deployment.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)!
                .Invoke(target, arguments);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
    }

    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (IOException) { return; }
        throw new CheckFailure(message);
    }
}