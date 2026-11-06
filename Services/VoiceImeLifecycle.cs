using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace TrueFluentPro.Services;

// 小范围 fixture seam：不替换文件系统，测试只在自己的目录使用真实文件；所有系统操作必须注入。
internal sealed class VoiceImeLifecycleBackend
{
    internal Func<bool> Administrator = null!;
    internal Func<string> Sid = null!;
    internal Func<string, (bool Present, bool Foreign, string[] Dlls, string Host)> Footprint = null!;
    internal Func<string, string, string, string?, Task<(int ExitCode, string Output)>> Helper = null!;
    internal Func<string, int> Delete = null!;
    internal Func<string, bool> Schedule = null!;
    internal Func<string, bool> Queued = null!;
    internal Func<string, bool>? ImageMapped;
    internal Func<string, string?>? FileVersion = null;
    internal Func<TimeSpan, Task>? Delay = null;
    internal Func<Task> WaitForHost = null!;
}

public sealed partial class VoiceImeRegistrationService
{
    private VoiceImeLifecycleBackend? _lifecycleBackend = null;
    private string MachineManifestPath => Path.Combine(_installRoot, "install-manifest.json");
    private string MaintenancePath => Path.Combine(_installRoot, "maintenance.lock");
    private bool LifecycleAdministrator() => _lifecycleBackend?.Administrator() ?? IsAdministrator();
    private string CurrentSid()
    {
        if (_lifecycleBackend != null) return _lifecycleBackend.Sid();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? throw new RegistrationException("无法确认当前用户 SID，未修改安装。");
    }

    private RegistrationFootprint LifecycleFootprint(string architecture)
    {
        if (_lifecycleBackend == null) return ReadFootprint(architecture);
        var data = _lifecycleBackend.Footprint(architecture);
        var result = new RegistrationFootprint { HasMachine = data.Present, HasCom = data.Dlls.Length > 0,
            HasForeignOwner = data.Foreign };
        result.DllPaths.AddRange(data.Dlls);
        if (data.Host.Length > 0) result.HostPaths.Add(data.Host);
        return result;
    }

    private async Task<Result> ManageLifecycleAsync(bool install)
    {
        // RunAsync 的管理员门禁先于这里；以下预检只读，任何一项失败均不能部署文件。
        var manifest = ReadLifecycleManifest();
        var plan = install ? PlanPackage() : null;
        var before = await InspectLifecycleAsync(manifest).ConfigureAwait(false);
        ClaimExistingFiles(manifest, before.Entries);
        ClaimOrphanPackages(manifest);
        ValidateReceipts(manifest);
        // 已不存在且系统队列也没有该路径才可释放路径保留；没重启就手动移走文件不能绕过队列。
        manifest.PendingCleanup.RemoveAll(p => !VoiceImeMaintenance.Exists(p.Path) && !IsDeletionQueued(p.Path));
        if (plan != null) CheckPlanDestinations(plan, manifest);
        ValidateMaintenanceGate(manifest);
        if (plan != null && manifest.PendingCleanup.Any(p => plan.Files.Any(f => SamePath(f.Path, p.Path))))
            return new Result(false, "此包路径已进入重启删除队列，禁止复用；请先重启 Windows，再安装/修复输入法。");
        if (install && before.Complete && manifest.Phase == "installed" && manifest.Previous == null
            && manifest.Package == plan!.Package && SamePath(manifest.HostPath, plan.HostPath)
            && manifest.PackageVersion == plan.PackageVersion
            && manifest.HostHash == plan.HostHash && manifest.PendingCleanup.Count == 0
            && before.Entries.All(e => plan.Entries.Any(p => SamePath(e.DllPath, p.DllPath) && SamePath(e.HelperPath, p.HelperPath)))
            && manifest.Files.Count == plan.Files.Count
            && manifest.Files.All(f => !f.Staging && File.Exists(f.Path) && HashFile(f.Path) == f.Hash)
            && !File.Exists(MaintenancePath))
            return new Result(true, "同版输入法已完整安装，Host 路径和双架构校验一致，无需重复安装。");

        VoiceImeMaintenance.GuardPath(_installRoot);
        VoiceImeMaintenance.GuardPath(_legacyInstallRoot);
        VoiceImeMaintenance.GuardPath(Path.Combine(_installRoot, "lifecycle.lock"));
        Directory.CreateDirectory(_installRoot);
        using var machineLock = new FileStream(Path.Combine(_installRoot, "lifecycle.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        using var userLock = AcquireInstallLock();
        // 跨进程锁下重读；机器 journal 先写，用户副本仅作 v1 兼容/恢复。
        manifest = ReadLifecycleManifest();
        before = await InspectLifecycleAsync(manifest).ConfigureAwait(false);
        var recovering = manifest.Previous != null;
        ClaimExistingFiles(manifest, before.Entries);
        ClaimOrphanPackages(manifest);
        ValidateReceipts(manifest);
        manifest.PendingCleanup.RemoveAll(p => !VoiceImeMaintenance.Exists(p.Path) && !IsDeletionQueued(p.Path));
        if (plan != null) CheckPlanDestinations(plan, manifest);
        ValidateMaintenanceGate(manifest);
        var sameInstallation = install && !recovering && before.Complete && manifest.Package == plan!.Package
            && SamePath(manifest.HostPath, plan.HostPath) && manifest.HostHash == plan.HostHash
            && before.Entries.All(e => plan.Entries.Any(p => SamePath(e.DllPath, p.DllPath) && SamePath(e.HelperPath, p.HelperPath)));
        manifest.OwnerSid = CurrentSid();
        manifest.Installations = before.Entries;
        if (string.IsNullOrEmpty(manifest.TransactionId)) manifest.TransactionId = Guid.NewGuid().ToString("N");
        if (manifest.Phase is "legacy" or "installed" or "removed" or "pending-reboot")
            manifest.Phase = install ? "staging" : "quiescing";
        WriteLifecycleManifest(manifest);
        CreateMaintenanceGate(manifest);
        FileStream? microphone = null;
        var quiesced = false;
        var waitAttempted = false;
        var committed = false;
        var restoreAttempted = false;
        async Task QuiesceAsync()
        {
            waitAttempted = true;
            if (_lifecycleBackend != null) await _lifecycleBackend.WaitForHost().ConfigureAwait(false);
            else microphone = await VoiceImeMaintenance.WaitForHostAsync(_legacyInstallRoot).ConfigureAwait(false);
            quiesced = true;
        }
        try
        {
            if (sameInstallation)
            {
                await QuiesceAsync().ConfigureAwait(false);
                await ConfirmInstalledAsync(before.Entries, before.Host).ConfigureAwait(false);
                committed = true; // 同版也以完整确认作为提交边界，后续仅收敛文件清理。
                await CleanupFilesAsync(manifest, before.Entries).ConfigureAwait(false);
                manifest.PackageVersion = plan!.PackageVersion;
                manifest.Phase = "installed";
                WriteLifecycleManifest(manifest);
                RemoveMaintenanceGate(manifest);
                // Host 部署统一留给原权限 UI 的 RefreshStandaloneHostAsync，事务/worker 不重复部署。
                return new Result(true, "同版输入法已完整安装，无需重复安装。" +
                    CleanupSummary(manifest)) { CleanupPending = manifest.PendingCleanup.Count > 0 };
            }
            if (recovering && install && manifest.Previous is { Complete: true })
            {
                await QuiesceAsync().ConfigureAwait(false);
                restoreAttempted = true;
                return await RestorePreviousAsync(manifest, () => committed = true).ConfigureAwait(false);
            }
            // 新包先 stage，旧版此时仍保持完整；每个目标和临时文件先有 receipt 再复制。
            if (plan != null)
            {
                manifest.Previous ??= new PreviousPackage { Package = string.IsNullOrEmpty(manifest.Package) && before.Entries.Count > 0
                        ? Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(before.Entries[0].DllPath)!))! : manifest.Package,
                    PackageVersion = manifest.PackageVersion,
                    HostPath = before.Host, HostHash = File.Exists(before.Host) ? HashFile(before.Host) : "",
                    Entries = before.Entries, Complete = before.Complete };
                foreach (var file in plan.Files)
                {
                    AddFile(manifest, file);
                    AddFile(manifest, new OwnedFile { Path = file.Path + ".stage", Hash = file.Hash, Staging = true });
                }
                WriteLifecycleManifest(manifest);
                foreach (var file in plan.Files)
                    CopyStaged(plan.Sources[file.Path], file);
                manifest.Files.RemoveAll(f => f.Staging && !VoiceImeMaintenance.Exists(f.Path));
                WriteLifecycleManifest(manifest);
            }
            manifest.Phase = install ? "quiescing" : "uninstalling";
            WriteLifecycleManifest(manifest);
            await QuiesceAsync().ConfigureAwait(false);

            var removal = before.Entries.Concat(manifest.Installations).DistinctBy(e => e.DllPath,
                StringComparer.OrdinalIgnoreCase).ToList();
            await RemoveEntriesAsync(removal).ConfigureAwait(false);
            await ConfirmAbsentAsync(removal.Count > 0 ? removal : Architectures.Select(GetPackageEntry).ToList()).ConfigureAwait(false);
            if (!install)
            {
                manifest.Phase = "cleaning";
                WriteLifecycleManifest(manifest);
                // 只有原生全量状态及固定 footprint 都为空，才允许清理 receipt 对应文件。
                await CleanupFilesAsync(manifest, Array.Empty<InstallEntry>()).ConfigureAwait(false);
                if (manifest.PendingCleanup.Count == 0) manifest.Installations.Clear();
                manifest.Previous = null;
                manifest.Phase = manifest.PendingCleanup.Count == 0 ? "removed" : "pending-reboot";
                WriteLifecycleManifest(manifest);
                RemoveMaintenanceGate(manifest);
                if (manifest.PendingCleanup.Count != 0)
                    return new Result(true, "输入法系统入口已卸载，已停止后台，不再作为输入法启用。" + CleanupSummary(manifest))
                        { CleanupPending = true };
                DeleteCompletedManifest();
                return new Result(true, before.Absent
                    ? "当前未安装，无需卸载；失败占位及已认领缓存已清理。"
                    : "语音输入法已卸载：双架构、共享 profile/category/COM/Host 及自有包文件已确认移除；主程序、配置和日志保留。");
            }

            manifest.Phase = "installing";
            manifest.Installations = plan!.Entries;
            manifest.Package = plan.Package;
            manifest.PackageVersion = plan.PackageVersion;
            manifest.HostPath = plan.HostPath;
            manifest.HostHash = plan.HostHash;
            WriteLifecycleManifest(manifest);
            if (HashFile(plan.HostPath) != plan.HostHash)
                throw new RegistrationException("预检后的 Host 文件已变化，未登记新包，将尝试恢复旧版。");
            foreach (var entry in plan.Entries)
            {
                var result = await RunHelperAsync(entry, "--register", plan.HostPath).ConfigureAwait(false);
                if (result.ExitCode != 0) throw new RegistrationException(DescribeHelperFailure(result, "安装"));
            }
            await ConfirmInstalledAsync(plan.Entries, plan.HostPath).ConfigureAwait(false);
            committed = true; // 从此仅收敛文件清理，不因旧缓存删除失败撤掉已校验的新安装。
            manifest.Previous = null;
            manifest.Phase = "cleaning";
            WriteLifecycleManifest(manifest);
            await CleanupFilesAsync(manifest, plan.Entries).ConfigureAwait(false);
            manifest.Phase = "installed";
            WriteLifecycleManifest(manifest);
            RemoveMaintenanceGate(manifest);
            // 与同版维修一致；UAC worker 也只完成安装事务，不启动高权限 Host。
            return new Result(true, "输入法安装/修复完成（当前用户 SID；未更改默认输入法）。" +
                CleanupSummary(manifest)) { CleanupPending = manifest.PendingCleanup.Count > 0 };
        }
        catch (Exception ex)
        {
            var message = FriendlyMessage(ex);
            // 未确认停止的 helper 绝不能与补偿助手并行；保留 gate/journal 供下次明确维修。
            if (install && !waitAttempted)
            {
                try { await QuiesceAsync().ConfigureAwait(false); }
                catch (Exception waitError) { message += " 等待后台停止失败：" + FriendlyMessage(waitError); }
            }
            if (install && !restoreAttempted && !committed && quiesced && ex is not HelperException { MayStillBeRunning: true }
                && manifest.Previous is { Complete: true })
            {
                try
                {
                    var restored = await RestorePreviousAsync(manifest, () => committed = true).ConfigureAwait(false);
                    return restored with { Message = message + " " + restored.Message };
                }
                catch (Exception restoreError) { message += " 旧版恢复补偿未完成：" + FriendlyMessage(restoreError); }
            }
            else if (install && !restoreAttempted && !sameInstallation && !committed && quiesced
                && ex is not HelperException { MayStillBeRunning: true })
            {
                try
                {
                    var actual = await InspectLifecycleAsync(manifest).ConfigureAwait(false);
                    await RemoveEntriesAsync(actual.Entries).ConfigureAwait(false);
                    await ConfirmAbsentAsync(plan!.Entries).ConfigureAwait(false);
                    await CleanupFilesAsync(manifest, Array.Empty<InstallEntry>()).ConfigureAwait(false);
                    if (manifest.PendingCleanup.Count == 0) manifest.Installations.Clear();
                    manifest.Previous = null;
                    manifest.Phase = manifest.PendingCleanup.Count > 0 ? "pending-reboot" : "removed";
                    WriteLifecycleManifest(manifest);
                    RemoveMaintenanceGate(manifest);
                    return new Result(false, message + " 本次失败安装的系统入口已撤回，文件已回收或记录为待重启清理；未报告安装成功。");
                }
                catch (Exception cleanupError) { message += " 失败安装清理补偿未完成：" + FriendlyMessage(cleanupError); }
            }
            manifest.Phase = "recoverable";
            WriteLifecycleManifest(manifest);
            return new Result(false, message + " 操作未完成，已保留维修阶段、维护门禁和精确恢复记录；使用同一用户再次安装/修复或卸载可继续。旧版未被当成升级成功。");
        }
        finally { microphone?.Dispose(); }
    }

    private async Task<Result> RestorePreviousAsync(InstallManifest manifest, Action? confirmed = null)
    {
        var previous = manifest.Previous ?? throw new RegistrationException("没有可回退的完整安装。");
        if (!previous.Complete || string.IsNullOrEmpty(previous.HostPath) || HashFile(previous.HostPath) != previous.HostHash)
            throw new RegistrationException("旧 Host 不再与回退记录一致，保留维修状态。");
        manifest.Phase = "rolling-back";
        WriteLifecycleManifest(manifest);
        ValidateReceipts(manifest);
        var oldReady = true;
        foreach (var entry in previous.Entries)
            oldReady &= (await QueryStatusAsync(entry).ConfigureAwait(false)).State == RegistrationState.Registered;
        if (!oldReady)
        {
            // 实际 COM 优先，且每次移除均核对精确路径；不能用新路径卸载旧路径。
            var actual = await InspectLifecycleAsync(manifest).ConfigureAwait(false);
            // 仅残留原包时，原地补齐其禁用/缺失架构，避免“恢复前再次解绑”卡住。
            // 不同包路径或 Host 仍先按精确实际路径清理；原生助手继续检查所有权。
            var originalOnly = actual.Entries.All(e => previous.Entries.Any(p =>
                p.Architecture == e.Architecture && SamePath(p.DllPath, e.DllPath)))
                && (string.IsNullOrEmpty(actual.Host) || SamePath(actual.Host, previous.HostPath));
            if (!originalOnly)
            {
                await RemoveEntriesAsync(actual.Entries).ConfigureAwait(false);
                await ConfirmAbsentAsync(previous.Entries).ConfigureAwait(false);
            }
            foreach (var entry in previous.Entries)
            {
                RequireReceiptFile(manifest, entry.DllPath);
                RequireReceiptFile(manifest, entry.HelperPath);
                // 固定 GUID/CLI/所有权契约保持兼容；恢复原 DLL/Host，不执行旧助手的历史偏好写入。
                var result = await RunHelperAsync(entry, "--register", previous.HostPath, useCurrentHelper: true).ConfigureAwait(false);
                if (result.ExitCode != 0) throw new RegistrationException(DescribeHelperFailure(result, "回退"));
            }
        }
        await ConfirmInstalledAsync(previous.Entries, previous.HostPath).ConfigureAwait(false);
        confirmed?.Invoke(); // 旧版已真实恢复；清理/写清单失败也不能再撤掉已确认的 COM。
        manifest.Installations = previous.Entries;
        manifest.Package = previous.Package;
        manifest.PackageVersion = previous.PackageVersion;
        manifest.HostPath = previous.HostPath;
        manifest.HostHash = previous.HostHash;
        manifest.Previous = null;
        await CleanupFilesAsync(manifest, manifest.Installations).ConfigureAwait(false);
        manifest.Phase = "installed";
        WriteLifecycleManifest(manifest);
        RemoveMaintenanceGate(manifest);
        return new Result(false, "本次安装/升级未完成，已恢复并校验原完整版本及原 Host；可再次安装/修复。" +
            (manifest.PendingCleanup.Count > 0 ? "失败新包文件待重启清理。" : "失败新包文件已回收。"));
    }

    private async Task<(List<InstallEntry> Entries, string Host, bool Complete, bool Absent)> InspectLifecycleAsync(InstallManifest manifest)
    {
        var entries = new List<InstallEntry>();
        var hosts = new List<string>();
        var complete = true;
        var absent = true;
        var saved = new InstallManifest { Installations = manifest.Installations
            .Concat(manifest.Previous?.Entries ?? new()).DistinctBy(e => e.DllPath, StringComparer.OrdinalIgnoreCase).ToList() };
        foreach (var architecture in Architectures)
        {
            var footprint = LifecycleFootprint(architecture);
            if (footprint.HasForeignOwner || footprint.DllPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                throw new RegistrationException("固定组件存在其他所有者或冲突路径，未修改系统及文件。");
            hosts.AddRange(footprint.HostPaths);
            var actual = FindActualEntry(architecture, saved, footprint);
            if (footprint.HasCom && actual == null)
                throw new RegistrationException("固定 COM 不在可验证的自有组件布局内，未猜测卸载路径。");
            var entry = actual ?? saved.Installations.FirstOrDefault(e => e.Architecture == architecture)
                ?? GetPackageEntry(architecture);
            // 即使 C# footprint 为空仍必须执行 --status，覆盖 API-only/category-only。
            var status = await QueryStatusAsync(entry).ConfigureAwait(false);
            if (status.State == RegistrationState.DifferentPath)
                throw new RegistrationException("原生精确路径/共享 Host 归属冲突，预检终止；未部署或覆盖组件。");
            absent &= status.State == RegistrationState.Unregistered && !footprint.HasFootprint;
            complete &= actual != null && status.State == RegistrationState.Registered;
            if (actual != null || saved.Installations.Contains(entry)) entries.Add(entry);
            if (status.State != RegistrationState.Unregistered && actual == null
                && !saved.Installations.Contains(entry))
                throw new RegistrationException("原生检测到 API/profile/category 残留，但无精确自有 COM 路径或清单；状态不是空，拒绝猜测认领。");
        }
        var distinctHosts = hosts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (distinctHosts.Count > 1) throw new RegistrationException("双视图 Host 路径冲突，未修改安装。");
        var host = distinctHosts.FirstOrDefault() ?? manifest.HostPath;
        if (!string.IsNullOrEmpty(host))
        {
            VoiceImeMaintenance.GuardPath(host);
            if (!Path.IsPathFullyQualified(host) || Path.GetFileName(host) != "TrueFluentPro.exe")
                throw new RegistrationException("原 Host 路径身份异常。");
            if (!string.IsNullOrEmpty(manifest.HostPath) && !SamePath(host, manifest.HostPath)
                && (manifest.Previous == null || !SamePath(host, manifest.Previous.HostPath)))
                throw new RegistrationException("原 Host 与本模块恢复清单不一致。");
        }
        complete &= entries.Count == Architectures.Length && !string.IsNullOrEmpty(host) && File.Exists(host);
        if (string.IsNullOrEmpty(manifest.OwnerSid) && entries.Count > 0 && !absent && string.IsNullOrEmpty(host)
            && !CanMigrateLegacyReceipt(manifest, entries))
            throw new RegistrationException("只有机器 COM 路径而缺少当前用户的自有 Host/SID 记录，无法确认同用户范围，未接管安装。");
        return (entries, host, complete, absent);
    }

    private bool CanMigrateLegacyReceipt(InstallManifest manifest, List<InstallEntry> actual)
    {
        // v1 原型失败时会撤走 Host，只剩本用户清单及精确 owned COM。
        // 仅允许当前用户旧清单迁移；没有清单、已有机器级 SID journal 或路径不一致均不认领。
        return manifest.Phase == "legacy" && string.IsNullOrEmpty(manifest.OwnerSid)
            && manifest.Previous == null && !File.Exists(MachineManifestPath) && File.Exists(ManifestPath)
            && manifest.Installations.Count > 0 && actual.Count > 0
            && actual.All(e => manifest.Installations.Any(saved => saved.Architecture == e.Architecture
                && SamePath(saved.DllPath, e.DllPath) && SamePath(saved.HelperPath, e.HelperPath)))
            && Architectures.Any(a => LifecycleFootprint(a).HasCom);
    }

    private async Task RemoveEntriesAsync(List<InstallEntry> entries)
    {
        foreach (var entry in entries)
        {
            var footprint = LifecycleFootprint(entry.Architecture);
            if (footprint.DllPaths.Count > 0 && !footprint.DllPaths.All(p => SamePath(p, entry.DllPath))) continue;
            var state = await QueryStatusAsync(entry).ConfigureAwait(false);
            if (state.State == RegistrationState.Unregistered && !footprint.HasFootprint) continue;
            if (state.State == RegistrationState.DifferentPath)
                throw new RegistrationException("卸载前实际路径已变化，保留恢复记录。");
            await RemoveRegistrationAsync(entry).ConfigureAwait(false);
        }
    }

    private async Task ConfirmAbsentAsync(List<InstallEntry> entries)
    {
        foreach (var architecture in Architectures)
        {
            var entry = entries.FirstOrDefault(e => e.Architecture == architecture) ?? GetPackageEntry(architecture);
            if ((await QueryStatusAsync(entry).ConfigureAwait(false)).State != RegistrationState.Unregistered
                || LifecycleFootprint(architecture).HasFootprint)
                throw new RegistrationException("卸载后仍有原生 profile/category/COM/Host 或固定 footprint，文件和恢复记录保留。");
        }
    }

    private async Task ConfirmInstalledAsync(List<InstallEntry> entries, string host)
    {
        foreach (var entry in entries)
        {
            var footprint = LifecycleFootprint(entry.Architecture);
            if (footprint.HasForeignOwner || footprint.DllPaths.Count == 0 || footprint.HostPaths.Count == 0
                || footprint.DllPaths.Any(p => !SamePath(p, entry.DllPath)) || footprint.HostPaths.Any(p => !SamePath(p, host)))
                throw new RegistrationException("安装后的实际 COM/Host 与预检身份不一致。");
            if ((await QueryStatusAsync(entry).ConfigureAwait(false)).State != RegistrationState.Registered)
                throw new RegistrationException("双架构安装后原生全量校验未通过。");
            var probe = await RunHelperAsync(entry, "--probe").ConfigureAwait(false);
            if (probe.ExitCode != 0) throw new RegistrationException("Windows 输入法枚举/启用/键盘类别探测未通过。");
        }
    }

    public static string GetProductVersionString() => AppVersionInfo.AppVersionString;

    public static string GetVoiceImeVersionString() => AppVersionInfo.VoiceImeVersionString;

    private PackagePlan PlanPackage()
    {
        var host = VoiceImeUiProtocol.SourceExecutable(Path.Combine(_baseDirectory, "TrueFluentPro.exe"));
        VoiceImeMaintenance.GuardPath(host);
        if (!File.Exists(host)) throw new RegistrationException("缺少主程序 TrueFluentPro.exe，无法安装/修复输入法。");
        var version = ReadPackageVersion(Architectures.Select(GetPackageEntry))
            ?? throw new RegistrationException("输入法组件缺少版本信息或双架构版本不一致，请重新构建完整组件后再安装。");
        var expectedImeVersion = GetVoiceImeVersionString();
        if (version != expectedImeVersion)
            throw new RegistrationException($"待安装输入法版本 v{version} 与配置版本 v{expectedImeVersion}（主程序 v{GetProductVersionString()}）不一致，请使用同版本完整包。");
        var plan = new PackagePlan { Package = GetPackageIdentity(), HostPath = host, HostHash = HashFile(host),
            PackageVersion = version };
        // 包内容身份与部署目录分开：旧目录若已排队删除，同版重装也改用新目录，
        // 不取消系统队列、不覆盖已加载 DLL，更不要求用户为了重装先重启。
        var journal = ReadLifecycleManifest();
        var deployment = plan.Package;
        if (journal.Package == plan.Package && journal.Installations.Count == Architectures.Length
            && journal.Phase is "installed" or "recoverable" && journal.Previous == null)
        {
            var roots = journal.Installations.Select(e => Path.GetDirectoryName(Path.GetDirectoryName(e.DllPath)!)!)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (roots.Count == 1 && SamePath(Path.GetDirectoryName(roots[0])!, _installRoot))
                deployment = Path.GetFileName(roots[0]);
        }
        if (journal.PendingCleanup.Any(p => p.Path.StartsWith(
                Path.Combine(_installRoot, deployment) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            || Architectures.Any(a =>
        {
            var path = Path.Combine(_installRoot, deployment, a, DllName);
            return IsDeletionQueued(path);
        }))
        {
            // 固定 hash/architecture 布局仍保持；随机目录只能凭事务先写入的 receipt 认领。
            // 丢失 receipt 时不会把任意 hash 目录冒充可回收的内容寻址包。
            deployment = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(plan.Package + ":" + Guid.NewGuid().ToString("N")))).ToLowerInvariant();
        }
        foreach (var architecture in Architectures)
        {
            var source = GetPackageEntry(architecture);
            VoiceImeMaintenance.GuardPath(source.DllPath);
            VoiceImeMaintenance.GuardPath(source.HelperPath);
            EnsureToolbarBinary(source.DllPath, architecture);
            var dllHash = HashFile(source.DllPath);
            var helperHash = HashFile(source.HelperPath);
            var cache = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(deployment + ":" + helperHash))).ToLowerInvariant();
            var directory = Path.Combine(_installRoot, deployment, architecture);
            var entry = new InstallEntry { Architecture = architecture, DllPath = Path.Combine(directory, DllName),
                HelperPath = Path.Combine(directory, cache, HelperName) };
            ValidateInstalledEntry(entry);
            plan.Entries.Add(entry);
            foreach (var pair in new[] { (source.DllPath, entry.DllPath, dllHash), (source.HelperPath, entry.HelperPath, helperHash) })
            {
                plan.Files.Add(new OwnedFile { Path = pair.Item2, Hash = pair.Item3 });
                plan.Sources.Add(pair.Item2, pair.Item1);
            }
        }
        var verifiedPackage = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",
            plan.Entries.Select(e => e.Architecture + ":" + plan.Files.Single(f => f.Path == e.DllPath).Hash + ":"
                + plan.Files.Single(f => f.Path == e.HelperPath).Hash))))).ToLowerInvariant();
        if (verifiedPackage != plan.Package) throw new RegistrationException("预检期间包文件发生变化，未部署文件。");
        return plan;
    }

    private string? ReadPackageVersion(IEnumerable<InstallEntry> entries, InstallManifest? manifest = null)
    {
        var components = entries.ToList();
        if (components.Count != Architectures.Length
            || Architectures.Any(a => components.Count(e => e.Architecture == a) != 1)) return null;
        string? common = null;
        foreach (var path in components.SelectMany(e => new[] { e.DllPath, e.HelperPath }))
        {
            VoiceImeMaintenance.GuardPath(path);
            if (!File.Exists(path)) return null;
            if (manifest != null)
            {
                var receipt = manifest.Files.SingleOrDefault(f => !f.Staging && SamePath(f.Path, path));
                if (receipt == null || HashFile(path) != receipt.Hash) return null;
            }
            var raw = _lifecycleBackend?.FileVersion is { } readVersion
                ? readVersion(path) : FileVersionInfo.GetVersionInfo(path).FileVersion;
            if (!Version.TryParse(raw, out var version) || version.Build < 0
                || (version.Major == 0 && version.Minor == 0 && version.Build == 0)) return null;
            var text = version.Revision > 0 ? version.ToString(4) : version.ToString(3);
            if (common != null && common != text) return null;
            common = text;
        }
        return common;
    }

    private void EnsureToolbarBinary(string dllPath, string architecture)
    {
        if (_lifecycleBackend != null) return;
        IntPtr large = IntPtr.Zero, small = IntPtr.Zero;
        try
        {
            var groups = ExtractIconExW(dllPath, -1, null, null, 0);
            var extracted = groups > 0 ? ExtractIconExW(dllPath, 0, out large, out small, 1) : 0;
            if (groups == 0 || extracted == 0 || extracted == uint.MaxValue || large == IntPtr.Zero || small == IntPtr.Zero)
                throw new RegistrationException($"{architecture} 组件仍是缺少方案 A 品牌图标/浮条的旧包，请先重新构建输入法组件。");
        }
        finally
        {
            if (large != IntPtr.Zero) DestroyIcon(large);
            if (small != IntPtr.Zero) DestroyIcon(small);
        }
    }

    internal Task<Result> RefreshStandaloneHostAsync() => RunAsync(async () =>
    {
        var state = await InspectLifecycleAsync(ReadLifecycleManifest()).ConfigureAwait(false);
        if (state.Complete && !File.Exists(MaintenancePath))
            DeployStandaloneHostDirectory(VoiceImeUiProtocol.SourceExecutable(Path.Combine(_baseDirectory, "TrueFluentPro.exe")));
        return new Result(true, "后台部署检查完成。");
    }, writes: false);

    private void DeployStandaloneHostDirectory(string sourceHostExe)
    {
        if (_lifecycleBackend != null || !OperatingSystem.IsWindows() || VoiceImeMaintenanceElevation.IsWorker) return;
        VoiceImeMaintenance.GuardPath(_legacyInstallRoot);
        if (IsDeletionQueued(_legacyInstallRoot))
            throw new RegistrationException("独立后台根目录有重启删除记录，未创建锁或复用该目录。");
        using var installLock = AcquireInstallLock();
        if (VoiceImeMaintenance.Exists(MaintenancePath)) return;
        var sourceDir = Path.GetDirectoryName(Path.GetFullPath(sourceHostExe))!;
        VoiceImeMaintenance.GuardPath(sourceHostExe);
        VoiceImeMaintenance.GuardPath(Path.Combine(sourceDir, "TrueFluentPro.dll"));
        if (!File.Exists(sourceHostExe) || !File.Exists(Path.Combine(sourceDir, "TrueFluentPro.dll"))) return;
        var hostDir = Path.Combine(_legacyInstallRoot, "host");
        VoiceImeMaintenance.GuardPath(hostDir);
        var legacyRuntime = Path.Combine(hostDir, "hostfxr.dll");
        VoiceImeMaintenance.GuardPath(legacyRuntime);
        if (VoiceImeMaintenance.Exists(legacyRuntime))
            throw new RegistrationException("独立后台包含旧自包含运行时，本次未覆盖、删除或启动；请保留现场核对部署布局。");

        var deployment = VoiceImeHostDeployment.Create(sourceDir, hostDir);
        var manifest = ReadLifecycleManifest();
        // 包括未变文件及目录：已排队的路径不能因为文件暂时缺失就被重新使用。
        foreach (var path in deployment.DestinationPaths)
            if (manifest.PendingCleanup.Any(p => SamePath(p.Path, path)) || IsDeletionQueued(path))
                throw new RegistrationException("独立后台路径有重启删除记录，未复用、覆盖或启动该目录。");

        var standaloneExe = Path.Combine(hostDir, "TrueFluentPro.exe");
        var hosts = FindStandaloneHostProcesses(standaloneExe);
        try
        {
            if (deployment.Changes.Count > 0)
            {
                if (hosts.Any(p => p.Id == Environment.ProcessId))
                    throw new RegistrationException("当前程序正在使用独立后台路径，本次未停止自身或覆盖文件。");
                foreach (var host in hosts)
                {
                    if (host.HasExited) continue;
                    VoiceImeMaintenance.GuardPath(standaloneExe);
                    if (VoiceImeMaintenance.Exists(MaintenancePath)) return;
                    if (host.MainModule?.FileName is not { } actual || !SamePath(actual, standaloneExe))
                        throw new RegistrationException("独立后台进程路径已变化，未停止或覆盖文件。");
                    host.Kill();
                    if (!host.WaitForExit(1500))
                        throw new RegistrationException("独立后台尚未确认退出，未覆盖或启动新后台。");
                }
            }
            if (VoiceImeMaintenance.Exists(MaintenancePath)) return;
            deployment.Synchronize(); // 任一文件失败则向上传递，不以部分复制成功启动 Host。
            foreach (var view in Architectures.Select(ViewFor))
            {
                using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
                using var uiConfig = user.CreateSubKey(@"Software\TrueFluentPro\VoiceImeUI", writable: true);
                if (uiConfig != null)
                {
                    // Host 自己打开的设置页不覆盖原主程序路径，也不猜测其所在目录。
                    if (!SamePath(sourceHostExe, standaloneExe)
                        && !string.Equals(uiConfig.GetValue("MainAppExe") as string, sourceHostExe, StringComparison.OrdinalIgnoreCase))
                        uiConfig.SetValue("MainAppExe", sourceHostExe, RegistryValueKind.String);
                    if (!string.Equals(uiConfig.GetValue("StandaloneHostExe") as string, standaloneExe, StringComparison.OrdinalIgnoreCase))
                        uiConfig.SetValue("StandaloneHostExe", standaloneExe, RegistryValueKind.String);
                }
            }
            // 精确进程存在仅用于避免重复拉起，不等于 owner/管道已就绪；仍由既有 owner.lock 协议仲裁。
            if (!VoiceImeMaintenance.Exists(MaintenancePath) && File.Exists(standaloneExe)
                && !hosts.Any(p => !p.HasExited))
            {
                // 复制期间可能已有 native/另一个入口拉起 Host，启动前再查一次精确路径。
                var current = FindStandaloneHostProcesses(standaloneExe);
                try
                {
                    if (current.Length == 0 && !VoiceImeMaintenance.Exists(MaintenancePath))
                    {
                        VoiceImeMaintenance.GuardPath(standaloneExe);
                        using var started = Process.Start(new ProcessStartInfo
                        {
                            FileName = standaloneExe,
                            Arguments = "--voice-ime-host --shared",
                            WorkingDirectory = hostDir,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        });
                    }
                }
                finally { foreach (var process in current) process.Dispose(); }
            }
        }
        finally { foreach (var process in hosts) process.Dispose(); }
    }

    private static Process[] FindStandaloneHostProcesses(string standaloneExe)
    {
        VoiceImeMaintenance.GuardPath(standaloneExe);
        var candidates = Process.GetProcessesByName("TrueFluentPro"); // 名称仅缩小候选，绝不作为 Host 身份。
        var matches = new List<Process>();
        try
        {
            foreach (var process in candidates)
            {
                _ = process.SafeHandle; // 先固定同一个进程对象的句柄，再核验路径并保留到后续操作结束。
                if (process.HasExited) continue;
                var actual = process.MainModule?.FileName
                    ?? throw new RegistrationException("无法核验独立后台进程路径，本次未按无后台处理。");
                if (SamePath(actual, standaloneExe)) matches.Add(process);
            }
        }
        catch
        {
            foreach (var process in candidates) process.Dispose();
            // 权限不足、进程查询竞态等均为未知；不能吞掉后启动第二个 Host。
            throw new RegistrationException("无法核验独立后台进程的精确路径，本次未覆盖、停止或重复启动；请稍后刷新状态。");
        }
        foreach (var process in candidates.Except(matches)) process.Dispose();
        return matches.ToArray();
    }

    private void CheckPlanDestinations(PackagePlan plan, InstallManifest manifest)
    {
        foreach (var file in plan.Files)
        {
            ValidateOwnedFile(file);
            if (manifest.PendingCleanup.Any(p => SamePath(p.Path, file.Path)))
                throw new RegistrationException("安装路径有重启删除记录，必须先重启，不能复用同一包目录。");
            if (VoiceImeMaintenance.Exists(file.Path) && HashFile(file.Path) != file.Hash)
                throw new RegistrationException("目标包文件摘要不符，未覆盖已有文件。");
            VoiceImeMaintenance.GuardPath(file.Path + ".stage");
            if (VoiceImeMaintenance.Exists(file.Path + ".stage")
                && !manifest.Files.Any(f => f.Staging && SamePath(f.Path, file.Path + ".stage")))
                throw new RegistrationException("发现未归属的暂存文件，不覆盖或删除。");
        }
    }

    private static void CopyStaged(string source, OwnedFile file)
    {
        VoiceImeMaintenance.GuardPath(file.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(file.Path)!);
        if (!File.Exists(file.Path))
        {
            var temporary = file.Path + ".stage";
            VoiceImeMaintenance.GuardPath(temporary);
            using (var input = File.OpenRead(source))
            using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            { input.CopyTo(output); output.Flush(true); }
            if (HashFile(temporary) != file.Hash) throw new RegistrationException("stage 时打包文件发生变化，未登记新包。");
            File.Move(temporary, file.Path);
        }
        if (HashFile(file.Path) != file.Hash) throw new RegistrationException("stage 后文件摘要不符。");
    }

    private void ClaimExistingFiles(InstallManifest manifest, List<InstallEntry> entries)
    {
        foreach (var entry in entries)
        {
            ValidateInstalledEntry(entry);
            foreach (var path in new[] { entry.DllPath, entry.HelperPath })
            {
                VoiceImeMaintenance.GuardPath(path);
                if (!VoiceImeMaintenance.Exists(path) || manifest.Files.Any(f => SamePath(f.Path, path))) continue;
                var directory = Path.GetDirectoryName(entry.DllPath)!;
                var root = Path.GetFileName(Path.GetDirectoryName(directory))!;
                // 无 receipt 的历史包只认固定 COM/清单指向的布局及内容摘要，不盲扫目录。
                var group = entries.Where(e => SamePath(Path.GetDirectoryName(Path.GetDirectoryName(e.DllPath)!)!,
                    Path.GetDirectoryName(directory)!)).OrderBy(e => e.Architecture == "win-x64" ? 0 : 1).ToList();
                var combined = group.Count == Architectures.Length && group.All(e => File.Exists(e.DllPath) && File.Exists(e.HelperPath))
                    ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",
                        group.Select(e => e.Architecture + ":" + HashFile(e.DllPath) + ":" + HashFile(e.HelperPath)))))).ToLowerInvariant() : "";
                var dllProof = File.Exists(entry.DllPath) && HashFile(entry.DllPath) == root;
                var helperParent = Path.GetFileName(Path.GetDirectoryName(entry.HelperPath))!;
                var helperProof = File.Exists(entry.HelperPath) && helperParent == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    Encoding.UTF8.GetBytes(root + ":" + HashFile(entry.HelperPath)))).ToLowerInvariant();
                if (combined != root && !(dllProof && (SamePath(path, entry.DllPath) || helperProof)))
                    throw new RegistrationException("无 receipt 的历史组件未通过固定布局和摘要取证，未认领或删除。");
                AddFile(manifest, new OwnedFile { Path = path, Hash = HashFile(path) });
            }
        }
    }

    private void ValidateOwnedFile(OwnedFile file)
    {
        if (file == null || !IsHash(file.Hash) || !Path.IsPathFullyQualified(file.Path))
            throw new RegistrationException("文件回收 receipt 异常，未删除文件。");
        if (file.Staging != file.Path.EndsWith(".stage", StringComparison.Ordinal))
            throw new RegistrationException("暂存标志与固定 .stage 文件名不一致，未删除文件。");
        var path = file.Staging && file.Path.EndsWith(".stage", StringComparison.Ordinal) ? file.Path[..^6] : file.Path;
        var name = Path.GetFileName(path);
        var root = new[] { _installRoot, _legacyInstallRoot }.FirstOrDefault(r =>
            Path.GetFullPath(path).StartsWith(Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase));
        if (root == null) throw new RegistrationException("文件 receipt 越过本组件目录边界。");
        var parts = Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar);
        if (parts.Length is not (3 or 4) || !IsHash(parts[0]) || !Architectures.Contains(parts[1])
            || (parts.Length == 4 && (!IsHash(parts[2]) || name != HelperName))
            || (parts.Length == 3 && name != DllName && name != HelperName))
            throw new RegistrationException("文件 receipt 不是固定 self-component 文件布局。");
        VoiceImeMaintenance.GuardPath(file.Path);
    }

    private void ClaimOrphanPackages(InstallManifest manifest)
    {
        // 仅 inventory 本模块根下的 hash/architecture/固定文件名；不递归扫用户文件。
        // 四组件包摘要或历史 DLL 摘要及 helper 双摘要必须吻合，未知布局保持原样。
        foreach (var root in new[] { _installRoot, _legacyInstallRoot })
        {
            VoiceImeMaintenance.GuardPath(root);
            if (!Directory.Exists(root)) continue;
            var versions = Directory.EnumerateDirectories(root).Where(d => IsHash(Path.GetFileName(d))).Take(65).ToList();
            if (versions.Count > 64) throw new RegistrationException("历史包目录过多，未自动清理；请保留现场核对。");
            foreach (var version in versions)
            {
                VoiceImeMaintenance.GuardPath(version);
                var candidates = new List<InstallEntry>();
                foreach (var architecture in Architectures)
                {
                    var directory = Path.Combine(version, architecture);
                    var dll = Path.Combine(directory, DllName);
                    VoiceImeMaintenance.GuardPath(dll);
                    if (!File.Exists(dll)) continue;
                    var helper = Path.Combine(directory, HelperName);
                    if (!File.Exists(helper))
                    {
                        var caches = Directory.EnumerateDirectories(directory).Where(d => IsHash(Path.GetFileName(d))).Take(65).ToList();
                        if (caches.Count > 64) throw new RegistrationException("历史助手目录过多，未自动认领。");
                        helper = "";
                        foreach (var cache in caches)
                        {
                            var path = Path.Combine(cache, HelperName);
                            VoiceImeMaintenance.GuardPath(path);
                            if (!File.Exists(path)) continue;
                            var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                                Encoding.UTF8.GetBytes(Path.GetFileName(version) + ":" + HashFile(path)))).ToLowerInvariant();
                            if (!string.Equals(Path.GetFileName(cache), expected, StringComparison.OrdinalIgnoreCase)) continue;
                            if (helper.Length > 0) throw new RegistrationException("历史助手身份有歧义，未删除缓存。");
                            helper = path;
                        }
                    }
                    if (helper.Length > 0 && File.Exists(helper))
                        candidates.Add(new InstallEntry { Architecture = architecture, DllPath = dll, HelperPath = helper });
                }
                if (candidates.Count == 0) continue;
                var claim = new InstallManifest();
                try { ClaimExistingFiles(claim, candidates); }
                catch (RegistrationException) { continue; } // 不能证明即不认领，更不能删。
                foreach (var file in claim.Files) AddFile(manifest, file);
            }
        }
    }

    private void ValidateReceipts(InstallManifest manifest)
    {
        foreach (var file in manifest.Files.Concat(manifest.PendingCleanup))
        {
            ValidateOwnedFile(file);
            if (!file.Staging && VoiceImeMaintenance.Exists(file.Path) && HashFile(file.Path) != file.Hash)
                throw new RegistrationException("已有文件与 receipt 摘要不符，停止维护，不删除或覆盖。");
        }
    }

    private static void AddFile(InstallManifest manifest, OwnedFile file)
    {
        var existing = manifest.Files.FirstOrDefault(f => SamePath(f.Path, file.Path));
        if (existing != null && existing.Hash != file.Hash) throw new RegistrationException("同一路径的 receipt 摘要冲突。");
        if (existing == null) manifest.Files.Add(file);
    }

    private static void RequireReceiptFile(InstallManifest manifest, string path)
    {
        var receipt = manifest.Files.SingleOrDefault(f => SamePath(f.Path, path) && !f.Staging);
        if (receipt == null || !File.Exists(path) || HashFile(path) != receipt.Hash)
            throw new RegistrationException("回退组件文件不完整或摘要改变。");
    }

    private async Task CleanupFilesAsync(InstallManifest manifest, IReadOnlyCollection<InstallEntry> keep)
    {
        var retryBudget = Stopwatch.StartNew();
        var protectedPaths = keep.SelectMany(e => new[] { e.DllPath, e.HelperPath }).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var directories = manifest.Files.Select(f => Path.GetDirectoryName(f.Path)!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var file in manifest.Files.OrderBy(f => Path.GetFileName(f.Path) == HelperName ? 1 : 0).ToList())
        {
            if (protectedPaths.Contains(file.Path)) continue;
            // 所有 DLL 先处理；有 pending 时留助手及安装路径，后续即使原包缺失也能完成 native 查询。
            if (Path.GetFileName(file.Path) == HelperName && manifest.PendingCleanup.Count > 0) continue;
            ValidateOwnedFile(file);
            var pending = manifest.PendingCleanup.FirstOrDefault(p => SamePath(p.Path, file.Path));
            if (!VoiceImeMaintenance.Exists(file.Path))
            {
                if (pending != null && IsDeletionQueued(file.Path)) continue;
                if (pending != null) manifest.PendingCleanup.Remove(pending);
                manifest.Files.Remove(file);
                WriteLifecycleManifest(manifest);
                continue;
            }
            if (!file.Staging && HashFile(file.Path) != file.Hash) throw new RegistrationException("文件摘要改变，未回收。");
            var queued = pending != null && IsDeletionQueued(file.Path);
            if (pending != null && !queued)
            {
                // 上轮 intent 尚未入队，或重启删除失败：重做实际删除/共享占用检查，不能永久卡住。
                manifest.PendingCleanup.Remove(pending);
                WriteLifecycleManifest(manifest);
            }
            var error = DeleteOwnedFile(file.Path);
            // 切换和 Host 退出后，给确定的共享/映像占用一个总计不超过两秒的释放窗口。
            for (var attempt = 0; error != 0 && attempt < 3 && retryBudget.Elapsed < TimeSpan.FromSeconds(2)
                && IsRetryableDelete(file, error); attempt++)
            {
                var delay = TimeSpan.FromMilliseconds(200 * (attempt + 1));
                if (_lifecycleBackend?.Delay is { } wait) await wait(delay).ConfigureAwait(false);
                else await Task.Delay(delay).ConfigureAwait(false);
                VoiceImeMaintenance.GuardPath(file.Path);
                if (VoiceImeMaintenance.Exists(file.Path) && !file.Staging && HashFile(file.Path) != file.Hash)
                    throw new RegistrationException("重试时文件摘要改变，未回收。");
                error = DeleteOwnedFile(file.Path);
            }
            if (error == 0)
            {
                // 可以提前删除物理文件，但系统队列仍按路径执行，receipt/路径禁用必须保留。
                if (queued) continue;
                manifest.Files.Remove(file);
                WriteLifecycleManifest(manifest);
                continue;
            }
            // 映像被加载时 DeleteFile 也可返回5；不能把所有拒绝访问当作占用。
            // 只对 receipt 已验真的非只读自有 DLL，再核对运行进程的精确映像路径。
            if (!IsRetryableDelete(file, error))
                throw new RegistrationException($"文件回收失败（Win32={error}），已保留 receipt。");
            if (queued) continue; // 已有系统排队，不重复入队；下次维护仍会机会性重试。
            pending = new OwnedFile { Path = file.Path, Hash = file.Hash, Staging = file.Staging, QueueState = "intent" };
            manifest.PendingCleanup.Add(pending);
            WriteLifecycleManifest(manifest); // 在不可回滚的系统队列写入前落盘，避免崩溃后误复用。
            if (!(_lifecycleBackend?.Schedule(file.Path) ?? MoveFileExW(file.Path, null, 4)))
            {
                manifest.PendingCleanup.Remove(pending);
                WriteLifecycleManifest(manifest);
                throw new RegistrationException("系统拒绝重启清理排队，文件和恢复记录保留。");
            }
            pending.QueueState = "queued";
            WriteLifecycleManifest(manifest);
        }
        // 不递归删除。只尝试 receipt 的已知父链，文件在先、空目录在后；未知文件/日志一律保留。
        foreach (var initial in directories)
        {
            for (var directory = initial; directory != null && !SamePath(directory, _installRoot)
                && !SamePath(directory, _legacyInstallRoot); directory = Path.GetDirectoryName(directory))
            {
                VoiceImeMaintenance.GuardPath(directory);
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory, false);
            }
        }
    }

    private bool IsRetryableDelete(OwnedFile file, int error)
        => error is 32 or 33 or 1224 || error == 5 && !file.Staging
            && Path.GetFileName(file.Path) == DllName && VoiceImeMaintenance.Exists(file.Path)
            && (File.GetAttributes(file.Path) & FileAttributes.ReadOnly) == 0 && IsImageMapped(file.Path);

    private static string CleanupSummary(InstallManifest manifest)
    {
        if (manifest.PendingCleanup.Count == 0) return "旧包文件已清理。";
        var remaining = manifest.PendingCleanup.Count(p => VoiceImeMaintenance.Exists(p.Path));
        return remaining > 0
            ? $"已自动尝试清理；仍有 {remaining} 个旧文件交由 Windows 在正常重启时回收，无需专门重启或再次维护。"
            : "旧 DLL 已清理；系统仍保留旧路径回收记录，无需操作，不会复用这些路径。";
    }

    private int DeleteOwnedFile(string path)
    {
        if (_lifecycleBackend != null) return _lifecycleBackend.Delete(path);
        if (DeleteFileW(path)) return 0;
        var error = Marshal.GetLastWin32Error();
        return error is 2 or 3 ? 0 : error;
    }

    private bool IsImageMapped(string path)
    {
        if (_lifecycleBackend != null) return _lifecycleBackend.ImageMapped?.Invoke(path) ?? false;
        var found = false;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (!found) foreach (ProcessModule module in process.Modules)
                        if (SamePath(module.FileName, path)) { found = true; break; }
                }
                catch (System.ComponentModel.Win32Exception) { }
                catch (InvalidOperationException) { } // 进程退出/权限不足不构成映像占用证据。
            }
        }
        return found;
    }

    private bool IsDeletionQueued(string path)
    {
        if (_lifecycleBackend != null) return _lifecycleBackend.Queued(path);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,
            Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32);
        using var session = machine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager", false);
        var value = session?.GetValue("PendingFileRenameOperations", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value != null && value is not string[]) throw new RegistrationException("系统重启删除队列类型未知，保留路径及恢复记录。");
        var paths = value as string[] ?? Array.Empty<string>();
        return paths.Any(value => IsQueuedPath(value, path));
    }

    private static bool IsQueuedPath(string value, string path)
    {
        var fullPath = Path.GetFullPath(path);
        // 仅将队列中的精确自有路径作为保留依据，不修改队列。
        // *1\??\ 为本机实测条目格式，内部标志含义未确认，不泛化为任意 *数字。
        return string.Equals(value, fullPath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, @"\??\" + fullPath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, @"*1\??\" + fullPath, StringComparison.OrdinalIgnoreCase);
    }

    private InstallManifest ReadLifecycleManifest()
    {
        InstallManifest? Read(string path)
        {
            VoiceImeMaintenance.GuardPath(path);
            if (!VoiceImeMaintenance.Exists(path)) return null;
            if (new FileInfo(path).Length > 256 * 1024) throw new RegistrationException("安装清单过大，未执行维护。");
            var value = JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(path))
                ?? throw new RegistrationException("安装清单为空。");
            if (value.Version != 1 || value.Installations == null || value.Files == null || value.PendingCleanup == null
                || value.Installations.Count > 64 || value.Files.Count > 512 || value.PendingCleanup.Count > 512
                || value.Phase is not ("legacy" or "staging" or "quiescing" or "uninstalling" or "installing"
                    or "rolling-back" or "cleaning" or "installed" or "removed" or "recoverable" or "pending-reboot"))
                throw new RegistrationException("安装清单版本或阶段异常，未修改系统或文件。");
            if (!string.IsNullOrEmpty(value.OwnerSid) && value.OwnerSid != CurrentSid())
                throw new RegistrationException("安装属于不同 SID；仅允许原安装用户维护，不能替其他用户解绑输入法。");
            if (value.Phase != "legacy" && (string.IsNullOrEmpty(value.OwnerSid)
                || !Guid.TryParseExact(value.TransactionId, "N", out _)))
                throw new RegistrationException("生命周期清单缺少 SID/事务身份，未执行维护。");
            if (value.Previous != null && (value.Previous.Entries == null || value.Previous.Entries.Count > 64))
                throw new RegistrationException("旧版恢复记录异常。");
            foreach (var hostPath in new[] { value.HostPath, value.Previous?.HostPath ?? "" })
            {
                if (string.IsNullOrEmpty(hostPath)) continue;
                if (!Path.IsPathFullyQualified(hostPath)
                    || !string.Equals(Path.GetFileName(hostPath), "TrueFluentPro.exe", StringComparison.OrdinalIgnoreCase))
                    throw new RegistrationException("恢复清单中的 Host 身份异常，未启动组件操作。");
                VoiceImeMaintenance.GuardPath(hostPath);
            }
            if ((!string.IsNullOrEmpty(value.Package) && !IsHash(value.Package))
                || (!string.IsNullOrEmpty(value.HostHash) && !IsHash(value.HostHash))
                || (value.Previous is { Complete: true } previous && (previous.Entries.Count != Architectures.Length
                    || string.IsNullOrEmpty(previous.HostPath) || !IsHash(previous.HostHash))))
                throw new RegistrationException("安装包或完整旧版快照的身份字段异常。");
            if (value.Files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.Files.Count
                || value.Installations.Select(e => e.DllPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.Installations.Count)
                throw new RegistrationException("清单有重复文件/安装路径。");
            foreach (var entry in value.Installations.Concat(value.Previous?.Entries ?? new())) ValidateInstalledEntry(entry);
            foreach (var file in value.Files.Concat(value.PendingCleanup)) ValidateOwnedFile(file);
            if (value.PendingCleanup.Any(p => p.QueueState is not ("intent" or "queued")
                || !value.Files.Any(f => SamePath(f.Path, p.Path) && f.Hash == p.Hash && f.Staging == p.Staging)))
                throw new RegistrationException("待重启清理记录缺少对应文件 receipt。");
            return value;
        }
        var machine = Read(MachineManifestPath);
        var user = Read(ManifestPath); // 坏副本仍 fail closed，不借另一副本偷偷删除。
        return machine ?? user ?? new InstallManifest();
    }

    private void WriteLifecycleManifest(InstallManifest manifest)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestJsonOptions);
        AtomicWrite(MachineManifestPath, json); // 机器清单是 authoritative journal，阻止另一 SID 接管。
        AtomicWrite(ManifestPath, json);
    }

    private static void AtomicWrite(string path, byte[] bytes)
    {
        VoiceImeMaintenance.GuardPath(path);
        VoiceImeMaintenance.GuardPath(path + ".tmp");
        using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        { stream.Write(bytes); stream.Flush(true); }
        // 自有 journal 与临时文件位于同一受保护目录，继承该目录 ACL。
        // 不需要 ReplaceFile 的旧文件属性/流合并；本机该路径已实测返回1175并留下旧副本。
        // 同目录 rename-replace 不经过先删后写窗口；真实共享/权限错误仍向上传递。
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private void CreateMaintenanceGate(InstallManifest manifest)
    {
        ValidateMaintenanceGate(manifest);
        if (VoiceImeMaintenance.Exists(MaintenancePath)) return;
        AtomicWrite(MaintenancePath, JsonSerializer.SerializeToUtf8Bytes(new { manifest.OwnerSid, manifest.TransactionId }));
    }

    private void ValidateMaintenanceGate(InstallManifest manifest)
    {
        VoiceImeMaintenance.GuardPath(MaintenancePath);
        if (VoiceImeMaintenance.Exists(MaintenancePath))
        {
            if (string.IsNullOrEmpty(manifest.TransactionId) || new FileInfo(MaintenancePath).Length > 4096)
                throw new RegistrationException("存在无法归属的维护门禁，未覆盖；请恢复本模块清单。");
            using var json = JsonDocument.Parse(File.ReadAllText(MaintenancePath));
            if (json.RootElement.GetProperty("OwnerSid").GetString() != manifest.OwnerSid
                || json.RootElement.GetProperty("TransactionId").GetString() != manifest.TransactionId)
                throw new RegistrationException("维护门禁不属于当前恢复事务，未覆盖。");
        }
    }

    private void RemoveMaintenanceGate(InstallManifest manifest)
    {
        CreateMaintenanceGate(manifest); // 精确核对而不是盲删别的维护事务。
        File.Delete(MaintenancePath);
    }

    private void DeleteCompletedManifest()
    {
        foreach (var path in new[] { ManifestPath, MachineManifestPath })
        { VoiceImeMaintenance.GuardPath(path); File.Delete(path); }
    }

    private async Task<Result> LifecycleStatusAsync()
    {
        var manifest = ReadLifecycleManifest();
        var state = await InspectLifecycleAsync(manifest).ConfigureAwait(false);
        var phase = manifest.Phase switch
        {
            "legacy" => "旧版清单（下次管理将精确认领）", "staging" => "暂存新包", "quiescing" => "等待后台停止",
            "uninstalling" => "移除系统入口", "installing" => "安装/修复", "rolling-back" => "恢复原版本",
            "cleaning" => "回收自有文件", "installed" => "已安装", "removed" => "已移除",
            "pending-reboot" => "已卸载（保留旧文件自动回收记录）", _ => "待修复（可恢复，未完成）"
        };
        // 展示 receipt 对应的实际已安装二进制，不用当前程序或历史 journal 冒充组件版本。
        var installedVersion = state.Absent ? null : ReadPackageVersion(state.Entries, manifest);
        var versionInfo = state.Absent ? "" : installedVersion != null
            ? $"（已安装组件 v{installedVersion}）" : "（已安装组件版本未知：旧包未标注或组件版本不一致）";
        return new Result(true, $"阶段：{phase}{versionInfo}；范围：当前用户 SID（不代其他用户解绑）。\n" +
            $"当前主程序 v{GetProductVersionString()}。\n" +
            (state.Complete ? "双架构原生安装状态完整。" : state.Absent ? "原生全量状态及固定 footprint 均无登记。" : "仍有不完整登记，不能视为已卸载。") +
            (manifest.PendingCleanup.Count > 0 ? "\n" + CleanupSummary(manifest) : "") +
            (VoiceImeMaintenance.Exists(MaintenancePath) ? "\n维护门禁生效，F8 暂停；请使用原用户继续安装/修复或卸载。" : ""));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteFileW(string path);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExW(string path, string? destination, uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint ExtractIconExW(string lpszFile, int nIconIndex, IntPtr[]? phiconLarge, IntPtr[]? phiconSmall, uint nIcons);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint ExtractIconExW(string lpszFile, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private sealed class OwnedFile
    {
        public OwnedFile() { }
        public string Path { get; set; } = "";
        public string Hash { get; set; } = "";
        public bool Staging { get; set; }
        public string QueueState { get; set; } = "";
    }
    private sealed class PreviousPackage
    {
        public PreviousPackage() { }
        public string Package { get; set; } = "";
        public string PackageVersion { get; set; } = "";
        public string HostPath { get; set; } = "";
        public string HostHash { get; set; } = "";
        public bool Complete { get; set; }
        public List<InstallEntry> Entries { get; set; } = new();
    }
    private sealed class PackagePlan
    {
        public string Package = "", PackageVersion = "", HostPath = "", HostHash = "";
        public List<InstallEntry> Entries = new();
        public List<OwnedFile> Files = new();
        public Dictionary<string, string> Sources = new(StringComparer.OrdinalIgnoreCase);
    }
}