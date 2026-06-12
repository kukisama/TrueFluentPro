using System;
using System.Buffers;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using TrueFluentPro.Models;
using TrueFluentPro.Services.Audio;

namespace TrueFluentPro.Services
{
    /// <summary>
    /// 第三方厂商「级联」实时翻译服务基类：ASR(WebSocket) → 同厂商机器翻译(HTTP)。
    /// 负责音频采集、WebSocket 收发循环、会话文件、诊断、音频电平等通用机制；
    /// 协议差异（连接/鉴权/帧格式/解析/翻译）由派生类实现。
    /// 只翻「定稿句」，中间结果仅展示原文。
    /// </summary>
    public abstract class CascadedRealtimeTranslationServiceBase : IRealtimeTranslationService
    {
        private readonly ISpeechResourceRuntimeResolver _speechResourceRuntimeResolver;
        protected readonly Action<string>? _auditLog;

        protected AzureSpeechConfig _config;
        private SpeechResource? _activeResource;
        private WasapiPcm16AudioSource? _audioSource;
        private ClientWebSocket? _webSocket;
        private CancellationTokenSource? _lifetimeCts;
        private Task? _receiveLoopTask;
        private Task? _audioSendLoopTask;
        private Task? _translationLoopTask;
        private Channel<byte[]>? _audioChannel;
        private Channel<string>? _translationChannel;
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        private bool _isTranslating;
        private string _currentSessionFilePath = string.Empty;
        private string _liveOriginal = string.Empty;
        private string _liveTranslated = string.Empty;
        private string _lastDiagnostics = "诊断: 未启动";
        private DateTime _lastAudioChunkUtc = DateTime.MinValue;
        private DateTime _lastServerEventUtc = DateTime.MinValue;
        private double _smoothedAudioLevel;

        protected CascadedRealtimeTranslationServiceBase(
            AzureSpeechConfig config,
            ISpeechResourceRuntimeResolver speechResourceRuntimeResolver,
            Action<string>? auditLog)
        {
            _config = config;
            _speechResourceRuntimeResolver = speechResourceRuntimeResolver;
            _auditLog = auditLog;
            InitializeSessionFile();
        }

        public abstract RealtimeConnectorFamily ConnectorFamily { get; }

        public event EventHandler<TranslationItem>? OnRealtimeTranslationReceived;
        public event EventHandler<TranslationItem>? OnFinalTranslationReceived;
        public event EventHandler<string>? OnStatusChanged;
        public event EventHandler<string>? OnReconnectTriggered;
        public event EventHandler<double>? OnAudioLevelUpdated;
        public event EventHandler<string>? OnDiagnosticsUpdated;

        // ===== 派生类需实现的协议细节 =====

        /// <summary>ASR 采样率（讯飞/百度均为 16000）。</summary>
        protected abstract int AsrSampleRate { get; }

        /// <summary>厂商显示名（用于状态提示）。</summary>
        protected abstract string VendorDisplayName { get; }

        /// <summary>构建 WebSocket 连接地址。</summary>
        protected abstract Uri BuildWebSocketUri(SpeechResource resource);

        /// <summary>连接成功后发送起始控制帧（如百度 START）。默认不发送。</summary>
        protected virtual Task OnConnectedAsync(SpeechResource resource, CancellationToken cancellationToken)
            => Task.CompletedTask;

        /// <summary>处理一段 PCM16 音频（派生类负责重切分并通过 SendBinaryAsync 发送）。</summary>
        protected abstract Task OnAudioChunkAsync(SpeechResource resource, byte[] pcm16, CancellationToken cancellationToken);

        /// <summary>音频结束时发送收尾控制帧（讯飞 {"end":true} / 百度 FINISH）。</summary>
        protected abstract Task OnAudioCompletedAsync(SpeechResource resource, CancellationToken cancellationToken);

        /// <summary>解析服务端文本消息，调用 ReportPartialOriginal / ReportFinalOriginal。</summary>
        protected abstract void HandleTextMessage(SpeechResource resource, string json);

        /// <summary>调用同厂商机器翻译，返回译文。</summary>
        protected abstract Task<string> TranslateAsync(SpeechResource resource, string sourceText, CancellationToken cancellationToken);

        // ===== 派生类可用的辅助方法 =====

        protected async Task SendBinaryAsync(byte[] payload, CancellationToken cancellationToken)
        {
            var socket = _webSocket;
            if (socket == null || socket.State != WebSocketState.Open)
            {
                return;
            }

            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await socket.SendAsync(payload, WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        protected async Task SendTextAsync(string text, CancellationToken cancellationToken)
        {
            var socket = _webSocket;
            if (socket == null || socket.State != WebSocketState.Open)
            {
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(text);
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        /// <summary>报告中间识别结果（仅显示原文，不触发翻译）。</summary>
        protected void ReportPartialOriginal(string? text)
        {
            _liveOriginal = text?.Trim() ?? string.Empty;
            _liveTranslated = string.Empty;
            PublishRealtimeItem();
        }

        /// <summary>报告定稿句（触发同厂商翻译）。</summary>
        protected void ReportFinalOriginal(string? text)
        {
            var sentence = text?.Trim();
            if (string.IsNullOrWhiteSpace(sentence))
            {
                return;
            }

            _liveOriginal = sentence;
            _liveTranslated = string.Empty;
            PublishRealtimeItem();

            if (_translationChannel?.Writer.TryWrite(sentence) != true)
            {
                _auditLog?.Invoke("[翻译流] CascadedFinalDropped translation-channel-unavailable");
            }
            else
            {
                LogDebug($"定稿句入队翻译: {Truncate(sentence, 120)}");
            }
        }

        protected void RaiseStatus(string message) => OnStatusChanged?.Invoke(this, message);

        /// <summary>写入调试日志（带 [翻译流] 前缀 + 厂商名，最终落到 Audit.log）。</summary>
        protected void LogDebug(string message) => _auditLog?.Invoke($"[翻译流] {VendorDisplayName} {message}");

        /// <summary>脱敏凭据，仅用于调试日志输出，绝不输出完整密钥。</summary>
        protected static string MaskCredential(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "<空>";
            }

            var v = value.Trim();
            if (v.Length <= 4)
            {
                return $"****(len={v.Length})";
            }

            return $"{v[..2]}***{v[^2..]}(len={v.Length})";
        }

        /// <summary>截断长文本用于日志输出。</summary>
        protected static string Truncate(string? text, int maxLength)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var oneLine = text.Replace('\r', ' ').Replace('\n', ' ');
            return oneLine.Length <= maxLength ? oneLine : oneLine[..maxLength] + "…";
        }

        /// <summary>
        /// 探活时判断服务端首帧是否表示握手真正成功。
        /// 默认仅凭“收到任意消息”判定成功；派生类可解析 err_no / action 区分鉴权失败，
        /// 避免出现“握手成功”但实际无法识别/翻译的误导性结论。
        /// </summary>
        protected virtual bool IsProbeResponseSuccess(string message, out string detail)
        {
            detail = string.Empty;
            return true;
        }

        // ===== 连通性探活（不开麦克风、不发音频）=====

        /// <summary>
        /// 仅做握手探活：建立 WebSocket、执行 <see cref="OnConnectedAsync"/> 起始帧，
        /// 等待首个服务端消息后立即关闭。复用派生类真实的连接/签名逻辑，
        /// 供配置页与批量测试做连通性验证，不会采集麦克风、不发送音频。
        /// </summary>
        public async Task<RealtimeProbeResult> ProbeConnectionAsync(SpeechResource resource, CancellationToken cancellationToken)
        {
            ClientWebSocket? socket = null;
            Uri? uri = null;
            try
            {
                uri = BuildWebSocketUri(resource);
                LogDebug($"探活开始 uri={uri}");
                socket = new ClientWebSocket();
                await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);

                // 暂存到字段，使 OnConnectedAsync 内的 SendTextAsync / SendBinaryAsync 可用。
                _webSocket = socket;
                await OnConnectedAsync(resource, cancellationToken).ConfigureAwait(false);

                // 等待首个服务端消息（最多 5s），确认握手被服务端接受。
                using var recvCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                recvCts.CancelAfter(TimeSpan.FromSeconds(5));
                var buffer = new byte[8192];
                try
                {
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), recvCts.Token).ConfigureAwait(false);
                    var preview = result.MessageType == WebSocketMessageType.Text
                        ? Encoding.UTF8.GetString(buffer, 0, result.Count)
                        : $"<binary {result.Count} bytes>";
                    LogDebug($"探活收到首帧: {Truncate(preview, 300)}");

                    if (result.MessageType == WebSocketMessageType.Text
                        && !IsProbeResponseSuccess(preview, out var failDetail))
                    {
                        LogDebug($"探活握手被拒绝: {failDetail}");
                        return new RealtimeProbeResult(false, $"{VendorDisplayName} 握手被拒绝：{failDetail}", preview, uri);
                    }

                    return new RealtimeProbeResult(true, $"{VendorDisplayName} 握手成功", preview, uri);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // 已连接但 5s 内无消息：连接层面已通过。
                    LogDebug("探活已连接，但 5s 等待窗口内未收到首帧");
                    return new RealtimeProbeResult(true, $"{VendorDisplayName} 已连接（等待窗口内未收到首帧）", string.Empty, uri);
                }
            }
            catch (Exception ex)
            {
                LogDebug($"探活连接失败: {ex.Message}");
                return new RealtimeProbeResult(false, $"{VendorDisplayName} 连接失败：{ex.Message}", string.Empty, uri);
            }
            finally
            {
                _webSocket = null;
                if (socket != null)
                {
                    try
                    {
                        if (socket.State == WebSocketState.Open)
                        {
                            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "probe done", CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                    catch
                    {
                        // 探活收尾失败不影响结论。
                    }

                    socket.Dispose();
                }
            }
        }

        /// <summary>
        /// 翻译链路探活：直接调用派生类真实的 <see cref="TranslateAsync"/> 翻译一小段示例文本，
        /// 验证机器翻译凭据是否可用。不连接 WebSocket、不开麦克风。
        /// </summary>
        public async Task<RealtimeProbeResult> ProbeTranslationAsync(SpeechResource resource, CancellationToken cancellationToken)
        {
            var sample = GetTranslationProbeSample();
            string? lastStatus = null;
            void Capture(object? _, string msg) => lastStatus = msg;
            OnStatusChanged += Capture;
            try
            {
                var translated = await TranslateAsync(resource, sample, cancellationToken).ConfigureAwait(false);

                // 翻译失败时派生类约定返回原文；据此区分“成功”与“未翻译”。
                if (string.IsNullOrWhiteSpace(translated)
                    || string.Equals(translated.Trim(), sample, StringComparison.Ordinal))
                {
                    var reason = string.IsNullOrWhiteSpace(lastStatus)
                        ? $"未返回有效译文（原文：{sample}）"
                        : lastStatus!;
                    return new RealtimeProbeResult(false, $"{VendorDisplayName} 翻译失败：{reason}", translated ?? string.Empty, null);
                }

                return new RealtimeProbeResult(true, $"{sample} → {Truncate(translated, 60)}", translated, null);
            }
            catch (Exception ex)
            {
                return new RealtimeProbeResult(false, $"{VendorDisplayName} 翻译异常：{ex.Message}", string.Empty, null);
            }
            finally
            {
                OnStatusChanged -= Capture;
            }
        }

        /// <summary>翻译探活使用的示例文本，按源语言选取，便于得到可读的译文反馈。</summary>
        protected virtual string GetTranslationProbeSample()
        {
            var src = (_config.SourceLanguage ?? string.Empty).ToLowerInvariant();
            return src.StartsWith("en") ? "hello" : "你好";
        }

        // ===== IRealtimeTranslationService 实现 =====

        public async Task<bool> StartTranslationAsync()
        {
            if (_isTranslating)
            {
                return true;
            }

            try
            {
                if (!_speechResourceRuntimeResolver.TryResolveActive(
                        _config,
                        SpeechCapability.RealtimeSpeechToText,
                        out var runtime,
                        out var errorMessage) || runtime == null)
                {
                    OnStatusChanged?.Invoke(this, errorMessage);
                    return false;
                }

                _activeResource = runtime.Resource;

                InitializeSessionFile();
                _lifetimeCts = new CancellationTokenSource();
                var token = _lifetimeCts.Token;

                _audioChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false
                });

                _translationChannel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false
                });
                _translationLoopTask = TranslateLoopAsync(_translationChannel.Reader, token);

                _webSocket = new ClientWebSocket();
                await _webSocket.ConnectAsync(BuildWebSocketUri(_activeResource), token).ConfigureAwait(false);

                _receiveLoopTask = ReceiveLoopAsync(_webSocket, token);
                await OnConnectedAsync(_activeResource, token).ConfigureAwait(false);
                LogDebug($"WebSocket 已连接并发送起始帧，资源={_activeResource.GetDisplayName()}");

                _audioSource = CreateAudioSource();
                _audioSource.Pcm16ChunkReady += OnAudioChunkReady;
                await _audioSource.StartAsync(token).ConfigureAwait(false);
                _audioSendLoopTask = SendAudioLoopAsync(_audioChannel.Reader, token);

                _isTranslating = true;
                _lastAudioChunkUtc = DateTime.UtcNow;
                _lastServerEventUtc = DateTime.UtcNow;
                PublishDiagnostics(force: true);
                OnStatusChanged?.Invoke(this, $"正在通过 {VendorDisplayName} 实时语音通道监听：{_activeResource.GetDisplayName()}...");
                return true;
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke(this, $"启动 {VendorDisplayName} 实时翻译失败: {ex.Message}");
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

            OnStatusChanged?.Invoke(this, $"配置已更改，正在重新连接 {VendorDisplayName} 实时通道...");
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

        // ===== 内部机制 =====

        private WasapiPcm16AudioSource CreateAudioSource()
        {
            var (enableLoopback, enableMic) = GetRecognitionRouting();
            return new WasapiPcm16AudioSource(
                _config.SelectedOutputDeviceId,
                _config.SelectedAudioDeviceId,
                _config.ChunkDurationMs,
                enableLoopback,
                enableMic,
                sampleRate: AsrSampleRate);
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
                    _auditLog?.Invoke("[翻译流] CascadedAudioChunkDropped channel-full");
                }
            }
            catch
            {
            }
        }

        private async Task SendAudioLoopAsync(ChannelReader<byte[]> reader, CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var chunk in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (_webSocket?.State != WebSocketState.Open || _activeResource == null)
                    {
                        continue;
                    }

                    await OnAudioChunkAsync(_activeResource, chunk, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _auditLog?.Invoke($"[翻译流] CascadedSendAudioLoopError {ex.Message}");
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

                    if (result.MessageType != WebSocketMessageType.Text || ms.Length == 0)
                    {
                        continue;
                    }

                    _lastServerEventUtc = DateTime.UtcNow;
                    var json = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                    if (_activeResource != null)
                    {
                        try
                        {
                            HandleTextMessage(_activeResource, json);
                        }
                        catch (Exception ex)
                        {
                            _auditLog?.Invoke($"[翻译流] CascadedParseError {ex.Message}");
                        }
                    }

                    PublishDiagnostics();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    OnReconnectTriggered?.Invoke(this, $"{VendorDisplayName} 连接中断");
                    OnStatusChanged?.Invoke(this, $"{VendorDisplayName} 连接中断: {ex.Message}");
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private async Task TranslateLoopAsync(ChannelReader<string> reader, CancellationToken cancellationToken)
        {
            await foreach (var sentence in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await TranslateSentenceAsync(sentence, cancellationToken).ConfigureAwait(false);
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

        private async Task TranslateSentenceAsync(string originalText, CancellationToken cancellationToken)
        {
            if (_activeResource == null)
            {
                return;
            }

            _liveOriginal = originalText;
            _liveTranslated = string.Empty;
            PublishRealtimeItem();

            string translatedText;
            try
            {
                translatedText = (await TranslateAsync(_activeResource, originalText, cancellationToken).ConfigureAwait(false))?.Trim() ?? string.Empty;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke(this, $"机器翻译失败: {ex.Message}");
                translatedText = string.Empty;
            }

            if (string.IsNullOrWhiteSpace(translatedText))
            {
                LogDebug($"译文为空，回退为显示原文: {Truncate(originalText, 120)}");
                translatedText = originalText;
            }
            else
            {
                LogDebug($"译文就绪: {Truncate(translatedText, 120)}");
            }

            _liveOriginal = originalText;
            _liveTranslated = translatedText;
            PublishRealtimeItem();

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

        private async Task CleanupAsync()
        {
            _lifetimeCts?.Cancel();
            _audioChannel?.Writer.TryComplete();

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

            // 音频已停止采集后，发送收尾控制帧让服务端给出最终结果。
            if (_webSocket?.State == WebSocketState.Open && _activeResource != null)
            {
                try
                {
                    await OnAudioCompletedAsync(_activeResource, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
            }

            _translationChannel?.Writer.TryComplete();
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
            _liveOriginal = string.Empty;
            _liveTranslated = string.Empty;
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
                writer.WriteLine($"=== {VendorDisplayName} 实时翻译会话记录 - {DateTime.Now} ===");
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
            var message = BuildDiagnosticsMessage(now);
            if (!force && _lastDiagnostics == message)
            {
                return;
            }

            _lastDiagnostics = message;
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

            return $"诊断 {VendorDisplayName} 峰值:{_smoothedAudioLevel:F2} 最近音频:{sinceAudio}s 最近服务事件:{sinceServer}s";
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

            return Math.Clamp(max / 32768.0, 0, 1);
        }
    }

    /// <summary>第三方实时语音厂商连通性探活结果。</summary>
    public sealed record RealtimeProbeResult(bool IsSuccess, string Message, string ResponsePreview, Uri? Uri);
}
