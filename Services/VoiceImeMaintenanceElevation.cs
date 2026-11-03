using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Result = TrueFluentPro.Services.VoiceImeRegistrationService.Result;

namespace TrueFluentPro.Services;

/// <summary>仅提权当前包的安装/卸载流程；不重启主 UI，不加载应用配置。</summary>
internal static class VoiceImeMaintenanceElevation
{
    internal const string Switch = "--voice-ime-maintenance";
    internal static bool IsWorker { get; private set; }
    private const string PipePrefix = "TrueFluentPro.VoiceIme.Maintenance.";
    private const int MaximumResultBytes = 32 * 1024;
    private const int WrongUserExit = 3, NotAdministratorExit = 4, RejectedParentExit = 5, UnknownResultExit = 6;
    private const byte BeginMaintenance = 1;
    private const PipeAccessRights ClientRights = PipeAccessRights.ReadData | PipeAccessRights.WriteData
        | PipeAccessRights.ReadAttributes | PipeAccessRights.Synchronize;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions ResultJson = new() { MaxDepth = 4 };
    private static string ExecutablePath => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "TrueFluentPro.exe"));

    // ShellExecute/runas 会同步等待 UAC，必须连同 Process.Start 一起离开 UI 线程。
    internal static Task<Result> RunElevatedAsync(bool install) => Task.Run(() => RunElevatedCoreAsync(install));

    private static async Task<Result> RunElevatedCoreAsync(bool install)
    {
        if (!OperatingSystem.IsWindows()) return new(false, "输入法维护提权仅适用于 Windows。");
        if (IsWorker) return new(false, "维护子进程不能再次发起提权。");
        var launched = false;
        try
        {
            if (!SameExecutable(Environment.ProcessPath) || !File.Exists(ExecutablePath))
                return new(false, "无法确认当前包的 TrueFluentPro.exe，未启动管理员维护流程。");
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User?.Value;
            if (sid == null) return new(false, "无法确认当前用户，未启动管理员维护流程。");
            using var parent = Process.GetCurrentProcess();
            var startInfo = BuildStartInfo(install, sid, PipePrefix + Guid.NewGuid().ToString("N"),
                parent.Id, parent.StartTime.ToUniversalTime().Ticks);
            using var pipe = CreateServer(startInfo.ArgumentList[3], sid);
            using var child = StartWorker(startInfo, Process.Start, out var failure);
            if (child == null) return failure!;
            launched = true;

            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var exited = child.WaitForExitAsync(timeout.Token);
            try
            {
                // 退出和连接竞争，尤其是选错管理员账户时，不空等整个超时周期。
                using var connectionStop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                while (true)
                {
                    var connected = pipe.WaitForConnectionAsync(connectionStop.Token);
                    await Task.WhenAny(connected, exited).ConfigureAwait(false);
                    if (!connected.IsCompleted && exited.IsCompleted)
                    {
                        connectionStop.Cancel();
                        try { await connected.ConfigureAwait(false); }
                        catch (OperationCanceledException) { }
                        await exited.ConfigureAwait(false);
                        return ResultForEarlyExit(child.ExitCode);
                    }
                    await connected.ConfigureAwait(false);
                    if (GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientPid)
                        && clientPid == (uint)child.Id) break;
                    // 不接受同用户的其他进程；仍只等待本次 Start 返回的唯一子进程。
                    pipe.Disconnect();
                    if (child.HasExited) return ResultForEarlyExit(child.ExitCode);
                }

                // 子进程也验证 server PID/父身份，并收到此授权后才可开始事务。
                await pipe.WriteAsync(new byte[] { BeginMaintenance }, timeout.Token).ConfigureAwait(false);
                return await ReadResultAsync(pipe, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return UnknownResult("等待管理员维护结果已超时。");
            }
            finally
            {
                // 仅取消本进程的等待，不 Kill 子进程，不向生命周期传入取消令牌。
                timeout.Cancel();
                try { await exited.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
        }
        catch
        {
            return launched ? UnknownResult("管理员维护结果通信中断。")
                : new(false, "无法准备管理员维护流程；主窗口和当前任务已保留，未自动重试。");
        }
    }

    /// <summary>由 Program.Main 在任何 Avalonia/App/配置初始化之前分流，直接返回此退出码。</summary>
    internal static async Task<int> RunWorkerAsync(string[] args)
    {
        if (!TryParseWorkerArguments(args, out var request) || request == null) return 2;
        IsWorker = true;
        if (!OperatingSystem.IsWindows()) return RejectedParentExit;
        var begun = false;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            // 另一管理员不会有管道 ACL；在连接及构造登记服务之前用固定退出码友好拒绝。
            if (!string.Equals(identity.User?.Value, request.Sid, StringComparison.Ordinal)) return WrongUserExit;
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return NotAdministratorExit;
            using var parent = Process.GetProcessById(request.ParentPid);
            using var current = Process.GetCurrentProcess();
            ValidateParent(parent, current, request);

            // 不用 CurrentUserOnly（它比较提权级别），也不允许 medium server 冒用高权限 client。
            using var pipe = new NamedPipeClientStream(".", request.PipeName, ClientRights, PipeOptions.Asynchronous,
                TokenImpersonationLevel.Identification, HandleInheritability.None);
            using (var admission = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                await pipe.ConnectAsync(admission.Token).ConfigureAwait(false);
                if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverPid)
                    || serverPid != (uint)request.ParentPid) return RejectedParentExit;
                ValidateParent(parent, current, request);
                var authorization = new byte[1];
                await pipe.ReadExactlyAsync(authorization, admission.Token).ConfigureAwait(false);
                if (authorization[0] != BeginMaintenance) return RejectedParentExit;
                // 父在 UAC/握手期间退出或 PID 重用，必须在事务开始之前拒绝。
                ValidateParent(parent, current, request);
            }

            begun = true;
            Result result;
            try
            {
                var service = new VoiceImeRegistrationService();
                result = await (request.Install ? service.RegisterAsync() : service.UnregisterAsync()).ConfigureAwait(false);
            }
            catch
            {
                result = UnknownResult("输入法维护流程出现异常。");
            }
            // 事务开始后不监控父生命期：UI 关闭/管道断开不能中断维护或补偿。
            using var reply = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await WriteResultAsync(pipe, result, reply.Token).ConfigureAwait(false);
            return result.Succeeded ? 0 : 1;
        }
        catch
        {
            // 不输出原始 Win32 异常、命令行、用户令牌或配置。
            return begun ? UnknownResultExit : RejectedParentExit;
        }
    }

    [SupportedOSPlatform("windows")]
    private static NamedPipeServerStream CreateServer(string pipeName, string sid)
    {
        // net10.0 的 System.IO.Pipes.AccessControl 引用已核实支持该工厂，无需额外包。
        // 显式 DACL 只给原用户 SID 和 LocalSystem 数据读写/属性查询/同步权限；
        // 不授予 Everyone/Administrators，不授予 CreateNewInstance 或更改 ACL 的权限。
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(new SecurityIdentifier(sid));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid), ClientRights, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            ClientRights, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 4096, 4096, security,
            HandleInheritability.None, (PipeAccessRights)0);
    }

    [SupportedOSPlatform("windows")]
    private static void ValidateParent(Process parent, Process current, WorkerRequest request)
    {
        // 先固定进程句柄，再查身份，避免仅凭 PID 接受已被重用的进程。
        var handle = parent.SafeHandle;
        if (parent.Id == current.Id || parent.HasExited
            || parent.StartTime.ToUniversalTime().Ticks != request.ParentStartTicks
            || parent.SessionId != current.SessionId || !SameExecutable(Environment.ProcessPath)
            || !SameExecutable(parent.MainModule?.FileName))
            throw new InvalidOperationException("维护父进程身份不符。");
        if (!OpenProcessToken(handle, 0x0008 /* TOKEN_QUERY */, out var token))
            throw new InvalidOperationException("无法核验维护父进程用户。");
        using (token)
        using (var parentIdentity = new WindowsIdentity(token.DangerousGetHandle()))
        {
            if (!string.Equals(parentIdentity.User?.Value, request.Sid, StringComparison.Ordinal) || parent.HasExited)
                throw new InvalidOperationException("维护父进程用户不符或已退出。");
        }
    }

    private static bool SameExecutable(string? path) => path != null
        && string.Equals(Path.GetFullPath(path), ExecutablePath, StringComparison.OrdinalIgnoreCase);

    // 无文件/注册表/进程访问的固定命令构建及参数解析，供隔离回归反射调用。
    private static ProcessStartInfo BuildStartInfo(bool install, string sid, string pipeName, int parentPid, long parentStartTicks)
    {
        var arguments = new[] { Switch, install ? "install" : "uninstall", sid, pipeName,
            parentPid.ToString(CultureInfo.InvariantCulture), parentStartTicks.ToString(CultureInfo.InvariantCulture) };
        if (!TryParseWorkerArguments(arguments, out _)) throw new ArgumentException("输入法维护参数无效。");
        var info = new ProcessStartInfo
        {
            FileName = ExecutablePath, WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = true, Verb = "runas"
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    private static bool TryParseWorkerArguments(string[]? args, out WorkerRequest? request)
    {
        request = null;
        if (args == null || args.Length != 6) return false;
        foreach (var arg in args) if (arg == null || arg.Length > 256) return false;
        if (args[0] != Switch || args[1] is not ("install" or "uninstall") || !IsCanonicalSid(args[2])
            || !args[3].StartsWith(PipePrefix, StringComparison.Ordinal) || args[3].Length != PipePrefix.Length + 32
            || !Guid.TryParseExact(args[3][PipePrefix.Length..], "N", out var nonce) || nonce == Guid.Empty
            || args[3][PipePrefix.Length..] != nonce.ToString("N")
            || !TryCanonicalNumber(args[4], out var pid) || pid == 0 || pid > int.MaxValue
            || !TryCanonicalNumber(args[5], out var ticks) || ticks == 0 || ticks > (ulong)DateTime.MaxValue.Ticks)
            return false;
        request = new(args[1] == "install", args[2], args[3], (int)pid, (long)ticks);
        return true;
    }

    private static bool IsCanonicalSid(string sid)
    {
        // 只解析 S-1-数字 authority-数字 subauthority，不接受别名/用户名/空格，
        // 不调用账户解析；真正的当前用户身份另由两个进程的 token 校验。
        var parts = sid.Split('-');
        if (parts.Length is < 4 or > 18 || parts[0] != "S" || parts[1] != "1") return false;
        for (var i = 2; i < parts.Length; i++)
            if (!TryCanonicalNumber(parts[i], out var value) || value > (i == 2 ? 0xFFFFFFFFFFFFUL : uint.MaxValue))
                return false;
        return true;
    }

    private static bool TryCanonicalNumber(string text, out ulong value)
        => ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && text == value.ToString(CultureInfo.InvariantCulture);

    // 唯一的启动注入点；回归传入抛出 Win32Exception(1223) 的纯委托，不调用 runas。
    private static Process? StartWorker(ProcessStartInfo info, Func<ProcessStartInfo, Process?> start, out Result? failure)
    {
        failure = null;
        try
        {
            var process = start(info);
            if (process != null) return process;
            failure = UnknownResult("无法取得管理员维护子进程身份。");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            failure = new(false, "已取消管理员授权；未执行本次维护，主窗口和当前任务已保留。");
        }
        catch
        {
            failure = new(false, "无法启动管理员维护流程；主窗口和当前任务已保留，未自动重试。");
        }
        return null;
    }

    private static Result ResultForEarlyExit(int exitCode) => exitCode switch
    {
        WrongUserExit => new(false, "UAC 使用了其他用户账户，已拒绝本次维护，未读取配置或修改登记。请使用当前用户授权；主窗口和当前任务已保留。"),
        NotAdministratorExit => new(false, "未取得管理员权限，未执行本次维护；主窗口和当前任务已保留。"),
        RejectedParentExit => new(false, "无法核验原程序身份或维护授权，未开始维护；主窗口和当前任务已保留。"),
        _ => UnknownResult("维护子进程已退出，但未收到完整结果。")
    };

    private static Result UnknownResult(string reason) => new(false,
        reason + "本次维护结果未知，不会自动重试或强制结束维护子进程；请稍后刷新状态核对，勿立即重复安装/卸载。主窗口和当前任务已保留。");

    private static async Task WriteResultAsync(Stream stream, Result result, CancellationToken token)
    {
        // 只序列化服务已友好化的 Result，不回传 Exception、原生 stdout 或任意诊断对象。
        var fallback = UnknownResult("维护返回消息超出通信上限或无效。");
        if (string.IsNullOrWhiteSpace(result.Message) || result.Message.Length > MaximumResultBytes) result = fallback;
        var payload = JsonSerializer.SerializeToUtf8Bytes(result, ResultJson);
        if (payload.Length > MaximumResultBytes) payload = JsonSerializer.SerializeToUtf8Bytes(fallback, ResultJson);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(payload, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private static async Task<Result> ReadResultAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumResultBytes) throw new InvalidDataException("维护结果长度无效。");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, token).ConfigureAwait(false);
        using var json = JsonDocument.Parse(StrictUtf8.GetString(payload), new JsonDocumentOptions { MaxDepth = 4 });
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("维护结果格式无效。");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!names.Add(property.Name) || property.Name is not ("Succeeded" or "Message" or "RequiresAdministrator" or "CleanupPending")
                || (property.Name != "Message" && property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)))
                throw new InvalidDataException("维护结果字段无效。");
        }
        if (!names.Contains("Succeeded") || !root.TryGetProperty("Message", out var message)
            || message.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(message.GetString()))
            throw new InvalidDataException("维护结果缺少必要字段。");
        // RequiresAdministrator 由服务的 Result init 属性承载；不靠 Message 文本判断权限。
        return root.Deserialize<Result>(ResultJson) ?? throw new InvalidDataException("维护结果为空。");
    }

    private sealed record WorkerRequest(bool Install, string Sid, string PipeName, int ParentPid, long ParentStartTicks);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint desiredAccess, out SafeAccessTokenHandle token);
}