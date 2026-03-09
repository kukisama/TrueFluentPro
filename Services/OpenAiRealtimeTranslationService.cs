using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Azure.Identity;
using TrueFluentPro.Models;
using TrueFluentPro.Services.Audio;

namespace TrueFluentPro.Services
{
    public sealed class OpenAiRealtimeTranslationService : IRealtimeTranslationService
    {
        private const int OpenAiInputSampleRate = 24000;
        private const int AzureDefaultPrefixPaddingMs = 300;
        private const string DefaultConversationInputTranscriptionModel = "whisper-1";

        private readonly IModelRuntimeResolver _modelRuntimeResolver;
        private readonly ISpeechResourceRuntimeResolver _speechResourceRuntimeResolver;
        private readonly IRealtimeConnectionSpecResolver _realtimeConnectionSpecResolver;
        private readonly Action<string>? _auditLog;

        private AzureSpeechConfig _config;
        private WasapiPcm16AudioSource? _audioSource;
        private ClientWebSocket? _webSocket;
        private CancellationTokenSource? _lifetimeCts;
        private Task? _receiveLoopTask;
        private Task? _audioSendLoopTask;
        private Task? _translationLoopTask;
        private Channel<byte[]>? _audioChannel;
        private Channel<TranscriptTranslationWorkItem>? _translationChannel;
        private AzureTokenProvider? _tokenProvider;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly Queue<string> _completedOriginalInputs = new();
        private readonly object _stateLock = new();

        private bool _isTranslating;
        private string _currentSessionFilePath = string.Empty;
        private string _liveOriginal = string.Empty;
        private string _liveTranslated = string.Empty;
        private string _lastDiagnostics = "诊断: 未启动";
        private string? _activeResponseId;
        private RealtimeSessionMode _activeSessionMode = RealtimeSessionMode.Conversation;
        private string _activeInputTranscriptionModel = DefaultConversationInputTranscriptionModel;
        private DateTime _lastAudioChunkUtc = DateTime.MinValue;
        private DateTime _lastServerEventUtc = DateTime.MinValue;
        private double _smoothedAudioLevel;

        public OpenAiRealtimeTranslationService(
            AzureSpeechConfig config,
            IModelRuntimeResolver modelRuntimeResolver,
            ISpeechResourceRuntimeResolver speechResourceRuntimeResolver,
            IRealtimeConnectionSpecResolver realtimeConnectionSpecResolver,
            Action<string>? auditLog = null)
        {
            _config = config;
            _modelRuntimeResolver = modelRuntimeResolver;
            _speechResourceRuntimeResolver = speechResourceRuntimeResolver;
            _realtimeConnectionSpecResolver = realtimeConnectionSpecResolver;
            _auditLog = auditLog;
            InitializeSessionFile();
        }

        private sealed class TranscriptTranslationWorkItem
        {
            public required string OriginalText { get; init; }
        }

        public RealtimeConnectorFamily ConnectorFamily => RealtimeConnectorFamily.OpenAiRealtimeWebSocket;

        public event EventHandler<TranslationItem>? OnRealtimeTranslationReceived;
        public event EventHandler<TranslationItem>? OnFinalTranslationReceived;
        public event EventHandler<string>? OnStatusChanged;
        public event EventHandler<string>? OnReconnectTriggered;
        public event EventHandler<double>? OnAudioLevelUpdated;
        public event EventHandler<string>? OnDiagnosticsUpdated;

        public async Task<bool> StartTranslationAsync()
        {
            if (_isTranslating)
            {
                return true;
            }

            try
            {
                if (!TryResolveRuntime(out var runtime, out var spec, out var errorMessage) ||
                    runtime == null ||
                    spec == null)
                {
                    OnStatusChanged?.Invoke(this, errorMessage);
                    return false;
                }

                InitializeSessionFile();
                _lifetimeCts = new CancellationTokenSource();
                _activeSessionMode = spec.SessionMode;
                _activeInputTranscriptionModel = ResolveInputTranscriptionModel(spec, runtime.AiRuntime!);

                if (_activeSessionMode == RealtimeSessionMode.Transcription
                    && !TryResolveTextTranslationRuntime(out _, out errorMessage))
                {
                    OnStatusChanged?.Invoke(this, errorMessage);
                    await CleanupAsync().ConfigureAwait(false);
                    return false;
                }

                _audioChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false
                });

                if (_activeSessionMode == RealtimeSessionMode.Transcription)
                {
                    _translationChannel = Channel.CreateUnbounded<TranscriptTranslationWorkItem>(new UnboundedChannelOptions
                    {
                        SingleReader = true,
                        SingleWriter = false
                    });
                    _translationLoopTask = TranslateLoopAsync(_translationChannel.Reader, _lifetimeCts.Token);
                }

                _webSocket = new ClientWebSocket();
                await ApplyAuthenticationAsync(_webSocket, runtime.AiRuntime!, spec, _lifetimeCts.Token).ConfigureAwait(false);
                await _webSocket.ConnectAsync(spec.WebSocketUri, _lifetimeCts.Token).ConfigureAwait(false);

                _receiveLoopTask = ReceiveLoopAsync(_webSocket, _lifetimeCts.Token);
                await SendSessionUpdateAsync(runtime.AiRuntime!, spec, _lifetimeCts.Token).ConfigureAwait(false);

                _audioSource = CreateAudioSource();
                _audioSource.Pcm16ChunkReady += OnAudioChunkReady;
                await _audioSource.StartAsync(_lifetimeCts.Token).ConfigureAwait(false);
                _audioSendLoopTask = SendAudioLoopAsync(_audioChannel.Reader, _webSocket, _lifetimeCts.Token);

                _isTranslating = true;
                _lastAudioChunkUtc = DateTime.UtcNow;
                _lastServerEventUtc = DateTime.UtcNow;
                PublishDiagnostics(force: true);
                OnStatusChanged?.Invoke(this, _activeSessionMode == RealtimeSessionMode.Transcription
                    ? $"正在通过官方 Realtime 转写通道监听：{runtime.Resource.GetDisplayName()}..."
                    : $"正在通过官方 Realtime 通道监听：{runtime.Resource.GetDisplayName()}...");
                return true;
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke(this, $"启动 Realtime 翻译失败: {ex.Message}");
                await CleanupAsync().ConfigureAwait(false);
                _isTranslating = false;
                return false;
            }
        }

        public async Task StopTranslationAsync()
        {
            if (!_isTranslating && _webSocket == null && _audioSource == null)
            {
                return;
            }

            await CleanupAsync().ConfigureAwait(false);
            _isTranslating = false;
            PublishAudioLevel(0);
            OnDiagnosticsUpdated?.Invoke(this, "诊断: 已停止");
            OnStatusChanged?.Invoke(this, "翻译已停止");
        }

        public async Task UpdateConfigAsync(AzureSpeechConfig newConfig)
        {
            var wasTranslating = _isTranslating;
            _config = newConfig;

            if (!wasTranslating)
            {
                return;
            }

            OnStatusChanged?.Invoke(this, "配置已更改，正在重新连接 Realtime 通道...");
            await StopTranslationAsync().ConfigureAwait(false);
            if (_config.IsValid())
            {
                await StartTranslationAsync().ConfigureAwait(false);
            }
        }

        public bool TryApplyLiveAudioRoutingFromCurrentConfig(int fadeMilliseconds = 30)
        {
            if (_audioSource == null || !_isTranslating)
            {
                return false;
            }

            var (enableLoopback, enableMic) = GetRecognitionRouting();
            _audioSource.UpdateRouting(enableLoopback, enableMic, Math.Clamp(fadeMilliseconds, 10, 50));
            PublishDiagnostics(force: true);
            return true;
        }

        private async Task CleanupAsync()
        {
            _lifetimeCts?.Cancel();
            _audioChannel?.Writer.TryComplete();
            _translationChannel?.Writer.TryComplete();

            if (_audioSource != null)
            {
                try
                {
                    _audioSource.Pcm16ChunkReady -= OnAudioChunkReady;
                    await _audioSource.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                }
                finally
                {
                    _audioSource = null;
                }
            }

            if (_audioSendLoopTask != null)
            {
                try
                {
                    await _audioSendLoopTask.ConfigureAwait(false);
                }
                catch
                {
                }
                finally
                {
                    _audioSendLoopTask = null;
                }
            }

            if (_translationLoopTask != null)
            {
                try
                {
                    await _translationLoopTask.ConfigureAwait(false);
                }
                catch
                {
                }
                finally
                {
                    _translationLoopTask = null;
                }
            }

            if (_webSocket != null)
            {
                try
                {
                    if (_webSocket.State == WebSocketState.Open || _webSocket.State == WebSocketState.CloseReceived)
                    {
                        await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "client stop", CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                }
                catch
                {
                }
                finally
                {
                    _webSocket.Dispose();
                    _webSocket = null;
                }
            }

            if (_receiveLoopTask != null)
            {
                try
                {
                    await _receiveLoopTask.ConfigureAwait(false);
                }
                catch
                {
                }
                finally
                {
                    _receiveLoopTask = null;
                }
            }

            _lifetimeCts?.Dispose();
            _lifetimeCts = null;
            _audioChannel = null;
            _translationChannel = null;
            _activeResponseId = null;
            _activeSessionMode = RealtimeSessionMode.Conversation;
            _activeInputTranscriptionModel = DefaultConversationInputTranscriptionModel;
            _liveOriginal = string.Empty;
            _liveTranslated = string.Empty;
            lock (_stateLock)
            {
                _completedOriginalInputs.Clear();
            }
        }

        private bool TryResolveRuntime(
            out SpeechResourceRuntimeResolution? runtime,
            out RealtimeConnectionSpec? spec,
            out string errorMessage)
        {
            runtime = null;
            spec = null;
            errorMessage = string.Empty;

            if (!_speechResourceRuntimeResolver.TryResolveActive(
                    _config,
                    SpeechCapability.RealtimeSpeechToText,
                    out runtime,
                    out errorMessage) || runtime == null)
            {
                return false;
            }

            if (runtime.IsMicrosoftSpeech)
            {
                errorMessage = "当前资源属于 Microsoft Speech SDK，请改用 Microsoft 实时执行器。";
                return false;
            }

            if (runtime.AiRuntime == null)
            {
                errorMessage = $"语音资源“{runtime.Resource.Name}”缺少可用的 AI 运行时。";
                return false;
            }

            if (!_realtimeConnectionSpecResolver.TryResolve(runtime.AiRuntime, out spec, out errorMessage) || spec == null)
            {
                return false;
            }

            if (spec.ConnectorFamily != RealtimeConnectorFamily.OpenAiRealtimeWebSocket)
            {
                errorMessage = $"暂不支持连接族“{spec.ConnectorFamily}”的 Realtime WebSocket。";
                return false;
            }

            return true;
        }

        private async Task ApplyAuthenticationAsync(
            ClientWebSocket socket,
            ModelRuntimeResolution runtime,
            RealtimeConnectionSpec spec,
            CancellationToken cancellationToken)
        {
            switch (spec.AuthTransportKind)
            {
                case RealtimeAuthTransportKind.AuthorizationBearer:
                    if (runtime.AzureAuthMode == AzureAuthMode.AAD)
                    {
                        _tokenProvider ??= new AzureTokenProvider(runtime.ProfileKey);
                        if (!_tokenProvider.IsLoggedIn)
                        {
                            var silentOk = await _tokenProvider.TrySilentLoginAsync(
                                runtime.AzureTenantId,
                                runtime.AzureClientId,
                                cancellationToken).ConfigureAwait(false);
                            if (!silentOk)
                            {
                                throw new InvalidOperationException("Azure AAD 认证未登录。请先在设置中完成该终结点的 AAD 登录。");
                            }
                        }

                        var token = await _tokenProvider.GetTokenAsync(cancellationToken).ConfigureAwait(false);
                        socket.Options.SetRequestHeader("Authorization", $"Bearer {token}");
                    }
                    else
                    {
                        socket.Options.SetRequestHeader("Authorization", $"Bearer {runtime.ApiKey}");
                    }
                    break;

                case RealtimeAuthTransportKind.ApiKeyHeader:
                    socket.Options.SetRequestHeader("api-key", runtime.ApiKey);
                    break;

                case RealtimeAuthTransportKind.ApiKeyQuery:
                    break;
            }
        }

        private WasapiPcm16AudioSource CreateAudioSource()
        {
            var (enableLoopback, enableMic) = GetRecognitionRouting();
            return new WasapiPcm16AudioSource(
                _config.SelectedOutputDeviceId,
                _config.SelectedAudioDeviceId,
                _config.ChunkDurationMs,
                enableLoopback,
                enableMic,
                sampleRate: OpenAiInputSampleRate);
        }

        private async Task SendSessionUpdateAsync(
            ModelRuntimeResolution runtime,
            RealtimeConnectionSpec spec,
            CancellationToken cancellationToken)
        {
            object payload = spec.RouteKind == RealtimeEndpointRouteKind.AzureOpenAiPreview
                ? BuildAzureSessionUpdatePayload(runtime, spec)
                : BuildOpenAiSessionUpdatePayload(runtime, spec);

            await SendJsonAsync(payload, cancellationToken).ConfigureAwait(false);
            _auditLog?.Invoke($"[翻译流] RealtimeSessionUpdated uri='{spec.WebSocketUri}' route='{spec.RouteKind}' model='{spec.ModelName}'");
        }

        private object BuildOpenAiSessionUpdatePayload(ModelRuntimeResolution runtime, RealtimeConnectionSpec spec)
        {
            if (spec.SessionMode == RealtimeSessionMode.Transcription)
            {
                return BuildOpenAiTranscriptionSessionUpdatePayload(runtime);
            }

            return new
            {
                type = "session.update",
                session = new
                {
                    type = "realtime",
                    instructions = BuildRealtimeInstructions(),
                    output_modalities = new[] { "text" },
                    audio = new
                    {
                        input = new
                        {
                            format = new
                            {
                                type = "audio/pcm",
                                rate = OpenAiInputSampleRate
                            },
                            transcription = BuildOpenAiInputTranscription(DefaultConversationInputTranscriptionModel),
                            turn_detection = BuildOpenAiTurnDetection(),
                            noise_reduction = new
                            {
                                type = "near_field"
                            }
                        }
                    }
                }
            };
        }

        private object BuildOpenAiTranscriptionSessionUpdatePayload(ModelRuntimeResolution runtime)
        {
            return new
            {
                type = "session.update",
                session = new
                {
                    type = "transcription",
                    audio = new
                    {
                        input = new
                        {
                            format = new
                            {
                                type = "audio/pcm",
                                rate = OpenAiInputSampleRate
                            },
                            noise_reduction = new
                            {
                                type = "near_field"
                            },
                            transcription = BuildOpenAiInputTranscription(ResolveInputTranscriptionModelName(runtime)),
                            turn_detection = BuildOpenAiTranscriptionTurnDetection()
                        }
                    }
                }
            };
        }

        private object BuildAzureSessionUpdatePayload(ModelRuntimeResolution runtime, RealtimeConnectionSpec spec)
        {
            if (spec.SessionMode == RealtimeSessionMode.Transcription)
            {
                throw new InvalidOperationException("Azure OpenAI preview Realtime transcription 会话暂未实现，请改用 GA /openai/v1/realtime 路线。");
            }

            return new
            {
                type = "session.update",
                session = new
                {
                    modalities = new[] { "text" },
                    instructions = BuildRealtimeInstructions(),
                    input_audio_format = "pcm16",
                    input_audio_transcription = BuildAzureInputTranscription(DefaultConversationInputTranscriptionModel),
                    turn_detection = BuildAzureTurnDetection(),
                    max_response_output_tokens = 512
                }
            };
        }

        private object BuildOpenAiTranscriptionTurnDetection()
        {
            return new
            {
                type = "semantic_vad",
                eagerness = "low",
                create_response = false,
                interrupt_response = false
            };
        }

        private object BuildOpenAiTurnDetection()
        {
            return new
            {
                type = "semantic_vad",
                eagerness = "low",
                create_response = true,
                interrupt_response = false
            };
        }

        private object BuildAzureTurnDetection()
        {
            return new
            {
                type = "server_vad",
                threshold = 0.5,
                prefix_padding_ms = AzureDefaultPrefixPaddingMs.ToString(),
                silence_duration_ms = Math.Clamp(_config.EndSilenceTimeoutSeconds * 1000, 200, 3000).ToString(),
                create_response = true,
                interrupt_response = false
            };
        }

        private object? BuildOpenAiInputTranscription(string modelName)
        {
            return new
            {
                model = modelName,
                language = NormalizeRealtimeLanguage(_config.SourceLanguage)
            };
        }

        private object? BuildAzureInputTranscription(string modelName)
        {
            return new
            {
                model = modelName,
                language = NormalizeRealtimeLanguage(_config.SourceLanguage)
            };
        }

        private string BuildRealtimeInstructions()
        {
            var sourceDescription = string.Equals(_config.SourceLanguage, "auto", StringComparison.OrdinalIgnoreCase)
                ? "自动识别说话者当前使用的语言"
                : $"将说话者的内容从 {_config.SourceLanguage}"
                ;

            return $"你现在不是聊天助手，也不是问答助手，而是一个实时翻译器。你的唯一任务是把别人刚刚说出的内容，{sourceDescription}直接翻译为 {_config.TargetLanguage}。别人说什么，你就翻什么；只做单纯复述式翻译，不要做任何思考、分析、解释、总结、补充、润色、改写或回答。不要补全说话者没说出的意思，不要根据常识发挥，不要提出建议，不要添加前缀、标题、括号备注或说明。只输出翻译后的目标语言文本本身。";
        }

        private static string? NormalizeRealtimeLanguage(string? language)
        {
            if (string.IsNullOrWhiteSpace(language) || string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return language.Trim() switch
            {
                "zh-CN" => "zh",
                "en-US" => "en",
                "ja-JP" => "ja",
                "ko-KR" => "ko",
                "fr-FR" => "fr",
                "de-DE" => "de",
                "es-ES" => "es",
                var value when value.Contains('-') => value[..value.IndexOf('-')],
                var value => value
            };
        }

        private void OnAudioChunkReady(byte[] chunk)
        {
            try
            {
                _lastAudioChunkUtc = DateTime.UtcNow;
                UpdateAudioLevel(chunk);
                PublishDiagnostics();

                if (_audioChannel?.Writer.TryWrite(chunk) != true)
                {
                    _auditLog?.Invoke("[翻译流] RealtimeAudioChunkDropped channel-full");
                }
            }
            catch
            {
            }
        }

        private async Task SendAudioLoopAsync(ChannelReader<byte[]> reader, ClientWebSocket socket, CancellationToken cancellationToken)
        {
            await foreach (var chunk in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (socket.State != WebSocketState.Open)
                {
                    return;
                }

                await SendJsonAsync(new
                {
                    type = "input_audio_buffer.append",
                    audio = Convert.ToBase64String(chunk)
                }, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task SendJsonAsync(object payload, CancellationToken cancellationToken)
        {
            if (_webSocket == null || _webSocket.State != WebSocketState.Open)
            {
                return;
            }

            var json = JsonSerializer.Serialize(payload);
            var bytes = Encoding.UTF8.GetBytes(json);

            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
            try
            {
                while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            return;
                        }

                        ms.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    if (result.MessageType != WebSocketMessageType.Text)
                    {
                        continue;
                    }

                    ms.Position = 0;
                    using var doc = await JsonDocument.ParseAsync(ms, cancellationToken: cancellationToken).ConfigureAwait(false);
                    HandleServerEvent(doc.RootElement);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    OnReconnectTriggered?.Invoke(this, "Realtime 连接中断");
                    OnStatusChanged?.Invoke(this, $"Realtime 连接中断: {ex.Message}");
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private void HandleServerEvent(JsonElement element)
        {
            if (!element.TryGetProperty("type", out var typeElement))
            {
                return;
            }

            var eventType = typeElement.GetString() ?? string.Empty;
            _lastServerEventUtc = DateTime.UtcNow;

            switch (eventType)
            {
                case "session.created":
                    OnStatusChanged?.Invoke(this, "Realtime 会话已建立。");
                    break;

                case "session.updated":
                    OnStatusChanged?.Invoke(this, "Realtime 会话配置已生效。");
                    break;

                case "input_audio_buffer.speech_started":
                    OnStatusChanged?.Invoke(this, "检测到语音输入...");
                    break;

                case "input_audio_buffer.speech_stopped":
                    OnStatusChanged?.Invoke(this, _activeSessionMode == RealtimeSessionMode.Transcription
                        ? "语音片段结束，正在整理转写..."
                        : "语音片段结束，正在生成翻译...");
                    break;

                case "conversation.item.input_audio_transcription.delta":
                    AppendOriginalDelta(GetString(element, "delta"));
                    break;

                case "conversation.item.input_audio_transcription.completed":
                    CompleteOriginalTranscript(GetString(element, "transcript"));
                    break;

                case "response.created":
                    _activeResponseId = GetNestedString(element, "response", "id");
                    _liveTranslated = string.Empty;
                    break;

                case "response.text.delta":
                case "response.output_text.delta":
                    AppendTranslatedDelta(GetString(element, "delta"));
                    break;

                case "response.audio_transcript.delta":
                case "response.output_audio_transcript.delta":
                    AppendTranslatedDelta(GetString(element, "delta"));
                    break;

                case "response.text.done":
                    SetTranslatedText(GetString(element, "text"));
                    break;

                case "response.audio_transcript.done":
                    if (string.IsNullOrWhiteSpace(_liveTranslated))
                    {
                        SetTranslatedText(GetString(element, "transcript"));
                    }
                    break;

                case "response.done":
                    CompleteResponse(element);
                    break;

                case "error":
                    OnStatusChanged?.Invoke(this, $"Realtime 错误: {GetNestedString(element, "error", "message")}");
                    break;
            }

            PublishDiagnostics();
        }

        private void AppendOriginalDelta(string? delta)
        {
            if (string.IsNullOrWhiteSpace(delta))
            {
                return;
            }

            if (UsesIncrementalInputTranscriptionDeltas())
            {
                _liveOriginal += delta;
            }
            else
            {
                _liveOriginal = delta.Trim();
            }

            PublishRealtimeItem();
        }

        private void CompleteOriginalTranscript(string? transcript)
        {
            if (!string.IsNullOrWhiteSpace(transcript))
            {
                _liveOriginal = transcript.Trim();

                if (_activeSessionMode == RealtimeSessionMode.Transcription)
                {
                    EnqueueTranscriptForTranslation(_liveOriginal);
                }
                else
                {
                    lock (_stateLock)
                    {
                        _completedOriginalInputs.Enqueue(_liveOriginal);
                    }
                }
            }

            PublishRealtimeItem();
        }

        private void EnqueueTranscriptForTranslation(string transcript)
        {
            if (string.IsNullOrWhiteSpace(transcript))
            {
                return;
            }

            if (_translationChannel?.Writer.TryWrite(new TranscriptTranslationWorkItem
                {
                    OriginalText = transcript
                }) == true)
            {
                OnStatusChanged?.Invoke(this, "收到实时转写结果，正在翻译...");
                return;
            }

            _auditLog?.Invoke("[翻译流] RealtimeTranscriptDropped translation-channel-unavailable");
        }

        private void AppendTranslatedDelta(string? delta)
        {
            if (string.IsNullOrWhiteSpace(delta))
            {
                return;
            }

            _liveTranslated += delta;
            PublishRealtimeItem();
        }

        private void SetTranslatedText(string? text)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                _liveTranslated = text.Trim();
                PublishRealtimeItem();
            }
        }

        private void CompleteResponse(JsonElement element)
        {
            var finalTranslated = string.IsNullOrWhiteSpace(_liveTranslated)
                ? TryExtractFinalResponseText(element)
                : _liveTranslated.Trim();

            string finalOriginal;
            lock (_stateLock)
            {
                finalOriginal = _completedOriginalInputs.Count > 0
                    ? _completedOriginalInputs.Dequeue()
                    : _liveOriginal.Trim();
            }

            if (string.IsNullOrWhiteSpace(finalOriginal) && string.IsNullOrWhiteSpace(finalTranslated))
            {
                return;
            }

            var item = new TranslationItem
            {
                Timestamp = DateTime.Now,
                OriginalText = finalOriginal,
                TranslatedText = finalTranslated
            };

            SaveTranslationToFile(item);
            OnFinalTranslationReceived?.Invoke(this, item);
            OnStatusChanged?.Invoke(this, "收到最终翻译结果");

            _activeResponseId = null;
            _liveOriginal = string.Empty;
            _liveTranslated = string.Empty;
        }

        private void PublishRealtimeItem()
        {
            var item = new TranslationItem
            {
                Timestamp = DateTime.Now,
                OriginalText = _liveOriginal,
                TranslatedText = _liveTranslated
            };

            OnRealtimeTranslationReceived?.Invoke(this, item);
        }

        private async Task TranslateLoopAsync(ChannelReader<TranscriptTranslationWorkItem> reader, CancellationToken cancellationToken)
        {
            await foreach (var workItem in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await TranslateTranscriptAsync(workItem.OriginalText, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    OnStatusChanged?.Invoke(this, $"实时翻译失败: {ex.Message}");
                }
            }
        }

        private async Task TranslateTranscriptAsync(string originalText, CancellationToken cancellationToken)
        {
            if (!TryResolveTextTranslationRuntime(out var runtime, out var errorMessage) || runtime == null)
            {
                OnStatusChanged?.Invoke(this, errorMessage);
                return;
            }

            var tokenProvider = await TryCreateTextTokenProviderAsync(runtime, cancellationToken).ConfigureAwait(false);
            var service = new AiInsightService(tokenProvider);
            var translatedBuilder = new StringBuilder();

            _liveOriginal = originalText;
            _liveTranslated = string.Empty;
            PublishRealtimeItem();

            await service.StreamChatAsync(
                runtime.CreateChatRequest(summaryEnableReasoning: false),
                BuildTextTranslationSystemPrompt(),
                BuildTextTranslationUserPrompt(originalText),
                chunk =>
                {
                    if (string.IsNullOrWhiteSpace(chunk))
                    {
                        return;
                    }

                    translatedBuilder.Append(chunk);
                    _liveOriginal = originalText;
                    _liveTranslated = translatedBuilder.ToString();
                    PublishRealtimeItem();
                },
                cancellationToken,
                AiChatProfile.Quick,
                enableReasoning: false).ConfigureAwait(false);

            var translatedText = translatedBuilder.ToString().Trim();
            if (string.IsNullOrWhiteSpace(translatedText))
            {
                translatedText = originalText;
            }

            var item = new TranslationItem
            {
                Timestamp = DateTime.Now,
                OriginalText = originalText,
                TranslatedText = translatedText
            };

            SaveTranslationToFile(item);
            OnFinalTranslationReceived?.Invoke(this, item);
            OnStatusChanged?.Invoke(this, "收到最终翻译结果");

            _liveOriginal = string.Empty;
            _liveTranslated = string.Empty;
        }

        private bool TryResolveTextTranslationRuntime(out ModelRuntimeResolution? runtime, out string errorMessage)
        {
            runtime = null;
            errorMessage = string.Empty;

            var ai = _config.AiConfig;
            if (ai == null)
            {
                errorMessage = "当前已切到 Realtime transcription 模式，请先在设置中配置一个文本模型用于译文生成。";
                return false;
            }

            var reference = ai.QuickModelRef ?? ai.InsightModelRef ?? ai.SummaryModelRef ?? ai.ReviewModelRef;
            if (_modelRuntimeResolver.TryResolve(_config, reference, ModelCapability.Text, out runtime, out errorMessage)
                && runtime != null)
            {
                return true;
            }

            errorMessage = string.IsNullOrWhiteSpace(errorMessage)
                ? "当前已切到 Realtime transcription 模式，但未找到可用的文本翻译模型。"
                : errorMessage;
            return false;
        }

        private async Task<AzureTokenProvider?> TryCreateTextTokenProviderAsync(ModelRuntimeResolution runtime, CancellationToken cancellationToken)
        {
            if (runtime.AzureAuthMode != AzureAuthMode.AAD)
            {
                return null;
            }

            var tokenProvider = new AzureTokenProvider(runtime.ProfileKey);
            if (!tokenProvider.IsLoggedIn)
            {
                var silentOk = await tokenProvider.TrySilentLoginAsync(
                    runtime.AzureTenantId,
                    runtime.AzureClientId,
                    cancellationToken).ConfigureAwait(false);
                if (!silentOk)
                {
                    throw new InvalidOperationException("文本翻译模型的 Azure AAD 认证未登录。请先在设置中完成该终结点的 AAD 登录。");
                }
            }

            return tokenProvider;
        }

        private string BuildTextTranslationSystemPrompt()
        {
            var sourceDescription = string.Equals(_config.SourceLanguage, "auto", StringComparison.OrdinalIgnoreCase)
                ? "自动识别原文语言"
                : $"将原文从 {_config.SourceLanguage}";

            return $"你现在是一个实时文本翻译器。你的唯一任务是{sourceDescription}直接翻译为 {_config.TargetLanguage}。只输出译文文本本身，不要解释，不要总结，不要添加标题、括号备注或额外说明。";
        }

        private static string BuildTextTranslationUserPrompt(string originalText)
            => originalText;

        private string ResolveInputTranscriptionModel(RealtimeConnectionSpec spec, ModelRuntimeResolution runtime)
            => spec.SessionMode == RealtimeSessionMode.Transcription
                ? ResolveInputTranscriptionModelName(runtime)
                : DefaultConversationInputTranscriptionModel;

        private static string ResolveInputTranscriptionModelName(ModelRuntimeResolution runtime)
            => string.IsNullOrWhiteSpace(runtime.ModelId)
                ? DefaultConversationInputTranscriptionModel
                : runtime.ModelId.Trim();

        private bool UsesIncrementalInputTranscriptionDeltas()
            => _activeInputTranscriptionModel.IndexOf("transcribe", StringComparison.OrdinalIgnoreCase) >= 0;

        private static string TryExtractFinalResponseText(JsonElement element)
        {
            if (!element.TryGetProperty("response", out var response) ||
                !response.TryGetProperty("output", out var output) ||
                output.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            foreach (var item in output.EnumerateArray())
            {
                if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var part in content.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
                    {
                        var text = textElement.GetString();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            return text.Trim();
                        }
                    }

                    if (part.TryGetProperty("transcript", out var transcriptElement) && transcriptElement.ValueKind == JsonValueKind.String)
                    {
                        var transcript = transcriptElement.GetString();
                        if (!string.IsNullOrWhiteSpace(transcript))
                        {
                            return transcript.Trim();
                        }
                    }
                }
            }

            return string.Empty;
        }

        private void InitializeSessionFile()
        {
            try
            {
                var sessionsPath = PathManager.Instance.SessionsPath;
                Directory.CreateDirectory(sessionsPath);

                _currentSessionFilePath = PathManager.Instance.GetSessionFile(
                    $"Session_Realtime_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

                using var fileStream = new FileStream(_currentSessionFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(fileStream);
                writer.WriteLine($"=== Realtime 翻译会话记录 - {DateTime.Now} ===");
                writer.WriteLine();
                writer.Flush();
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke(this, $"创建会话文件失败: {ex.Message}");
            }
        }

        private void SaveTranslationToFile(TranslationItem item)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_currentSessionFilePath))
                {
                    return;
                }

                using var fileStream = new FileStream(_currentSessionFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(fileStream);
                writer.WriteLine($"[{item.Timestamp:yyyy-MM-dd HH:mm:ss}]");
                writer.WriteLine($"原文: {item.OriginalText}");
                writer.WriteLine($"译文: {item.TranslatedText}");
                writer.WriteLine();
                writer.Flush();
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke(this, $"保存翻译到文件失败: {ex.Message}");
            }
        }

        private (bool enableLoopback, bool enableMic) GetRecognitionRouting()
        {
            if (_config.AudioSourceMode == AudioSourceMode.Loopback)
            {
                return (true, false);
            }

            if (_config.AudioSourceMode == AudioSourceMode.DefaultMic)
            {
                return (false, true);
            }

            return (_config.UseOutputForRecognition, _config.UseInputForRecognition);
        }

        private void UpdateAudioLevel(byte[] chunk)
        {
            var peak = GetPeakLevel(chunk);
            var gain = Math.Max(0.1, _config.AudioLevelGain);
            _smoothedAudioLevel = (_smoothedAudioLevel * 0.8) + (peak * 0.2);
            PublishAudioLevel(Math.Clamp(_smoothedAudioLevel * gain, 0, 1));
        }

        private void PublishAudioLevel(double level)
        {
            OnAudioLevelUpdated?.Invoke(this, Math.Clamp(level, 0, 1));
        }

        private void PublishDiagnostics(bool force = false)
        {
            var now = DateTime.UtcNow;
            if (!force && _lastDiagnostics == BuildDiagnosticsMessage(now))
            {
                return;
            }

            _lastDiagnostics = BuildDiagnosticsMessage(now);
            OnDiagnosticsUpdated?.Invoke(this, _lastDiagnostics);
        }

        private string BuildDiagnosticsMessage(DateTime now)
        {
            var sinceAudio = _lastAudioChunkUtc == DateTime.MinValue
                ? -1
                : (int)Math.Max(0, (now - _lastAudioChunkUtc).TotalSeconds);
            var sinceServer = _lastServerEventUtc == DateTime.MinValue
                ? -1
                : (int)Math.Max(0, (now - _lastServerEventUtc).TotalSeconds);

            return $"诊断 Realtime 峰值:{_smoothedAudioLevel:F2} 最近音频:{sinceAudio}s 最近服务事件:{sinceServer}s";
        }

        private static double GetPeakLevel(byte[] chunk)
        {
            var max = 0;
            for (var i = 0; i + 1 < chunk.Length; i += 2)
            {
                var sample = (short)(chunk[i] | (chunk[i + 1] << 8));
                var abs = Math.Abs(sample);
                if (abs > max)
                {
                    max = abs;
                }
            }

            return Math.Clamp(max / 32768d, 0, 1);
        }

        private static string GetString(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
                ? property.GetString() ?? string.Empty
                : string.Empty;
        }

        private static string GetNestedString(JsonElement element, string outerName, string innerName)
        {
            if (!element.TryGetProperty(outerName, out var outer) || outer.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            return GetString(outer, innerName);
        }
    }
}