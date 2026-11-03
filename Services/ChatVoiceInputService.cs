using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using NAudio.CoreAudioApi;
using TrueFluentPro.Models;
using TrueFluentPro.Services.Audio;
using TrueFluentPro.Services.RealtimeSpeech.Asr;
using TrueFluentPro.Services.RealtimeSpeech.Composition;

namespace TrueFluentPro.Services;

public interface IChatVoiceInputFactory
{
    IChatVoiceInputSession Create(AzureSpeechConfig config);
}

public interface IChatVoiceInputSession : IAsyncDisposable
{
    /// <summary>当前句的完整原文快照；true 仅表示服务端定稿，不是累积的会话全文。</summary>
    event Action<string, bool>? TextRecognized;
    event Action<string>? Failed;
    event Action<double>? AudioLevelChanged;
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 无状态工厂可以复用；Create 每次返回独立、只启动一次的会话，不能将会话注册为 singleton。
/// 只做识别，不创建翻译服务、录音、字幕、会话文件或文本模型请求。
/// </summary>
public sealed class ChatVoiceInputFactory : IChatVoiceInputFactory
{
    private readonly ISpeechResourceRuntimeResolver _speechResources;
    private readonly IRealtimeConnectionSpecResolver _connectionSpecs;
    private readonly IAzureTokenProviderStore _tokens;
    private readonly bool _useConfiguredLanguage;
    private readonly double? _endSilenceTimeoutSeconds;

    public ChatVoiceInputFactory(
        ISpeechResourceRuntimeResolver speechResourceRuntimeResolver,
        IRealtimeConnectionSpecResolver realtimeConnectionSpecResolver,
        IAzureTokenProviderStore azureTokenProviderStore,
        bool useConfiguredLanguage = false,
        double? endSilenceTimeoutSeconds = null)
    {
        _speechResources = speechResourceRuntimeResolver ?? throw new ArgumentNullException(nameof(speechResourceRuntimeResolver));
        _connectionSpecs = realtimeConnectionSpecResolver ?? throw new ArgumentNullException(nameof(realtimeConnectionSpecResolver));
        _tokens = azureTokenProviderStore ?? throw new ArgumentNullException(nameof(azureTokenProviderStore));
        _useConfiguredLanguage = useConfiguredLanguage;
        _endSilenceTimeoutSeconds = endSilenceTimeoutSeconds;
    }

    public IChatVoiceInputSession Create(AzureSpeechConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        // 完整深拷贝（包含旧资源、迁移标记、endpoint、model/deployment 与 AAD 来源），不读写磁盘。
        // 不能只拷贝 GetActiveSpeechResource：AI/AAD 解析仍需要完整 endpoint 注册表。
        AzureSpeechConfig snapshot;
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(config);
            try
            {
                snapshot = JsonSerializer.Deserialize<AzureSpeechConfig>(bytes)
                    ?? throw new InvalidOperationException();
            }
            finally
            {
                Array.Clear(bytes);
            }
        }
        catch
        {
            // 不向调用方泄露可能包含凭据的序列化异常正文。
            throw new InvalidOperationException("无法创建语音输入配置快照，请重试。");
        }

        if (!_useConfiguredLanguage) snapshot.SourceLanguage = "auto";
        return new ChatVoiceInputSession(snapshot, _speechResources, _connectionSpecs, _tokens, _endSilenceTimeoutSeconds);
    }
}

/// <summary>
/// Microsoft: Continuous LID，覆盖界面已有的中/英/日/韩/法/德/西候选，不支持逐词切换。
/// 讯飞/百度: 沿用现有连接器的 auto 映射（中文），不声称具备任意语种自动识别。
/// OpenAI: transcription 使用当前资源的模型；conversation 的输入 ASR 固定 whisper-1，
/// 与实时翻译保持一致。不发送 response.create，所有 turn_detection 均禁止自动回答。
/// </summary>
internal sealed class ChatVoiceInputSession : IChatVoiceInputSession, IAsrUplink, IAsrResultObserver
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(8);
    private const string ConversationAsrModel = "whisper-1";
    private const int OpenAiSampleRate = 24000;

    private readonly AzureSpeechConfig _config;
    private readonly ISpeechResourceRuntimeResolver _speechResources;
    private readonly IRealtimeConnectionSpecResolver _connectionSpecs;
    private readonly IAzureTokenProviderStore _tokens;
    private readonly int? _endSilenceMilliseconds;
    private readonly object _stateLock = new();
    private readonly object _textLock = new();
    private readonly SemaphoreSlim _resourceGate = new(1, 1);
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _startupCancellation = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource<bool> _sessionReady = NewSignal();
    private readonly TaskCompletionSource<bool> _resourcesReleased = NewSignal();
    private readonly Dictionary<string, InputTurn> _turns = new(StringComparer.Ordinal);
    private readonly Queue<InputTurn> _turnOrder = new();
    private readonly HashSet<string> _speechResultIds = new(StringComparer.Ordinal);

    private Task? _startTask;
    private Task? _stopTask;
    private Task? _sendTask;
    private Task? _receiveTask;
    private Task? _microphoneMonitorTask;
    private WasapiPcm16AudioSource? _audioSource;
    private ClientWebSocket? _socket;
    private Channel<byte[]>? _audio;
    private SpeechConfig? _speechConfig;
    private AudioStreamFormat? _streamFormat;
    private PushAudioInputStream? _pushStream;
    private AudioConfig? _audioConfig;
    private AutoDetectSourceLanguageConfig? _autoLanguages;
    private SpeechRecognizer? _recognizer;
    private IAsrConnector? _connector;
    private RealtimeConnectionSpec? _spec;
    private string _inputModel = ConversationAsrModel;
    private bool _incrementalDeltas;
    private bool _recognizerStopRequested;
    private string? _microphoneId;
    private string _failureMessage = "语音输入连接失败，请检查当前语音资源及网络后重试。";
    private long _lastLevelTick;
    private long _lastAudioTick;
    private int _stopping;
    private int _disposed;
    private int _failureReported;
    private int _callbacksClosed;
    private int _acceptAudio;

    public ChatVoiceInputSession(
        AzureSpeechConfig config,
        ISpeechResourceRuntimeResolver speechResources,
        IRealtimeConnectionSpecResolver connectionSpecs,
        IAzureTokenProviderStore tokens,
        double? endSilenceTimeoutSeconds = null)
    {
        _config = config;
        _speechResources = speechResources;
        _connectionSpecs = connectionSpecs;
        _tokens = tokens;
        _endSilenceMilliseconds = endSilenceTimeoutSeconds.HasValue
            ? (int)Math.Clamp(endSilenceTimeoutSeconds.Value * 1000, 200, 3000) : null;
    }

    public event Action<string, bool>? TextRecognized;
    public event Action<string>? Failed;
    public event Action<double>? AudioLevelChanged;

    // 共享输入法下一轮等待真实后台清理；公开 Stop 的快速超时行为保持不变。
    internal Task ResourcesReleased => _resourcesReleased.Task;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_stopTask != null || _stopping != 0)
                throw new InvalidOperationException("语音输入会话已结束，请创建新的会话。");

            return _startTask ??= StartBoundedAsync(cancellationToken);
        }
    }

    private async Task StartBoundedAsync(CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _startupCancellation.Token, _lifetime.Token);
        budget.CancelAfter(StartTimeout);
        // WASAPI/SDK 构造、启动和后续释放都不在 UI 调用栈执行。
        var startToken = budget.Token;
        var worker = Task.Run(() => StartWorkerAsync(startToken));
        Observe(worker);
        try
        {
            await worker.WaitAsync(startToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var wasStopping = Volatile.Read(ref _stopping) != 0;
            if (!wasStopping)
            {
                var message = cancellationToken.IsCancellationRequested
                    ? "语音输入启动已取消。"
                    : ex is VoiceInputException safe ? safe.Message
                    : budget.IsCancellationRequested ? "语音输入启动超时，请检查网络及麦克风后重试。"
                    : "语音输入启动失败，请检查语音资源、登录状态及麦克风权限。";
                SignalFailure(message);
            }

            AbortTransport();
            // 即使 native 初始化迟到才返回，资源仍归本会话所有；排队的 cleanup 会接管它。
            // 不为了等待不支持取消的 native 调用而把公开 Start 卡到十秒以上。
            var cleanup = StopAsync();
            try { await cleanup.WaitAsync(TimeSpan.FromMilliseconds(800)).ConfigureAwait(false); }
            catch { /* cleanup 继续持有资源，直到后台 native 调用返回 */ }

            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException("语音输入启动已取消。", cancellationToken);
            if (wasStopping)
                throw new OperationCanceledException("语音输入已停止。");
            throw new InvalidOperationException(_failureMessage);
        }
    }

    private async Task StartWorkerAsync(CancellationToken cancellationToken)
    {
        await _resourceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_speechResources.TryResolveActive(_config, SpeechCapability.RealtimeSpeechToText,
                    out var runtime, out _) || runtime == null)
                throw new VoiceInputException("当前实时语音资源不可用，请在实时翻译设置中检查资源及识别模型。");

            var sampleRate = 16000;
            if (runtime.IsMicrosoftSpeech)
            {
                await OpenMicrosoftAsync(runtime, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _connector = SpeechRoleResolver.ResolveAsrConnector(runtime.Resource);
                Uri uri;
                if (_connector != null)
                {
                    sampleRate = _connector.SampleRate;
                    uri = _connector.BuildUri(runtime.Resource, _config);
                }
                else
                {
                    if (runtime.AiRuntime == null || !_connectionSpecs.TryResolve(runtime.AiRuntime, out _spec, out _)
                        || _spec == null || !_spec.UsesOpenAiRealtimeProtocol)
                        throw new VoiceInputException("当前资源无法按现有实时语音协议连接，请检查实时翻译的资源配置。");

                    sampleRate = OpenAiSampleRate;
                    uri = _spec.WebSocketUri; // 保留 resolver 的 Azure GA/preview 路由与身份传输。
                    _inputModel = _spec.SessionMode == RealtimeSessionMode.Transcription
                        ? (runtime.AiRuntime.IsAzureEndpoint
                            ? runtime.AiRuntime.EffectiveDeploymentName : runtime.AiRuntime.ModelId)
                        : ConversationAsrModel;
                    _inputModel = _inputModel.Trim();
                    // Azure 的任意 deployment 别名不能用来猜 ASR 的 delta 语义。
                    _incrementalDeltas = _spec.SessionMode == RealtimeSessionMode.Transcription
                        && runtime.AiRuntime.ModelId.Contains("transcribe", StringComparison.OrdinalIgnoreCase);
                }

                _socket = new ClientWebSocket();
                _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(3);
                _socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(5);
                if (_connector == null)
                    await AuthenticateSocketAsync(runtime.AiRuntime!, _spec!, cancellationToken).ConfigureAwait(false);
                await _socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
                _receiveTask = ReceiveAsync(_socket, _lifetime.Token);
                Observe(_receiveTask);
                if (_connector != null)
                {
                    await _connector.OnConnectedAsync(runtime.Resource, _config, this, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await SendJsonAsync(BuildSessionUpdate(_spec!), cancellationToken).ConfigureAwait(false);
                    // 配置确认之前不采集，不把音频交给默认 create_response=true 的会话。
                    await _sessionReady.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            _audio = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(128)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });
            _sendTask = SendAudioAsync(_audio.Reader, _lifetime.Token);
            Observe(_sendTask);

            // WasapiPcm16AudioSource 对失效 ID 会回退默认设备；先校验并固定实际输入 ID，
            // 避免错误地从另一只麦克风采集，也拒绝被误存为输入 ID 的 render endpoint。
            using (var enumerator = new MMDeviceEnumerator())
            using (var microphone = string.IsNullOrWhiteSpace(_config.SelectedAudioDeviceId)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia)
                : enumerator.GetDevice(_config.SelectedAudioDeviceId))
            {
                if (microphone.DataFlow != DataFlow.Capture || microphone.State != DeviceState.Active)
                    throw new VoiceInputException("配置的麦克风不可用，请重新选择可用的输入设备。");
                _microphoneId = microphone.ID;
            }

            _audioSource = new WasapiPcm16AudioSource(
                loopbackDeviceId: null,
                micDeviceId: _microphoneId,
                chunkDurationMs: Math.Clamp(_config.ChunkDurationMs, 20, 200),
                enableLoopback: false,
                enableMic: true,
                sampleRate: sampleRate);
            _audioSource.Pcm16ChunkReady += OnAudio;
            Volatile.Write(ref _acceptAudio, 1);
            Interlocked.Exchange(ref _lastAudioTick, Stopwatch.GetTimestamp());
            await _audioSource.StartAsync(_lifetime.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!_audioSource.HasMicCapture)
                throw new VoiceInputException("无法启动配置的麦克风，请检查设备及系统权限。");

            _microphoneMonitorTask = MonitorMicrophoneAsync(_lifetime.Token);
            Observe(_microphoneMonitorTask);
        }
        finally
        {
            _resourceGate.Release();
        }
    }

    private async Task OpenMicrosoftAsync(SpeechResourceRuntimeResolution runtime, CancellationToken cancellationToken)
    {
        if (runtime.IsAadMicrosoftSpeech)
        {
            var endpoint = _config.Endpoints.FirstOrDefault(e =>
                string.Equals(e.Id, runtime.Resource.AadEndpointId, StringComparison.OrdinalIgnoreCase));
            if (endpoint == null || !FoundrySpeechEndpointResolver.TryResolve(endpoint, out var derived, out _)
                || derived == null)
                throw new VoiceInputException("当前 Foundry Speech 的 AAD 来源无效，请在终结点设置中重新配置。");

            var provider = await _tokens.GetAuthenticatedProviderAsync(
                FoundrySpeechEndpointResolver.BuildEndpointProfileKey(endpoint.Id),
                endpoint.AzureTenantId, endpoint.AzureClientId, cancellationToken).ConfigureAwait(false);
            if (provider?.Credential == null)
                throw new VoiceInputException("当前语音资源尚未登录，请先在终结点设置中完成 AAD 登录。");

            var endpointUri = new UriBuilder(derived.ResourceEndpoint)
            {
                Scheme = "wss", Port = -1, Path = "/stt/speech/universal/v2", Query = string.Empty
            }.Uri;
            _speechConfig = SpeechConfig.FromEndpoint(endpointUri, provider.Credential);
        }
        else
        {
            var subscription = runtime.MicrosoftSubscription;
            if (subscription == null)
                throw new VoiceInputException("当前 Microsoft Speech 资源缺少有效连接信息。");

            // 使用和实时翻译相同的中国域/区域解析，但识别的 Continuous LID 要走 universal/v2。
            var suffix = subscription.IsChinaEndpoint ? "azure.cn" : "microsoft.com";
            var endpointUri = new Uri($"wss://{subscription.GetEffectiveRegion()}.stt.speech.{suffix}/speech/universal/v2");
            _speechConfig = SpeechConfig.FromEndpoint(endpointUri, subscription.SubscriptionKey);
        }

        _speechConfig.SetProperty(PropertyId.SpeechServiceConnection_InitialSilenceTimeoutMs, "300000");
        if (_endSilenceMilliseconds.HasValue)
            _speechConfig.SetProperty(PropertyId.Speech_SegmentationSilenceTimeoutMs,
                _endSilenceMilliseconds.Value.ToString(CultureInfo.InvariantCulture));
        _streamFormat = AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1);
        _pushStream = AudioInputStream.CreatePushStream(_streamFormat);
        _audioConfig = AudioConfig.FromStreamInput(_pushStream);
        if (_config.SourceLanguage == "auto")
        {
            _speechConfig.SetProperty(PropertyId.SpeechServiceConnection_LanguageIdMode, "Continuous");
            _autoLanguages = AutoDetectSourceLanguageConfig.FromLanguages(
                new[] { "zh-CN", "en-US", "ja-JP", "ko-KR", "fr-FR", "de-DE", "es-ES" });
            _recognizer = new SpeechRecognizer(_speechConfig, _autoLanguages, _audioConfig);
        }
        else
        {
            _speechConfig.SpeechRecognitionLanguage = _config.SourceLanguage;
            _recognizer = new SpeechRecognizer(_speechConfig, _audioConfig);
        }
        _recognizer.Recognizing += OnRecognizing;
        _recognizer.Recognized += OnRecognized;
        _recognizer.Canceled += OnRecognitionCanceled;
        _recognizer.SessionStopped += OnRecognitionStopped;
        await _recognizer.StartContinuousRecognitionAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task AuthenticateSocketAsync(ModelRuntimeResolution runtime, RealtimeConnectionSpec spec, CancellationToken token)
    {
        switch (spec.AuthTransportKind)
        {
            case RealtimeAuthTransportKind.AuthorizationBearer:
                var bearer = runtime.ApiKey;
                if (runtime.AzureAuthMode == AzureAuthMode.AAD)
                {
                    var provider = await _tokens.GetAuthenticatedProviderAsync(
                        runtime.ProfileKey, runtime.AzureTenantId, runtime.AzureClientId, token).ConfigureAwait(false);
                    if (provider == null)
                        throw new VoiceInputException("当前语音资源尚未登录，请先在终结点设置中完成 AAD 登录。");
                    bearer = await provider.GetTokenAsync(token).ConfigureAwait(false);
                }
                _socket!.Options.SetRequestHeader("Authorization", "Bearer " + bearer);
                break;
            case RealtimeAuthTransportKind.ApiKeyHeader:
                _socket!.Options.SetRequestHeader("api-key", runtime.ApiKey);
                break;
            case RealtimeAuthTransportKind.ApiKeyQuery:
                // URI 完全来自现有 resolver，不自行拼接、回显带密钥/签名的 URL。
                break;
        }
    }

    private object BuildSessionUpdate(RealtimeConnectionSpec spec)
    {
        var transcription = new Dictionary<string, object> { ["model"] = _inputModel };
        if (_config.SourceLanguage != "auto" && !string.IsNullOrWhiteSpace(_config.SourceLanguage))
            transcription["language"] = _config.SourceLanguage.Split('-')[0].ToLowerInvariant();
        if (spec.RouteKind == RealtimeEndpointRouteKind.AzureOpenAiPreview)
        {
            // 保留当前 Azure preview 的平铺 session，不擅自改成 GA 的 audio.input 结构。
            return new
            {
                type = "session.update",
                session = new
                {
                    modalities = new[] { "text" },
                    instructions = string.Empty,
                    input_audio_format = "pcm16",
                    input_audio_transcription = transcription,
                    turn_detection = new
                    {
                        type = "server_vad", threshold = 0.5,
                        prefix_padding_ms = "300",
                        silence_duration_ms = (_endSilenceMilliseconds ?? Math.Clamp((long)_config.EndSilenceTimeoutSeconds * 1000, 200, 3000))
                            .ToString(CultureInfo.InvariantCulture),
                        create_response = false, interrupt_response = false
                    }
                }
            };
        }

        object turnDetection = _endSilenceMilliseconds.HasValue ? new
        {
            type = "server_vad", threshold = 0.5, prefix_padding_ms = 300,
            silence_duration_ms = _endSilenceMilliseconds.Value,
            create_response = false, interrupt_response = false
        } : new
        {
            type = "semantic_vad", eagerness = "low",
            create_response = false, interrupt_response = false
        };
        var audio = new
        {
            input = new
            {
                format = new { type = "audio/pcm", rate = OpenAiSampleRate },
                transcription,
                noise_reduction = new { type = "near_field" },
                turn_detection = turnDetection
            }
        };
        if (spec.SessionMode == RealtimeSessionMode.Transcription)
            return new { type = "session.update", session = new { type = "transcription", audio } };

        return new
        {
            type = "session.update",
            session = new { type = "realtime", instructions = string.Empty, output_modalities = new[] { "text" }, audio }
        };
    }

    private void OnAudio(byte[] pcm)
    {
        if (Volatile.Read(ref _acceptAudio) == 0)
            return;
        Interlocked.Exchange(ref _lastAudioTick, Stopwatch.GetTimestamp());
        var now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastLevelTick), now) >= TimeSpan.FromMilliseconds(70))
        {
            Interlocked.Exchange(ref _lastLevelTick, now);
            var peak = 0;
            for (var i = 0; i + 1 < pcm.Length; i += 2)
                peak = Math.Max(peak, Math.Abs((int)(short)(pcm[i] | pcm[i + 1] << 8)));
            try { AudioLevelChanged?.Invoke(Math.Clamp(peak / 32768d * 3, 0, 1)); }
            catch { /* 电平显示不能影响音频推流。 */ }
        }
        // 不能静默 DropOldest：发不出去就结束本次会话，避免假装仍在正常听写。
        if (_audio?.Writer.TryWrite(pcm) != true)
            SignalFailure("语音上传速度不足，语音输入已停止，请检查网络后重试。");
    }

    private async Task SendAudioAsync(ChannelReader<byte[]> reader, CancellationToken token)
    {
        try
        {
            await foreach (var pcm in reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();
                if (_pushStream != null)
                    _pushStream.Write(pcm);
                else if (_connector != null)
                    await _connector.OnAudioChunkAsync(pcm, this, token).ConfigureAwait(false);
                else
                    await SendJsonAsync(new { type = "input_audio_buffer.append", audio = Convert.ToBase64String(pcm) }, token)
                        .ConfigureAwait(false);
            }
        }
        catch
        {
            if (Volatile.Read(ref _stopping) == 0)
                SignalFailure("语音上传中断，语音输入已停止，请检查网络及语音资源。");
            throw;
        }
    }

    private Task SendJsonAsync(object payload, CancellationToken token)
        => SendTextAsync(JsonSerializer.Serialize(payload), token);

    public Task SendBinaryAsync(byte[] payload, CancellationToken cancellationToken)
        => SendSocketAsync(payload, WebSocketMessageType.Binary, cancellationToken);

    public Task SendTextAsync(string text, CancellationToken cancellationToken)
        => SendSocketAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, cancellationToken);

    private async Task SendSocketAsync(byte[] bytes, WebSocketMessageType type, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await _sendGate.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            var socket = _socket;
            if (socket == null || socket.State != WebSocketState.Open)
                throw new VoiceInputException("语音识别连接已断开。");
            await socket.SendAsync(new ArraySegment<byte>(bytes), type, true, deadline.Token)
                .WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        finally { _sendGate.Release(); }
    }

    private async Task ReceiveAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[16384];
        try
        {
            while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;
                    if (message.Length + result.Count > 1024 * 1024)
                        throw new VoiceInputException("语音识别服务返回了过大的消息。");
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text || message.Length == 0)
                    continue;
                var json = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                if (_connector != null)
                    _connector.HandleMessage(json, this);
                else
                {
                    using var document = JsonDocument.Parse(json);
                    HandleRealtimeEvent(document.RootElement);
                }
            }
        }
        catch
        {
            // 正常释放主动取消 Receive 不报错；收尾期间真正的网络/协议异常仍只报告一次。
            if (!token.IsCancellationRequested)
                SignalFailure("语音识别连接异常，语音输入已停止，请检查网络及资源权限。");
        }
        finally
        {
            if (Volatile.Read(ref _stopping) == 0)
                SignalFailure("语音识别连接已结束，请重新开启语音输入。");
        }
    }

    private void HandleRealtimeEvent(JsonElement root)
    {
        var type = ReadString(root, "type");
        switch (type)
        {
            case "session.updated":
            case "transcription_session.updated":
                _sessionReady.TrySetResult(true);
                break;
            case "input_audio_buffer.committed":
                lock (_textLock) { GetTurn(ReadString(root, "item_id")); }
                break;
            case "conversation.item.input_audio_transcription.delta":
                lock (_textLock)
                {
                    var turn = GetTurn(ReadString(root, "item_id"));
                    if (turn.Complete)
                        break;
                    var delta = ReadString(root, "delta");
                    // 与已有实时翻译一致：transcribe 是增量；whisper-1 的 delta 是整句快照。
                    turn.Partial = _incrementalDeltas ? turn.Partial + delta : delta;
                    if (_turnOrder.TryPeek(out var head) && ReferenceEquals(head, turn))
                        EmitText(turn.Partial, false);
                }
                break;
            case "conversation.item.input_audio_transcription.completed":
                lock (_textLock)
                {
                    var turn = GetTurn(ReadString(root, "item_id"));
                    if (turn.Complete)
                        break;
                    turn.Final = ReadString(root, "transcript");
                    turn.Complete = true;
                    // completion 允许跨句乱序；按 committed 的输入顺序发 final，不能串句或重复追加。
                    while (_turnOrder.TryPeek(out var head) && head.Complete)
                    {
                        _turnOrder.Dequeue();
                        EmitText(head.Final, true);
                        head.Final = head.Partial = string.Empty;
                    }
                    if (_turnOrder.TryPeek(out var next) && !string.IsNullOrEmpty(next.Partial))
                        EmitText(next.Partial, false);
                }
                break;
            case "conversation.item.input_audio_transcription.failed":
                SignalFailure("语音转写失败，请检查当前识别模型及资源权限后重试。");
                break;
            case "error":
                SignalFailure("语音识别服务返回错误，请检查当前资源、识别模型及权限。");
                break;
            // 不消费 response.*、输出音频转录或任何译文事件。
        }
    }

    private InputTurn GetTurn(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new VoiceInputException("语音识别服务缺少输入片段标识。");
        if (!_turns.TryGetValue(id, out var turn))
        {
            turn = new InputTurn();
            _turns.Add(id, turn);
            _turnOrder.Enqueue(turn);
        }
        return turn;
    }

    private void OnRecognizing(object? sender, SpeechRecognitionEventArgs e)
    {
        if (e.Result.Reason == ResultReason.RecognizingSpeech)
        {
            lock (_textLock)
            {
                if (!_speechResultIds.Contains(e.Result.ResultId))
                    EmitText(e.Result.Text, false);
            }
        }
    }

    private void OnRecognized(object? sender, SpeechRecognitionEventArgs e)
    {
        lock (_textLock)
        {
            if (e.Result.Reason == ResultReason.RecognizedSpeech && _speechResultIds.Add(e.Result.ResultId))
                EmitText(e.Result.Text, true);
            else if (e.Result.Reason == ResultReason.NoMatch)
                EmitText(string.Empty, false);
        }
    }

    private void OnRecognitionCanceled(object? sender, SpeechRecognitionCanceledEventArgs e)
    {
        if (Volatile.Read(ref _stopping) == 0 || e.Reason == CancellationReason.Error)
            SignalFailure("语音识别已取消，请检查语音资源、登录状态及网络后重试。");
    }

    private void OnRecognitionStopped(object? sender, SessionEventArgs e)
    {
        if (Volatile.Read(ref _stopping) == 0)
            SignalFailure("语音识别会话已结束，请重新开启语音输入。");
    }

    public void ReportPartial(string? text)
    {
        lock (_textLock) { EmitText(text ?? string.Empty, false); }
    }

    public void ReportFinal(string? text)
    {
        lock (_textLock) { EmitText(text ?? string.Empty, true); }
    }

    public void ReportError(string message)
        => SignalFailure("第三方语音识别返回错误，请检查识别资源的凭据、权限及服务状态。");

    public void LogDebug(string message) { /* 不记录厂商正文、凭据、URL 或转写内容。 */ }

    private void EmitText(string text, bool final)
    {
        if (Volatile.Read(ref _callbacksClosed) != 0 || Volatile.Read(ref _failureReported) != 0)
            return;
        // 服务端的空定稿应清掉上一条 partial，不能把它提升成 final，也不能留在输入框。
        if (final && string.IsNullOrWhiteSpace(text))
        {
            text = string.Empty;
            final = false;
        }
        try { TextRecognized?.Invoke(text, final); }
        catch { /* UI 订阅者异常不能使网络/音频回调线程失效。 */ }
    }

    private async Task MonitorMicrophoneAsync(CancellationToken token)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            while (!token.IsCancellationRequested && Volatile.Read(ref _stopping) == 0)
            {
                await Task.Delay(1000, token).ConfigureAwait(false);
                using var microphone = enumerator.GetDevice(_microphoneId!);
                if (microphone.State != DeviceState.Active
                    || Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastAudioTick)) > TimeSpan.FromSeconds(3))
                {
                    SignalFailure("麦克风已断开或采集已停止，请检查输入设备后重试。");
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (Volatile.Read(ref _stopping) == 0)
                SignalFailure("麦克风不可用，语音输入已停止，请检查输入设备。");
        }
    }

    private void SignalFailure(string message)
    {
        if (Volatile.Read(ref _callbacksClosed) != 0 || Interlocked.CompareExchange(ref _failureReported, 1, 0) != 0)
            return;
        _failureMessage = message;
        // 先安排释放，再通知 UI；UI 可以自行 Stop/Dispose，始终共享同一 stop task。
        _ = StopAsync();
        AbortTransport();
        try { Failed?.Invoke(message); }
        catch { /* 不向后台回调传播订阅者异常。 */ }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            if (_stopTask != null)
                return _stopTask;
            Volatile.Write(ref _stopping, 1);
            Volatile.Write(ref _callbacksClosed, 1);
            Volatile.Write(ref _acceptAudio, 0);
            // 先发布共享任务，再取消启动。取消回调可能重入 Stop，不能创建第二个 cleanup。
            _stopTask = Task.Run(() => StopBoundedAsync(cancellationToken));
            AbortTransport();
            return _stopTask;
        }
    }

    private async Task StopBoundedAsync(CancellationToken cancellationToken)
    {
        var cleanup = Task.Run(async () =>
        {
            // 与初始化串行：取消发生在 native Start 中时，迟到的麦克风也一定由这里释放。
            await _resourceGate.WaitAsync().ConfigureAwait(false);
            try { await ReleaseResourcesAsync().ConfigureAwait(false); }
            finally { _resourceGate.Release(); _resourcesReleased.TrySetResult(true); }
        });
        Observe(cleanup);
        try
        {
            await cleanup.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AbortTransport();
            throw new OperationCanceledException("语音输入已结束，正在后台释放设备。", cancellationToken);
        }
        catch
        {
            // 不等待尾句、上传队列或远端关闭握手；迟到的 native 释放继续由 cleanup 接管。
            AbortTransport();
        }
    }

    private async Task ReleaseResourcesAsync()
    {
        Volatile.Write(ref _acceptAudio, 0);
        Volatile.Write(ref _callbacksClosed, 1);
        AbortTransport();
        _audio?.Writer.TryComplete();
        if (_audioSource != null)
        {
            _audioSource.Pcm16ChunkReady -= OnAudio;
            try { await _audioSource.DisposeAsync().ConfigureAwait(false); }
            catch { }
            _audioSource = null;
        }
        if (_recognizer != null)
        {
            _recognizer.Recognizing -= OnRecognizing;
            _recognizer.Recognized -= OnRecognized;
            _recognizer.Canceled -= OnRecognitionCanceled;
            _recognizer.SessionStopped -= OnRecognitionStopped;
            // 即使麦克风初始化失败，已经创建的 SDK 识别器也必须停止并释放。
            if (!_recognizerStopRequested)
            {
                _recognizerStopRequested = true;
                try
                {
                    _pushStream?.Close();
                    await _recognizer.StopContinuousRecognitionAsync().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                }
                catch { }
            }
        }
        // 先中止 socket 解除 pending Receive/Send，再释放；不会无限等待 CloseAsync 握手。
        try { _socket?.Dispose(); } catch { }
        _socket = null;
        foreach (var task in new[] { _sendTask, _receiveTask, _microphoneMonitorTask })
        {
            if (task == null) continue;
            try { await task.WaitAsync(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false); }
            catch { Observe(task); }
        }
        try { _recognizer?.Dispose(); } catch { }
        _recognizer = null;
        try { _audioConfig?.Dispose(); } catch { }
        _audioConfig = null;
        try { _pushStream?.Dispose(); } catch { }
        _pushStream = null;
        try { _streamFormat?.Dispose(); } catch { }
        _streamFormat = null;
        _autoLanguages = null;
        _speechConfig = null;
        _connector = null;
        _audio = null;
        lock (_textLock)
        {
            _turns.Clear();
            _turnOrder.Clear();
            _speechResultIds.Clear();
        }
        // gate/CTS 不在公开 Dispose 中抢先 Dispose，迟到的 native 操作仍可能持有其 token。
        // 会话不可重启，后台任务完成后由 GC 回收这些纯托管同步对象。
    }

    private void AbortTransport()
    {
        try { _startupCancellation.Cancel(); } catch { }
        try { _lifetime.Cancel(); } catch { }
        try { _socket?.Abort(); } catch { }
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return new ValueTask(StopAsync());
    }

    private static string ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty : string.Empty;

    private static TaskCompletionSource<bool> NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void Observe(Task task)
    {
        _ = task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private sealed class InputTurn
    {
        public string Partial = string.Empty;
        public string Final = string.Empty;
        public bool Complete;
    }

    private sealed class VoiceInputException : Exception
    {
        public VoiceInputException(string message) : base(message) { }
    }
}