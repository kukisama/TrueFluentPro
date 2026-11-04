using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using static Program;

/// <summary>仅调用带完整注入 backend 的生产生命周期；所有路径在 Artifacts 内，绝不执行真实 helper。</summary>
internal static class LifecycleRegression
{
    private static readonly Type Service = typeof(VoiceImeRegistrationService);
    private static readonly string[] Architectures = Environment.Is64BitOperatingSystem ? ["win-x64", "win-x86"] : ["win-x86"];
    private static Fixture? _currentFixture;
    [ThreadStatic] private static bool _observingException;

    internal static async Task VerifyAsync()
    {
        await Case("same-version-idempotent-and-partial-repair", async () =>
        {
            var f = new Fixture("idempotent");
            await InstallBaselineAsync(f, "fixture 首次安装失败");
            var journal = File.ReadAllText(f.Manifest);
            var writes = f.Writes;
            Check((await f.Instance.RegisterAsync()).Message.Contains("无需重复安装") && f.Writes == writes
                && File.ReadAllText(f.Manifest) == journal, "同版重复安装改写系统或清单");
            f.Partial = true;
            Check((await f.Instance.RegisterAsync()).Succeeded && f.Removals >= Architectures.Length
                && f.State.Count == Architectures.Length && !File.Exists(f.Gate), "partial 未先安全卸载自有残留再修复");
        });
        await Case("installed-version-comes-from-binaries-not-stale-journal", async () =>
        {
            var f = new Fixture("installed-version");
            await InstallBaselineAsync(f, "版本基线安装失败");
            var saved = JsonNode.Parse(File.ReadAllText(f.Manifest))!;
            saved["PackageVersion"] = "1.0.0.0";
            foreach (var path in new[] { f.Manifest, Path.Combine(f.UserRoot, "install-manifest.json") })
                File.WriteAllText(path, saved.ToJsonString());
            var writes = f.Writes;
            var productVersion = VoiceImeRegistrationService.GetProductVersionString();
            var imeVersion = VoiceImeRegistrationService.GetVoiceImeVersionString();
            var status = await f.Instance.RefreshStatusAsync();
            Check(status.Succeeded && status.Message.Contains("已安装组件 v" + imeVersion)
                && !status.Message.Contains("v1.0.0.0"), "旧清单版本覆盖了实际组件版本：" + status.Message);
            Check((await f.Instance.RegisterAsync()).Succeeded && f.Writes == writes
                && JsonNode.Parse(File.ReadAllText(f.Manifest))!["PackageVersion"]!.GetValue<string>() == imeVersion,
                "同包版本元数据修复未收敛，或重复登记组件");
            f.BinaryVersion = "2.2.1.0";
            status = await f.Instance.RefreshStatusAsync();
            Check(status.Succeeded && status.Message.Contains("已安装组件 v2.2.1")
                && status.Message.Contains("当前主程序 v" + productVersion), "旧组件被当前程序版本冒充");
            var rejected = await f.Instance.RegisterAsync();
            Check(!rejected.Succeeded && rejected.Message.Contains("与配置版本") && f.Writes == writes,
                "版本不一致的待安装包没有在写入前拒绝");
            f.BinaryVersion = null;
            status = await f.Instance.RefreshStatusAsync();
            Check(status.Succeeded && status.Message.Contains("已安装组件版本未知")
                && !status.Message.Contains("已安装组件 v" + imeVersion), "无版本资源的旧组件被伪报新版");
        });
        await Case("observed-reboot-queue-prefix-reserves-exact-path-only", () =>
        {
            var path = Path.Combine(Artifacts, "queued", "TrueFluentVoiceIme.dll");
            var match = Service.GetMethod("IsQueuedPath", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new CheckFailure("缺少生产静态方法：IsQueuedPath");
            foreach (var value in new[] { path, @"\??\" + path, @"*1\??\" + path })
                Check((bool)match.Invoke(null, [value, path])!, "遗漏精确队列路径：" + value);
            foreach (var value in new[] { @"*1\??\" + path + ".other", @"*2\??\" + path, "prefix" + path })
                Check(!(bool)match.Invoke(null, [value, path])!, "队列匹配越过精确路径边界：" + value);
            return Task.CompletedTask;
        });
        await Case("upgrade-failure-restores-original-host-and-entries", async () =>
        {
            var f = new Fixture("rollback");
            await InstallBaselineAsync(f, "升级基线安装失败");
            var old = f.State.ToDictionary(p => p.Key, p => p.Value);
            var oldHost = f.Host;
            f.NewSource("new-host", "v2");
            var newRoot = f.PackageRoot;
            f.ExpectedStage = newRoot;
            f.FailRegisterRoot = newRoot;
            var result = await f.Instance.RegisterAsync();
            Check(!result.Succeeded && result.Message.Contains("恢复并校验原完整版本")
                && f.Host == oldHost && old.All(p => f.State[p.Key] == p.Value), "升级失败未恢复旧 Host/双架构精确路径：" + result.Message);
            Check(!Directory.Exists(newRoot) && !File.Exists(f.Gate), "失败的新包未回收或回退后门禁未释放");
            var saved = JsonNode.Parse(File.ReadAllText(f.Manifest))!;
            Check(saved["Phase"]!.GetValue<string>() == "installed" && saved["Previous"] == null,
                "回退后清单不是原完整版本的稳定状态");
            f.FailRegisterRoot = null;
            var upgraded = await f.Instance.RegisterAsync();
            Check(upgraded.Succeeded && f.Host == Path.Combine(f.Source, "TrueFluentPro.exe")
                && f.State.Values.All(p => p.StartsWith(newRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                && old.Values.All(p => !File.Exists(p)) && File.Exists(oldHost),
                "升级没有实际切换新包/Host，或误删旧主程序：" + upgraded.Message);
        });
            await Case("host-record-drift-is-rejected-without-changing-installation", async () =>
            {
                var f = new Fixture("host-record-drift");
                await InstallBaselineAsync(f, "Host 漂移基线安装失败");
                var oldHost = f.Host;
                var journal = File.ReadAllText(f.Manifest);
                var writes = f.Writes;
                var removals = f.Removals;
                f.Host = Path.Combine(f.UserRoot, "host", "TrueFluentPro.exe");
                var status = await f.Instance.RefreshStatusAsync();
                var repair = await f.Instance.RegisterAsync();
                var uninstall = await f.Instance.UnregisterAsync();
                Check(new[] { status, repair, uninstall }.All(r => !r.Succeeded
                    && r.Message.Contains("原 Host 与本模块恢复清单不一致"))
                && f.Writes == writes && f.Removals == removals && !File.Exists(f.Gate)
                && File.ReadAllText(f.Manifest) == journal, "Host 漂移被静默认领或修改了原安装");
                f.Host = oldHost;
                Check((await f.Instance.RefreshStatusAsync()).Succeeded
                && (await f.Instance.RegisterAsync()).Succeeded && f.Writes == writes
                && f.Removals == removals, "恢复精确 Host 记录后同版维修仍无法收敛");
            });
        await Case("restored-and-same-version-cleanup-failure-keeps-confirmed-installation", async () =>
        {
            var f = new Fixture("restore-cleanup-failure");
            await InstallBaselineAsync(f, "恢复清理基线安装失败");
            var old = f.State.ToDictionary(p => p.Key, p => p.Value);
            var oldHost = f.Host;
            var writes = f.Writes;
            var removals = f.Removals;
            f.NewSource("interrupted-new", "v2");
            var newRoot = f.PackageRoot;
            f.WaitFails = true;
            Check(!(await f.Instance.RegisterAsync()).Succeeded && File.Exists(f.Gate)
                && JsonNode.Parse(File.ReadAllText(f.Manifest))!["Previous"] != null, "未留下完整旧版恢复快照");
            f.WaitFails = false;
            f.FailDeletePath = Path.Combine(newRoot, Architectures[0], "TrueFluentVoiceIme.dll");
            var result = await f.Instance.RegisterAsync();
            var saved = JsonNode.Parse(File.ReadAllText(f.Manifest))!;
            Check(!result.Succeeded && result.Message.Contains("文件回收失败（Win32=5）")
                && f.State.Count == old.Count && old.All(p => f.State.TryGetValue(p.Key, out var path) && path == p.Value)
                && f.Host == oldHost && f.Removals == removals && f.Writes == writes,
                "恢复确认后的清理失败撤掉了旧 COM 或丢失错误：" + result.Message);
            Check(saved["Phase"]!.GetValue<string>() == "recoverable" && saved["Previous"] == null
                && saved["HostPath"]!.GetValue<string>() == oldHost
                && saved["Installations"]!.AsArray().All(e => old[e!["Architecture"]!.GetValue<string>()] == e["DllPath"]!.GetValue<string>())
                && saved["Files"]!.AsArray().Any(e => e!["Path"]!.GetValue<string>() == f.FailDeletePath)
                && File.Exists(f.Gate) && File.Exists(f.FailDeletePath), "恢复后清理失败丢失 gate/原版身份/待清理 receipt");
            // 同版重试仍只收敛清理；再次失败也不能重登记或撤掉已确认的系统入口。
            f.NewSource("source", "v1");
            var sameVersion = await f.Instance.RegisterAsync();
            Check(!sameVersion.Succeeded && sameVersion.Message.Contains("文件回收失败（Win32=5）")
                && f.State.Count == old.Count && old.All(p => f.State.TryGetValue(p.Key, out var path) && path == p.Value)
                && f.Host == oldHost && f.Removals == removals && f.Writes == writes && File.Exists(f.Gate),
                "同版确认后的清理失败改动系统入口或虚报成功：" + sameVersion.Message);
            f.FailDeletePath = null;
            Check((await f.Instance.RegisterAsync()).Succeeded && !File.Exists(f.Gate) && !Directory.Exists(newRoot)
                && f.Writes == writes && f.Host == oldHost, "清理故障解除后同版重试未收敛");
        });
        await Case("compensation-failures-preserve-friendly-details-and-maintenance", async () =>
        {
            foreach (var restorePrevious in new[] { true, false })
            {
                var f = new Fixture(restorePrevious ? "restore-compensation-error" : "cleanup-compensation-error");
                if (restorePrevious) await InstallBaselineAsync(f, "补偿错误基线安装失败");
                var old = f.State.ToDictionary(p => p.Key, p => p.Value);
                var oldHost = f.Host;
                f.NewSource("failing-new", "v2");
                var newRoot = f.PackageRoot;
                f.FailRegisterRoot = newRoot;
                f.FailDeletePath = Path.Combine(newRoot, Architectures[0], "TrueFluentVoiceIme.dll");
                var result = await f.Instance.RegisterAsync();
                var saved = JsonNode.Parse(File.ReadAllText(f.Manifest))!;
                var compensation = restorePrevious ? "旧版恢复补偿未完成：" : "失败安装清理补偿未完成：";
                Check(!result.Succeeded && result.Message.Contains("安装失败（退出码 4")
                    && result.Message.Contains("RegistrationPostcheck")
                    && result.Message.Contains(compensation + "文件回收失败（Win32=5）")
                    && !result.Message.Contains("文件已回收") && !result.Message.Contains("安装/修复完成"),
                    "原始错误或具体补偿失败被吞，或虚报成功：" + result.Message);
                Check(saved["Phase"]!.GetValue<string>() == "recoverable" && File.Exists(f.Gate)
                    && saved["Files"]!.AsArray().Any(e => e!["Path"]!.GetValue<string>() == f.FailDeletePath)
                    && File.Exists(f.FailDeletePath), "补偿异常后未保留维护门禁/待清理 receipt");
                Check(f.State.Count == old.Count && old.All(p => f.State.TryGetValue(p.Key, out var path) && path == p.Value)
                    && f.Host == oldHost, "补偿清理失败改动了已恢复旧版，或未撤掉失败新登记");
            }
        });
        await Case("uninstall-failure-keeps-receipt-and-retry-converges", async () =>
        {
            var f = new Fixture("uninstall-retry");
            await InstallBaselineAsync(f, "卸载基线安装失败");
            f.FailRemove = true;
            var result = await f.Instance.UnregisterAsync();
            var journal = JsonNode.Parse(File.ReadAllText(f.Manifest))!;
            Check(!result.Succeeded && journal["Phase"]!.GetValue<string>() == "recoverable"
                && journal["Installations"]!.AsArray().Count == Architectures.Length
                && journal["Files"]!.AsArray().Count == Architectures.Length * 2 && File.Exists(f.Gate),
                "卸载失败丢失 receipt 或虚报成功：" + result.Message + "; phase=" + journal["Phase"]
                    + "; entries=" + journal["Installations"]!.AsArray().Count + "; files=" + journal["Files"]!.AsArray().Count);
            f.FailRemove = false;
            Check((await f.Instance.UnregisterAsync()).Succeeded && !File.Exists(f.Manifest)
                && !File.Exists(f.Gate) && f.State.Count == 0 && File.Exists(Path.Combine(f.Source, "TrueFluentPro.exe")),
                "卸载重试未收敛/误删主程序");
        });
        await Case("sharing-lock-pending-reboot-path-reservation-and-convergence", async () =>
        {
            var f = new Fixture("pending");
            await InstallBaselineAsync(f, "占用基线安装失败");
            var dll = f.State.Values.First();
            using (var loaded = new FileStream(dll, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var result = await f.Instance.UnregisterAsync();
                var saved = JsonNode.Parse(File.ReadAllText(f.Manifest))!;
                Check(result.Succeeded && result.CleanupPending && result.Message.Contains("系统入口已卸载")
                    && saved["Phase"]!.GetValue<string>() == "pending-reboot"
                    && saved["PendingCleanup"]!.AsArray().Count == 1 && f.Queue.Contains(dll)
                    && File.Exists(dll) && f.State.Count == 0, "共享锁没有变成持久 pending，或把排队冒充删除");
                var writes = f.Writes;
                var reinstall = await f.Instance.RegisterAsync();
                Check(reinstall.Succeeded && f.Writes > writes && f.State.Count == Architectures.Length
                    && f.State.Values.All(p => p != dll && !f.Queue.Contains(p)),
                    "同版重装没有自动避开已排队路径，或仍要求重启：" + reinstall.Message);
                var stableWrites = f.Writes;
                Check((await f.Instance.RegisterAsync()).Succeeded && f.Writes == stableWrites,
                    "自动新目录重装后同版维修又重复登记");
            }
            // 占用释放后无需等重启：正常维护立即再删，但不能遗忘仍在系统队列中的路径。
            var scheduled = f.ScheduleCalls;
            var retried = await f.Instance.UnregisterAsync();
            Check(retried.Succeeded && retried.CleanupPending && !File.Exists(dll)
                && f.ScheduleCalls == scheduled && f.Queue.Contains(dll), "已排队旧 DLL 释放后未自动删除，或重复排队");
            var reserved = JsonNode.Parse(File.ReadAllText(f.Manifest))!;
            Check(reserved["PendingCleanup"]!.AsArray().Count == 1
                && reserved["Files"]!.AsArray().Any(v => v!["Path"]!.GetValue<string>() == dll),
                "提前删除文件后丢失精确 receipt 或路径禁用记录");
            var fresh = await f.Instance.RegisterAsync();
            Check(fresh.Succeeded && f.State.Values.All(p => !f.Queue.Contains(p)),
                "队列仍在时重装没有选择安全新路径：" + fresh.Message);
            // 模拟 OS 已消费队列；不调用 MoveFileEx/修改系统队列。
            f.Queue.Clear();
            var cleanup = await f.Instance.UnregisterAsync();
            Check(cleanup.Succeeded && !File.Exists(f.Manifest)
                && !File.Exists(Path.Combine(f.UserRoot, "install-manifest.json")), "pending 后续操作未确认清理并移除恢复记录：" + cleanup.Message);
        });
        await Case("transient-sharing-release-cleans-without-reboot-queue", async () =>
        {
            var f = new Fixture("transient-sharing");
            await InstallBaselineAsync(f, "短暂占用基线失败");
            f.TransientSharing = 1;
            var result = await f.Instance.UnregisterAsync();
            Check(result.Succeeded && !result.CleanupPending && f.ScheduleCalls == 0
                && f.TransientSharing == 0 && !File.Exists(f.Manifest), "短暂共享占用未自动等待释放，或过早加入重启队列");
        });
        await Case("UI-permission-prompt-cancel-and-single-operation-resume", async () =>
        {
            var method = Service.GetMethod("ManageFromUiAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            async Task<VoiceImeRegistrationService.Result> Invoke(bool install,
                Func<bool, Task<VoiceImeRegistrationService.Result>> manage,
                Func<bool, Task<bool>> confirm, Func<bool, Task<VoiceImeRegistrationService.Result>> elevate)
                => await (Task<VoiceImeRegistrationService.Result>)method.Invoke(null, [install, manage, confirm, elevate])!;
            foreach (var install in new[] { true, false })
            {
                var operations = 0; var prompts = 0; var launches = 0;
                var denied = new VoiceImeRegistrationService.Result(false, "前置权限不足") { RequiresAdministrator = true };
                Task<VoiceImeRegistrationService.Result> Manage(bool value)
                { Check(value == install, "维护操作被改变"); operations++; return Task.FromResult(denied); }
                Task<bool> Confirm(bool value)
                { Check(value == install, "确认框操作被改变"); prompts++; return Task.FromResult(false); }
                Task<VoiceImeRegistrationService.Result> Elevate(bool value)
                { Check(value == install, "提权续办操作被改变"); launches++; return Task.FromResult(denied); }
                Check(!(await Invoke(install, Manage, Confirm, Elevate)).Succeeded
                    && operations == 1 && prompts == 1 && launches == 0, "取消提示后仍提权或重复维护");
                operations = prompts = launches = 0;
                await Invoke(install, Manage, value => { prompts++; return Task.FromResult(true); }, Elevate);
                Check(operations == 1 && prompts == 1 && launches == 1, "提权返回失败后又循环授权或重做原操作");
                operations = prompts = launches = 0;
                var result = await Invoke(install, value => { operations++; return Task.FromResult(new VoiceImeRegistrationService.Result(true, "完成")); }, Confirm, Elevate);
                Check(result.Succeeded && operations == 1 && prompts == 0 && launches == 0, "有权限的维护仍弹窗提权");
            }
        });
        await Case("sid-and-admin-denied-before-any-write", async () =>
        {
            var f = new Fixture("permission");
            f.Admin = false;
            Check((await f.Instance.RegisterAsync()).RequiresAdministrator && (await f.Instance.UnregisterAsync()).RequiresAdministrator
                && f.HelperCalls == 0 && !Directory.Exists(f.SystemRoot) && !Directory.Exists(f.UserRoot),
                "权限拒绝发生在目录/助手/清单写入之后");
            f.Admin = true;
            await InstallBaselineAsync(f, "SID 基线安装失败");
            var digest = Digest(f.Manifest);
            var calls = f.HelperCalls;
            f.Sid = "S-1-5-21-999-999-999-1002";
            Check(!(await f.Instance.UnregisterAsync()).Succeeded && !(await f.Instance.RegisterAsync()).Succeeded
                && Digest(f.Manifest) == digest && f.HelperCalls == calls, "另一 SID 接管原用户安装");
        });
        await Case("v1-placeholder-and-lost-receipt-fixed-layout-recovery", async () =>
        {
            var f = new Fixture("v1-placeholder");
            Directory.CreateDirectory(f.UserRoot);
            var directory = Path.Combine(f.SystemRoot, new string('a', 64), Architectures[0]);
            File.WriteAllText(Path.Combine(f.UserRoot, "install-manifest.json"), JsonSerializer.Serialize(new
            {
                Version = 1,
                Installations = new[] { new { Architecture = Architectures[0],
                    DllPath = Path.Combine(directory, "TrueFluentVoiceIme.dll"),
                    HelperPath = Path.Combine(directory, "VoiceImeRegister.exe") } }
            }));
            var result = await f.Instance.UnregisterAsync();
            Check(result.Succeeded && result.Message.Contains("无需卸载") && !result.Message.Contains("已卸载")
                && f.Writes == 0 && !File.Exists(f.Manifest), "失败占位被报告成真实卸载");
            var h = new Fixture("v1-without-host");
            await InstallBaselineAsync(h, "v1 残留迁移基线失败");
            var oldReceipt = JsonNode.Parse(File.ReadAllText(h.Manifest))!;
            File.Delete(h.Manifest);
            File.WriteAllText(Path.Combine(h.UserRoot, "install-manifest.json"), new JsonObject
            {
                ["Version"] = 1,
                ["Installations"] = oldReceipt["Installations"]!.DeepClone()
            }.ToJsonString());
            h.Host = "";
            h.Partial = true;
            Check((await h.Instance.UnregisterAsync()).Succeeded && h.State.Count == 0 && !File.Exists(h.Manifest),
                "当前用户 v1 精确清单及 owned COM 残留缺 Host 时不能安全迁移卸载");
            var g = new Fixture("lost-receipt");
            await InstallBaselineAsync(g, "丢清单恢复基线安装失败");
            File.Delete(g.Manifest);
            File.Delete(Path.Combine(g.UserRoot, "install-manifest.json"));
            Check((await g.Instance.UnregisterAsync()).Succeeded && g.State.Count == 0,
                "固定自有 COM 路径和完整 hash 布局未能恢复卸载");
            File.WriteAllText(g.Manifest, "{");
            var calls = g.HelperCalls;
            Check(!(await g.Instance.UnregisterAsync()).Succeeded && File.ReadAllText(g.Manifest) == "{"
                && g.HelperCalls == calls, "坏清单被忽略/删除或进入系统操作");
        });
        await Case("api-only-and-pathguard-fail-closed", async () =>
        {
            var f = new Fixture("api-only");
            f.ApiOnly = true;
            Check(!(await f.Instance.UnregisterAsync()).Succeeded && f.HelperCalls > 0
                && f.Writes == 0 && !Directory.Exists(f.SystemRoot), "API/category-only 被 C# 空 footprint 漏判");
            var g = new Fixture("pathguard");
            await InstallBaselineAsync(g, "pathguard 基线安装失败");
            var saved = JsonNode.Parse(File.ReadAllText(g.Manifest))!;
            saved["Files"]![0]!["Path"] = Path.Combine(g.Source, "TrueFluentPro.exe");
            File.WriteAllText(g.Manifest, saved.ToJsonString());
            var writes = g.Writes;
            Check(!(await g.Instance.UnregisterAsync()).Succeeded && g.Writes == writes
                && File.Exists(Path.Combine(g.Source, "TrueFluentPro.exe")), "receipt 越界被执行");
            var outside = Path.Combine(g.Root, "not-component");
            Directory.CreateDirectory(outside);
            var link = Path.Combine(g.Root, "junction");
            Directory.CreateSymbolicLink(link, outside);
            var guard = typeof(VoiceImeHost).Assembly.GetType("TrueFluentPro.Services.VoiceImeMaintenance")!
                .GetMethod("GuardPath", BindingFlags.NonPublic | BindingFlags.Static)!;
            var rejected = false;
            try { guard.Invoke(null, [Path.Combine(link, "TrueFluentVoiceIme.dll")]); }
            catch (TargetInvocationException ex) when (ex.InnerException is IOException) { rejected = true; }
            Check(rejected, "reparse 路径未拒绝");
        });
        await Case("crash-journal-reentry-recovers-complete-previous", async () =>
        {
            var f = new Fixture("crash-reentry");
            await InstallBaselineAsync(f, "崩溃重进基线失败");
            var oldHost = f.Host;
            f.NewSource("crash-new", "v3");
            f.WaitFails = true;
            var interrupted = await f.Instance.RegisterAsync();
            Check(!interrupted.Succeeded && File.Exists(f.Gate) && f.Host == oldHost
                && JsonNode.Parse(File.ReadAllText(f.Manifest))!["Previous"] != null, "quiesce 失败不应拆除旧版或丢恢复快照");
            f.WaitFails = false;
            var result = await f.Instance.RegisterAsync();
            Check(!result.Succeeded && result.Message.Contains("恢复并校验原完整版本") && !File.Exists(f.Gate)
                && f.Host == oldHost && !Directory.Exists(f.PackageRoot), "重进未恢复或把旧版仍存在当升级成功");
        });
        await Case("partial-previous-uses-current-helper-and-restores-only-once", async () =>
        {
            var f = new Fixture("partial-previous-current-helper");
            await InstallBaselineAsync(f, "恢复工具选择基线失败");
            var old = f.State.ToDictionary(p => p.Key, p => p.Value);
            var oldHost = f.Host;
            f.NewSource("new-source", "v2");
            f.WaitFails = true;
            Check(!(await f.Instance.RegisterAsync()).Succeeded && File.Exists(f.Gate), "未保留恢复快照");
            f.WaitFails = false;
            f.Partial = true;
            f.FailRegisterRoot = Path.GetDirectoryName(Path.GetDirectoryName(old[Architectures[0]])!)!;
            f.RegisteredHelpers.Clear();
            var failed = await f.Instance.RegisterAsync();
            Check(!failed.Succeeded && f.RegisteredHelpers.Count == 1
                && f.RegisteredHelpers[0].Helper == Path.Combine(f.Source, "voice-ime", Architectures[0], "VoiceImeRegister.exe")
                && f.RegisteredHelpers[0].Dll == old[Architectures[0]]
                && File.Exists(f.Gate) && JsonNode.Parse(File.ReadAllText(f.Manifest))!["Previous"] != null,
                "恢复调用了旧 helper、重复写入，或丢失原路径/gate：" + failed.Message);
            f.FailRegisterRoot = null;
            var recovered = await f.Instance.RegisterAsync();
            Check(!recovered.Succeeded && recovered.Message.Contains("恢复并校验原完整版本")
                && f.Host == oldHost && old.All(p => f.State[p.Key] == p.Value) && !File.Exists(f.Gate),
                "解除恢复故障后未确认原版本，或把恢复当升级：" + recovered.Message);
        });
        await Case("confirmed-old-image-access-denied-queues-without-blocking-new-install", async () =>
        {
            var f = new Fixture("mapped-old-image");
            await InstallBaselineAsync(f, "映像占用基线失败");
            var oldDll = f.State.Values.First();
            f.NewSource("upgraded", "v2");
            f.FailDeletePath = oldDll;
            f.MappedImage = oldDll;
            var result = await f.Instance.RegisterAsync();
            var saved = JsonNode.Parse(File.ReadAllText(f.Manifest))!;
            Check(result.Succeeded && result.CleanupPending && result.Message.Contains("自动尝试清理") && !File.Exists(f.Gate)
                && f.Queue.Contains(oldDll) && File.Exists(oldDll)
                && saved["Phase"]!.GetValue<string>() == "installed"
                && f.State.Values.All(p => p.StartsWith(f.PackageRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)),
                "已证明旧映像映射仍阻塞新安装，或把排队冒充删除：" + result.Message);
        });
        await Case("maintenance-cancels-fake-voice-and-refuses-new-run-before-config", VerifyMaintenanceAsync);
        await Case("elevation-entry-and-result-protocol-no-UAC", ElevationRegression.VerifyAsync);
        Note("LIFECYCLE all fixture paths retained under artifacts; actual helper, Windows registration, UAC, user config, microphone and cloud calls=0");
    }

    private static async Task Case(string name, Func<Task> test)
    {
        _currentFixture = null;
        var errors = new Queue<string>();
        void Observe(object? sender, FirstChanceExceptionEventArgs args)
        {
            if (_observingException || args.Exception is not (IOException or UnauthorizedAccessException)) return;
            _observingException = true;
            try
            {
                var ex = args.Exception;
                // 首次通知时异常对象可能尚无完整传播栈；在抛出线程上直接采集调用阶段。
                var stack = new System.Diagnostics.StackTrace(1, true).ToString();
                if (!stack.Contains("TrueFluentPro.Services.VoiceIme", StringComparison.Ordinal)) return;
                // 这两处明确捕获缺失并返回；不让正常路径探测挤掉真正的读写错误。
                if (ex is FileNotFoundException or DirectoryNotFoundException
                    && (stack.Contains("VoiceImeMaintenance.GuardPath", StringComparison.Ordinal)
                        || stack.Contains("VoiceImeMaintenance.Exists", StringComparison.Ordinal))) return;
                lock (errors)
                {
                    if (errors.Count == 8) errors.Dequeue();
                    errors.Enqueue(ExceptionFields(ex) + "; lastBackend=" + _currentFixture?.LastBackend + "\n" + stack);
                }
            }
            catch { } // 通知中禁止 I/O，也不能让诊断异常递归取代原始异常。
            finally { _observingException = false; }
        }
        AppDomain.CurrentDomain.FirstChanceException += Observe;
        try
        {
            await test();
            AppDomain.CurrentDomain.FirstChanceException -= Observe;
            foreach (var error in errors) Note("FILE_EXCEPTION lifecycle/" + name + " " + error);
            Note("PASS lifecycle/" + name);
        }
        catch (Exception ex)
        {
            AppDomain.CurrentDomain.FirstChanceException -= Observe;
            while (ex is TargetInvocationException { InnerException: { } inner }) ex = inner;
            var reason = ex is CheckFailure ? ex.Message : ExceptionFields(ex);
            Note("FAIL lifecycle/" + name + ": " + reason);
            foreach (var error in errors) Note("FILE_EXCEPTION " + error);
            if (_currentFixture != null) Note(_currentFixture.DescribeFiles());
            throw new CheckFailure(reason);
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= Observe;
            _currentFixture = null;
        }
    }

    private static async Task InstallBaselineAsync(Fixture fixture, string failure)
    {
        var result = await fixture.Instance.RegisterAsync();
        Check(result.Succeeded, failure + "：" + result.Message);
    }

    private static string ExceptionFields(Exception ex)
        => $"type={ex.GetType().FullName}; HRESULT=0x{ex.HResult:X8}; Win32="
            + (((uint)ex.HResult & 0xFFFF0000u) == 0x80070000u ? (ex.HResult & 0xFFFF).ToString() : "n/a");

    private static async Task VerifyMaintenanceAsync()
    {
        var directory = Path.Combine(Artifacts, "lifecycle", "host");
        Directory.CreateDirectory(directory);
        var gate = Path.Combine(directory, "maintenance.lock");
        var run = typeof(VoiceImeHost).GetMethod("RunMaintainedSessionAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var voice = new FakeVoice();
        var reads = 0;
        Func<Task<AzureSpeechConfig>> load = () => { reads++; return Task.FromResult(Configuration()); };
        Func<AzureSpeechConfig, IChatVoiceInputSession> create = _ => voice;
        using var stream = new BlockingStream();
        var active = (Task<int>)run.Invoke(null, [stream, load, create, CancellationToken.None, gate])!;
        await voice.Started.Task.WaitAsync(Deadline);
        File.WriteAllText(gate, "fixture maintenance");
        Check(await active.WaitAsync(Deadline) == 0 && voice.Token.IsCancellationRequested
            && voice.Stops == 1 && voice.Disposals == 1, "维护没有取消当前会话/Stop/Dispose");
        var before = reads;
        var next = (Task<int>)run.Invoke(null, [stream, load, create, CancellationToken.None, gate])!;
        Check(await next.WaitAsync(Deadline) == 0 && reads == before && voice.Starts == 1,
            "维护中新 run 仍读配置或创建采音源");
    }

    private sealed class Fixture
    {
        internal readonly string Root, SystemRoot, UserRoot;
        internal string Source = "", Sid = "S-1-5-21-111-222-333-1001", Host = "";
        internal string? BinaryVersion = VoiceImeRegistrationService.GetVoiceImeVersionString() + ".0";
        internal string? ExpectedStage, FailRegisterRoot, FailDeletePath, MappedImage;
        internal bool Admin = true, Partial, ApiOnly, FailRemove, WaitFails;
        internal int Writes, Removals, HelperCalls, ScheduleCalls, TransientSharing;
        internal string LastBackend = "none";
        internal readonly Dictionary<string, string> State = new(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> Queue = new(StringComparer.OrdinalIgnoreCase);
        internal readonly List<(string Helper, string Dll)> RegisteredHelpers = new();
        internal readonly VoiceImeRegistrationService Instance;
        internal string Manifest => Path.Combine(SystemRoot, "install-manifest.json");
        internal string Gate => Path.Combine(SystemRoot, "maintenance.lock");
        internal string PackageRoot => Path.Combine(SystemRoot, (string)Method(Service, "GetPackageIdentity").Invoke(Instance, null)!);

        internal Fixture(string name)
        {
            Root = Path.Combine(Artifacts, "lifecycle", name);
            SystemRoot = Path.Combine(Root, "machine");
            UserRoot = Path.Combine(Root, "user");
            _currentFixture = this;
            Instance = new VoiceImeRegistrationService();
            Set(Instance, "_installRoot", SystemRoot);
            Set(Instance, "_legacyInstallRoot", UserRoot);
            NewSource("source", "v1");
            var backendType = Service.Assembly.GetType("TrueFluentPro.Services.VoiceImeLifecycleBackend")!;
            var backend = Activator.CreateInstance(backendType, true)!;
            Set(backend, "Administrator", (Func<bool>)(() => Admin));
            Set(backend, "Sid", (Func<string>)(() => Sid));
            Set(backend, "Footprint", (Func<string, (bool, bool, string[], string)>)(architecture =>
                (State.Count > 0, false, State.TryGetValue(architecture, out var dll) ? [dll] : [], Host)));
            Set(backend, "Helper", (Func<string, string, string, string?, Task<(int, string)>>)HelperAsync);
            Set(backend, "Delete", (Func<string, int>)(path =>
            {
                LastBackend = "delete:" + Path.GetRelativePath(Root, path);
                Check(Path.GetFullPath(path).StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                    "fixture 删除越界");
                if (TransientSharing > 0) { TransientSharing--; return 32; }
                if (string.Equals(path, FailDeletePath, StringComparison.OrdinalIgnoreCase)) return 5;
                try { File.Delete(path); return 0; }
                catch (IOException ex) { return ex.HResult & 0xFFFF; }
            }));
            Set(backend, "Schedule", (Func<string, bool>)(path => { ScheduleCalls++; Queue.Add(path); return true; }));
            Set(backend, "Queued", (Func<string, bool>)(path => Queue.Contains(path)));
            Set(backend, "ImageMapped", (Func<string, bool>)(path => path == MappedImage));
            Set(backend, "FileVersion", (Func<string, string?>)(_ => BinaryVersion));
            Set(backend, "Delay", (Func<TimeSpan, Task>)(_ => Task.CompletedTask));
            Set(backend, "WaitForHost", (Func<Task>)(() => WaitFails
                ? Task.FromException(new IOException("synthetic still alive")) : Task.CompletedTask));
            Set(Instance, "_lifecycleBackend", backend);
            Check(Field(Service, "_lifecycleBackend").GetValue(Instance) == backend, "backend 注入未落地，禁止运行");
        }

        internal void NewSource(string name, string version)
        {
            Source = Path.Combine(Root, name);
            Directory.CreateDirectory(Source);
            File.WriteAllText(Path.Combine(Source, "TrueFluentPro.exe"), "SYNTHETIC_NOT_EXECUTABLE_HOST:" + version);
            foreach (var architecture in Architectures)
            {
                var directory = Path.Combine(Source, "voice-ime", architecture);
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "TrueFluentVoiceIme.dll"), "SYNTHETIC_DLL:" + architecture + version);
                File.WriteAllText(Path.Combine(directory, "VoiceImeRegister.exe"), "SYNTHETIC_HELPER:" + architecture + version);
            }
            Set(Instance, "_baseDirectory", Source);
        }

        private Task<(int, string)> HelperAsync(string helper, string dll, string operation, string? host)
        {
            LastBackend = operation + ":" + Path.GetRelativePath(Root, dll);
            HelperCalls++;
            var architecture = Path.GetFileName(Path.GetDirectoryName(dll))!;
            Check(Architectures.Contains(architecture), "fixture 架构参数异常");
            if (operation == "--status")
            {
                var state = State.TryGetValue(architecture, out var actual)
                    ? !string.Equals(actual, dll, StringComparison.OrdinalIgnoreCase) ? "different-path" : Partial ? "partial" : "registered"
                    : State.Count > 0 || ApiOnly ? "partial" : "unregistered";
                return Task.FromResult((state == "registered" ? 0 : 1,
                    JsonSerializer.Serialize(new { registered = state == "registered", state, requiresAdmin = true })));
            }
            if (operation == "--probe") return Task.FromResult((State.Count == Architectures.Length && !Partial ? 0 : 1, "probe fixture"));
            Check(File.Exists(Gate) && File.Exists(Manifest), "系统写入前缺少维护门禁或 journal");
            Writes++;
            if (operation == "--register")
            {
                RegisteredHelpers.Add((helper, dll));
                Check(File.Exists(dll) && File.Exists(host), "助手调用前 stage/Host 不完整");
                State[architecture] = dll;
                Host = host!;
                Partial = false;
                if (FailRegisterRoot != null && dll.StartsWith(FailRegisterRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult((4, "failed stage=RegistrationPostcheck HRESULT=0x80004005"));
                return Task.FromResult((0, "registered"));
            }
            Check(operation == "--unregister", "fixture 收到未授权系统操作");
            Removals++;
            if (ExpectedStage != null && State.Values.Any(p => !p.StartsWith(ExpectedStage, StringComparison.OrdinalIgnoreCase)))
                Check(Architectures.All(a => File.Exists(Path.Combine(ExpectedStage, a, "TrueFluentVoiceIme.dll"))
                    && Directory.EnumerateFiles(Path.Combine(ExpectedStage, a), "VoiceImeRegister.exe", SearchOption.AllDirectories).Count() == 1),
                    "升级拆旧前未完整 stage 两架构新包");
            if (FailRemove && architecture == Architectures[^1])
                return Task.FromResult((4, "failed stage=UnregisterProfile HRESULT=0x80004005"));
            if (State.TryGetValue(architecture, out var owned))
            {
                Check(string.Equals(owned, dll, StringComparison.OrdinalIgnoreCase), "传错实际卸载 DLL 路径");
                State.Remove(architecture);
            }
            if (State.Count == 0) { Host = ""; ApiOnly = false; }
            return Task.FromResult((0, "unregistered"));
        }

        internal string DescribeFiles()
        {
            var text = new StringBuilder($"FIXTURE root={Root}; writes={Writes}; removals={Removals}; helpers={HelperCalls}; "
                + $"state={State.Count}; queued={Queue.Count}; lastBackend={LastBackend}");
            // 只读本 fixture；不跟随 pathguard 场景的重解析点，不用 Exists 把访问错误冒充缺失。
            try
            {
                foreach (var file in Directory.EnumerateFiles(Root, "*", new EnumerationOptions
                { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }).Take(128))
                {
                    try
                    {
                        text.Append($"\nFILE {Path.GetRelativePath(Root, file)}; bytes={new FileInfo(file).Length}; attributes={File.GetAttributes(file)}");
                    }
                    catch (Exception ex) { text.Append("; " + ExceptionFields(ex)); }
                }
            }
            catch (Exception ex) { text.Append("\nINVENTORY " + ExceptionFields(ex)); }
            foreach (var directory in new[] { SystemRoot, UserRoot })
            foreach (var name in new[] { "install-manifest.json", "install-manifest.json.tmp", "maintenance.lock", "maintenance.lock.tmp" })
            {
                var path = Path.Combine(directory, name);
                text.Append("\nJOURNAL " + Path.GetRelativePath(Root, path));
                try
                {
                    _ = File.GetAttributes(path);
                    if (new FileInfo(path).Length > 256 * 1024) { text.Append("; oversized"); continue; }
                    var json = JsonNode.Parse(File.ReadAllText(path))!;
                    text.Append($"; phase={json["Phase"]}; entries={json["Installations"]?.AsArray().Count}; "
                        + $"files={json["Files"]?.AsArray().Count}; pending={json["PendingCleanup"]?.AsArray().Count}; "
                        + $"previous={json["Previous"] != null}");
                }
                catch (Exception ex) { text.Append("; " + ExceptionFields(ex)); }
            }
            return text.ToString();
        }

        private static void Set(object target, string field, object value)
        {
            Field(target.GetType(), field).SetValue(target, value);
            Check(Equals(Field(target.GetType(), field).GetValue(target), value), "fixture 字段设置失败：" + field);
        }
    }

    private sealed class FakeVoice : IChatVoiceInputSession
    {
        public event Action<string, bool>? TextRecognized { add { } remove { } }
        public event Action<string>? Failed { add { } remove { } }
        public event Action<double>? AudioLevelChanged { add { } remove { } }
        public int Starts, Stops, Disposals;
        public CancellationToken Token;
        public readonly TaskCompletionSource<bool> Started = Signal();
        public Task StartAsync(CancellationToken cancellationToken = default)
        { Starts++; Token = cancellationToken; Started.TrySetResult(true); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken = default) { Stops++; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }

    private sealed class BlockingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}