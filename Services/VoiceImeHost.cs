using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services;

/// <summary>每次 F8 听写一个无界面进程。只读主配置，不启动主程序、不注册热键、不保存音频。</summary>
public static class VoiceImeHost
{
    private const int MaximumFrame = 65536;
    private static readonly JsonSerializerOptions ConfigJson = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public static async Task<int> RunAsync(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Length != 5 || args[1] != "--pipe" || args[3] != "--parent"
            || !int.TryParse(args[4], out var parentId) || parentId <= 0
            || !Regex.IsMatch(args[2], @"\ATrueFluentPro\.VoiceIme\." + parentId + @"\.[0-9]{1,10}\.[0-9a-fA-F]{32}\z"))
            return 2;
        try
        {
            using var parent = Process.GetProcessById(parentId);
            using var lifetime = new CancellationTokenSource();
            parent.EnableRaisingEvents = true;
            parent.Exited += (_, _) => lifetime.Cancel();
            if (parent.HasExited) return 2;
            await using var pipe = new NamedPipeServerStream(args[2], PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                connect.CancelAfter(TimeSpan.FromSeconds(10));
                await pipe.WaitForConnectionAsync(connect.Token).ConfigureAwait(false);
            }
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var actual)
                || actual != (uint)parentId) return 3;

            // 连接并认证父进程之后、创建锁/读取配置/采音之前检查固定维护文件。
            using var monitorStop = new CancellationTokenSource();
            var monitor = VoiceImeMaintenance.MonitorAsync(VoiceImeMaintenance.GatePath, lifetime, monitorStop.Token);
            try
            {
                VoiceImeMaintenance.Check(VoiceImeMaintenance.GatePath, lifetime);

                // 文件锁只约束本输入法实例；进程异常退出也由系统释放。
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrueFluentPro", "VoiceIme");
                VoiceImeMaintenance.GuardPath(Path.Combine(directory, "microphone.lock"));
                Directory.CreateDirectory(directory);
                FileStream microphoneLock;
                try { microphoneLock = new FileStream(Path.Combine(directory, "microphone.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException)
                {
                    await WriteFrameAsync(pipe, 'E', "另一个语音输入法实例正在使用麦克风，请先停止它。", lifetime.Token).ConfigureAwait(false);
                    return 4;
                }
                using (microphoneLock)
                {
                    VoiceImeMaintenance.Check(VoiceImeMaintenance.GatePath, lifetime);
                    VoiceImeMaintenance.RecordHost(directory);
                    var resolver = new SpeechResourceRuntimeResolver(new ModelRuntimeResolver());
                    var tokens = new AzureTokenProviderStore();
                    return await RunSessionAsync(pipe,
                        () => { VoiceImeMaintenance.Check(VoiceImeMaintenance.GatePath, lifetime); return LoadConfigurationAsync(); },
                        config =>
                        {
                            VoiceImeMaintenance.Check(VoiceImeMaintenance.GatePath, lifetime);
                            return new ChatVoiceInputFactory(resolver, new RealtimeConnectionSpecResolver(), tokens,
                                useConfiguredLanguage: true,
                                endSilenceTimeoutSeconds: config.VoiceIme.EndSilenceTimeoutSeconds).Create(config);
                        }, lifetime.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                monitorStop.Cancel();
                await monitor.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { return 0; }
        catch { return 1; } // 不记录配置、认证异常、识别正文或签名 URL。
    }

    private static async Task<AzureSpeechConfig> LoadConfigurationAsync()
    {
        var path = new ConfigurationService().GetConfigFilePath();
        // 不调用 LoadConfigAsync：该方法首次运行时会写默认配置，后台只允许读取。
        if (!File.Exists(path)) throw new InvalidOperationException("请先在主程序中配置语音输入法。");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > 16 * 1024 * 1024) throw new InvalidDataException();
        return await JsonSerializer.DeserializeAsync<AzureSpeechConfig>(stream, ConfigJson).ConfigureAwait(false)
            ?? throw new InvalidDataException();
    }

    /// <summary>只修改深拷贝；显式模型优先，否则使用单独指定/跟随的语音资源。</summary>
    public static AzureSpeechConfig CreateRuntimeConfiguration(AzureSpeechConfig source)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(source);
        AzureSpeechConfig config;
        try { config = JsonSerializer.Deserialize<AzureSpeechConfig>(bytes, ConfigJson) ?? throw new InvalidDataException(); }
        finally { Array.Clear(bytes); }
        config.EnsureSpeechResourcesBackfilledFromLegacy();
        config.EnsureSpeechSubscriptionsMigratedToEndpoints();
        config.EnsureEndpointVendorCredentialsMigrated();
        var settings = config.VoiceIme ??= new VoiceImeConfig();
        if (settings.ModelRef != null)
        {
            if (!new ModelRuntimeResolver().TryResolve(config, settings.ModelRef, ModelCapability.SpeechToText, out _, out _))
                throw new InvalidOperationException("输入法模型不可用，请在主程序设置中重新选择。");
            var resource = SpeechResource.CreateLegacyAiResource(settings.ModelRef, null, null)
                ?? throw new InvalidOperationException("输入法模型配置无效。");
            resource.Id = "voice-ime-runtime-model";
            config.SpeechResources.RemoveAll(r => r.Id == resource.Id);
            config.SpeechResources.Add(resource);
            config.ActiveSpeechResourceId = resource.Id;
        }
        else if (!string.IsNullOrWhiteSpace(settings.SpeechResourceId))
        {
            var resource = config.GetEffectiveSpeechResources().FirstOrDefault(r => r.Id == settings.SpeechResourceId);
            if (resource == null || !resource.IsEnabled || !resource.HasCapability(SpeechCapability.RealtimeSpeechToText))
                throw new InvalidOperationException("输入法语音资源不可用，请在主程序设置中重新选择。");
            config.ActiveSpeechResourceId = resource.Id;
        }
        config.SourceLanguage = settings.RecognitionLanguage;
        config.SelectedAudioDeviceId = settings.MicrophoneDeviceId;
        config.EnableRecording = false;
        config.UseInputForRecognition = true;
        config.UseOutputForRecognition = false;
        if (!double.IsFinite(settings.EndSilenceTimeoutSeconds) || settings.EndSilenceTimeoutSeconds is < 0.2 or > 3)
            throw new InvalidOperationException("输入法分句停顿应在 0.2 到 3 秒之间。");
        return config;
    }

    // 可注入识别工厂的定向验证入口；生产仍只从上方固定配置路径读取，不接受外部配置/密钥参数。
    internal static async Task<int> RunSessionAsync(Stream stream, Func<Task<AzureSpeechConfig>> load,
        Func<AzureSpeechConfig, IChatVoiceInputSession> create, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = lifetime.Token;
        var frames = Channel.CreateBounded<(char Kind, string Text)>(new BoundedChannelOptions(64)
        {
            SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait
        });
        IChatVoiceInputSession? voice = null;
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepting = 1;
        void Publish(char kind, string text)
        {
            if (Volatile.Read(ref accepting) == 0) return;
            if (!frames.Writer.TryWrite((kind, text))) lifetime.Cancel();
            if (kind == 'E') ready.TrySetResult(false);
        }
        var reader = ReadStopAsync();
        var writer = WriteResultsAsync();
        try
        {
            token.ThrowIfCancellationRequested();
            var config = CreateRuntimeConfiguration(await load().WaitAsync(token).ConfigureAwait(false));
            token.ThrowIfCancellationRequested();
            voice = create(config);
            voice.TextRecognized += OnText;
            voice.Failed += OnFailure;
            var starting = voice.StartAsync(token);
            // 有些识别器先发 Failed，再结束启动 Task；不能等待该 Task 才开始释放麦克风。
            _ = starting.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            await Task.WhenAny(starting, finished.Task).WaitAsync(token).ConfigureAwait(false);
            if (finished.Task.IsCompleted) return 1;
            await starting.ConfigureAwait(false);
            ready.TrySetResult(true);
            await finished.Task.WaitAsync(token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return 0; }
        catch
        {
            Publish('E', "语音输入启动失败，请在主程序中检查输入法资源、模型、登录和麦克风设置。");
            ready.TrySetResult(false);
            try { await writer.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch { }
            return 1;
        }
        finally
        {
            Interlocked.Exchange(ref accepting, 0);
            lifetime.Cancel();
            frames.Writer.TryComplete();
            if (voice != null)
            {
                voice.TextRecognized -= OnText;
                voice.Failed -= OnFailure;
                try { await voice.StopAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
                try { await voice.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false); } catch { }
            }
            try { await Task.WhenAll(reader, writer).WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false); } catch { }
        }

        void OnText(string text, bool final) => Publish(final ? 'F' : 'P', text);
        void OnFailure(string _) => Publish('E', "语音识别中断，请在主程序中检查资源和麦克风后重试。");

        async Task ReadStopAsync()
        {
            try
            {
                var command = new byte[5];
                await stream.ReadExactlyAsync(command, token).ConfigureAwait(false);
                // 除唯一 stop 指令外，任何输入/断连均结束当前听写，不提供通用远程控制接口。
            }
            catch { }
            finally { Interlocked.Exchange(ref accepting, 0); lifetime.Cancel(); }
        }
        async Task WriteResultsAsync()
        {
            try
            {
                if (await ready.Task.WaitAsync(token).ConfigureAwait(false))
                    await WriteFrameAsync(stream, 'R', "", token).ConfigureAwait(false);
                await foreach (var frame in frames.Reader.ReadAllAsync(token).ConfigureAwait(false))
                {
                    if (Volatile.Read(ref accepting) == 0) break;
                    await WriteFrameAsync(stream, frame.Kind, frame.Text, token).ConfigureAwait(false);
                    if (frame.Kind == 'E') { finished.TrySetResult(true); break; }
                }
            }
            catch { finished.TrySetResult(true); }
        }
    }

    // fixture 只替换门禁路径/配置/voice；生产路径在 RunAsync 固定，不对外提供新参数。
    internal static async Task<int> RunMaintainedSessionAsync(Stream stream, Func<Task<AzureSpeechConfig>> load,
        Func<AzureSpeechConfig, IChatVoiceInputSession> create, CancellationToken token, string maintenancePath)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var stop = new CancellationTokenSource();
        var monitor = VoiceImeMaintenance.MonitorAsync(maintenancePath, lifetime, stop.Token);
        try
        {
            return await RunSessionAsync(stream,
                () => { VoiceImeMaintenance.Check(maintenancePath, lifetime); return load(); },
                config => { VoiceImeMaintenance.Check(maintenancePath, lifetime); return create(config); }, lifetime.Token).ConfigureAwait(false);
        }
        finally { stop.Cancel(); await monitor.ConfigureAwait(false); }
    }

    private static async Task WriteFrameAsync(Stream stream, char kind, string text, CancellationToken token)
    {
        var size = Encoding.UTF8.GetByteCount(text) + 1;
        if (size > MaximumFrame) throw new InvalidDataException("识别片段过长。");
        var frame = new byte[4 + size];
        BinaryPrimitives.WriteInt32LittleEndian(frame, size);
        frame[4] = checked((byte)kind);
        Encoding.UTF8.GetBytes(text, frame.AsSpan(5));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        await stream.WriteAsync(frame, deadline.Token).ConfigureAwait(false);
        await stream.FlushAsync(deadline.Token).ConfigureAwait(false);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint processId);
}