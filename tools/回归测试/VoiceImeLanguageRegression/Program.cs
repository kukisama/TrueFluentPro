using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using TrueFluentPro.ViewModels.Settings;
using TrueFluentPro.Views;

internal static class Program
{
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    public static async Task<int> Main()
    {
        var proxyNames = new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY" };
        var proxyValues = proxyNames.Select(Environment.GetEnvironmentVariable).ToArray();
        foreach (var name in proxyNames.Take(3)) Environment.SetEnvironmentVariable(name, "http://127.0.0.1:9");
        Environment.SetEnvironmentVariable("NO_PROXY", "127.0.0.1,localhost");
        try
        {
            await OrderAndSourceAsync();
            await CancellationAsync(disposeOnly: false);
            await CancellationAsync(disposeOnly: true);
            await FailureAsync(empty: false);
            await FailureAsync(empty: true);
            await MissingModelAsync();
            await ExistingTextServiceAsync(fail: false);
            await ExistingTextServiceAsync(fail: true);
            await PersistenceAsync();
            Console.WriteLine("PASS: linked production adapter + language VM/window compiled; no main build, audio, user config or cloud calls.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            for (var index = 0; index < proxyNames.Length; index++)
                Environment.SetEnvironmentVariable(proxyNames[index], proxyValues[index]);
        }
    }

    private static async Task OrderAndSourceAsync()
    {
        var asr = new ChatVoiceInputSession();
        var firstStarted = Signal<bool>();
        var releaseFirst = Signal<string>();
        var done = Signal<bool>();
        var calls = new ConcurrentQueue<string>();
        var sources = new ConcurrentQueue<(string, bool)>();
        var outputs = new ConcurrentQueue<(string, bool)>();
        await using var voice = new VoiceImeTranslationSession(asr, async (text, token) =>
        {
            calls.Enqueue(text);
            if (text == "第一句") { firstStarted.SetResult(true); return await releaseFirst.Task.WaitAsync(token); }
            return "Second sentence.";
        });
        voice.SourceRecognized += (text, final) => sources.Enqueue((text, final));
        voice.TextRecognized += (text, final) => { outputs.Enqueue((text, final)); if (outputs.Count == 2) done.TrySetResult(true); };
        var level = 0d;
        voice.AudioLevelChanged += value => level = value;
        await voice.PrewarmAsync();
        Check(asr.Prewarms == 1, "prewarm must reach ASR");
        await voice.StartAsync();
        asr.Emit("第", false); asr.Emit("第一句", true);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        asr.Emit("", false); asr.Emit("第二", false); asr.Emit("第二句", true); asr.Level(0.6);
        Check(calls.Count == 1 && outputs.IsEmpty, "translation must stay serial and original partial must never escape");
        releaseFirst.SetResult("First sentence.");
        await done.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(outputs.ToArray().SequenceEqual(new[] { ("First sentence.", true), ("Second sentence.", true) }), "wrong translation order");
        Check(sources.Count == 5 && sources.First() == ("第", false) && level == 0.6, "source/audio forwarding failed");
        await voice.StopAsync();
        Check(voice.ResourcesReleased.IsCompletedSuccessfully, "release signal not forwarded");
        Console.WriteLine("PASS: serial finals, raw source partial/empty/final, no original output, audio and lifecycle forwarding.");
    }

    private static async Task CancellationAsync(bool disposeOnly)
    {
        var asr = new ChatVoiceInputSession();
        var started = Signal<bool>();
        var late = Signal<string>();
        var requestToken = CancellationToken.None;
        var calls = 0;
        var outputs = 0;
        var sourceCount = 0;
        var voice = new VoiceImeTranslationSession(asr, (_, token) =>
        { requestToken = token; Interlocked.Increment(ref calls); started.TrySetResult(true); return late.Task; });
        voice.TextRecognized += (_, _) => Interlocked.Increment(ref outputs);
        voice.SourceRecognized += (_, _) => Interlocked.Increment(ref sourceCount);
        await voice.StartAsync();
        asr.Emit("在途", true);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        asr.Emit("排队", true);
        if (disposeOnly) await voice.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        else await voice.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        late.SetResult("Must not be inserted.");
        asr.Emit("迟到", true);
        await voice.DisposeAsync(); await voice.DisposeAsync();
        Check(requestToken.IsCancellationRequested && calls == 1 && outputs == 0 && sourceCount == 2, "cancel must discard in-flight, queued and late results");
        Check(asr.Stops == 1 && asr.Disposes == 1, "cleanup must be idempotent");
        Console.WriteLine($"PASS: {(disposeOnly ? "Dispose" : "Stop")} cancels real request token, ignores non-cooperative completion, drops queued sentences.");
    }

    private static async Task FailureAsync(bool empty)
    {
        var asr = new ChatVoiceInputSession();
        var failure = Signal<string>();
        var outputs = 0;
        await using var voice = new VoiceImeTranslationSession(asr, (_, _) => empty
            ? Task.FromResult(" \n ") : Task.FromException<string>(new Exception("SECRET_REQUEST_URL_AND_ORIGINAL")));
        voice.TextRecognized += (_, _) => outputs++;
        voice.Failed += text => failure.TrySetResult(text);
        await voice.StartAsync();
        asr.Emit("原文不能回退", true);
        var message = await failure.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await voice.StopAsync();
        Check(outputs == 0 && !message.Contains("SECRET") && !message.Contains("原文不能回退"), "failed translation leaked source or exception");
        Console.WriteLine($"PASS: {(empty ? "empty translation" : "request failure")} reports sanitized error, never falls back to original.");
    }

    private static async Task MissingModelAsync()
    {
        var asr = new ChatVoiceInputSession();
        var failure = Signal<string>();
        await using var voice = new VoiceImeTranslationSession(asr, new AzureSpeechConfig());
        voice.Failed += text => failure.TrySetResult(text);
        try { await voice.StartAsync(); throw new Exception("missing model incorrectly succeeded"); }
        catch (InvalidOperationException) { }
        var message = await failure.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(asr.Starts == 0 && message.Contains("文本模型"), "missing model must fail before microphone start");
        Console.WriteLine("PASS: missing text model fails before ASR start; no fake enabled translation.");
    }

    private static async Task ExistingTextServiceAsync(bool fail)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var address = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        var endpoint = new AiEndpoint
        {
            Id = "local-fixture", BaseUrl = address, ApiKey = "fixture-not-a-secret",
            TextApiProtocolMode = TextApiProtocolMode.ChatCompletionsV1,
            Models = new() { new() { ModelId = "fixture-text", Capabilities = ModelCapability.Text } }
        };
        var config = new AzureSpeechConfig
        {
            SourceLanguage = "zh-CN", TargetLanguage = "ja-JP", Endpoints = new() { endpoint },
            AiConfig = new AiConfig { QuickModelRef = new ModelReference { EndpointId = endpoint.Id, ModelId = "fixture-text" } }
        };
        var server = ServeAsync();
        var asr = new ChatVoiceInputSession();
        await using var voice = new VoiceImeTranslationSession(asr, config);
        // 创建后再改调用方配置，实际请求必须保留原语言、模型与凭据快照。
        config.SourceLanguage = "fr-FR"; config.TargetLanguage = "de-DE";
        endpoint.BaseUrl = "http://127.0.0.1:1"; endpoint.ApiKey = "changed-after-create";
        var result = Signal<string>();
        var outputs = 0;
        voice.TextRecognized += (text, final) => { outputs++; Check(final, "production adapter exposed non-final output"); result.TrySetResult(text); };
        voice.Failed += text => result.TrySetResult(text);
        await voice.StartAsync(budget.Token);
        asr.Emit("测试原句", true);
        var actual = await result.Task.WaitAsync(budget.Token);
        var body = await server.WaitAsync(budget.Token);
        await voice.StopAsync();
        var requestBody = JsonNode.Parse(body)!;
        var prompt = requestBody["messages"]![0]!["content"]!.GetValue<string>();
        Check(prompt.Contains("zh-CN") && prompt.Contains("ja-JP") && !prompt.Contains("de-DE")
            && requestBody["model"]!.GetValue<string>() == "fixture-text"
            && requestBody["messages"]![1]!["content"]!.GetValue<string>() == "测试原句", "request snapshot/prompt/model mismatch");
        Check(fail ? outputs == 0 && !actual.Contains("UPSTREAM_SECRET") : outputs == 1 && actual == "翻訳結果。", "existing text service result/error contract failed");
        Console.WriteLine($"PASS: existing ModelRuntimeResolver + AiInsightService loopback {(fail ? "503 sanitized, no fallback" : "SSE translated final, frozen source/target/model/key") }.");

        async Task<string> ServeAsync()
        {
            using var client = await listener.AcceptTcpClientAsync(budget.Token);
            await using var stream = client.GetStream();
            using var headerBytes = new MemoryStream();
            var one = new byte[1];
            while (true)
            {
                await stream.ReadExactlyAsync(one, budget.Token);
                headerBytes.WriteByte(one[0]);
                var header = Encoding.ASCII.GetString(headerBytes.GetBuffer(), 0, (int)headerBytes.Length);
                Check(headerBytes.Length < 32768, "oversized fixture headers");
                if (!header.EndsWith("\r\n\r\n")) continue;
                Check(header.Contains("fixture-not-a-secret") && !header.Contains("changed-after-create"), "credential snapshot was not frozen");
                var lengthHeader = header.Split("\r\n").Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                var payload = new byte[int.Parse(lengthHeader.Split(':')[1])];
                await stream.ReadExactlyAsync(payload, budget.Token);
                var responseBody = fail ? "UPSTREAM_SECRET" : "data: {\"choices\":[{\"delta\":{\"content\":\"翻訳結果。\"}}]}\n\ndata: [DONE]\n\n";
                var response = Encoding.UTF8.GetBytes(responseBody);
                var status = fail ? "503 Service Unavailable" : "200 OK";
                var responseHeader = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/event-stream\r\nContent-Length: {response.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(responseHeader, budget.Token);
                await stream.WriteAsync(response, budget.Token);
                await stream.FlushAsync(budget.Token);
                return Encoding.UTF8.GetString(payload);
            }
        }
    }

    private static async Task PersistenceAsync()
    {
        var settings = JsonSerializer.Deserialize<VoiceImeConfig>("{}")!;
        Check(!settings.EnableTranslation && settings.TargetLanguage == "en-US", "legacy defaults changed");
        var vm = new VoiceImeLanguageViewModel();
        var changes = 0;
        vm.Changed += () => changes++;
        vm.LoadFrom(settings);
        Check(changes == 0 && vm.SelectedSource?.Code == "auto" && vm.SelectedTarget?.Code == "en-US", "load must not dirty VM");
        vm.EnableTranslation = true;
        vm.SelectedSource = vm.SourceLanguages.Single(option => option.Code == "zh-CN");
        vm.SelectedTarget = vm.TargetLanguages.Single(option => option.Code == "ja-JP");
        var draft = vm.CreateDraft();
        var directory = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "config.json");
        // 模拟窗口持有旧草稿期间，另一个窗口已保存模型/设备与未知字段。
        const string latest = """
        {"SourceLanguage":"fr-FR","TargetLanguage":"de-DE","Endpoints":[{"Id":"latest-model"}],"Opaque":{"keep":42},
         "voiceime":{"MicrophoneDeviceId":"new-mic","SpeechResourceId":"new-resource","EndSilenceTimeoutSeconds":1.2,"FutureField":true}}
        """;
        await File.WriteAllTextAsync(path, latest);
        await VoiceImeLanguageWindow.SaveSettingsAsync(path, draft);
        var actual = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        var ime = actual["voiceime"]!;
        Check(actual["SourceLanguage"]!.GetValue<string>() == "fr-FR" && actual["TargetLanguage"]!.GetValue<string>() == "de-DE", "main language overwritten");
        Check(actual["Endpoints"]![0]!["Id"]!.GetValue<string>() == "latest-model" && actual["Opaque"]!["keep"]!.GetValue<int>() == 42, "latest unrelated config lost");
        Check(ime["MicrophoneDeviceId"]!.GetValue<string>() == "new-mic" && ime["FutureField"]!.GetValue<bool>(), "IME non-language fields overwritten");
        Check(ime["RecognitionLanguage"]!.GetValue<string>() == "zh-CN" && ime["TargetLanguage"]!.GetValue<string>() == "ja-JP" && ime["EnableTranslation"]!.GetValue<bool>(), "three-field patch failed");
        Check(await File.ReadAllTextAsync(path + ".bak") == latest, "backup must contain latest shared config");
        var valid = await File.ReadAllBytesAsync(path);
        try { await VoiceImeLanguageWindow.SaveSettingsAsync(path, new VoiceImeConfig { TargetLanguage = "auto" }); throw new Exception("invalid target accepted"); }
        catch (InvalidOperationException) { }
        var afterInvalid = await File.ReadAllBytesAsync(path);
        Check(valid.SequenceEqual(afterInvalid), "invalid target changed config");
        Console.WriteLine("PASS: false/en-US defaults, VM draft, latest config + unknown fields preserved, three fields patched, backup and invalid-target refusal.");
    }
}

namespace TrueFluentPro.Services
{
    // 仅替代原程序 internal ASR 类用于生命周期测试；公共 ASR 接口、翻译服务与窗口 API 均来自真实依赖。
    internal sealed class ChatVoiceInputSession : IChatVoiceInputSession
    {
        public event Action<string, bool>? TextRecognized;
        public event Action<string>? Failed;
        public event Action<double>? AudioLevelChanged;
        public int Starts, Stops, Disposes, Prewarms;
        private readonly TaskCompletionSource<bool> _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task ResourcesReleased => _released.Task;
        internal Task PrewarmAsync(CancellationToken token) { Prewarms++; return Task.CompletedTask; }
        public void Emit(string text, bool final) => TextRecognized?.Invoke(text, final);
        public void Level(double value) => AudioLevelChanged?.Invoke(value);
        public void Fail(string value) => Failed?.Invoke(value);
        public Task StartAsync(CancellationToken token = default) { Starts++; token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken token = default) { Interlocked.Increment(ref Stops); _released.TrySetResult(true); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Interlocked.Increment(ref Disposes); return ValueTask.CompletedTask; }
    }
}