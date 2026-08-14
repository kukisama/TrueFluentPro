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
using TrueFluentPro.Services.RealtimeSpeech.Asr;
using TrueFluentPro.Services.RealtimeSpeech.Composition;
using TrueFluentPro.Services.RealtimeSpeech.Pipeline;
using TrueFluentPro.Services.RealtimeSpeech.Sinks;
using TrueFluentPro.Services.RealtimeSpeech.Translation;

namespace TrueFluentPro.Services.RealtimeSpeech
{
    /// <summary>
    /// 解耦后的「积木式」级联实时语音服务：识别(IAsrConnector) 与 翻译(ITranslationProvider) 各自独立、可任意组合。
    /// 本类只负责通用编排：音频采集 → AudioPipeline 分发（识别分支 + 录音分支）→ WebSocket 收发 →
    /// 翻译 → 会话/字幕落盘 → 事件上报。所有厂商协议差异都收敛在连接器/Provider 积木内。
    /// 录音(MP3)与字幕(SRT/VTT)使用同一份被识别的 PCM，时间轴天然对齐，可直接进入批量复盘。
    /// </summary>
    public sealed class CascadedRealtimeSpeechService : IRealtimeTranslationService
    {
        private readonly ISpeechResourceRuntimeResolver _speechResourceRuntimeResolver;
        private readonly Action<string>? _auditLog;

        private AzureSpeechConfig _config;
        private SpeechResource? _activeResource;
        private IAsrConnector? _connector;
        private ITranslationProvider? _translation;
        private RealtimeConnectorFamily _connectorFamily = RealtimeConnectorFamily.Custom;
        private string _vendorDisplayName = "实时语音";

        private WasapiPcm16AudioSource? _audioSource;
        private ClientWebSocket? _webSocket;
        private CancellationTokenSource? _lifetimeCts;
        private Task? _receiveLoopTask;
        private Task? _uplinkLoopTask;
        private Task? _translationLoopTask;
        private Channel<byte[]>? _uplinkChannel;
        private Channel<string>? _translationChannel;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private AudioPipeline? _pipeline;
        private Mp3RecordingSink? _recordingSink;

        private bool _isTranslating;
        private string _currentRunStamp = string.Empty;
        private string? _currentAudioMp3Path;
        private string _currentSessionFilePath = string.Empty;
        private string _liveOriginal = string.Empty;
        private string _liveTranslated = string.Empty;
        private string _lastDiagnostics = "诊断: 未启动";
        private DateTime _lastAudioChunkUtc = DateTime.MinValue;
        private DateTime _lastServerEventUtc = DateTime.MinValue;
        private double _smoothedAudioLevel;

        // 字幕（无 SDK 时间戳，按会话墙钟相对时间生成）。
        private StreamWriter? _srtWriter;
        private StreamWriter? _vttWriter;
        private int _subtitleIndex = 1;
        private TimeSpan _lastSubtitleEnd = TimeSpan.Zero;
        private DateTime _sessionStartUtc;
        private readonly object _subtitleLock = new();

        public CascadedRealtimeSpeechService(
            AzureSpeechConfig config,
            ISpeechResourceRuntimeResolver speechResourceRuntimeResolver,
            Action<string>? auditLog)
        {
            _config = config;
            _speechResourceRuntimeResolver = speechResourceRuntimeResolver;
            _auditLog = auditLog;

            // 预解析以确定连接器族（供 ConnectorFamily 查询），失败则在 Start 时再解析。
            if (_speechResourceRuntimeResolver.TryResolveActive(
                    _config, SpeechCapability.RealtimeSpeechToText, out var runtime, out _)
                && runtime != null)
            {
                _connectorFamily = runtime.Resource.ConnectorType switch
                {
                    SpeechConnectorType.XunfeiRtasr => RealtimeConnectorFamily.XunfeiRtasr,
                    SpeechConnectorType.BaiduRealtimeAsr => RealtimeConnectorFamily.BaiduRealtimeAsr,
                    _ => RealtimeConnectorFamily.Custom
                };
            }
        }

        public RealtimeConnectorFamily ConnectorFamily => _connectorFamily;

        public event EventHandler<TranslationItem>? OnRealtimeTranslationReceived;
        public event EventHandler<TranslationItem>? OnFinalTranslationReceived;
        public event EventHandler<string>? OnStatusChanged;
        public event EventHandler<string>? OnReconnectTriggered;
        public event EventHandler<double>? OnAudioLevelUpdated;
        public event EventHandler<string>? OnDiagnosticsUpdated;

        // ===== IRealtimeTranslationService =====

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

                _connector = SpeechRoleResolver.ResolveAsrConnector(_activeResource);
                if (_connector == null)
                {
                    OnStatusChanged?.Invoke(this, $"语音资源“{_activeResource.GetDisplayName()}”不是讯飞 / 百度实时识别类型。");
                    return false;
                }

                _vendorDisplayName = _connector.DisplayName;
                _connectorFamily = _activeResource.ConnectorType switch
                {
                    SpeechConnectorType.XunfeiRtasr => RealtimeConnectorFamily.XunfeiRtasr,
                    SpeechConnectorType.BaiduRealtimeAsr => RealtimeConnectorFamily.BaiduRealtimeAsr,
                    _ => RealtimeConnectorFamily.Custom
                };

                _translation = SpeechRoleResolver.ResolveTranslationProvider(
                    _activeResource,
                    status: msg => OnStatusChanged?.Invoke(this, msg),
                    log: LogDebug);

                _currentRunStamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                _sessionStartUtc = DateTime.UtcNow;
                InitializeSessionFile();
                InitializeSubtitleWriters();

                _lifetimeCts = new CancellationTokenSource();
                var token = _lifetimeCts.Token;

                _uplinkChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
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
                await _webSocket.ConnectAsync(_connector.BuildUri(_activeResource, _config), token).ConfigureAwait(false);

                var uplink = new Uplink(this);
                _receiveLoopTask = ReceiveLoopAsync(_webSocket, token);
                await _connector.OnConnectedAsync(_activeResource, _config, uplink, token).ConfigureAwait(false);
                LogDebug($"WebSocket 已连接并发送起始帧，资源={_activeResource.GetDisplayName()}");

                BuildPipeline();

                _audioSource = CreateAudioSource();
                _audioSource.Pcm16ChunkReady += OnAudioChunkReady;
                await _audioSource.StartAsync(token).ConfigureAwait(false);
                _uplinkLoopTask = UplinkLoopAsync(_uplinkChannel.Reader, uplink, token);

                _isTranslating = true;
                _lastAudioChunkUtc = DateTime.UtcNow;
                _lastServerEventUtc = DateTime.UtcNow;
                PublishDiagnostics(force: true);

                var translateNote = _translation.IsConfigured ? $"，翻译：{_translation.DisplayName}" : "，仅显示原文";
                OnStatusChanged?.Invoke(this, $"正在通过 {_vendorDisplayName} 实时语音通道监听：{_activeResource.GetDisplayName()}{translateNote}...");
                if (_config.EnableRecording && !string.IsNullOrEmpty(_currentAudioMp3Path))
                {
                    OnStatusChanged?.Invoke(this, $"录音已开始: {_currentAudioMp3Path}");
                }

                return true;
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke(this, $"启动 {_vendorDisplayName} 实时翻译失败: {ex.Message}");
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

            OnStatusChanged?.Invoke(this, $"配置已更改，正在重新连接 {_vendorDisplayName} 实时通道...");
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

        // ===== 内部编排 =====

        private void BuildPipeline()
        {
            var pipeline = new AudioPipeline();

            // 识别分支：裸 PCM 直送 WebSocket（不做预处理，保持厂商帧约束）。
            pipeline.AddBranch(new AsrWebSocketSink(_uplinkChannel!.Writer, _auditLog));

            // 录音分支：可选，按配置写 MP3（自带 AutoGain）。与识别用同一份 PCM，时间轴对齐。
            _currentAudioMp3Path = null;
            _recordingSink = null;
            if (_config.EnableRecording)
            {
                _currentAudioMp3Path = PathManager.Instance.GetSessionFile($"Audio_{_currentRunStamp}.mp3");
                var (autoGainEnabled, targetRms, minGain, maxGain, smoothing) = GetAutoGainSettings();
                _recordingSink = new Mp3RecordingSink(
                    _currentAudioMp3Path,
                    16000,
                    _config.RecordingMp3BitrateKbps,
                    autoGainEnabled,
                    targetRms,
                    minGain,
                    maxGain,
                    smoothing);
                pipeline.AddBranch(_recordingSink);
            }

            _pipeline = pipeline;
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
                sampleRate: _connector!.SampleRate);
        }

        private void OnAudioChunkReady(byte[] chunk)
        {
            try
            {
                _lastAudioChunkUtc = DateTime.UtcNow;
                UpdateAudioLevel(chunk);
                PublishDiagnostics();

                var frame = new AudioFrame(chunk, _connector?.SampleRate ?? 16000, 1, DateTime.UtcNow);
                _pipeline?.Push(frame);
            }
            catch
            {
            }
        }

        private async Task UplinkLoopAsync(ChannelReader<byte[]> reader, IAsrUplink uplink, CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var chunk in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (_webSocket?.State != WebSocketState.Open || _connector == null)
                    {
                        continue;
                    }

                    await _connector.OnAudioChunkAsync(chunk, uplink, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _auditLog?.Invoke($"[翻译流] CascadedUplinkLoopError {ex.Message}");
            }
        }

        private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
        {
            var observer = new ResultObserver(this);
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
                    if (_connector != null)
                    {
                        try
                        {
                            _connector.HandleMessage(json, observer);
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
                    OnReconnectTriggered?.Invoke(this, $"{_vendorDisplayName} 连接中断");
                    OnStatusChanged?.Invoke(this, $"{_vendorDisplayName} 连接中断: {ex.Message}");
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
            _liveOriginal = originalText;
            _liveTranslated = string.Empty;
            PublishRealtimeItem();

            string translatedText = string.Empty;
            if (_translation?.IsConfigured == true)
            {
                try
                {
                    translatedText = (await _translation.TranslateAsync(
                        originalText,
                        _config.SourceLanguage,
                        _config.TargetLanguage,
                        cancellationToken).ConfigureAwait(false))?.Trim() ?? string.Empty;
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
            }

            if (string.IsNullOrWhiteSpace(translatedText))
            {
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
            WriteSubtitleEntry(translatedText);
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

        // 中间识别结果（仅展示原文，不触发翻译）。
        private void ReportPartialOriginal(string? text)
        {
            _liveOriginal = text?.Trim() ?? string.Empty;
            _liveTranslated = string.Empty;
            PublishRealtimeItem();
        }

        // 定稿句（触发翻译）。
        private void ReportFinalOriginal(string? text)
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

        private async Task SendBinaryAsync(byte[] payload, CancellationToken cancellationToken)
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

        private async Task SendTextAsync(string text, CancellationToken cancellationToken)
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

        private async Task CleanupAsync()
        {
            _lifetimeCts?.Cancel();
            _uplinkChannel?.Writer.TryComplete();

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

            // 收尾录音分支（关闭 MP3 编码器）。
            try
            {
                _pipeline?.Close();
            }
            catch
            {
            }
            finally
            {
                _pipeline = null;
            }

            if (_uplinkLoopTask != null)
            {
                try
                {
                    await _uplinkLoopTask.ConfigureAwait(false);
                }
                catch
                {
                }
                finally
                {
                    _uplinkLoopTask = null;
                }
            }

            // 音频已停止采集后，发送收尾控制帧让服务端给出最终结果。
            if (_webSocket?.State == WebSocketState.Open && _connector != null)
            {
                try
                {
                    await _connector.OnCompletedAsync(new Uplink(this), CancellationToken.None).ConfigureAwait(false);
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

            DisposeSubtitleWriters();

            _lifetimeCts?.Dispose();
            _lifetimeCts = null;
            _uplinkChannel = null;
            _translationChannel = null;
            _recordingSink = null;
            _liveOriginal = string.Empty;
            _liveTranslated = string.Empty;
        }

        // ===== 会话文件 =====

        private void InitializeSessionFile()
        {
            try
            {
                var sessionsPath = PathManager.Instance.SessionsPath;
                Directory.CreateDirectory(sessionsPath);

                _currentSessionFilePath = PathManager.Instance.GetSessionFile(
                    $"Session_Realtime_{_currentRunStamp}.txt");

                using var fileStream = new FileStream(_currentSessionFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(fileStream);
                writer.WriteLine($"=== {_vendorDisplayName} 实时翻译会话记录 - {DateTime.Now} ===");
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

        // ===== 字幕 =====

        private void InitializeSubtitleWriters()
        {
            DisposeSubtitleWriters();

            _subtitleIndex = 1;
            _lastSubtitleEnd = TimeSpan.Zero;

            if (!_config.ExportSrtSubtitles && !_config.ExportVttSubtitles)
            {
                return;
            }

            try
            {
                var sessionsPath = PathManager.Instance.SessionsPath;
                Directory.CreateDirectory(sessionsPath);
                var baseName = $"Audio_{_currentRunStamp}";

                if (_config.ExportSrtSubtitles)
                {
                    var srtPath = PathManager.Instance.GetSessionFile($"{baseName}.srt");
                    _srtWriter = new StreamWriter(new FileStream(srtPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite));
                }

                if (_config.ExportVttSubtitles)
                {
                    var vttPath = PathManager.Instance.GetSessionFile($"{baseName}.vtt");
                    _vttWriter = new StreamWriter(new FileStream(vttPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite));
                    _vttWriter.WriteLine("WEBVTT");
                    _vttWriter.WriteLine();
                    _vttWriter.Flush();
                }
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke(this, $"创建字幕文件失败: {ex.Message}");
            }
        }

        private void WriteSubtitleEntry(string translatedText)
        {
            if (string.IsNullOrWhiteSpace(translatedText) || (_srtWriter == null && _vttWriter == null))
            {
                return;
            }

            // 无 SDK 时间戳：start = 上一句结束；end = 距会话开始的墙钟时长（至少比 start 多 0.5s）。
            var start = _lastSubtitleEnd;
            var end = DateTime.UtcNow - _sessionStartUtc;
            if (end < start + TimeSpan.FromMilliseconds(500))
            {
                end = start + TimeSpan.FromMilliseconds(500);
            }

            lock (_subtitleLock)
            {
                if (_srtWriter != null)
                {
                    _srtWriter.WriteLine(_subtitleIndex);
                    _srtWriter.WriteLine($"{FormatSrtTime(start)} --> {FormatSrtTime(end)}");
                    _srtWriter.WriteLine(translatedText);
                    _srtWriter.WriteLine();
                    _srtWriter.Flush();
                }

                if (_vttWriter != null)
                {
                    _vttWriter.WriteLine($"{FormatVttTime(start)} --> {FormatVttTime(end)}");
                    _vttWriter.WriteLine(translatedText);
                    _vttWriter.WriteLine();
                    _vttWriter.Flush();
                }

                _subtitleIndex++;
            }

            _lastSubtitleEnd = end;
        }

        private void DisposeSubtitleWriters()
        {
            lock (_subtitleLock)
            {
                try
                {
                    _srtWriter?.Flush();
                    _srtWriter?.Dispose();
                }
                catch
                {
                }

                try
                {
                    _vttWriter?.Flush();
                    _vttWriter?.Dispose();
                }
                catch
                {
                }

                _srtWriter = null;
                _vttWriter = null;
            }
        }

        private static string FormatSrtTime(TimeSpan time)
        {
            if (time < TimeSpan.Zero)
            {
                time = TimeSpan.Zero;
            }

            return string.Format("{0:00}:{1:00}:{2:00},{3:000}",
                (int)time.TotalHours, time.Minutes, time.Seconds, time.Milliseconds);
        }

        private static string FormatVttTime(TimeSpan time)
        {
            if (time < TimeSpan.Zero)
            {
                time = TimeSpan.Zero;
            }

            return string.Format("{0:00}:{1:00}:{2:00}.{3:000}",
                (int)time.TotalHours, time.Minutes, time.Seconds, time.Milliseconds);
        }

        // ===== 路由 / 电平 / 诊断 =====

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

        private (bool enabled, double targetRms, double minGain, double maxGain, double smoothing) GetAutoGainSettings()
        {
            if (!_config.AutoGainEnabled || _config.AutoGainPreset == AutoGainPreset.Off)
            {
                return (false, 0.12, 0.5, 6.0, 0.08);
            }

            return _config.AutoGainPreset switch
            {
                AutoGainPreset.Low => (true, 0.08, 0.7, 3.5, 0.05),
                AutoGainPreset.High => (true, 0.18, 0.4, 8.0, 0.12),
                _ => (true, 0.12, 0.5, 6.0, 0.08)
            };
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

            return $"诊断 {_vendorDisplayName} 峰值:{_smoothedAudioLevel:F2} 最近音频:{sinceAudio}s 最近服务事件:{sinceServer}s";
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

        private void LogDebug(string message) => _auditLog?.Invoke($"[翻译流] {_vendorDisplayName} {message}");

        private static string Truncate(string? text, int maxLength)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var oneLine = text.Replace('\r', ' ').Replace('\n', ' ');
            return oneLine.Length <= maxLength ? oneLine : oneLine[..maxLength] + "…";
        }

        // ===== 积木回调适配 =====

        /// <summary>把编排器的 WebSocket 发送能力适配为连接器使用的上行通道。</summary>
        private sealed class Uplink : IAsrUplink
        {
            private readonly CascadedRealtimeSpeechService _owner;

            public Uplink(CascadedRealtimeSpeechService owner) => _owner = owner;

            public Task SendBinaryAsync(byte[] payload, CancellationToken cancellationToken)
                => _owner.SendBinaryAsync(payload, cancellationToken);

            public Task SendTextAsync(string text, CancellationToken cancellationToken)
                => _owner.SendTextAsync(text, cancellationToken);
        }

        /// <summary>把连接器解析出的识别结果路由到编排器的展示 / 翻译 / 状态通道。</summary>
        private sealed class ResultObserver : IAsrResultObserver
        {
            private readonly CascadedRealtimeSpeechService _owner;

            public ResultObserver(CascadedRealtimeSpeechService owner) => _owner = owner;

            public void ReportPartial(string? text) => _owner.ReportPartialOriginal(text);

            public void ReportFinal(string? text) => _owner.ReportFinalOriginal(text);

            public void ReportError(string message) => _owner.OnStatusChanged?.Invoke(_owner, message);

            public void LogDebug(string message) => _owner.LogDebug(message);
        }
    }
}
