using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace TrueFluentPro.Services
{
    /// <summary>
    /// 当前 SID 的输入法安装/修复/升级及卸载入口。不自动提权、不写主配置。
    /// 事务和文件回收见 VoiceImeLifecycle；原生助手负责正式 Windows 输入法 API。
    /// </summary>
    public sealed partial class VoiceImeRegistrationService
    {
        public static event Action? RegistrationChanged;

        public static bool HasNativeRegistration()
        {
            if (!OperatingSystem.IsWindows()) return false;
            try
            {
                foreach (var architecture in Architectures)
                {
                    using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, ViewFor(architecture));
                    using var config = user.OpenSubKey(NativeConfigKey, writable: false);
                    var host = config?.GetValue("HostExe") as string;
                    if (!IsOwned(config) || string.IsNullOrWhiteSpace(host)
                        || !Path.IsPathFullyQualified(host) || !File.Exists(host)) continue;
                    using var userCom = user.OpenSubKey(ComKey, writable: false);
                    using var userTip = user.OpenSubKey(TipKey, writable: false);
                    using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, ViewFor(architecture));
                    using var machineCom = machine.OpenSubKey(ComKey, writable: false);
                    if (userCom != null || userTip != null || machineCom != null) return true;
                }
                return false;
            }
            catch { return false; }
        }
        private const string DllName = "TrueFluentVoiceIme.dll";
        private const string HelperName = "VoiceImeRegister.exe";
        private const string NativeOwner = "TrueFluentPro.VoiceIme.v1";
        private const string NativeConfigKey = @"Software\TrueFluentPro\VoiceIme";
        private const string ComponentClsid = "{5638E6E5-D18C-4FE8-B778-E59A5170B481}";
        private const string ComKey = @"Software\Classes\CLSID\" + ComponentClsid;
        private const string TipKey = @"Software\Microsoft\CTF\TIP\" + ComponentClsid;
        private const string AdministratorMessage = "请关闭主程序，使用原安装用户右键以管理员身份运行，再安装/修复或卸载输入法；日常听写不需要管理员，不会自动提权。";
        private static readonly SemaphoreSlim OperationGate = new(1, 1);
        private static readonly JsonSerializerOptions ManifestJsonOptions = new() { WriteIndented = true };
        private readonly string _baseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        private readonly string _installRoot = VoiceImeMaintenance.InstallRoot;
        private readonly string _legacyInstallRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrueFluentPro", "VoiceIme");

        public sealed record Result(bool Succeeded, string Message);

        public Task<Result> RegisterAsync() => RunAsync(() => ManageLifecycleAsync(true), writes: true, requiresAdministrator: true);
        public Task<Result> UnregisterAsync() => RunAsync(() => ManageLifecycleAsync(false), writes: true, requiresAdministrator: true);
        public Task<Result> RefreshStatusAsync() => RunAsync(LifecycleStatusAsync, writes: false);

        private string ManifestPath => Path.Combine(_legacyInstallRoot, "install-manifest.json");
        private static string[] Architectures => Environment.Is64BitOperatingSystem
            ? new[] { "win-x64", "win-x86" } : new[] { "win-x86" };

        // 文件操作、哈希计算及 Process.Start 也不占用 UI 线程。
        private Task<Result> RunAsync(Func<Task<Result>> operation, bool writes, bool requiresAdministrator = false) => Task.Run(async () =>
        {
            if (!OperatingSystem.IsWindows()) return new Result(false, "语音输入法注册仅适用于 Windows。");
            if (!await OperationGate.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false))
                return new Result(false, "已有输入法组件操作正在进行，请稍后刷新状态。");
            var notify = false;
            try
            {
                // 必须早于建目录、写锁文件、读取清单以及启动助手。
                if (requiresAdministrator && !LifecycleAdministrator()) return new Result(false, AdministratorMessage);
                notify = writes;
                return await operation().ConfigureAwait(false);
            }
            catch (Exception ex) { return new Result(false, FriendlyMessage(ex)); }
            finally
            {
                OperationGate.Release();
                if (notify)
                {
                    try { RegistrationChanged?.Invoke(); } catch { }
                }
            }
        });

        private async Task RemoveRegistrationAsync(InstallEntry entry)
        {
            EnsureRemovalTarget(entry);
            var result = await RunHelperAsync(entry, "--unregister").ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new RegistrationException(DescribeHelperFailure(result, "反注册"));
        }

        private InstallEntry GetPackageEntry(string architecture)
        {
            var directory = Path.Combine(_baseDirectory, "voice-ime", architecture);
            return new InstallEntry
            {
                Architecture = architecture,
                DllPath = Path.Combine(directory, DllName),
                HelperPath = Path.Combine(directory, HelperName)
            };
        }

        private string GetPackageIdentity()
        {
            var identities = new List<string>();
            foreach (var architecture in Architectures)
            {
                var package = GetPackageEntry(architecture);
                if (!File.Exists(package.DllPath) || !File.Exists(package.HelperPath))
                    throw new RegistrationException($"{architecture} 组件未构建，请先补齐双架构组件。");
                identities.Add(architecture + ":" + HashFile(package.DllPath) + ":" + HashFile(package.HelperPath));
            }
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", identities)))).ToLowerInvariant();
        }

        private static string HashFile(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        private void ValidateInstalledEntry(InstallEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.DllPath) || string.IsNullOrWhiteSpace(entry.HelperPath)
                || !Path.IsPathFullyQualified(entry.DllPath) || !Path.IsPathFullyQualified(entry.HelperPath))
                throw new RegistrationException("输入法安装路径记录不完整，无法安全执行组件操作。");
            VoiceImeMaintenance.GuardPath(entry.DllPath);
            VoiceImeMaintenance.GuardPath(entry.HelperPath);
            foreach (var root in new[] { _installRoot, _legacyInstallRoot })
            {
                var parts = Path.GetRelativePath(root, Path.GetFullPath(entry.DllPath)).Split(Path.DirectorySeparatorChar);
                if (parts.Length != 3 || !IsHash(parts[0])
                    || entry.Architecture is not ("win-x64" or "win-x86") || parts[1] != entry.Architecture
                    || !string.Equals(parts[2], DllName, StringComparison.OrdinalIgnoreCase)) continue;
                var directory = Path.GetDirectoryName(Path.GetFullPath(entry.DllPath))!;
                if (SamePath(entry.HelperPath, Path.Combine(directory, HelperName))) return;
                var helperParts = Path.GetRelativePath(directory, Path.GetFullPath(entry.HelperPath)).Split(Path.DirectorySeparatorChar);
                if (helperParts.Length == 2 && IsHash(helperParts[0])
                    && string.Equals(helperParts[1], HelperName, StringComparison.OrdinalIgnoreCase)) return;
            }
            throw new RegistrationException("安装记录指向本组件目录之外或格式异常，已拒绝执行。");
        }

        private static bool IsHash(string text) => text.Length == 64 && text.All(Uri.IsHexDigit);

        private FileStream AcquireInstallLock()
        {
            VoiceImeMaintenance.GuardPath(Path.Combine(_legacyInstallRoot, "install.lock"));
            Directory.CreateDirectory(_legacyInstallRoot);
            try
            {
                return new FileStream(Path.Combine(_legacyInstallRoot, "install.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) { throw new RegistrationException("安装记录正被其他实例使用，请稍后重试。"); }
        }

        private static bool IsAdministrator()
        {
            if (!OperatingSystem.IsWindows()) return false;
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        private InstallEntry? FindActualEntry(string architecture, InstallManifest manifest, RegistrationFootprint footprint)
        {
            if (footprint.DllPaths.Count == 0 || footprint.HasForeignOwner) return null;
            if (footprint.DllPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1) return null;
            var path = footprint.DllPaths[0];
            var saved = manifest.Installations.FirstOrDefault(e => e.Architecture == architecture && SamePath(e.DllPath, path));
            var entry = saved ?? new InstallEntry
            {
                Architecture = architecture, DllPath = path,
                HelperPath = Path.Combine(Path.GetDirectoryName(path)!, HelperName)
            };
            try
            {
                ValidateInstalledEntry(entry);
                if (saved == null && !File.Exists(entry.HelperPath))
                {
                    var directory = Path.GetDirectoryName(entry.DllPath)!;
                    if (Directory.Exists(directory))
                    {
                        // 只恢复此 DLL 摘要目录内、双摘要身份吻合的助手，不扫描其他安装。
                        foreach (var candidate in Directory.EnumerateDirectories(directory).Take(64))
                        {
                            var identity = Path.GetFileName(candidate);
                            var helper = Path.Combine(candidate, HelperName);
                            if (!IsHash(identity) || !File.Exists(helper)) continue;
                            VoiceImeMaintenance.GuardPath(helper);
                            var dllHash = Path.GetFileName(Path.GetDirectoryName(directory))!;
                            var expected = Convert.ToHexString(SHA256.HashData(
                                Encoding.UTF8.GetBytes(dllHash + ":" + HashFile(helper)))).ToLowerInvariant();
                            if (!string.Equals(identity, expected, StringComparison.OrdinalIgnoreCase)) continue;
                            entry.HelperPath = helper;
                            break;
                        }
                    }
                }
                ValidateInstalledEntry(entry);
                return entry;
            }
            catch (RegistrationException) { return null; }
        }

        private void EnsureRemovalTarget(InstallEntry entry)
        {
            ValidateInstalledEntry(entry);
            var footprint = LifecycleFootprint(entry.Architecture);
            if (footprint.HasForeignOwner || footprint.DllPaths.Any(path => !SamePath(path, entry.DllPath)))
                throw new RegistrationException("登记所有权或实际 DLL 路径不匹配，未修改其他路径的登记。");
        }

        private static bool SamePath(string a, string b)
            => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

        private async Task<NativeStatus> QueryStatusAsync(InstallEntry entry)
        {
            var footprint = LifecycleFootprint(entry.Architecture);
            if (_lifecycleBackend == null && !HasHelper(entry))
            {
                throw new RegistrationException("注册助手缺失，无法排除 API/category 残留；请恢复完整打包助手后查询，不会推断为未安装。");
            }
            var result = await RunHelperAsync(entry, "--status").ConfigureAwait(false);
            // 不将任意非零退出码或缺 DLL 当成“未注册”。原生助手须提供明确机器状态。
            if (result.ExitCode is not (0 or 1))
                throw new RegistrationException(DescribeHelperFailure(result, "状态查询"));
            var output = result.Output.Trim();
            // 旧助手只检查手写 HKCU 键：兼容它的文本，但绝不冒充 Windows 枚举通过。
            if (result.ExitCode == 0 && (output == "registered-current-user; visibility-not-probed"
                || string.Equals(output, "registered", StringComparison.OrdinalIgnoreCase)))
                return new NativeStatus(RegistrationState.LegacyUser, footprint.HasMachine);
            if (result.ExitCode == 1 && (output == "not-registered-or-incomplete"
                || string.Equals(output, "unregistered", StringComparison.OrdinalIgnoreCase)
                || string.Equals(output, "not-registered", StringComparison.OrdinalIgnoreCase)))
                throw new RegistrationException("旧助手不能确认完整 API/category 状态；请恢复带 JSON 状态契约的完整助手，未判为已卸载。");
            try
            {
                using var json = JsonDocument.Parse(output);
                var root = json.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("registered", out var registered)
                    && registered.ValueKind is JsonValueKind.True or JsonValueKind.False
                    && root.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String
                    && root.TryGetProperty("requiresAdmin", out var admin)
                    && admin.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    RegistrationState? parsed = state.GetString() switch
                    {
                        "registered" => RegistrationState.Registered,
                        "unregistered" => RegistrationState.Unregistered,
                        "partial" => RegistrationState.Partial,
                        "different-path" => RegistrationState.DifferentPath,
                        "legacy-user" => RegistrationState.LegacyUser,
                        _ => null
                    };
                    if (parsed.HasValue && registered.GetBoolean() == (parsed == RegistrationState.Registered)
                        && result.ExitCode == (registered.GetBoolean() ? 0 : 1))
                    {
                        var observed = LifecycleFootprint(entry.Architecture);
                        if (parsed == RegistrationState.Unregistered && observed.HasFootprint)
                            return StatusFromFootprint(observed, entry.DllPath);
                        return new NativeStatus(parsed.Value, admin.GetBoolean());
                    }
                }
            }
            catch (JsonException) { }
            throw new RegistrationException("注册助手未返回可识别的状态，当前登记未知；请确认原生组件版本后刷新。");
        }

        private static NativeStatus StatusFromFootprint(RegistrationFootprint footprint, string expectedDll)
        {
            var state = footprint.HasForeignOwner || footprint.DllPaths.Any(path => !SamePath(path, expectedDll))
                ? RegistrationState.DifferentPath : footprint.HasLegacyUser ? RegistrationState.LegacyUser
                : footprint.HasFootprint ? RegistrationState.Partial : RegistrationState.Unregistered;
            return new NativeStatus(state, footprint.HasMachine);
        }

        private static RegistryView ViewFor(string architecture)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            return architecture == "win-x64" ? RegistryView.Registry64 : RegistryView.Registry32;
        }

        private static bool IsOwned(RegistryKey? key)
            => string.Equals(key?.GetValue("VoiceImeOwner") as string, NativeOwner, StringComparison.Ordinal);

        private static RegistrationFootprint ReadFootprint(string architecture)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            var footprint = new RegistrationFootprint();
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            {
                using var root = RegistryKey.OpenBaseKey(hive, ViewFor(architecture));
                using var com = root.OpenSubKey(ComKey, writable: false);
                using var tip = root.OpenSubKey(TipKey, writable: false);
                using var server = com?.OpenSubKey("InprocServer32", writable: false);
                var path = server?.GetValue(null, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                if (com != null)
                {
                    // 实际 COM 路径和明确所有权是操作依据，不能只信本地清单。
                    footprint.HasCom = true;
                    if (!IsOwned(com) || (!string.IsNullOrWhiteSpace(path) && !Path.IsPathFullyQualified(path)))
                        footprint.HasForeignOwner = true;
                    if (!string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path))
                        footprint.DllPaths.Add(Path.GetFullPath(path));
                }
                var tipOwner = tip?.GetValue("VoiceImeOwner") as string;
                if (tipOwner != null && !string.Equals(tipOwner, NativeOwner, StringComparison.Ordinal))
                    footprint.HasForeignOwner = true;
                if (hive == RegistryHive.LocalMachine) footprint.HasMachine = com != null || tip != null;
                else
                {
                    footprint.HasLegacyUser = com != null || tip != null;
                    using var config = root.OpenSubKey(NativeConfigKey, writable: false);
                    footprint.HasOwnedConfig = IsOwned(config);
                    if (config != null && !IsOwned(config)) footprint.HasForeignOwner = true;
                    if (IsOwned(config) && config?.GetValue("HostExe") is string host && !string.IsNullOrWhiteSpace(host))
                    {
                        if (!Path.IsPathFullyQualified(host)) footprint.HasForeignOwner = true;
                        else footprint.HostPaths.Add(Path.GetFullPath(host));
                    }
                }
            }
            return footprint;
        }

        private static bool HasAnyRegistrationFootprint()
            => Architectures.Any(architecture => ReadFootprint(architecture).HasFootprint);

        private string HelperFor(InstallEntry entry)
        {
            // 优先当前完整包的助手，但始终传实际登记 DLL；缺包时兼容历史缓存助手。
            var package = GetPackageEntry(entry.Architecture);
            if (File.Exists(package.DllPath) && File.Exists(package.HelperPath))
            {
                VoiceImeMaintenance.GuardPath(package.DllPath);
                VoiceImeMaintenance.GuardPath(package.HelperPath);
                return package.HelperPath;
            }
            return entry.HelperPath;
        }

        private bool HasHelper(InstallEntry entry) => File.Exists(HelperFor(entry));

        private async Task<HelperResult> RunHelperAsync(InstallEntry entry, string operation, string? hostPath = null,
            bool useCurrentHelper = false)
        {
            var helperPath = operation == "--register" && !useCurrentHelper ? entry.HelperPath : HelperFor(entry);
            if (_lifecycleBackend != null)
            {
                var fake = await _lifecycleBackend.Helper(helperPath, entry.DllPath, operation, hostPath).ConfigureAwait(false);
                return new HelperResult(fake.ExitCode, fake.Output);
            }
            VoiceImeMaintenance.GuardPath(helperPath);
            if (!File.Exists(helperPath) || (operation == "--register" && !File.Exists(entry.DllPath)))
                throw new RegistrationException("安装 DLL 或注册助手缺失或不可访问；请恢复原组件文件。反注册不会删除 DLL。");
            if (SamePath(helperPath, entry.HelperPath) && !SamePath(helperPath, GetPackageEntry(entry.Architecture).HelperPath))
            {
                ValidateInstalledEntry(entry);
                var manifest = ReadLifecycleManifest();
                var receipt = manifest.Files.FirstOrDefault(f => SamePath(f.Path, helperPath) && !f.Staging);
                var hash = HashFile(helperPath);
                var packageRoot = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(entry.DllPath)!))!;
                var expectedCache = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(packageRoot + ":" + hash))).ToLowerInvariant();
                if (receipt != null ? receipt.Hash != hash
                    : !string.Equals(Path.GetFileName(Path.GetDirectoryName(helperPath)), expectedCache, StringComparison.OrdinalIgnoreCase))
                    throw new RegistrationException("缓存助手未通过 receipt/摘要路径验证，拒绝执行。");
            }
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = helperPath,
                    WorkingDirectory = Path.GetDirectoryName(helperPath)!,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                }
            };
            process.StartInfo.ArgumentList.Add(operation);
            if (operation != "--probe") process.StartInfo.ArgumentList.Add(entry.DllPath);
            if (hostPath != null) process.StartInfo.ArgumentList.Add(hostPath);
            try
            {
                if (!process.Start()) throw new RegistrationException("无法启动输入法注册助手。");
            }
            catch (RegistrationException) { throw; }
            catch { throw new RegistrationException("无法启动注册助手；请检查组件架构及文件权限。安装/反安装需手动以管理员运行主程序，不会自动提权。"); }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var stdout = ReadBoundedAsync(process.StandardOutput, timeout.Token);
            var stderr = ReadBoundedAsync(process.StandardError, timeout.Token);
            try
            {
                await Task.WhenAll(process.WaitForExitAsync(timeout.Token), stdout, stderr)
                    .WaitAsync(timeout.Token).ConfigureAwait(false);
                return new HelperResult(process.ExitCode, await stdout.ConfigureAwait(false));
            }
            catch
            {
                var exited = false;
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await process.WaitForExitAsync(cleanup.Token).ConfigureAwait(false);
                    exited = process.HasExited;
                }
                catch { }
                throw new HelperException(exited
                    ? "注册助手超时或通信失败，已结束本次助手进程。"
                    : "注册助手超时且无法确认终止，请稍后刷新状态，暂勿重复登记。", !exited);
            }
        }

        private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
        {
            var text = new StringBuilder();
            var buffer = new char[1024];
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
            {
                var retained = Math.Min(count, Math.Max(0, 16384 - text.Length));
                text.Append(buffer, 0, retained); // 超出上限仍持续排空，避免重定向管道死锁。
            }
            return text.ToString();
        }

        private static string DescribeHelperFailure(HelperResult result, string operation)
        {
            if (result.ExitCode == 10) return AdministratorMessage + " 助手报告 requires-admin，未写入系统登记。";
            // 只投影固定诊断字段，不向 UI 转发整个 stdout、路径或任意原生输出。
            var errors = Regex.Matches(result.Output, @"(?i)\b(?:HRESULT|hr)\s*[=:]\s*(0x[89a-f][0-9a-f]{7})");
            var error = errors.Count > 0 ? errors[0]
                : Regex.Match(result.Output, @"(?i)\b(?:HRESULT|hr)\s*[=:]\s*(0x[0-9a-f]{8})");
            var diagnostic = result.Output;
            if (error.Success)
            {
                var start = result.Output.LastIndexOf('\n', error.Index);
                var end = result.Output.IndexOf('\n', error.Index);
                diagnostic = result.Output.Substring(start + 1, (end < 0 ? result.Output.Length : end) - start - 1);
            }
            var stage = Regex.Match(diagnostic, @"(?im)\bstage[=:\s]+([A-Za-z0-9_.:/()-]{1,96})");
            var stageName = stage.Groups[1].Value;
            var friendlyStage = stageName.ToLowerInvariant() switch
            {
                var text when text.Contains("probe") || text.Contains("enum") || text.Contains("postcheck") => "Windows 输入法枚举校验",
                var text when text.Contains("category") => "输入法类别登记",
                var text when text.Contains("profile") => "Windows profile 登记",
                var text when text.Contains("com") => "机器级 COM 登记",
                _ => "原生组件操作"
            };
            var hresult = error.Groups[1].Value.ToUpperInvariant();
            var detail = (stage.Success ? $"；阶段 {friendlyStage}（{stageName}）" : "")
                + (hresult.Length > 0 ? $"；HRESULT={hresult}" : "");
            // 旧助手的 4 专指 profile 后置检查；正式助手会给出实际 stage，不能仍一概说成枚举失败。
            var explanation = operation == "注册" && result.ExitCode == 4 && !stage.Success
                ? " 旧助手未能通过 Windows 输入法校验；请使用本次构建的完整组件重试。" : "";
            return $"{operation}失败（退出码 {result.ExitCode}{detail}）。{explanation}";
        }

        private static string FriendlyMessage(Exception ex) => ex switch
        {
            RegistrationException => ex.Message,
            UnauthorizedAccessException => "无法访问输入法安装目录或登记。安装/反安装需手动以管理员运行主程序；查询不需要提权。",
            System.Security.SecurityException => "无法读取本组件的输入法登记，注册状态未知；不会自动提权。",
            JsonException => "输入法安装记录无法读取，注册状态未知；未修改其他输入法。",
            IOException => "输入法组件文件读写失败，文件可能被占用；请刷新状态后重试，不要删除已加载的 DLL。",
            _ => "输入法组件操作失败，请检查打包文件并刷新注册状态。"
        };

        private sealed record HelperResult(int ExitCode, string Output);
        private enum RegistrationState { Registered, Unregistered, Partial, DifferentPath, LegacyUser }
        private sealed record NativeStatus(RegistrationState State, bool RequiresAdmin);
        private sealed class RegistrationFootprint
        {
            public bool HasCom { get; set; }
            public bool HasMachine { get; set; }
            public bool HasLegacyUser { get; set; }
            public bool HasOwnedConfig { get; set; }
            public bool HasForeignOwner { get; set; }
            public bool HasComponent => HasMachine || HasLegacyUser;
            public bool HasFootprint => HasComponent || HasOwnedConfig || HasForeignOwner;
            public List<string> DllPaths { get; } = new();
            public List<string> HostPaths { get; } = new();
        }
        private class RegistrationException : Exception
        {
            public RegistrationException(string message) : base(message) { }
        }
        private sealed class HelperException : RegistrationException
        {
            public bool MayStillBeRunning { get; }
            public HelperException(string message, bool mayStillBeRunning) : base(message)
                => MayStillBeRunning = mayStillBeRunning;
        }
        private sealed class InstallManifest
        {
            public InstallManifest() { }
            public int Version { get; set; } = 1;
            public string Phase { get; set; } = "legacy";
            public string Package { get; set; } = "";
            public string PackageVersion { get; set; } = "";
            public string HostPath { get; set; } = "";
            public string HostHash { get; set; } = "";
            public string OwnerSid { get; set; } = "";
            public string TransactionId { get; set; } = "";
            public PreviousPackage? Previous { get; set; }
            public List<OwnedFile> Files { get; set; } = new();
            public List<OwnedFile> PendingCleanup { get; set; } = new();
            public List<InstallEntry> Installations { get; set; } = new();
        }
        private sealed class InstallEntry
        {
            public InstallEntry() { }
            public string Architecture { get; set; } = "";
            public string DllPath { get; set; } = "";
            public string HelperPath { get; set; } = "";
        }
    }
}