using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Security.Principal;
using TrueFluentPro.Services;
using static Program;
using Result = TrueFluentPro.Services.VoiceImeRegistrationService.Result;

/// <summary>纯命令/内存协议与临时本地管道；不启动进程、UAC、UI 或登记操作。</summary>
internal static class ElevationRegression
{
    private static readonly Type Elevation = typeof(VoiceImeRegistrationService).Assembly
        .GetType("TrueFluentPro.Services.VoiceImeMaintenanceElevation", throwOnError: true)!;
    private const string Switch = "--voice-ime-maintenance";
    private const string Sid = "S-1-5-21-111-222-333-1001";
    private const string Pipe = "TrueFluentPro.VoiceIme.Maintenance.0123456789abcdef0123456789abcdef";
    private const long Ticks = 639293472000000000;
    private const int Limit = 32 * 1024;

    internal static async Task VerifyAsync()
    {
        var workerBefore = Elevation.GetProperty("IsWorker", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null);
        foreach (var install in new[] { true, false })
        {
            var info = (ProcessStartInfo)Call("BuildStartInfo", install, Sid, Pipe, 123, Ticks)!;
            Check(info.FileName == Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "TrueFluentPro.exe"))
                && Path.IsPathFullyQualified(info.FileName) && info.UseShellExecute && info.Verb == "runas"
                && info.WorkingDirectory == AppContext.BaseDirectory && info.Arguments.Length == 0
                && !info.RedirectStandardOutput && !info.RedirectStandardError,
                "维护命令不是当前包固定 exe / runas / ArgumentList");
            var expected = new[] { Switch, install ? "install" : "uninstall", Sid, Pipe, "123", Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            Check(info.ArgumentList.SequenceEqual(expected) && Parses(expected), "固定维护参数不能往返解析");
        }
        var valid = new[] { Switch, "install", Sid, Pipe, "123", Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        Check(!Parses(null) && !Parses([]) && !Parses(valid[..^1])
            && !Parses([.. valid, "--voice-ime-host"]) && !Parses([.. valid, "uninstall"]), "允许缺参/多参或混合模式");
        foreach (var (index, text) in new (int, string)[]
        {
            (0, "--voice-ime-host"), (1, "refresh"), (1, "INSTALL"), (1, "install uninstall"),
            (2, "current-user"), (2, "BA"), (2, "S-1-5-21-1-2-3-4294967296"), (2, "S-1-5-21-01-2-3-1001"),
            (2, "S-1-281474976710656-1"), (2, Sid + " "), (2, Sid + "\0"),
            (3, "\\\\remote\\pipe\\" + Pipe), (3, "../" + Pipe), (3, Pipe.ToUpperInvariant()),
            (3, "TrueFluentPro.VoiceIme.Maintenance." + new string('0', 32)),
            (4, "0"), (4, "-1"), (4, "0123"), (4, "2147483648"), (5, "0"), (5, "3155378976000000000")
        })
        {
            var bad = valid.ToArray();
            bad[index] = text;
            Check(!Parses(bad), "接受了错误 SID/管道/操作/父进程参数，位置=" + index);
        }
        Check(Equals(workerBefore, Elevation.GetProperty("IsWorker", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)),
            "纯解析意外进入了 worker 模式");
        Note("PASS elevation/fixed-command-and-strict-arguments (no path/script/extra operation accepted)");

        var command = (ProcessStartInfo)Call("BuildStartInfo", true, Sid, Pipe, 123, Ticks)!;
        var attempts = 0;
        Func<ProcessStartInfo, Process?> cancel = info =>
        {
            attempts++;
            Check(ReferenceEquals(info, command), "启动注入收到其他命令");
            throw new Win32Exception(1223, ErrorCanary);
        };
        object?[] launch = [command, cancel, null];
        Check(Call("StartWorker", launch) == null && launch[2] is Result { Succeeded: false } cancelled
            && cancelled.Message.Contains("已取消") && cancelled.Message.Contains("主窗口和当前任务已保留")
            && !cancelled.Message.Contains(ErrorCanary) && attempts == 1, "UAC 1223 未友好映射或发生自动重试");
        var wrongUser = (Result)Call("ResultForEarlyExit", 3)!;
        Check(!wrongUser.Succeeded && wrongUser.Message.Contains("其他用户") && wrongUser.Message.Contains("未读取配置或修改登记"),
            "另一管理员账户退出没有友好拒绝");
        Note("PASS elevation/UAC-cancel-injected-once-and-wrong-user-exit (real start calls=0)");

        using var deadline = new CancellationTokenSource(Deadline);
        foreach (var result in new[] { new Result(true, "输入法安装完成；主窗口保留。"), new Result(false, "未完成维护。") })
        {
            using var output = new MemoryStream();
            await WriteAsync(output, result, deadline.Token);
            var bytes = output.ToArray();
            Check(BinaryPrimitives.ReadInt32LittleEndian(bytes) == bytes.Length - 4 && bytes.Length <= Limit + 4,
                "结果帧长度不是有界小端字节数");
            using var fragmented = new FragmentedStream(bytes);
            Check(await ReadAsync(fragmented, deadline.Token) == result, "中文 Result 分片读取后不一致");
        }
        // 精确 32 KiB 可接收，超限长度必须在读取正文之前拒绝。
        var empty = Encoding.UTF8.GetByteCount("{\"Succeeded\":true,\"Message\":\"\"}");
        using (var boundary = new MemoryStream(Frame(Encoding.UTF8.GetBytes("{\"Succeeded\":true,\"Message\":\""
            + new string('x', Limit - empty) + "\"}"))))
            Check((await ReadAsync(boundary, deadline.Token)).Succeeded, "恰好 32 KiB 的结果被错误拒绝");
        foreach (var length in new[] { -1, 0, Limit + 1, int.MaxValue })
        {
            var prefix = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(prefix, length);
            using var invalidLength = new MemoryStream(prefix);
            await RejectAsync<InvalidDataException>(() => ReadAsync(invalidLength, deadline.Token), "未拒绝异常帧长度");
        }
        foreach (var body in new[]
        {
            "null", "{}", "{\"Message\":\"x\"}", "{\"Succeeded\":true,\"Message\":null}",
            "{\"Succeeded\":true,\"Message\":\"x\",\"Succeeded\":false}",
            "{\"Succeeded\":true,\"Message\":\"x\",\"script\":\"not-accepted\"}"
        })
        {
            using var malformed = new MemoryStream(Frame(Encoding.UTF8.GetBytes(body)));
            await RejectAsync<InvalidDataException>(() => ReadAsync(malformed, deadline.Token), "未拒绝缺字段/重复字段/任意字段");
        }
        using (var badUtf8 = new MemoryStream(Frame([0xFF])))
            await RejectAsync<DecoderFallbackException>(() => ReadAsync(badUtf8, deadline.Token), "未拒绝无效 UTF8");
        using (var partial = new MemoryStream(Frame(Encoding.UTF8.GetBytes("{}"))[..^1]))
            await RejectAsync<EndOfStreamException>(() => ReadAsync(partial, deadline.Token), "截断帧没有立即结束");
        using (var oversized = new MemoryStream())
        {
            await WriteAsync(oversized, new Result(true, new string('中', Limit)), deadline.Token);
            Check(oversized.Length <= Limit + 4, "写入结果超过 32 KiB");
            oversized.Position = 0;
            var unknown = await ReadAsync(oversized, deadline.Token);
            Check(!unknown.Succeeded && unknown.Message.Contains("结果未知"), "超限结果被静默截断或冒充成功");
        }
        using (var blocked = new BlockedStream())
        using (var cancellation = new CancellationTokenSource())
        {
            var pending = ReadAsync(blocked, cancellation.Token);
            cancellation.Cancel();
            await RejectAsync<OperationCanceledException>(async () => await pending.WaitAsync(Deadline), "读取未遵守取消边界");
        }
        Note("PASS elevation/bounded-UTF8-JSON-frames-and-cancellation (memory only; no maintenance or system access)");
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var name = "TrueFluentPro.VoiceIme.Maintenance." + Guid.NewGuid().ToString("N");
            using var server = (NamedPipeServerStream)Call("CreateServer", name, identity.User!.Value)!;
            var rights = (PipeAccessRights)(int)Elevation.GetField("ClientRights", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
            using var client = new NamedPipeClientStream(".", name, rights, PipeOptions.Asynchronous,
                TokenImpersonationLevel.Identification, HandleInheritability.None);
            using var pipeTimeout = new CancellationTokenSource(Deadline);
            await Task.WhenAll(server.WaitForConnectionAsync(pipeTimeout.Token), client.ConnectAsync(pipeTimeout.Token));
            var result = new Result(true, "维护结果回传") { CleanupPending = true };
            var receive = ReadAsync(server, pipeTimeout.Token);
            await WriteAsync(client, result, pipeTimeout.Token);
            Check(await receive == result, "真实当前用户 ACL 管道未传回维护结果");
            Note("PASS elevation/real-current-user-ACL-pipe-roundtrip (same token only; no UAC or system maintenance)");
        }
    }

    private static bool Parses(string[]? arguments)
    {
        object?[] values = [arguments, null];
        return (bool)Call("TryParseWorkerArguments", values)!;
    }

    private static object? Call(string name, params object?[] arguments)
    {
        var method = Elevation.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new CheckFailure("缺少生产提权方法：" + name);
        try { return method.Invoke(null, arguments); }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
    }

    private static Task WriteAsync(Stream stream, Result result, CancellationToken token)
        => (Task)Call("WriteResultAsync", stream, result, token)!;
    private static Task<Result> ReadAsync(Stream stream, CancellationToken token)
        => (Task<Result>)Call("ReadResultAsync", stream, token)!;
    private static byte[] Frame(byte[] payload)
    {
        var bytes = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, payload.Length);
        payload.CopyTo(bytes, 4);
        return bytes;
    }

    private static async Task RejectAsync<T>(Func<Task> action, string message) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new CheckFailure(message);
    }

    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], cancellationToken);
    }

    private sealed class BlockedStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
    }
}