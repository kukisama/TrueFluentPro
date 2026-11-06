using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TrueFluentPro.Models;
using TrueFluentPro.Services;

internal static class Program
{
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(4);
    internal const string ConfigCanary = "SYNTHETIC_CONFIG_ONLY_CANARY";
    internal const string KeyCanary = "SYNTHETIC_KEY_NEVER_AUTHENTICATE";
    internal const string ErrorCanary = "SYNTHETIC_RAW_EXCEPTION_CANARY";
    private static readonly List<object> Results = new();
    private static StreamWriter? _log;
    internal static string Artifacts = "";

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../.."));
        var repo = Path.GetFullPath(Path.Combine(project, "../.."));
        var hostDeploymentOnly = args.SequenceEqual(new[] { "--host-deployment" });
        Artifacts = Path.Combine(hostDeploymentOnly ? Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "voice-ime-host-deployment")
            : Path.Combine(project, "artifacts"), DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Artifacts);
        using var log = new StreamWriter(Path.Combine(Artifacts, "run.log"), false, new UTF8Encoding(false)) { AutoFlush = true };
        _log = log;
        var failures = 0;
        if (hostDeploymentOnly)
            await Group("host deployment file fixture only (no processes/registration/native helpers)", HostDeploymentRegression.VerifyAsync);
        else if (args.SequenceEqual(new[] { "--shared" }))
            await Group("shared owner fixture only (duplex pipes, fake voice/auth/UI)", SharedSessionRegression.VerifyAsync);
        else if (args.SequenceEqual(new[] { "--usage" }))
            await Group("IME usage only (repository-isolated SQLite, fake voice/auth/UI/clock)", UsageRegression.VerifyAsync);
        else if (args.SequenceEqual(new[] { "--lifecycle" }))
            await Group("lifecycle fixture only (no native helper/system registration)", LifecycleRegression.VerifyAsync);
        else
        {
        await Group("1 configuration and production payloads", VerifyConfigurationAsync);
        await Group("2 actual duplex pipe, ready ordering and stop isolation", PipeRegression.VerifyDuplexAsync);
        await Group("3 connecting stop/error and resource lifetime", PipeRegression.VerifyConnectingAsync);
        await Group("4 registration guards, package identity and memory-only diagnostics", RegistrationRegression.VerifyAsync);
        await Group("entry invalid arguments, exit 2 and no splash/main window", () =>
        {
            NativeEntry.Verify(Path.Combine(repo, "bin", "Debug", "net10.0", "TrueFluentPro.exe"));
            return Task.CompletedTask;
        });
        }
        var exit = failures == 0 ? 0 : 1;
        File.WriteAllText(Path.Combine(Artifacts, "results.json"), JsonSerializer.Serialize(new
        {
            startedFrom = Directory.GetCurrentDirectory(), productionAssembly = typeof(VoiceImeHost).Assembly.Location,
            exitCode = exit, groups = Results, constraints = hostDeploymentOnly
                ? "file-only helper; synthetic files retained under repository artifacts; no process queries/start/stop, registry, registration/unregistration, native helpers, UAC, user config, microphone or cloud"
                : "synthetic configuration; fake voice only; registration denial and fixture-only no-footprint cleanup; no system registration/unregistration, helper execution, UAC, user config, token, microphone or cloud"
        }, new JsonSerializerOptions { WriteIndented = true }));
        Note($"TEST_EXIT={exit}");
        Note($"ARTIFACTS={Artifacts}");
        return exit;

        async Task Group(string name, Func<Task> test)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                await test();
                Results.Add(new { name, status = "PASS", elapsedMs = watch.ElapsedMilliseconds });
                Note("PASS " + name);
            }
            catch (Exception ex)
            {
                while (ex is TargetInvocationException { InnerException: { } inner }) ex = inner;
                // 不把未经审查的生产异常或配置快照写进测试日志。
                var reason = ex is CheckFailure ? ex.Message : ex.GetType().FullName;
                failures++;
                Results.Add(new { name, status = "FAIL", reason, elapsedMs = watch.ElapsedMilliseconds });
                Note($"FAIL {name}: {reason}");
            }
        }
    }

    internal static void Note(string message) { Console.WriteLine(message); _log?.WriteLine(message); }
    internal static void Check(bool value, string message) { if (!value) throw new CheckFailure(message); }
    internal sealed class CheckFailure(string message) : Exception(message);
    internal static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static FieldInfo Field(Type type, string name)
        => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new CheckFailure("缺少生产字段：" + name);
    internal static MethodInfo Method(Type type, string name)
        => type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new CheckFailure("缺少生产方法：" + name);

    internal static AzureSpeechConfig Configuration() => new()
    {
        SpeechSubscriptionsMigratedToEndpoints = true,
        ActiveSpeechResourceId = "speech-main", SourceLanguage = "ja-JP", SelectedAudioDeviceId = "main-device",
        EndSilenceTimeoutSeconds = 2, EnableRecording = true, UseInputForRecognition = false, UseOutputForRecognition = true,
        Endpoints = new()
        {
            new() { Id = "test-openai", Name = ConfigCanary, EndpointType = EndpointApiType.OpenAiCompatible,
                BaseUrl = "https://api.openai.com/v1", ApiKey = KeyCanary,
                Models = new() { new() { ModelId = "whisper-1", Capabilities = ModelCapability.SpeechToText },
                    new() { ModelId = "gpt-4o-transcribe", Capabilities = ModelCapability.SpeechToText } } }
        },
        SpeechResources = new()
        {
            Resource("speech-main", "whisper-1"), Resource("speech-ime", "gpt-4o-transcribe")
        },
        VoiceIme = new() { SpeechResourceId = "speech-ime", RecognitionLanguage = "en-US",
            MicrophoneDeviceId = "ime-device", EndSilenceTimeoutSeconds = 0.8 }
    };

    private static SpeechResource Resource(string id, string model) => new()
    {
        Id = id, Name = id, ConnectorType = SpeechConnectorType.AiSpeech, Vendor = SpeechVendorType.OpenAI,
        Capabilities = SpeechCapability.RealtimeSpeechToText,
        RealtimeSpeechToTextModelRef = new() { EndpointId = "test-openai", ModelId = model }
    };

    private static async Task VerifyConfigurationAsync()
    {
        var source = Configuration();
        var before = JsonSerializer.Serialize(source);
        var runtime = VoiceImeHost.CreateRuntimeConfiguration(source);
        Check(JsonSerializer.Serialize(source) == before, "创建 runtime 修改了主配置");
        Check(runtime.ActiveSpeechResourceId == "speech-ime" && source.ActiveSpeechResourceId == "speech-main",
            "独立资源选择影响了主程序或未选择指定资源");
        Check(!ReferenceEquals(source, runtime) && !ReferenceEquals(source.VoiceIme, runtime.VoiceIme)
            && !ReferenceEquals(source.Endpoints, runtime.Endpoints) && !ReferenceEquals(source.Endpoints[0], runtime.Endpoints[0])
            && !ReferenceEquals(source.Endpoints[0].Models[0], runtime.Endpoints[0].Models[0])
            && !ReferenceEquals(source.SpeechResources[0], runtime.SpeechResources[0])
            && !ReferenceEquals(source.SpeechResources[0].RealtimeSpeechToTextModelRef, runtime.SpeechResources[0].RealtimeSpeechToTextModelRef),
            "配置存在共享嵌套引用，不是深拷贝");
        Check(runtime.SourceLanguage == "en-US" && runtime.SelectedAudioDeviceId == "ime-device"
            && runtime.VoiceIme.EndSilenceTimeoutSeconds == 0.8 && runtime.EndSilenceTimeoutSeconds == 2
            && !runtime.EnableRecording && runtime.UseInputForRecognition && !runtime.UseOutputForRecognition,
            "语言/设备/分句或仅麦克风不录制配置不正确");
        var resolver = new SpeechResourceRuntimeResolver(new ModelRuntimeResolver());
        Check(resolver.TryResolveActive(runtime, SpeechCapability.RealtimeSpeechToText, out var selected, out _)
            && selected?.AiRuntime?.ModelId == "gpt-4o-transcribe", "真实注册表无法解析指定资源");
        Check(new RealtimeConnectionSpecResolver().TryResolve(selected!.AiRuntime!, out var actualSpec, out _)
            && actualSpec?.RouteKind == RealtimeEndpointRouteKind.OpenAiGa
            && actualSpec.SessionMode == RealtimeSessionMode.Transcription, "真实模型/endpoint 不可用作实时转写");
        runtime.Endpoints[0].Models[0].ModelId = "changed-in-clone";
        runtime.SpeechResources[0].RealtimeSpeechToTextModelRef!.ModelId = "changed-in-clone";
        runtime.VoiceIme.RecognitionLanguage = "ko-KR";
        Check(JsonSerializer.Serialize(source) == before, "修改 runtime 嵌套对象污染主配置");

        source.VoiceIme.ModelRef = new() { EndpointId = "test-openai", ModelId = "whisper-1" };
        before = JsonSerializer.Serialize(source);
        var overridden = VoiceImeHost.CreateRuntimeConfiguration(source);
        Check(overridden.ActiveSpeechResourceId == "voice-ime-runtime-model"
            && resolver.TryResolveActive(overridden, SpeechCapability.RealtimeSpeechToText, out var model, out _)
            && model?.AiRuntime?.ModelId == "whisper-1"
            && overridden.SpeechResources.Single(r => r.Id == "speech-ime").RealtimeSpeechToTextModelRef!.ModelId == "gpt-4o-transcribe"
            && JsonSerializer.Serialize(source) == before, "ModelRef 覆盖改写共享资源/主配置，或覆盖未生效");
        source.VoiceIme.ModelRef = null;
        Check(VoiceImeHost.CreateRuntimeConfiguration(source).ActiveSpeechResourceId == "speech-ime", "模型覆盖泄漏到下一次 runtime");
        source.VoiceIme.SpeechResourceId = "missing-explicit-resource";
        before = JsonSerializer.Serialize(source);
        var rejected = false;
        try { VoiceImeHost.CreateRuntimeConfiguration(source); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected && JsonSerializer.Serialize(source) == before, "丢失显式资源被回退到其它有效资源，或改写主配置");
        source.VoiceIme.SpeechResourceId = "";
        Check(VoiceImeHost.CreateRuntimeConfiguration(source).ActiveSpeechResourceId == "speech-main", "跟随活动资源失败");
        Note("CONFIG source unchanged, nested mutation isolated, explicit selection/model override/missing-resource rejection/follow verified");

        var tokens = new NoToken();
        foreach (var language in new[] { "en-US", "auto" })
        {
            var config = Configuration();
            config.VoiceIme.RecognitionLanguage = language;
            var copy = VoiceImeHost.CreateRuntimeConfiguration(config);
            await using var voice = new ChatVoiceInputFactory(resolver, new RealtimeConnectionSpecResolver(), tokens,
                useConfiguredLanguage: true, endSilenceTimeoutSeconds: copy.VoiceIme.EndSilenceTimeoutSeconds).Create(copy);
            VerifyPayloads(voice, language, configured: true);
        }
        var chatConfig = Configuration();
        chatConfig.SourceLanguage = "en-US";
        before = JsonSerializer.Serialize(chatConfig);
        await using (var chat = new ChatVoiceInputFactory(resolver, new RealtimeConnectionSpecResolver(), tokens).Create(chatConfig))
        {
            VerifyPayloads(chat, "auto", configured: false);
            Check(JsonSerializer.Serialize(chatConfig) == before, "默认聊天 factory 修改了主配置");
        }
        Check(tokens.Attempts == 0, "构造/检查消息不应访问任何凭据");
        Note("PAYLOAD en-US=>en, auto omits language; IME server_vad=800ms; chat GA auto/semantic and preview original protocol; token calls=0");
    }

    private static void VerifyPayloads(IChatVoiceInputSession voice, string language, bool configured)
    {
        var type = voice.GetType();
        Check(((AzureSpeechConfig)Field(type, "_config").GetValue(voice)!).SourceLanguage == language, "factory 语言策略错误");
        Check(Field(type, "_startTask").GetValue(voice) == null && Field(type, "_audioSource").GetValue(voice) == null
            && Field(type, "_socket").GetValue(voice) == null, "纯消息检查意外启动采集或网络");
        foreach (var route in new[] { RealtimeEndpointRouteKind.OpenAiGa, RealtimeEndpointRouteKind.AzureOpenAiGa, RealtimeEndpointRouteKind.AzureOpenAiPreview })
        {
            var preview = route == RealtimeEndpointRouteKind.AzureOpenAiPreview;
            var payload = Method(type, "BuildSessionUpdate").Invoke(voice, new object[]
            {
                new RealtimeConnectionSpec { RouteKind = route, SessionMode = preview ? RealtimeSessionMode.Conversation : RealtimeSessionMode.Transcription }
            })!;
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(payload));
            var root = json.RootElement;
            var session = root.GetProperty("session");
            Check(root.GetProperty("type").GetString() == "session.update", "消息不是原有 session.update");
            var input = preview ? session : session.GetProperty("audio").GetProperty("input");
            var transcription = input.GetProperty(preview ? "input_audio_transcription" : "transcription");
            Check(language == "auto" ? !transcription.TryGetProperty("language", out _)
                : transcription.GetProperty("language").GetString() == "en", "ISO 语言字段错误");
            Check(transcription.GetProperty("model").GetString() == "whisper-1", "既有默认输入 ASR 模型改变");
            var vad = input.GetProperty("turn_detection");
            Check(!vad.GetProperty("create_response").GetBoolean() && !vad.GetProperty("interrupt_response").GetBoolean(), "允许模型回复/打断");
            Check(vad.GetProperty("type").GetString() == (configured || preview ? "server_vad" : "semantic_vad"), "VAD 协议改变");
            if (configured || preview)
                Check(preview ? vad.GetProperty("silence_duration_ms").GetString() == (configured ? "800" : "2000")
                    : vad.GetProperty("silence_duration_ms").GetInt32() == 800, "分句不是 800ms 或聊天 preview 原值");
            else Check(vad.GetProperty("eagerness").GetString() == "low" && !vad.TryGetProperty("silence_duration_ms", out _), "默认聊天 semantic 配置改变");
            if (preview)
                Check(session.GetProperty("input_audio_format").GetString() == "pcm16" && !session.TryGetProperty("audio", out _), "preview 不应改成 GA 嵌套协议");
            else Check(session.GetProperty("type").GetString() == "transcription"
                && input.GetProperty("format").GetProperty("rate").GetInt32() == 24000, "GA 转写/PCM 格式改变");
            var label = configured ? language : "default-chat";
            File.WriteAllText(Path.Combine(Artifacts, $"payload-{label}-{route}.json"), root.GetRawText());
        }
    }

    internal static string Digest(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}

internal sealed class NoToken : IAzureTokenProviderStore
{
    public int Attempts;
    public AzureTokenProvider GetProvider(string? profileKey = null) { Attempts++; throw new Program.CheckFailure("隔离验证禁止访问凭据"); }
    public Task<AzureTokenProvider?> GetAuthenticatedProviderAsync(string? profileKey, string? tenantId, string? clientId,
        CancellationToken cancellationToken = default) { Attempts++; throw new Program.CheckFailure("隔离验证禁止认证"); }
}