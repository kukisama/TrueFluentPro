using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.CognitiveServices.Speech.Translation;
using TrueFluentPro.Models;
using TrueFluentPro.Services.Audio;

namespace TrueFluentPro.Services
{
    public class SpeechTranslationService
    {
        private static readonly string[] AutoDetectSourceLanguages =
        {
            "zh-CN",
            "en-US",
            "ja-JP",
            "ko-KR"
        };
        private AzureSpeechConfig _config;
        private readonly Action<string>? _auditLog;
        private TranslationRecognizer? _recognizer;
        private bool _isTranslating; private string _currentSessionFilePath = string.Empty;

        private string _currentRunStamp = "";
        private string? _currentAudioWavPath;
        private string? _currentAudioMp3Path;

        private AudioConfig? _audioConfig;
        private PushAudioInputStream? _pushStream;
        private WasapiPcm16AudioSource? _naudioSource;
        private readonly object _audioSourceSwapLock = new();
        private readonly object _liveRoutingDebounceLock = new();
        private readonly object _liveRoutingApplyLock = new();
        private CancellationTokenSource? _liveRoutingDebounceCts;
        private int _liveRoutingDebouncePendingCount;
        private int _liveRoutingRequestVersion;
        private string _recognizeInputDeviceIdInUse = "";
        private string _recognizeOutputDeviceIdInUse = "";

        private HighQualityRecorder? _highQualityRecorder;
        private Task? _pendingTranscodeTask;

        private readonly object _subtitleLock = new();
        private StreamWriter? _srtWriter;
        private StreamWriter? _vttWriter;
        private int _subtitleIndex = 1;
        private DateTime _sessionStartUtc;
        private TimeSpan _lastSubtitleEnd = TimeSpan.Zero;

        private readonly SemaphoreSlim _restartLock = new(1, 1);
        private readonly object _managedReconnectLock = new();
        private CancellationTokenSource? _noResponseMonitorCts;
        private Task? _noResponseMonitorTask;
        private CancellationTokenSource? _managedReconnectCts;
        private DateTime _lastRecognitionUtc = DateTime.MinValue;
        private DateTime _lastAudioActivityUtc = DateTime.MinValue;
        private DateTime _lastDiagnosticsUtc = DateTime.MinValue;
        private int _managedReconnectAttempt;
        private double _smoothedAudioLevel;
        private bool _lastChunkHadActivity;
        private bool _recognizeLoopbackEnabled;
        private bool _recognizeMicEnabled;
        private bool _recordLoopbackEnabled;
        private bool _recordMicEnabled;
        private AutoGainProcessor _autoGainProcessor;

        public event EventHandler<TranslationItem>? OnRealtimeTranslationReceived;
        public event EventHandler<TranslationItem>? OnFinalTranslationReceived;
        public event EventHandler<string>? OnStatusChanged;
        public event EventHandler<string>? OnReconnectTriggered;
        public event EventHandler<double>? OnAudioLevelUpdated;
        public event EventHandler<string>? OnDiagnosticsUpdated;
        public SpeechTranslationService(AzureSpeechConfig config, Action<string>? auditLog = null)
        {
            _config = config;
            _auditLog = auditLog;
            _isTranslating = false;
            _autoGainProcessor = CreateAutoGainProcessor();
            InitializeSessionFile();
        }

        private void InitializeSessionFile()
        {
            try
            {

                var sessionsPath = PathManager.Instance.SessionsPath;

                if (!Directory.Exists(sessionsPath))
                {
                    Directory.CreateDirectory(sessionsPath);
                }

                _currentSessionFilePath = PathManager.Instance.GetSessionFile(
                    $"Session_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

                using (var fileStream = new FileStream(_currentSessionFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                using (var writer = new StreamWriter(fileStream))
                {
                    writer.WriteLine($"=== 实时翻译会话记录 - {DateTime.Now} ===");
                    writer.WriteLine();
                    writer.Flush();
                }

                OnStatusChanged?.Invoke(this, $"会话文件已创建: {_currentSessionFilePath}");
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke(this, $"创建会话文件失败: {ex.Message}");
            }
        }
        public async Task<bool> StartTranslationAsync()
        {
            if (_isTranslating)
                return true;

            try
            {
                ResetManagedReconnectState();
                _currentRunStamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                _sessionStartUtc = DateTime.UtcNow;
                _audioConfig = CreateAudioConfigAndStartSource();
                InitializeSubtitleWriters();
                var speechConfig = CreateSpeechConfig();
                _recognizer = CreateTranslationRecognizer(speechConfig, _audioConfig);

                _recognizer.Recognized += OnRecognized;
                _recognizer.Recognizing += OnRecognizing;
                _recognizer.Canceled += OnCanceled;
                _recognizer.SessionStarted += OnSessionStarted;
                _recognizer.SessionStopped += OnSessionStopped;

                await _recognizer.StartContinuousRecognitionAsync();
                _isTranslating = true;
                _lastRecognitionUtc = DateTime.UtcNow;
                _lastAudioActivityUtc = DateTime.UtcNow;
                PublishDiagnostics(force: true);
                StartNoResponseMonitor();

                var inputName = GetInputDisplayName();
                var statusMessage = _config.FilterModalParticles
                    ? $"正在监听：{inputName}... (已启用语气助词过滤)"
                    : $"正在监听：{inputName}...";
                OnStatusChanged?.Invoke(this, statusMessage);
                return true;
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke(this, $"启动翻译失败: {ex.Message}");
                await CleanupAudioAsync().ConfigureAwait(false);
                _isTranslating = false;
                return false;
            }
        }

        public async Task StopTranslationAsync()
        {
            if (!_isTranslating || _recognizer == null)
                return;

            try
            {
                StopNoResponseMonitor();
                ResetManagedReconnectState();
                lock (_liveRoutingDebounceLock)
                {
                    _liveRoutingDebounceCts?.Cancel();
                    _liveRoutingDebounceCts?.Dispose();
                    _liveRoutingDebounceCts = null;
                }
                await _recognizer.StopContinuousRecognitionAsync();
                _recognizer.Recognized -= OnRecognized;
                _recognizer.Recognizing -= OnRecognizing;
                _recognizer.Canceled -= OnCanceled;
                _recognizer.SessionStarted -= OnSessionStarted;
                _recognizer.SessionStopped -= OnSessionStopped;

                _recognizer.Dispose();
                _recognizer = null;
                _isTranslating = false;
                PublishAudioLevel(0);
                OnDiagnosticsUpdated?.Invoke(this, "诊断: 已停止");

                DisposeSubtitleWriters();

                await CleanupAudioAsync().ConfigureAwait(false);

                OnStatusChanged?.Invoke(this, "翻译已停止");
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke(this, $"停止翻译失败: {ex.Message}");
            }
        }

        private void OnRecognized(object? sender, TranslationRecognitionEventArgs e)
        {
            if (e.Result.Reason == ResultReason.TranslatedSpeech)
            {
                _lastRecognitionUtc = DateTime.UtcNow;
                ResetManagedReconnectState();
                string originalText = e.Result.Text;
                string translatedText = e.Result.Translations.ContainsKey(_config.TargetLanguage)
                    ? e.Result.Translations[_config.TargetLanguage]
                    : "";

                if (_config.FilterModalParticles)
                {
                    originalText = FilterModalParticles(originalText);
                }
                var translationItem = new TranslationItem
                {
                    Timestamp = DateTime.Now,
                    OriginalText = originalText,
                    TranslatedText = translatedText
                };

                SaveTranslationToFile(translationItem);
                WriteSubtitleEntry(e.Result, translatedText);
                OnFinalTranslationReceived?.Invoke(this, translationItem);

                OnStatusChanged?.Invoke(this, "收到最终识别结果");
            }
        }

        private void OnRecognizing(object? sender, TranslationRecognitionEventArgs e)
        {
            if (e.Result.Reason == ResultReason.TranslatingSpeech)
            {
                _lastRecognitionUtc = DateTime.UtcNow;
                ResetManagedReconnectState();
                string originalText = e.Result.Text;
                string translatedText = e.Result.Translations.ContainsKey(_config.TargetLanguage)
                    ? e.Result.Translations[_config.TargetLanguage]
                    : "";

                if (_config.FilterModalParticles)
                {
                    originalText = FilterModalParticles(originalText);
                }

                var translationItem = new TranslationItem
                {
                    Timestamp = DateTime.Now,
                    OriginalText = originalText,
                    TranslatedText = translatedText
                };
                OnRealtimeTranslationReceived?.Invoke(this, translationItem);

                OnStatusChanged?.Invoke(this, "实时更新 (中间结果)");
            }
        }

        private void OnCanceled(object? sender, TranslationRecognitionCanceledEventArgs e)
        {
            if (e.Reason == CancellationReason.Error)
            {
                OnStatusChanged?.Invoke(this, $"语音服务连接异常：{e.ErrorCode}。系统将尝试自动恢复。{(string.IsNullOrWhiteSpace(e.ErrorDetails) ? string.Empty : $" 详情：{e.ErrorDetails}")}");
                ScheduleManagedReconnect($"检测到连接异常（{e.ErrorCode}）", immediateFirstAttempt: true);
            }
        }

        private void OnSessionStarted(object? sender, SessionEventArgs e)
        {
            ResetManagedReconnectState();
            var inputName = GetInputDisplayName();
            var statusMessage = _config.FilterModalParticles
                ? $"正在监听：{inputName}... (已启用语气助词过滤) (点击停止退出)"
                : $"正在监听：{inputName}... (点击停止退出)";

            OnStatusChanged?.Invoke(this, statusMessage);
        }

        private void OnSessionStopped(object? sender, SessionEventArgs e)
        {
            OnStatusChanged?.Invoke(this, "翻译会话已结束");
        }

        private string FilterModalParticles(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            string[] fillers = { 
                "啊", "呀", "吧", "啦", "嘛", "呢", "哦", "呐", "哈", "呵", "嗯", "唉", "哎",
                "那个", "这个", "就是", "然后", "就是说", "怎么说", "你知道", "对吧", "是吧",
                "呃", "额", "嗯嗯", "啊啊", "哦哦"
            };

            string result = text;

            foreach (string filler in fillers)
            {
                result = System.Text.RegularExpressions.Regex.Replace(result, $@"^{System.Text.RegularExpressions.Regex.Escape(filler)}[，,]?\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                result = System.Text.RegularExpressions.Regex.Replace(result, $@"\s+{System.Text.RegularExpressions.Regex.Escape(filler)}\s+", " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                result = System.Text.RegularExpressions.Regex.Replace(result, $@"{System.Text.RegularExpressions.Regex.Escape(filler)}([。！？，,]?)$", "$1", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                result = result.Replace($"，{filler}", "，")
                               .Replace($"。{filler}", "。")
                               .Replace($"？{filler}", "？")
                               .Replace($"！{filler}", "！");
            }

            result = System.Text.RegularExpressions.Regex.Replace(result, @"\s+", " ");
            result = System.Text.RegularExpressions.Regex.Replace(result, @"[，,]\s*[，,]", "，");
            result = result.Trim();

            return result;
        }

        private void SaveTranslationToFile(TranslationItem item)
        {
            try
            {
                if (string.IsNullOrEmpty(_currentSessionFilePath))
                    return;

                using (var fileStream = new FileStream(_currentSessionFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                using (var writer = new StreamWriter(fileStream))
                {
                    writer.WriteLine($"[{item.Timestamp:HH:mm:ss}]");
                    writer.WriteLine($"原文: {item.OriginalText}");
                    writer.WriteLine($"译文: {item.TranslatedText}");
                    writer.WriteLine();
                    writer.Flush();
                }

                item.HasBeenWrittenToFile = true;
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke(this, $"保存翻译到文件失败: {ex.Message}");
            }
        }

        private void InitializeSubtitleWriters()
        {
            DisposeSubtitleWriters();

            _subtitleIndex = 1;
            _lastSubtitleEnd = TimeSpan.Zero;

            if (!_config.ExportSrtSubtitles && !_config.ExportVttSubtitles)
            {
                return;
            }

            var sessionsPath = PathManager.Instance.SessionsPath;
            Directory.CreateDirectory(sessionsPath);
            var baseName = GetSubtitleBaseName();

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

        private string GetSubtitleBaseName()
        {
            var audioPath = _currentAudioMp3Path ?? _currentAudioWavPath;
            if (!string.IsNullOrWhiteSpace(audioPath))
            {
                return Path.GetFileNameWithoutExtension(audioPath);
            }

            return $"Audio_{_currentRunStamp}";
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
                    // ignore
                }

                try
                {
                    _vttWriter?.Flush();
                    _vttWriter?.Dispose();
                }
                catch
                {
                    // ignore
                }

                _srtWriter = null;
                _vttWriter = null;
            }
        }

        private void WriteSubtitleEntry(TranslationRecognitionResult result, string translatedText)
        {
            if (string.IsNullOrWhiteSpace(translatedText))
            {
                return;
            }

            if (_srtWriter == null && _vttWriter == null)
            {
                return;
            }

            if (!TryGetSubtitleTiming(result, out var start, out var end))
            {
                var fallbackEnd = DateTime.UtcNow - _sessionStartUtc;
                if (fallbackEnd < _lastSubtitleEnd + TimeSpan.FromMilliseconds(200))
                {
                    fallbackEnd = _lastSubtitleEnd + TimeSpan.FromMilliseconds(200);
                }
                start = _lastSubtitleEnd;
                end = fallbackEnd;
            }

            if (end <= start)
            {
                end = start + TimeSpan.FromMilliseconds(300);
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

        private static string FormatSrtTime(TimeSpan time)
        {
            if (time < TimeSpan.Zero)
            {
                time = TimeSpan.Zero;
            }

            return string.Format("{0:00}:{1:00}:{2:00},{3:000}",
                (int)time.TotalHours,
                time.Minutes,
                time.Seconds,
                time.Milliseconds);
        }

        private static string FormatVttTime(TimeSpan time)
        {
            if (time < TimeSpan.Zero)
            {
                time = TimeSpan.Zero;
            }

            return string.Format("{0:00}:{1:00}:{2:00}.{3:000}",
                (int)time.TotalHours,
                time.Minutes,
                time.Seconds,
                time.Milliseconds);
        }

        private static bool TryGetSubtitleTiming(TranslationRecognitionResult result, out TimeSpan start, out TimeSpan end)
        {
            start = TimeSpan.Zero;
            end = TimeSpan.Zero;

            var json = result.Properties.GetProperty(PropertyId.SpeechServiceResponse_JsonResult);
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!TryReadOffsetDuration(doc.RootElement, out var offset, out var duration))
                {
                    return false;
                }

                start = TimeSpan.FromTicks(Math.Max(0, offset));
                end = start + TimeSpan.FromTicks(Math.Max(0, duration));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadOffsetDuration(JsonElement root, out long offset, out long duration)
        {
            offset = 0;
            duration = 0;

            if (root.TryGetProperty("Offset", out var offsetElement) &&
                root.TryGetProperty("Duration", out var durationElement) &&
                offsetElement.TryGetInt64(out offset) &&
                durationElement.TryGetInt64(out duration))
            {
                return true;
            }

            if (root.TryGetProperty("NBest", out var nbest) &&
                nbest.ValueKind == JsonValueKind.Array &&
                nbest.GetArrayLength() > 0)
            {
                var first = nbest[0];
                if (first.TryGetProperty("Offset", out var nbOffset) &&
                    first.TryGetProperty("Duration", out var nbDuration) &&
                    nbOffset.TryGetInt64(out offset) &&
                    nbDuration.TryGetInt64(out duration))
                {
                    return true;
                }
            }

            return false;
        }
        public async Task UpdateConfigAsync(AzureSpeechConfig newConfig)
        {
            bool wasTranslating = _isTranslating;

            _auditLog?.Invoke($"[翻译流] 配置更新开始 翻译中={wasTranslating}");

            if (wasTranslating)
            {
                OnStatusChanged?.Invoke(this, "配置已更改，正在重新连接...");
                await StopTranslationAsync();
                _auditLog?.Invoke("[翻译流] 配置更新 停止翻译完成");
            }

            _config = newConfig;
            _autoGainProcessor = CreateAutoGainProcessor();

            if (wasTranslating && _config.IsValid())
            {
                await StartTranslationAsync();
                _auditLog?.Invoke("[翻译流] 配置更新 重新启动翻译完成");
                OnStatusChanged?.Invoke(this, "配置更新完成，翻译已重新开始");
            }
            else if (wasTranslating && !_config.IsValid())
            {
                _auditLog?.Invoke("[翻译流] 配置更新 配置无效 翻译未重启");
                OnStatusChanged?.Invoke(this, "配置无效，翻译已停止");
            }
            else
            {
                _auditLog?.Invoke("[翻译流] 配置更新完成 无需重启翻译");
            }
        }

        public bool TryApplyLiveAudioRoutingFromCurrentConfig(int fadeMilliseconds = 30)
        {
            if (!_isTranslating || _naudioSource == null)
            {
                return false;
            }

            ScheduleDebouncedLiveRoutingApply(Math.Clamp(fadeMilliseconds, 10, 50));
            return true;
        }

        private void ScheduleDebouncedLiveRoutingApply(int fadeMilliseconds)
        {
            lock (_liveRoutingDebounceLock)
            {
                var hadPending = _liveRoutingDebounceCts != null;
                var version = ++_liveRoutingRequestVersion;
                _liveRoutingDebounceCts?.Cancel();
                _liveRoutingDebounceCts?.Dispose();
                _liveRoutingDebounceCts = new CancellationTokenSource();
                var token = _liveRoutingDebounceCts.Token;
                _liveRoutingDebouncePendingCount = hadPending
                    ? _liveRoutingDebouncePendingCount + 1
                    : 1;

                _auditLog?.Invoke($"[翻译流] 实时路由立即应用 次数={_liveRoutingDebouncePendingCount} 淡入毫秒={fadeMilliseconds}");

                _ = Task.Run(async () =>
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    ApplyLiveRoutingNow(fadeMilliseconds, version);
                    await Task.CompletedTask;
                }, token);
            }
        }

        private void ApplyLiveRoutingNow(int fadeMilliseconds, int requestVersion)
        {
            if (!_isTranslating || _naudioSource == null)
            {
                return;
            }

            lock (_liveRoutingApplyLock)
            {
                if (requestVersion != _liveRoutingRequestVersion)
                {
                    _auditLog?.Invoke($"[翻译流] 实时路由应用 已跳过 version={requestVersion} current={_liveRoutingRequestVersion}");
                    return;
                }

                try
                {
                    var debounceCount = _liveRoutingDebouncePendingCount;
                    _liveRoutingDebouncePendingCount = 0;

                    var (recLoopback, recMic) = GetRecognitionRouting();
                    var prevRecLoopback = _recognizeLoopbackEnabled;
                    var prevRecMic = _recognizeMicEnabled;
                    _recognizeLoopbackEnabled = recLoopback;
                    _recognizeMicEnabled = recMic;

                    var inputChanged = recMic && !string.Equals(_recognizeInputDeviceIdInUse, _config.SelectedAudioDeviceId ?? "", StringComparison.Ordinal);
                    var outputChanged = recLoopback && !string.Equals(_recognizeOutputDeviceIdInUse, _config.SelectedOutputDeviceId ?? "", StringComparison.Ordinal);
                    var topologyChanged = _naudioSource.HasLoopbackCapture != recLoopback ||
                                          _naudioSource.HasMicCapture != recMic;
                    var deviceChanged = inputChanged || outputChanged;
                    _auditLog?.Invoke($"[翻译流] 实时路由应用 合并次数={debounceCount} 识别目标[回环:{recLoopback},麦:{recMic}] 拓扑变更={topologyChanged} 设备变更={deviceChanged}");

                    if (topologyChanged || deviceChanged)
                    {
                        RebuildRecognitionAudioSource(recLoopback, recMic,
                            topologyChanged ? "拓扑变化" : "设备切换");
                    }
                    else
                    {
                        _naudioSource.UpdateRouting(recLoopback, recMic, fadeMilliseconds);
                    }

                    if (prevRecMic != recMic || prevRecLoopback != recLoopback)
                    {
                        _auditLog?.Invoke($"[翻译流] 识别切换信号 回环:{prevRecLoopback}->{recLoopback} 麦克风:{prevRecMic}->{recMic}");
                    }

                    if (_highQualityRecorder != null)
                    {
                        var (recordLoopback, recordMic) = GetRecordingRouting();
                        var prevRecordLoopback = _recordLoopbackEnabled;
                        var prevRecordMic = _recordMicEnabled;
                        _recordLoopbackEnabled = recordLoopback;
                        _recordMicEnabled = recordMic;
                        _highQualityRecorder.UpdateRouting(recordLoopback, recordMic, fadeMilliseconds);

                        if (prevRecordMic != recordMic || prevRecordLoopback != recordLoopback)
                        {
                            _auditLog?.Invoke($"[录制流] 录制切换信号 回环:{prevRecordLoopback}->{recordLoopback} 麦克风:{prevRecordMic}->{recordMic}");
                        }
                    }

                    PublishDiagnostics(force: true);
                }
                catch (Exception ex)
                {
                    OnStatusChanged?.Invoke(this, $"音频热切换失败: {ex.Message}");
                }
            }
        }

        private void RebuildRecognitionAudioSource(bool enableLoopback, bool enableMic, string reason)
        {
            lock (_audioSourceSwapLock)
            {
                var oldSource = _naudioSource;
                if (oldSource == null)
                {
                    return;
                }

                var newSource = new WasapiPcm16AudioSource(
                    _config.SelectedOutputDeviceId,
                    _config.SelectedAudioDeviceId,
                    _config.ChunkDurationMs,
                    enableLoopback,
                    enableMic);

                _auditLog?.Invoke($"[翻译流] 识别热重建 开始 原因={reason} 目标[回环:{enableLoopback},麦:{enableMic}] 旧拓扑[回环:{oldSource.HasLoopbackCapture},麦:{oldSource.HasMicCapture}] 输入设备ID='{_config.SelectedAudioDeviceId}' 输出设备ID='{_config.SelectedOutputDeviceId}'");

                newSource.Pcm16ChunkReady += OnPcm16ChunkReady;
                newSource.StartAsync().GetAwaiter().GetResult();
                _recognizeInputDeviceIdInUse = _config.SelectedAudioDeviceId ?? "";
                _recognizeOutputDeviceIdInUse = _config.SelectedOutputDeviceId ?? "";

                _naudioSource = newSource;

                try
                {
                    oldSource.Pcm16ChunkReady -= OnPcm16ChunkReady;
                    oldSource.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch
                {
                    // ignore old source disposal failures
                }

                _auditLog?.Invoke($"[翻译流] 识别热重建 完成 新拓扑[回环:{newSource.HasLoopbackCapture},麦:{newSource.HasMicCapture}]");
            }
        }

        private AudioConfig CreateAudioConfigAndStartSource()
        {
            if (!OperatingSystem.IsWindows())
            {
                if (_config.EnableRecording)
                {
                    OnStatusChanged?.Invoke(this, "当前平台不支持 NAudio 录音/设备枚举；识别将回退默认麦克风，且不会本地录音。");
                }
                _pushStream = null;
                _naudioSource = null;
                return AudioConfig.FromDefaultMicrophoneInput();
            }

            var streamFormat = AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1);
            _pushStream = AudioInputStream.CreatePushStream(streamFormat);

            var (recognizeLoopback, recognizeMic) = GetRecognitionRouting();
            _recognizeLoopbackEnabled = recognizeLoopback;
            _recognizeMicEnabled = recognizeMic;
            _auditLog?.Invoke($"[翻译流] 识别路由 音源模式={_config.AudioSourceMode} 输入识别={_config.UseInputForRecognition} 输出识别={_config.UseOutputForRecognition} => 回环={recognizeLoopback} 麦克风={recognizeMic} 输入设备ID='{_config.SelectedAudioDeviceId}' 输出设备ID='{_config.SelectedOutputDeviceId}'");
            _naudioSource = new WasapiPcm16AudioSource(
                _config.SelectedOutputDeviceId,
                _config.SelectedAudioDeviceId,
                _config.ChunkDurationMs,
                recognizeLoopback,
                recognizeMic);
            _recognizeInputDeviceIdInUse = _config.SelectedAudioDeviceId ?? "";
            _recognizeOutputDeviceIdInUse = _config.SelectedOutputDeviceId ?? "";
            _naudioSource.Pcm16ChunkReady += OnPcm16ChunkReady;

            try
            {
                _naudioSource.StartAsync().GetAwaiter().GetResult();

                if (_config.EnableRecording)
                {
                    _currentAudioMp3Path = PathManager.Instance.GetSessionFile($"Audio_{_currentRunStamp}.mp3");
                    try
                    {
                        var (autoGainEnabled, targetRms, minGain, maxGain, smoothing) = GetAutoGainSettings();
                        var (recordLoopback, recordMic) = GetRecordingRouting();
                        _recordLoopbackEnabled = recordLoopback;
                        _recordMicEnabled = recordMic;
                        _highQualityRecorder = new HighQualityRecorder(
                            _currentAudioMp3Path,
                            _config.SelectedOutputDeviceId,
                            _config.SelectedAudioDeviceId,
                            recordLoopback,
                            recordMic,
                            _config.RecordingMp3BitrateKbps,
                            autoGainEnabled,
                            targetRms,
                            minGain,
                            maxGain,
                            smoothing,
                            _auditLog);
                        _highQualityRecorder.StartAsync().GetAwaiter().GetResult();
                        OnStatusChanged?.Invoke(this, $"录音已开始: {_currentAudioMp3Path}");
                    }
                    catch (Exception ex)
                    {
                        _highQualityRecorder = null;
                        OnStatusChanged?.Invoke(this, $"录音启动失败: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke(this, $"启动音频采集失败，已回退默认麦克风: {ex.Message}");
                _naudioSource.Pcm16ChunkReady -= OnPcm16ChunkReady;
                _naudioSource.DisposeAsync().AsTask().GetAwaiter().GetResult();
                _naudioSource = null;

                _highQualityRecorder?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                _highQualityRecorder = null;
                _currentAudioWavPath = null;
                _currentAudioMp3Path = null;

                _pushStream.Dispose();
                _pushStream = null;

                return AudioConfig.FromDefaultMicrophoneInput();
            }

            return AudioConfig.FromStreamInput(_pushStream);
        }

        private (bool enableLoopback, bool enableMic) GetRecognitionRouting()
        {
            if (_config.AudioSourceMode == AudioSourceMode.Loopback)
            {
                // 用户明确选择环回时，严格只走环回，不做麦克风回退
                return (true, false);
            }

            if (_config.AudioSourceMode == AudioSourceMode.DefaultMic)
            {
                return (false, true);
            }

            var enableLoopback = _config.UseOutputForRecognition;
            var enableMic = _config.UseInputForRecognition;

            return (enableLoopback, enableMic);
        }

        private (bool enableLoopback, bool enableMic) GetRecordingRouting()
        {
            return _config.RecordingMode switch
            {
                RecordingMode.LoopbackOnly => (true, false),
                RecordingMode.LoopbackWithMic => (true, true),
                RecordingMode.MicOnly => (false, true),
                _ => (true, true)
            };
        }

        private SpeechTranslationConfig CreateSpeechConfig()
        {
            var activeSubscription = _config.GetActiveSubscription();
            SpeechTranslationConfig speechConfig;

            if (activeSubscription != null && activeSubscription.IsChinaEndpoint)
            {
                var host = new Uri(activeSubscription.GetCognitiveServicesHost());
                speechConfig = SpeechTranslationConfig.FromHost(host, _config.SubscriptionKey);
            }
            else
            {
                speechConfig = SpeechTranslationConfig.FromSubscription(_config.SubscriptionKey, _config.ServiceRegion);
            }
            if (!IsAutoDetectSourceLanguage())
            {
                speechConfig.SpeechRecognitionLanguage = _config.SourceLanguage;
            }
            speechConfig.AddTargetLanguage(_config.TargetLanguage);
            speechConfig.OutputFormat = OutputFormat.Detailed;

            if (_config.EnableAutoTimeout)
            {
                var initialSeconds = Math.Clamp(_config.InitialSilenceTimeoutSeconds, 1, 300);
                var endSeconds = Math.Clamp(_config.EndSilenceTimeoutSeconds, 1, 30);
                speechConfig.SetProperty(PropertyId.SpeechServiceConnection_InitialSilenceTimeoutMs,
                    (initialSeconds * 1000).ToString());
                speechConfig.SetProperty(PropertyId.SpeechServiceConnection_EndSilenceTimeoutMs,
                    (endSeconds * 1000).ToString());
            }
            else
            {
                speechConfig.SetProperty(PropertyId.SpeechServiceConnection_InitialSilenceTimeoutMs, "300000");
                speechConfig.SetProperty(PropertyId.SpeechServiceConnection_EndSilenceTimeoutMs, "300000");
            }

            if (_config.FilterModalParticles)
            {
                OnStatusChanged?.Invoke(this, "已启用语气助词过滤功能");
                speechConfig.SetProperty(PropertyId.SpeechServiceResponse_PostProcessingOption, "TrueText");
                speechConfig.SetProperty(PropertyId.SpeechServiceResponse_ProfanityOption, "Raw");
            }
            else
            {
                OnStatusChanged?.Invoke(this, "未启用语气助词过滤功能");
            }

            return speechConfig;
        }

        private void OnPcm16ChunkReady(byte[] chunk)
        {
            try
            {
                if (_config.AutoGainEnabled)
                {
                    _autoGainProcessor.ProcessInPlace(chunk, chunk.Length);
                }

                _lastChunkHadActivity = HasAudioActivity(chunk, _config.AudioActivityThreshold);
                if (_lastChunkHadActivity)
                {
                    _lastAudioActivityUtc = DateTime.UtcNow;
                }
                UpdateAudioLevel(chunk);
                _pushStream?.Write(chunk);
                PublishDiagnostics();
            }
            catch
            {
                // ignore: can happen during shutdown
            }
        }

        private AutoGainProcessor CreateAutoGainProcessor()
        {
            var (_, targetRms, minGain, maxGain, smoothing) = GetAutoGainSettings();
            return new AutoGainProcessor(targetRms, minGain, maxGain, smoothing);
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
            _smoothedAudioLevel = (_smoothedAudioLevel * 0.8) + (peak * 0.2);
            var gain = Math.Max(0.1, _config.AudioLevelGain);
            PublishAudioLevel(Math.Clamp(_smoothedAudioLevel * gain, 0, 1));
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

        private void PublishAudioLevel(double level)
        {
            OnAudioLevelUpdated?.Invoke(this, Math.Clamp(level, 0, 1));
        }

        private void PublishDiagnostics(bool force = false)
        {
            var now = DateTime.UtcNow;
            if (!force && (now - _lastDiagnosticsUtc).TotalMilliseconds < 400)
            {
                return;
            }

            _lastDiagnosticsUtc = now;
            var sinceRecognition = _lastRecognitionUtc == DateTime.MinValue
                ? -1
                : (int)Math.Max(0, (now - _lastRecognitionUtc).TotalSeconds);

            var recognitionPart = sinceRecognition < 0
                ? "尚无识别"
                : $"最近识别:{sinceRecognition}s";

            var message =
                $"诊断 识别[回环:{(_recognizeLoopbackEnabled ? "开" : "关")},麦:{(_recognizeMicEnabled ? "开" : "关")}] " +
                $"录制[回环:{(_recordLoopbackEnabled ? "开" : "关")},麦:{(_recordMicEnabled ? "开" : "关")}] " +
                $"活动:{(_lastChunkHadActivity ? "有" : "无")} 峰值:{_smoothedAudioLevel:F2} {recognitionPart}";

            OnDiagnosticsUpdated?.Invoke(this, message);
        }

        private static bool HasAudioActivity(byte[] chunk, int threshold)
        {
            var sampleThreshold = (short)Math.Clamp(Math.Abs(threshold), 50, 8000);
            for (var i = 0; i + 1 < chunk.Length; i += 2)
            {
                var sample = (short)(chunk[i] | (chunk[i + 1] << 8));
                if (sample >= sampleThreshold || sample <= -sampleThreshold)
                {
                    return true;
                }
            }

            return false;
        }

        private void StartNoResponseMonitor()
        {
            if (!_config.EnableNoResponseRestart)
            {
                return;
            }

            _noResponseMonitorCts?.Cancel();
            _noResponseMonitorCts = new CancellationTokenSource();
            var token = _noResponseMonitorCts.Token;

            _noResponseMonitorTask = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(500, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    if (!_isTranslating || _recognizer == null || !_config.EnableNoResponseRestart)
                    {
                        continue;
                    }

                    var thresholdSeconds = Math.Max(1, _config.NoResponseRestartSeconds);
                    var threshold = TimeSpan.FromSeconds(thresholdSeconds);
                    var now = DateTime.UtcNow;

                    if (now - _lastAudioActivityUtc > threshold)
                    {
                        // 静音属于有效输入状态：保持当前路由并继续推流，不做自动回退或重建设备
                        continue;
                    }

                    if (now - _lastRecognitionUtc < threshold)
                    {
                        continue;
                    }

                    if (now - _lastAudioActivityUtc > threshold)
                    {
                        continue;
                    }

                    ScheduleManagedReconnect($"无回显超过 {thresholdSeconds} 秒", immediateFirstAttempt: true);
                }
            }, token);
        }

        private void StopNoResponseMonitor()
        {
            _noResponseMonitorCts?.Cancel();
            _noResponseMonitorCts?.Dispose();
            _noResponseMonitorCts = null;
            _noResponseMonitorTask = null;
        }

        private void ResetManagedReconnectState()
        {
            lock (_managedReconnectLock)
            {
                _managedReconnectAttempt = 0;
                _managedReconnectCts?.Cancel();
                _managedReconnectCts?.Dispose();
                _managedReconnectCts = null;
            }
        }

        private void ScheduleManagedReconnect(string reason, bool immediateFirstAttempt)
        {
            if (!_isTranslating || _audioConfig == null)
            {
                return;
            }

            CancellationTokenSource reconnectCts;
            int attempt;
            int delaySeconds;

            lock (_managedReconnectLock)
            {
                if (_managedReconnectCts != null)
                {
                    return;
                }

                attempt = _managedReconnectAttempt + 1;
                delaySeconds = GetReconnectDelaySeconds(attempt, immediateFirstAttempt);
                reconnectCts = new CancellationTokenSource();
                _managedReconnectCts = reconnectCts;
                _managedReconnectAttempt = attempt;
            }

            var status = delaySeconds > 0
                ? $"{reason}，{delaySeconds} 秒后将进行第 {attempt} 次重试。"
                : $"{reason}，正在进行第 {attempt} 次重试。";
            OnStatusChanged?.Invoke(this, status);

            _ = Task.Run(async () =>
            {
                try
                {
                    if (delaySeconds > 0)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(delaySeconds), reconnectCts.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                finally
                {
                    lock (_managedReconnectLock)
                    {
                        if (ReferenceEquals(_managedReconnectCts, reconnectCts))
                        {
                            _managedReconnectCts?.Dispose();
                            _managedReconnectCts = null;
                        }
                    }
                }

                var success = await RestartRecognitionAsync($"{reason}（第 {attempt} 次）").ConfigureAwait(false);
                if (!success && _isTranslating)
                {
                    ScheduleManagedReconnect(reason, immediateFirstAttempt: false);
                }
            });
        }

        private int GetReconnectDelaySeconds(int attempt, bool immediateFirstAttempt)
        {
            var baseDelay = Math.Max(2, _config.NoResponseRestartSeconds);
            if (attempt <= 1 && immediateFirstAttempt)
            {
                return 0;
            }

            if (attempt <= 1)
            {
                return baseDelay;
            }

            var factor = (int)Math.Pow(2, Math.Min(attempt - 2, 3));
            return Math.Min(baseDelay * factor, 30);
        }

        private async Task<bool> RestartRecognitionAsync(string reason)
        {
            await _restartLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_isTranslating || _audioConfig == null)
                {
                    return false;
                }

                OnReconnectTriggered?.Invoke(this, reason);
                OnStatusChanged?.Invoke(this, $"{reason}，正在恢复语音连接...");

                try
                {
                    if (_recognizer != null)
                    {
                        await _recognizer.StopContinuousRecognitionAsync().ConfigureAwait(false);
                    }
                }
                catch
                {
                    // ignore stop failures
                }

                if (_recognizer != null)
                {
                    _recognizer.Recognized -= OnRecognized;
                    _recognizer.Recognizing -= OnRecognizing;
                    _recognizer.Canceled -= OnCanceled;
                    _recognizer.SessionStarted -= OnSessionStarted;
                    _recognizer.SessionStopped -= OnSessionStopped;

                    _recognizer.Dispose();
                }
                _recognizer = null;

                var speechConfig = CreateSpeechConfig();
                _recognizer = CreateTranslationRecognizer(speechConfig, _audioConfig);

                _recognizer.Recognized += OnRecognized;
                _recognizer.Recognizing += OnRecognizing;
                _recognizer.Canceled += OnCanceled;
                _recognizer.SessionStarted += OnSessionStarted;
                _recognizer.SessionStopped += OnSessionStopped;

                await _recognizer.StartContinuousRecognitionAsync().ConfigureAwait(false);
                _lastRecognitionUtc = DateTime.UtcNow;
                OnStatusChanged?.Invoke(this, "语音连接已恢复，翻译继续进行。"
                );
                return true;
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke(this, $"本次重试未成功：{ex.Message}");
                return false;
            }
            finally
            {
                _restartLock.Release();
            }
        }

        private string GetInputDisplayName()
        {
            return _config.AudioSourceMode switch
            {
                AudioSourceMode.DefaultMic => "默认麦克风",
                AudioSourceMode.Loopback => "系统输出回环",
                AudioSourceMode.CaptureDevice => "选择输入设备",
                _ => "音频输入"
            };
        }

        private TranslationRecognizer CreateTranslationRecognizer(SpeechTranslationConfig speechConfig, AudioConfig audioConfig)
        {
            if (IsAutoDetectSourceLanguage())
            {
                var autoConfig = AutoDetectSourceLanguageConfig.FromLanguages(AutoDetectSourceLanguages);
                return new TranslationRecognizer(speechConfig, autoConfig, audioConfig);
            }

            return new TranslationRecognizer(speechConfig, audioConfig);
        }

        private bool IsAutoDetectSourceLanguage()
        {
            return string.Equals(_config.SourceLanguage, "auto", StringComparison.OrdinalIgnoreCase);
        }

        private async Task CleanupAudioAsync()
        {
            if (_highQualityRecorder != null)
            {
                try
                {
                    await _highQualityRecorder.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // ignore
                }
            }

            var wavPath = _currentAudioWavPath;
            var mp3Path = _currentAudioMp3Path;
            _highQualityRecorder = null;
            _currentAudioWavPath = null;
            _currentAudioMp3Path = null;

            if (_naudioSource != null)
            {
                try
                {
                    _naudioSource.Pcm16ChunkReady -= OnPcm16ChunkReady;
                    await _naudioSource.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // ignore
                }
                finally
                {
                    _naudioSource = null;
                }
            }

            if (_pushStream != null)
            {
                try
                {
                    _pushStream.Close();
                    _pushStream.Dispose();
                }
                catch
                {
                    // ignore
                }
                finally
                {
                    _pushStream = null;
                }
            }

            if (_audioConfig != null)
            {
                try
                {
                    _audioConfig.Dispose();
                }
                catch
                {
                    // ignore
                }
                finally
                {
                    _audioConfig = null;
                }
            }

            ResetManagedReconnectState();

            if (!string.IsNullOrWhiteSpace(wavPath) && _config.EnableRecording)
            {
                if (string.IsNullOrWhiteSpace(mp3Path))
                {
                    mp3Path = PathManager.Instance.GetSessionFile($"Audio_{DateTime.Now:yyyyMMdd_HHmmss}.mp3");
                }

                var bitrate = _config.RecordingMp3BitrateKbps;
                var deleteWav = _config.DeleteWavAfterMp3;

                _pendingTranscodeTask = WavToMp3Transcoder
                    .TranscodeToMp3AndOptionallyDeleteWavAsync(wavPath, mp3Path, bitrate, deleteWav)
                    .ContinueWith(t =>
                    {
                        if (t.IsFaulted)
                        {
                            OnStatusChanged?.Invoke(this, $"MP3 转码失败（WAV 已保留）: {t.Exception?.GetBaseException().Message}");
                        }
                        else if (t.IsCanceled)
                        {
                            OnStatusChanged?.Invoke(this, "MP3 转码已取消（WAV 已保留）");
                        }
                        else
                        {
                            OnStatusChanged?.Invoke(this, $"MP3 已生成: {mp3Path}");
                        }
                    });

                OnStatusChanged?.Invoke(this, "正在后台转码为 MP3...");
            }
        }
    }
}

