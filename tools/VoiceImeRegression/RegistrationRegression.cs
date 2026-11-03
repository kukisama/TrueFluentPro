using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using TrueFluentPro.Services;
using static Program;

internal static class RegistrationRegression
{
    private const string DllName = "TrueFluentVoiceIme.dll";
    private const string HelperName = "VoiceImeRegister.exe";
    private static readonly Type ServiceType = typeof(VoiceImeRegistrationService);

    internal static async Task VerifyAsync()
    {
        var cases = new List<CaseResult>();
        await Case("package-identity", VerifyPackageIdentityAsync);
        await Case("non-admin-register-no-writes", VerifyNonAdminAsync);
        await Case("no-helper-placeholder-fails-closed", VerifyAbsentUnregisterAsync);
        await Case("exit4-category-not-profile-enumeration", () =>
        {
            var message = Describe(4, "failed stage=RegisterCategory HRESULT=0x80004005\n" + ErrorCanary);
            Check(message.Contains("注册失败（退出码 4")
                && message.Contains("阶段 输入法类别登记（RegisterCategory）")
                && message.Contains("HRESULT=0x80004005", StringComparison.OrdinalIgnoreCase),
                "exit 4 的 RegisterCategory 阶段或 HRESULT 投影丢失");
            Check(!message.Contains("枚举") && !message.Contains("Windows profile 登记"),
                "exit 4 的 RegisterCategory 被错误归因为 profile 枚举失败：" + message);
            Note("REGISTRATION category diagnostic=" + message);
            return Task.CompletedTask;
        });
        await Case("failed-hresult-selects-own-stage-and-redacts", () =>
        {
            var output = "stage=RegisterCategory HRESULT=0x00000000\n"
                + "failed stage=Probe.EnumProfiles HRESULT=0x80004005\n"
                + ErrorCanary + " " + KeyCanary + @" C:\SYNTHETIC_PRIVATE_PATH\host.exe";
            var message = Describe(4, output);
            Check(message.Contains("阶段 Windows 输入法枚举校验（Probe.EnumProfiles）")
                && message.Contains("HRESULT=0x80004005", StringComparison.OrdinalIgnoreCase)
                && !message.Contains("0x00000000", StringComparison.OrdinalIgnoreCase)
                && !message.Contains("RegisterCategory"),
                "诊断选择了前一条成功 HRESULT/阶段，而非失败行：" + message);
            Check(!message.Contains(KeyCanary) && !message.Contains("SYNTHETIC_PRIVATE_PATH"),
                "诊断投影泄漏原始 helper 输出");
            Note("REGISTRATION failed-line diagnostic=" + message);
            return Task.CompletedTask;
        });

        var assembly = ServiceType.Assembly.Location;
        File.WriteAllText(Path.Combine(Artifacts, "registration-results.json"), JsonSerializer.Serialize(new
        {
            productionAssembly = assembly, productionAssemblySha256 = Digest(assembly),
            operatingSystem64Bit = Environment.Is64BitOperatingSystem,
            constraints = "instance fixture paths only; dummy bytes never executed; no StagePackage, real helper, system writes, UAC, user manifest/config or environment changes; footprint read only",
            cases
        }, new JsonSerializerOptions { WriteIndented = true }));
        Note($"REGISTRATION SUMMARY PASS={cases.Count(c => c.Status == "PASS")} FAIL={cases.Count(c => c.Status == "FAIL")} SKIP={cases.Count(c => c.Status == "SKIP")}");
        Check(cases.All(c => c.Status != "FAIL"), string.Join("; ",
            cases.Where(c => c.Status == "FAIL").Select(c => c.Name + ": " + c.Reason)));

        async Task Case(string name, Func<Task> test)
        {
            try
            {
                await test();
                cases.Add(new(name, "PASS", null));
                Note("PASS registration/" + name);
            }
            catch (Skip ex)
            {
                cases.Add(new(name, "SKIP", ex.Message));
                Note("SKIP registration/" + name + ": " + ex.Message);
            }
            catch (Exception ex)
            {
                while (ex is TargetInvocationException { InnerException: { } inner }) ex = inner;
                var reason = ex is CheckFailure ? ex.Message : ex.GetType().FullName;
                cases.Add(new(name, "FAIL", reason));
                Note("FAIL registration/" + name + ": " + reason);
            }
        }
    }

    private static Task VerifyPackageIdentityAsync()
    {
        if (!Environment.Is64BitOperatingSystem)
            throw new Skip("双架构包契约需要 64 位操作系统，不伪造全局架构环境");
        var fixture = new Fixture("package-identity");
        var files = new List<string>();
        foreach (var architecture in new[] { "win-x64", "win-x86" })
        {
            var directory = Path.Combine(fixture.Source, "voice-ime", architecture);
            Directory.CreateDirectory(directory);
            foreach (var name in new[] { DllName, HelperName })
            {
                var path = Path.Combine(directory, name);
                File.WriteAllBytes(path, Encoding.UTF8.GetBytes("SYNTHETIC_NOT_EXECUTABLE:" + architecture + ":" + name));
                files.Add(path);
            }
        }
        var service = fixture.CreateService();
        var identityMethod = Method(ServiceType, "GetPackageIdentity");
        string Identity() => (string)identityMethod.Invoke(service, null)!;
        var baseline = Identity();
        Check(baseline.Length == 64 && baseline.All(Uri.IsHexDigit), "包身份不是 64 位十六进制 SHA-256");
        Check(Identity() == baseline, "相同双架构文件的包身份不稳定");
        Check(Digest(files[0]) != Digest(files[2]), "fixture 双架构 DLL 必须具有不同摘要");
        foreach (var path in files)
        {
            var original = File.ReadAllBytes(path);
            try
            {
                File.WriteAllBytes(path, original.Concat(new byte[] { 0x7F }).ToArray());
                Check(Identity() != baseline, "包身份未包含组件：" + Path.GetRelativePath(fixture.Source, path));
            }
            finally { File.WriteAllBytes(path, original); }
            Check(Identity() == baseline, "恢复组件后包身份未恢复");
            Note("REGISTRATION package includes=" + Path.GetRelativePath(fixture.Source, path));
        }
        // 另一架构缺 DLL 或 helper 都必须拒绝，不能静默得到单架构身份。
        foreach (var path in files.Where(p => Path.GetFileName(Path.GetDirectoryName(p)) == "win-x86"))
        {
            var absent = path + ".absent";
            File.Move(path, absent);
            var rejected = false;
            try { Identity(); }
            catch (TargetInvocationException ex) when (ex.InnerException?.GetType().Name == "RegistrationException")
            {
                rejected = ex.InnerException.Message.Contains("win-x86") && ex.InnerException.Message.Contains("组件未构建");
            }
            finally { File.Move(absent, path); }
            Check(rejected, "另一架构组件缺失时没有明确拒绝：" + Path.GetFileName(path));
        }
        fixture.CheckInstallRootsAbsent();
        Note("REGISTRATION package identity=" + baseline + "; stable, four-file sensitivity, missing-x86 DLL/helper rejected; StagePackage not called");
        return Task.CompletedTask;
    }

    private static async Task VerifyNonAdminAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new Skip("管理员令牌用例仅适用于 Windows");
        using var identity = WindowsIdentity.GetCurrent();
        var administrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        Note("REGISTRATION current-process administrator=" + administrator);
        if (administrator) throw new Skip("当前进程已提权，绝不调用 RegisterAsync");
        var fixture = new Fixture("non-admin");
        var service = fixture.CreateService();
        fixture.CheckInstallRootsAbsent();
        // Source 故意为空：即使管理员前置门禁回归，也不能进入实际登记流程。
        var result = await service.RegisterAsync().WaitAsync(Deadline);
        fixture.CheckInstallRootsAbsent();
        Check(!Directory.EnumerateFileSystemEntries(fixture.Source).Any(), "注册拒绝分支写入了 fixture source");
        Check(!result.Succeeded && result.Message.Contains("管理员身份运行") && result.Message.Contains("安装/修复"),
            "非管理员未得到明确的管理员要求：" + result.Message);
        Note("REGISTRATION non-admin denied before directory/manifest/lock creation: " + result.Message);
    }

    private static async Task VerifyAbsentUnregisterAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new Skip("固定组件 footprint 检查仅适用于 Windows");
        if (HasFootprint()) throw new Skip("当前确有本组件登记，未创建清单、未调用 UnregisterAsync");
        var fixture = new Fixture("absent-placeholder");
        var service = fixture.CreateService();
        Directory.CreateDirectory(fixture.LegacyRoot);
        var architecture = Environment.Is64BitOperatingSystem ? "win-x64" : "win-x86";
        var directory = Path.Combine(fixture.InstallRoot, new string('a', 64), architecture);
        var manifestPath = Path.Combine(fixture.LegacyRoot, "install-manifest.json");
        // 格式属于该实例的 owned 路径，文件全不存在，仅表示失败安装的恢复占位。
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(new
        {
            Version = 1,
            Installations = new[] { new { Architecture = architecture,
                DllPath = Path.Combine(directory, DllName),
                HelperPath = Path.Combine(directory, new string('b', 64), HelperName) } }
        }));
        File.Copy(manifestPath, Path.Combine(fixture.Root, "manifest-before.json"));
        Check(!Directory.EnumerateFiles(fixture.Root, "*.exe", SearchOption.AllDirectories).Any(),
            "无登记清理 fixture 不得有可执行助手");
        // 紧邻调用再检查一次；所有路径仍指向无助手的 fixture，不能回退真实包或用户清单。
        if (HasFootprint()) throw new Skip("调用前本组件登记已变化，未调用 UnregisterAsync");
        var result = await service.UnregisterAsync().WaitAsync(Deadline);
        // 空注册表不能排除 API-only 残留；无原生助手时保持未知，不删除恢复证据。
        Check(!result.Succeeded && result.Message.Contains("注册助手缺失") && !result.Message.Contains("已卸载"),
            "无助手时把未知状态当成已卸载：" + result.Message);
        using var saved = JsonDocument.Parse(File.ReadAllText(manifestPath));
        Check(saved.RootElement.GetProperty("Version").GetInt32() == 1
            && saved.RootElement.GetProperty("Installations").GetArrayLength() == 1
            && Digest(manifestPath) == Digest(Path.Combine(fixture.Root, "manifest-before.json")),
            "无助手时改变了 fixture 恢复占位记录");
        var files = Directory.EnumerateFileSystemEntries(fixture.LegacyRoot).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        Check(files.SequenceEqual(new[] { "install-manifest.json" }), "无助手拒绝分支创建了锁或额外内容");
        Check(!Directory.Exists(fixture.InstallRoot) && !Directory.EnumerateFileSystemEntries(fixture.Source).Any(),
            "无登记分支创建了安装包或写入 source");
        Check(!HasFootprint(), "操作后 footprint 已变化，不能报告无登记清理通过");
        Note("REGISTRATION no global footprint; missing helper leaves manifest unchanged and state unknown: " + result.Message);
    }

    private static bool HasFootprint()
        => (bool)StaticMethod("HasAnyRegistrationFootprint").Invoke(null, null)!;

    private static string Describe(int exit, string output)
    {
        var resultType = ServiceType.GetNestedType("HelperResult", BindingFlags.NonPublic)
            ?? throw new CheckFailure("缺少生产 HelperResult");
        var constructor = resultType.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new[] { typeof(int), typeof(string) }, null) ?? throw new CheckFailure("缺少 HelperResult 构造函数");
        var result = constructor.Invoke(new object[] { exit, output });
        var message = (string)StaticMethod("DescribeHelperFailure").Invoke(null, new[] { result, "注册" })!;
        Check(!message.Contains(ErrorCanary), "诊断投影泄漏原始 helper 输出");
        return message;
    }

    private static MethodInfo StaticMethod(string name)
        => ServiceType.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new CheckFailure("缺少生产静态方法：" + name);

    private sealed class Fixture
    {
        internal readonly string Root, Source, InstallRoot, LegacyRoot;

        internal Fixture(string name)
        {
            Root = Path.Combine(Artifacts, "registration", name);
            Source = Path.Combine(Root, "source");
            InstallRoot = Path.Combine(Root, "machine-install");
            LegacyRoot = Path.Combine(Root, "legacy-install");
            Directory.CreateDirectory(Source);
            Note("REGISTRATION fixture=" + Root);
        }

        internal VoiceImeRegistrationService CreateService()
        {
            var service = new VoiceImeRegistrationService();
            Set("_baseDirectory", Source);
            Set("_installRoot", InstallRoot);
            Set("_legacyInstallRoot", LegacyRoot);
            return service;

            void Set(string name, string value)
            {
                var field = Field(ServiceType, name);
                field.SetValue(service, value);
                Check((string?)field.GetValue(service) == value, "实例 fixture 路径重定向未生效：" + name);
            }
        }

        internal void CheckInstallRootsAbsent()
            => Check(!Directory.Exists(InstallRoot) && !Directory.Exists(LegacyRoot)
                && !File.Exists(InstallRoot) && !File.Exists(LegacyRoot), "只读/拒绝分支创建了 fixture 安装目录或文件");
    }

    private sealed class Skip(string message) : Exception(message);
    private sealed record CaseResult(string Name, string Status, string? Reason);
}