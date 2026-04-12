using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using TrueFluentPro.Services.Speech;
using TrueFluentPro.Services.Storage;
using TrueFluentPro.ViewModels.Settings;

namespace TrueFluentPro.ViewModels
{
    public partial class AudioLabViewModel : ViewModelBase, IDisposable
    {
        private int _audioLibraryRefreshVersion;
        // ── 六色说话人调色板 ───────────────────────────────────
        private static readonly string[] SpeakerColors =
        {
            "#4A90D9", // 蓝
            "#E8913A", // 橙
            "#50B86C", // 绿
            "#9B59B6", // 紫
            "#E74C3C", // 红
            "#F1C40F"  // 黄
        };

        // ── 服务依赖 ──────────────────────────────────────────
        private readonly IAiInsightService _aiInsightService;
        private readonly IAzureTokenProviderStore _azureTokenProviderStore;
        private readonly IModelRuntimeResolver _modelRuntimeResolver;
        private readonly ISpeechResourceRuntimeResolver _speechResourceRuntimeResolver;
        private readonly IAiAudioTranscriptionService _aiAudioTranscriptionService;
        private readonly Func<AzureSpeechConfig> _configProvider;
        private readonly ConfigurationService _configService;
        private readonly AudioLifecyclePipelineService _pipeline;
        private readonly IAudioTaskQueueService? _queueService;
        private readonly ITaskEventBus? _eventBus;

        // ── 会话管理（每个音频文件一个独立会话） ─────────────────
        private readonly Dictionary<string, AudioFileSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
        private AudioFileSession? _activeSession;

        private sealed class AudioFileSession : IDisposable
        {
            public string FilePath { get; }
            public CancellationTokenSource Cts { get; private set; } = new();

            public List<TranscriptSegment> Segments { get; set; } = new();
            public string SummaryMarkdown { get; set; } = "";
            public string InsightMarkdown { get; set; } = "";
            public string PodcastMarkdown { get; set; } = "";
            public MindMapNode? MindMapRoot { get; set; }
            public List<ResearchTopic> ResearchTopics { get; set; } = new();
            public string ResearchReportMarkdown { get; set; } = "";
            public ResearchPhase CurrentResearchPhase { get; set; } = ResearchPhase.Idle;
            public string StatusMessage { get; set; } = "就绪";
            public bool IsGenerating { get; set; }
            public bool IsSummaryEditing { get; set; }
            public string SummaryEditText { get; set; } = "";

            public AudioFileSession(string filePath) => FilePath = filePath;

            public CancellationToken ResetCts()
            {
                Cts?.Cancel();
                Cts?.Dispose();
                Cts = new CancellationTokenSource();
                return Cts.Token;
            }

            public void Dispose()
            {
                Cts?.Cancel();
                Cts?.Dispose();
            }
        }

        private bool IsActive(AudioFileSession s) => s == _activeSession;

        private void PostIfActive(AudioFileSession s, Action action)
        {
            if (IsActive(s)) Dispatcher.UIThread.Post(action);
        }

        private async Task InvokeIfActiveAsync(AudioFileSession s, Action action)
        {
            if (IsActive(s)) await Dispatcher.UIThread.InvokeAsync(action);
        }

        private void SyncUiFromSession(AudioFileSession s)
        {
            SummaryMarkdown = s.SummaryMarkdown;
            InsightMarkdown = s.InsightMarkdown;
            PodcastMarkdown = s.PodcastMarkdown;
            MindMapRoot = s.MindMapRoot;
            ResearchReportMarkdown = s.ResearchReportMarkdown;
            CurrentResearchPhase = s.CurrentResearchPhase;
            StatusMessage = s.StatusMessage;
            IsGenerating = s.IsGenerating;
            IsSummaryEditing = s.IsSummaryEditing;
            SummaryEditText = s.SummaryEditText;
            CurrentSegment = null;

            Segments.Clear();
            foreach (var seg in s.Segments) Segments.Add(seg);

            AutoTags.Clear();

            ResearchTopics.Clear();
            foreach (var t in s.ResearchTopics) ResearchTopics.Add(t);

            RaiseAllCommandsCanExecuteChanged();
        }

        private void SaveUiToSession(AudioFileSession s)
        {
            s.SummaryMarkdown = SummaryMarkdown;
            s.InsightMarkdown = InsightMarkdown;
            s.PodcastMarkdown = PodcastMarkdown;
            s.MindMapRoot = MindMapRoot;
            s.ResearchReportMarkdown = ResearchReportMarkdown;
            s.CurrentResearchPhase = CurrentResearchPhase;
            s.StatusMessage = StatusMessage;
            s.IsGenerating = IsGenerating;
            s.IsSummaryEditing = IsSummaryEditing;
            s.SummaryEditText = SummaryEditText;
            s.Segments = new List<TranscriptSegment>(Segments);
            s.ResearchTopics = new List<ResearchTopic>(ResearchTopics);
        }

        private void SyncSegmentsFromSession(AudioFileSession s)
        {
            Segments.Clear();
            foreach (var seg in s.Segments) Segments.Add(seg);
        }

        // ── 当前音频 ──────────────────────────────────────────
        [ObservableProperty]
        private string _currentFileName = "";

        [ObservableProperty]
        private string _currentFilePath = "";

        [ObservableProperty]
        private string _breadcrumbText = "听析中心";

        // ── 标签页 ────────────────────────────────────────────
        [ObservableProperty]
        private AudioLabTabKind _selectedTab = AudioLabTabKind.Summary;

        // ── 总结 ──────────────────────────────────────────────
        [ObservableProperty]
        private string _summaryMarkdown = "";

        [ObservableProperty]
        private bool _isSummaryEditing;

        [ObservableProperty]
        private string _summaryEditText = "";

        public ObservableCollection<string> AutoTags { get; } = new();

        // ── 转录 ──────────────────────────────────────────────
        public ObservableCollection<TranscriptSegment> Segments { get; } = new();

        [ObservableProperty]
        private TranscriptSegment? _currentSegment;

        // ── 思维导图 ──────────────────────────────────────────
        [ObservableProperty]
        private MindMapNode? _mindMapRoot;

        // ── 顿悟 / 播客 ──────────────────────────────────────
        [ObservableProperty]
        private string _insightMarkdown = "";

        [ObservableProperty]
        private string _podcastMarkdown = "";

        // ── 播客音频播放 ──────────────────────────────────────
        public AudioLabPlaybackViewModel PodcastPlayback { get; }

        [ObservableProperty]
        private bool _hasPodcastAudioFile;

        [ObservableProperty]
        private string _podcastAudioPath = "";

        /// <summary>播客播放栏是否可见（仅播客标签页 + 有音频文件时显示）</summary>
        public bool ShowPodcastPlayback => HasPodcastAudioFile && SelectedTab == AudioLabTabKind.Podcast;

        partial void OnHasPodcastAudioFileChanged(bool value) => OnPropertyChanged(nameof(ShowPodcastPlayback));
        partial void OnSelectedTabChanged(AudioLabTabKind value) => OnPropertyChanged(nameof(ShowPodcastPlayback));

        // ── 深度研究 ──────────────────────────────────────────
        public ObservableCollection<ResearchTopic> ResearchTopics { get; } = new();

        [ObservableProperty]
        private string _researchReportMarkdown = "";

        [ObservableProperty]
        private ResearchPhase _currentResearchPhase = ResearchPhase.Idle;

        // ── 生成状态 ──────────────────────────────────────────
        [ObservableProperty]
        private bool _isGenerating;

        partial void OnIsGeneratingChanged(bool value)
        {
            OnPropertyChanged(nameof(HasActiveProcessing));
        }

        [ObservableProperty]
        private string _statusMessage = "就绪";

        // ── 各阶段内容状态（队列化三态） ─────────────────────
        [ObservableProperty]
        private StageContentState _transcribeState = StageContentState.Empty;

        [ObservableProperty]
        private StageContentState _summaryState = StageContentState.Empty;

        [ObservableProperty]
        private StageContentState _mindMapState = StageContentState.Empty;

        [ObservableProperty]
        private StageContentState _insightState = StageContentState.Empty;

        [ObservableProperty]
        private StageContentState _podcastState = StageContentState.Empty;

        [ObservableProperty]
        private StageContentState _researchState = StageContentState.Empty;

        // ── 各阶段 Processing 计算属性（供 XAML 绑定） ─────────
        public bool IsTranscribeProcessing => TranscribeState == StageContentState.Processing;
        public bool IsSummaryProcessing => SummaryState == StageContentState.Processing;
        public bool IsMindMapProcessing => MindMapState == StageContentState.Processing;
        public bool IsInsightProcessing => InsightState == StageContentState.Processing;
        public bool IsPodcastProcessing => PodcastState == StageContentState.Processing;
        public bool IsResearchProcessing => ResearchState == StageContentState.Processing;

        partial void OnTranscribeStateChanged(StageContentState value)
        {
            OnPropertyChanged(nameof(IsTranscribeProcessing));
            OnPropertyChanged(nameof(HasActiveProcessing));
        }

        partial void OnSummaryStateChanged(StageContentState value)
        {
            OnPropertyChanged(nameof(IsSummaryProcessing));
            OnPropertyChanged(nameof(HasActiveProcessing));
        }

        partial void OnMindMapStateChanged(StageContentState value)
        {
            OnPropertyChanged(nameof(IsMindMapProcessing));
            OnPropertyChanged(nameof(HasActiveProcessing));
        }

        partial void OnInsightStateChanged(StageContentState value)
        {
            OnPropertyChanged(nameof(IsInsightProcessing));
            OnPropertyChanged(nameof(HasActiveProcessing));
        }

        partial void OnPodcastStateChanged(StageContentState value)
        {
            OnPropertyChanged(nameof(IsPodcastProcessing));
            OnPropertyChanged(nameof(HasActiveProcessing));
        }

        partial void OnResearchStateChanged(StageContentState value)
        {
            OnPropertyChanged(nameof(IsResearchProcessing));
            OnPropertyChanged(nameof(HasActiveProcessing));
        }

        /// <summary>
        /// 是否有任何阶段正在处理。
        /// IsGenerating 为前台直接生成模式；IsXxxProcessing 为后台任务队列模式
        /// （来自 StageContentState.Processing，即有 Pending/Running 的队列任务）。
        /// </summary>
        public bool HasActiveProcessing => IsGenerating
            || IsTranscribeProcessing || IsSummaryProcessing || IsMindMapProcessing
            || IsInsightProcessing || IsPodcastProcessing || IsResearchProcessing;

        /// <summary>当前音频的 audio_item_id（DB 主键），LoadAudioFile 后设置。</summary>
        private string? _currentAudioItemId;

        // ── 播放器 ────────────────────────────────────────────
        public AudioLabPlaybackViewModel Playback { get; }

        // ── 文件面板 ──────────────────────────────────────────
        private readonly ObservableCollection<MediaFileItem> _audioFiles = new();
        public ObservableCollection<MediaFileItem> AudioFiles => _audioFiles;

        [ObservableProperty]
        private MediaFileItem? _selectedAudioFile;

        private bool _isFilePanelOpen;
        public bool IsFilePanelOpen
        {
            get => _isFilePanelOpen;
            set
            {
                if (!SetProperty(ref _isFilePanelOpen, value)) return;
                FilePanelStateChanged?.Invoke(value);
                // 持久化到配置
                var config = _configProvider();
                config.AudioLabFilePanelOpen = value;
                _ = _configService.SaveConfigAsync(config);
            }
        }

        /// <summary>面板展开/收起事件，View 订阅后调整宽度。</summary>
        public event Action<bool>? FilePanelStateChanged;

        // ── Commands ──────────────────────────────────────────
        public ICommand LoadFileCommand { get; }
        public ICommand ToggleEditModeCommand { get; }
        public ICommand RefreshAudioFilesCommand { get; }
        public ICommand TranscribeCommand { get; }
        public ICommand GenerateSummaryCommand { get; }
        public ICommand GenerateInsightCommand { get; }
        public ICommand GenerateResearchCommand { get; }
        public ICommand GeneratePodcastCommand { get; }
        public ICommand StopGenerationCommand { get; }

        /// <summary>控制面板 ViewModel — 管理生命周期和 TTS 配置。</summary>
        public AudioLabControlPanelViewModel ControlPanel { get; }

        public AudioLabViewModel(
            IAiInsightService aiInsightService,
            IAzureTokenProviderStore azureTokenProviderStore,
            IModelRuntimeResolver modelRuntimeResolver,
            ISpeechResourceRuntimeResolver speechResourceRuntimeResolver,
            IAiAudioTranscriptionService aiAudioTranscriptionService,
            Func<AzureSpeechConfig> configProvider,
            ConfigurationService configService,
            AudioLifecyclePipelineService pipeline,
            AudioLabControlPanelViewModel controlPanel,
            IAudioTaskQueueService? queueService = null,
            ITaskEventBus? eventBus = null)
        {
            _aiInsightService = aiInsightService;
            _azureTokenProviderStore = azureTokenProviderStore;
            _modelRuntimeResolver = modelRuntimeResolver;
            _speechResourceRuntimeResolver = speechResourceRuntimeResolver;
            _aiAudioTranscriptionService = aiAudioTranscriptionService;
            _configProvider = configProvider;
            _configService = configService;
            _pipeline = pipeline;
            _queueService = queueService;
            _eventBus = eventBus;
            ControlPanel = controlPanel;

            // 从配置恢复文件面板展开状态（默认展开）
            _isFilePanelOpen = configProvider().AudioLabFilePanelOpen;

            // 订阅自动补齐请求
            ControlPanel.AutoFillMissingRequested += OnAutoFillMissingRequested;
            ControlPanel.PodcastAudioSynthesized += OnPodcastAudioSynthesized;

            Playback = new AudioLabPlaybackViewModel(
                () => Segments,
                seg => CurrentSegment = seg,
                () => CurrentSegment);

            // 播客音频专用播放器（无段落跳转）
            var emptySegments = new ObservableCollection<TranscriptSegment>();
            PodcastPlayback = new AudioLabPlaybackViewModel(
                () => emptySegments,
                _ => { },
                () => null);

            LoadFileCommand = new RelayCommand(p =>
            {
                if (p is string path) LoadAudioFile(path);
            });

            ToggleEditModeCommand = new RelayCommand(_ =>
            {
                if (IsSummaryEditing)
                {
                    SummaryMarkdown = SummaryEditText;
                    IsSummaryEditing = false;
                }
                else
                {
                    SummaryEditText = SummaryMarkdown;
                    IsSummaryEditing = true;
                }
            });

            RefreshAudioFilesCommand = new RelayCommand(_ => _ = RefreshAudioFilesAsync());
            TranscribeCommand = new RelayCommand(_ => _ = TranscribeAsync(), _ => !IsGenerating && !string.IsNullOrWhiteSpace(CurrentFilePath));
            GenerateSummaryCommand = new RelayCommand(_ => _ = GenerateSummaryAsync(), _ => !IsGenerating && Segments.Count > 0);
            GenerateInsightCommand = new RelayCommand(_ => _ = GenerateInsightAsync(), _ => !IsGenerating && Segments.Count > 0);
            GenerateResearchCommand = new RelayCommand(_ => _ = GenerateResearchAsync(), _ => !IsGenerating && Segments.Count > 0);
            GeneratePodcastCommand = new RelayCommand(_ => _ = GeneratePodcastAsync(), _ => !IsGenerating && Segments.Count > 0);
            StopGenerationCommand = new RelayCommand(_ => _activeSession?.Cts?.Cancel(), _ => IsGenerating);

            // 订阅任务事件总线（队列化模式下，任务完成后自动刷新 UI）
            if (_eventBus != null)
            {
                _eventBus.TaskStatusChanged += OnTaskStatusChanged;
                _eventBus.TaskProgressUpdated += OnTaskProgressUpdated;
            }
        }

        partial void OnSelectedAudioFileChanged(MediaFileItem? value)
        {
            if (value != null && !string.IsNullOrWhiteSpace(value.FullPath))
                LoadAudioFile(value.FullPath);
        }

        // ── 加载音频 ──────────────────────────────────────────
        public void LoadAudioFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return;

            // 保存当前会话的 UI 编辑状态
            if (_activeSession != null)
                SaveUiToSession(_activeSession);

            // 获取或创建目标文件会话
            if (!_sessions.TryGetValue(filePath, out var session))
            {
                session = new AudioFileSession(filePath);
                _sessions[filePath] = session;
            }

            _activeSession = session;
            CurrentFilePath = session.FilePath;
            CurrentFileName = Path.GetFileName(session.FilePath);
            BreadcrumbText = $"听析中心 / {CurrentFileName}";

            Playback.LoadAudio(filePath);

            // 确保音频在数据库中有记录，同步控制面板
            var audioItemId = _pipeline.EnsureAudioItem(filePath);
            _currentAudioItemId = audioItemId;
            ControlPanel.SetCurrentAudio(audioItemId, CurrentFileName);

            // 尝试从生命周期缓存恢复内容（避免重新生成）
            TryRestoreFromLifecycleCache(session, audioItemId);

            // 恢复播客音频播放器（如果有已合成的播客音频）
            var podcastPath = _pipeline.TryLoadCachedFilePath(audioItemId, AudioLifecycleStage.PodcastAudio);
            if (!string.IsNullOrWhiteSpace(podcastPath) && File.Exists(podcastPath))
            {
                PodcastAudioPath = podcastPath;
                HasPodcastAudioFile = true;
                PodcastPlayback.LoadAudio(podcastPath);
            }
            else
            {
                PodcastAudioPath = "";
                HasPodcastAudioFile = false;
            }

            // 从会话恢复所有状态到 UI
            SyncUiFromSession(session);
            SelectedTab = AudioLabTabKind.Summary;

            // 全新会话（无转录、无生成中、数据库无转录记录）才自动开始转录
            var hasDbTranscription = _pipeline.GetCompletedStages(audioItemId)
                .Contains(AudioLifecycleStage.Transcribed);
            if (session.Segments.Count == 0 && !session.IsGenerating && !hasDbTranscription)
            {
                // 优先通过队列提交（后台执行），回退到直接执行
                if (_queueService != null)
                    _queueService.SubmitAll(audioItemId);
                else
                    _ = TranscribeSessionAsync(session);
            }

            // 刷新各阶段状态
            RefreshStageStates(audioItemId);
        }

        // ── 转录段合并（从外部 SubtitleCue 列表生成） ─────────
        public void LoadTranscriptFromCues(IList<SubtitleCue> cues)
        {
            Segments.Clear();
            if (cues == null || cues.Count == 0) return;

            var speakerMap = new Dictionary<string, int>();
            TranscriptSegment? current = null;

            foreach (var cue in cues)
            {
                var (speaker, text) = ParseSpeaker(cue.Text);

                if (current != null && current.Speaker == speaker)
                {
                    // 合并到当前段
                    current.Text += "\n" + text;
                    current.SourceCues.Add(cue);
                }
                else
                {
                    // 新段
                    if (!speakerMap.ContainsKey(speaker))
                        speakerMap[speaker] = speakerMap.Count;

                    current = new TranscriptSegment
                    {
                        Speaker = speaker,
                        SpeakerIndex = speakerMap[speaker],
                        StartTime = cue.Start,
                        Text = text,
                        SourceCues = new List<SubtitleCue> { cue }
                    };
                    Segments.Add(current);
                }
            }
        }

        /// <summary>
        /// 获取说话人对应的颜色。
        /// </summary>
        public static string GetSpeakerColor(int speakerIndex)
            => SpeakerColors[speakerIndex % SpeakerColors.Length];

        /// <summary>
        /// 从 cue 文本中解析说话人前缀，例如 "Speaker 1: 内容" → ("Speaker 1", "内容")
        /// </summary>
        private static (string speaker, string text) ParseSpeaker(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return ("未知", "");

            // 常见格式：Speaker 1: xxx / 说话人1：xxx
            var colonIdx = raw.IndexOf(':');
            if (colonIdx < 0) colonIdx = raw.IndexOf('：');

            if (colonIdx > 0 && colonIdx < 30)
            {
                var speaker = raw[..colonIdx].Trim();
                var text = raw[(colonIdx + 1)..].Trim();
                if (!string.IsNullOrEmpty(speaker))
                    return (speaker, text);
            }

            return ("未知", raw.Trim());
        }

        // ── 音频文件扫描（与 FileLibraryViewModel 同源逻辑） ──
        public async Task RefreshAudioFilesAsync(CancellationToken cancellationToken = default)
        {
            var refreshVersion = Interlocked.Increment(ref _audioLibraryRefreshVersion);

            List<MediaFileItem> files;
            try
            {
                files = await Task.Run(() =>
                {
                    var sessionsPath = PathManager.Instance.SessionsPath;
                    var result = new List<MediaFileItem>();
                    if (!Directory.Exists(sessionsPath))
                        return result;

                    var paths = Directory.GetFiles(sessionsPath, "*.mp3")
                        .Concat(Directory.GetFiles(sessionsPath, "*.wav"))
                        .OrderByDescending(path => File.GetLastWriteTimeUtc(path));

                    foreach (var file in paths)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        result.Add(new MediaFileItem
                        {
                            Name = Path.GetFileName(file),
                            FullPath = file
                        });
                    }
                    return result;
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (refreshVersion != _audioLibraryRefreshVersion)
                    return;

                var selectedPath = SelectedAudioFile?.FullPath;
                _audioFiles.Clear();
                foreach (var f in files)
                    _audioFiles.Add(f);

                if (!string.IsNullOrWhiteSpace(selectedPath))
                {
                    var matched = _audioFiles.FirstOrDefault(item =>
                        string.Equals(item.FullPath, selectedPath, StringComparison.OrdinalIgnoreCase));
                    SelectedAudioFile = matched;
                }
            });
        }

        // ── AI/ASR 配置解析（与 BatchProcessingViewModel 同模式） ──

        private static string GetEndpointProfileKey(AiEndpoint endpoint) => $"endpoint_{endpoint.Id}";

        private static ModelReference? SelectTextModelReference(AiConfig ai)
            => ai.ReviewModelRef ?? ai.SummaryModelRef ?? ai.QuickModelRef ?? ai.InsightModelRef;

        private bool TryBuildTextRuntimeConfig(
            AzureSpeechConfig config,
            out AiChatRequestConfig runtimeRequest,
            out AiEndpoint? endpoint,
            out string errorMessage)
        {
            runtimeRequest = new AiChatRequestConfig();
            endpoint = null;
            errorMessage = "";

            // 优先使用听析中心专用的文本模型引用
            var reference = config.AudioLabTextModelRef;

            // 回退到全局 AiConfig 的模型引用
            if (reference == null)
            {
                var ai = config.AiConfig;
                if (ai == null)
                {
                    errorMessage = "AI 配置不存在，请先在「设置 → 听析中心」中配置文本模型。";
                    return false;
                }
                reference = SelectTextModelReference(ai);
            }

            if (_modelRuntimeResolver.TryResolve(config, reference, ModelCapability.Text, out var runtime, out var resolveError)
                && runtime != null)
            {
                endpoint = runtime.Endpoint;
                runtimeRequest = runtime.CreateChatRequest(config.AiConfig?.SummaryEnableReasoning ?? false);
                return true;
            }

            errorMessage = string.IsNullOrWhiteSpace(resolveError)
                ? "未配置可用的文本模型，请在「设置 → 听析中心」中选择文本生成模型。"
                : resolveError;
            return false;
        }

        // TryResolveAiSpeechRuntime 已移除：转录改用 Speech Batch API，
        // 语音端点解析在 TranscribeWithAadAsync / TranscribeWithTraditionalAsync 中完成。

        private async Task<AiInsightService> CreateAuthenticatedInsightServiceAsync(
            AiChatRequestConfig runtimeRequest, AiEndpoint? endpoint, CancellationToken token)
        {
            AzureTokenProvider? tokenProvider = null;
            if (runtimeRequest.AzureAuthMode == AzureAuthMode.AAD && endpoint != null)
            {
                tokenProvider = await _azureTokenProviderStore.GetAuthenticatedProviderAsync(
                    GetEndpointProfileKey(endpoint),
                    runtimeRequest.AzureTenantId,
                    runtimeRequest.AzureClientId);
            }
            return new AiInsightService(tokenProvider);
        }

        private static string FormatTranscriptForAi(IList<TranscriptSegment> segments)
        {
            if (segments.Count == 0) return "(暂无转录内容)";

            var sb = new StringBuilder();
            foreach (var seg in segments)
            {
                var time = seg.StartTime.ToString(@"hh\:mm\:ss");
                sb.AppendLine($"[{time}] {seg.Speaker}: {seg.Text}");
            }
            return sb.ToString();
        }

        private static List<TranscriptSegment> BuildSegmentsFromCues(IList<SubtitleCue> cues)
        {
            var segments = new List<TranscriptSegment>();
            if (cues == null || cues.Count == 0) return segments;

            var speakerMap = new Dictionary<string, int>();

            // 每条 cue 保持为独立段落，保留断句粒度
            foreach (var cue in cues)
            {
                var (speaker, text) = ParseSpeaker(cue.Text);
                if (string.IsNullOrWhiteSpace(text)) continue;

                if (!speakerMap.ContainsKey(speaker))
                    speakerMap[speaker] = speakerMap.Count;

                segments.Add(new TranscriptSegment
                {
                    Speaker = speaker,
                    SpeakerIndex = speakerMap[speaker],
                    StartTime = cue.Start,
                    Text = text,
                    SourceCues = new List<SubtitleCue> { cue }
                });
            }
            return segments;
        }

        // ── 转录 ─────────────────────────────────────────────

        private Task TranscribeAsync()
        {
            var session = _activeSession;
            return session != null ? TranscribeSessionAsync(session) : Task.CompletedTask;
        }

        private async Task TranscribeSessionAsync(AudioFileSession session)
        {
            if (string.IsNullOrWhiteSpace(session.FilePath) || !File.Exists(session.FilePath))
                return;

            var config = _configProvider();
            var token = session.ResetCts();

            session.IsGenerating = true;
            session.StatusMessage = "正在转录音频...";
            await InvokeIfActiveAsync(session, () =>
            {
                IsGenerating = true;
                StatusMessage = session.StatusMessage;
                RaiseAllCommandsCanExecuteChanged();
            });

            try
            {
                var splitOptions = new BatchSubtitleSplitOptions
                {
                    EnableSentenceSplit = config.EnableBatchSubtitleSentenceSplit,
                    SplitOnComma = config.BatchSubtitleSplitOnComma,
                    MaxChars = config.BatchSubtitleMaxChars,
                    MaxDurationSeconds = config.BatchSubtitleMaxDurationSeconds,
                    PauseSplitMs = config.BatchSubtitlePauseSplitMs
                };
                var locale = RealtimeSpeechTranscriber.GetTranscriptionSourceLanguage(
                    config.AudioLabSourceLanguage ?? config.SourceLanguage);

                List<SubtitleCue> cues;

                if (config.AudioLabSpeechMode == 0)
                    cues = await TranscribeWithAadAsync(session, config, locale, splitOptions, token);
                else
                    cues = await TranscribeWithTraditionalAsync(session, config, locale, splitOptions, token);

                // 将结果写入会话
                session.Segments = BuildSegmentsFromCues(cues);

                if (session.Segments.Count == 0)
                {
                    session.StatusMessage = "转录完成，但未识别到内容。";
                    await InvokeIfActiveAsync(session, () => StatusMessage = session.StatusMessage);
                    return;
                }

                session.StatusMessage = $"转录完成：{session.Segments.Count} 个段落";
                await InvokeIfActiveAsync(session, () =>
                {
                    SyncSegmentsFromSession(session);
                    StatusMessage = session.StatusMessage;
                    RaiseAllCommandsCanExecuteChanged();
                });

                // 保存转录到生命周期数据库
                SaveTranscriptionToLifecycle(session);

                // 转录完成后并发填充所有下游内容
                await AutoFillDownstreamAsync(session);
            }
            catch (OperationCanceledException)
            {
                session.StatusMessage = "转录已取消";
                await InvokeIfActiveAsync(session, () => StatusMessage = session.StatusMessage);
            }
            catch (Exception ex)
            {
                session.StatusMessage = $"转录失败：{ex.Message}";
                await InvokeIfActiveAsync(session, () => StatusMessage = session.StatusMessage);
            }
            finally
            {
                session.IsGenerating = false;
                await InvokeIfActiveAsync(session, () =>
                {
                    IsGenerating = false;
                    RaiseAllCommandsCanExecuteChanged();
                });
            }
        }

        private async Task<List<SubtitleCue>> TranscribeWithAadAsync(
            AudioFileSession session, AzureSpeechConfig config, string locale,
            BatchSubtitleSplitOptions splitOptions, CancellationToken token)
        {
            var endpointId = config.AudioLabAadEndpointId;
            if (string.IsNullOrWhiteSpace(endpointId))
                throw new InvalidOperationException("听析中心未选择 AAD 终结点，请在「设置 → 听析中心」中选择 Foundry 终结点。");

            var endpoint = config.Endpoints.FirstOrDefault(e => e.Id == endpointId);
            if (endpoint == null || !endpoint.IsEnabled)
                throw new InvalidOperationException("听析中心选择的 AAD 终结点已不存在或已禁用，请重新配置。");

            if (!config.BatchStorageIsValid || string.IsNullOrWhiteSpace(config.BatchStorageConnectionString))
                throw new InvalidOperationException("存储账号未配置或未验证，批量转录需要 Azure Blob Storage。请在「设置 → 录音与存储」中配置。");

            var tokenProvider = await _azureTokenProviderStore.GetAuthenticatedProviderAsync(
                GetEndpointProfileKey(endpoint),
                endpoint.AzureTenantId,
                endpoint.AzureClientId);

            if (tokenProvider?.IsLoggedIn != true)
                throw new InvalidOperationException("AAD 认证未登录。请先在终结点设置中完成 AAD 登录。");

            session.StatusMessage = "转录：上传音频...";
            await InvokeIfActiveAsync(session, () => StatusMessage = session.StatusMessage);
            var (audioContainer, _) = await BlobStorageService.GetBatchContainersAsync(
                config.BatchStorageConnectionString,
                config.BatchAudioContainerName,
                config.BatchResultContainerName,
                token);
            var uploadedBlob = await BlobStorageService.UploadAudioToBlobAsync(session.FilePath, audioContainer, token);
            var contentUrl = BlobStorageService.CreateBlobReadSasUri(uploadedBlob, TimeSpan.FromHours(24));

            var subdomain = AudioLabSectionVM.ParseSubdomainFromFoundryUrl(endpoint.BaseUrl);
            if (string.IsNullOrWhiteSpace(subdomain))
                throw new InvalidOperationException($"无法从终结点「{endpoint.Name}」的 URL 解析子域名。");
            var cognitiveHost = AudioLabSectionVM.IsAzureChinaUrl(endpoint.BaseUrl)
                ? $"https://{subdomain}.cognitiveservices.azure.cn"
                : $"https://{subdomain}.cognitiveservices.azure.com";
            var batchEndpoint = $"{cognitiveHost}/speechtotext/v3.1/transcriptions";

            session.StatusMessage = "转录：提交批量任务...";
            await InvokeIfActiveAsync(session, () => StatusMessage = session.StatusMessage);
            var (cues, _) = await SpeechBatchApiClient.BatchTranscribeSpeechToCuesAsync(
                contentUrl, locale, batchEndpoint,
                async ct => await tokenProvider.GetTokenAsync(ct),
                token,
                status => { session.StatusMessage = status; PostIfActive(session, () => StatusMessage = status); },
                splitOptions);

            return cues;
        }

        private async Task<List<SubtitleCue>> TranscribeWithTraditionalAsync(
            AudioFileSession session, AzureSpeechConfig config, string locale,
            BatchSubtitleSplitOptions splitOptions, CancellationToken token)
        {
            AzureSubscription? subscription = null;
            var endpointId = config.AudioLabSpeechEndpointId;

            if (!string.IsNullOrWhiteSpace(endpointId))
            {
                var ep = config.Endpoints.FirstOrDefault(e => e.Id == endpointId && e.IsSpeechEndpoint && e.IsEnabled);
                if (ep != null)
                {
                    var region = !string.IsNullOrWhiteSpace(ep.SpeechRegion)
                        ? ep.SpeechRegion
                        : AzureSubscription.ParseRegionFromEndpoint(ep.SpeechEndpoint ?? "");
                    subscription = new AzureSubscription
                    {
                        Name = ep.Name,
                        SubscriptionKey = ep.SpeechSubscriptionKey,
                        ServiceRegion = region ?? "",
                        Endpoint = ep.SpeechEndpoint ?? ""
                    };
                }
            }

            if (subscription == null || !subscription.IsValid())
            {
                if (!_speechResourceRuntimeResolver.TryResolveActive(config, SpeechCapability.BatchSpeechToText, out var resolution, out var resolveError)
                    || resolution == null)
                {
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(resolveError)
                        ? "未配置语音转写资源，请在「设置 → 听析中心」中选择语音终结点。"
                        : resolveError);
                }
                if (resolution.MicrosoftSubscription != null)
                    subscription = resolution.MicrosoftSubscription;
                else
                    throw new InvalidOperationException("当前语音资源不支持批量转录。请在「设置 → 听析中心」中选择传统语音终结点或切换到 AAD 模式。");
            }

            if (!config.BatchStorageIsValid || string.IsNullOrWhiteSpace(config.BatchStorageConnectionString))
                throw new InvalidOperationException("存储账号未配置或未验证，批量转录需要 Azure Blob Storage。请在「设置 → 录音与存储」中配置。");

            session.StatusMessage = "转录：上传音频...";
            await InvokeIfActiveAsync(session, () => StatusMessage = session.StatusMessage);
            var (audioContainer, _) = await BlobStorageService.GetBatchContainersAsync(
                config.BatchStorageConnectionString,
                config.BatchAudioContainerName,
                config.BatchResultContainerName,
                token);
            var uploadedBlob = await BlobStorageService.UploadAudioToBlobAsync(session.FilePath, audioContainer, token);
            var contentUrl = BlobStorageService.CreateBlobReadSasUri(uploadedBlob, TimeSpan.FromHours(24));

            session.StatusMessage = "转录：提交批量任务...";
            await InvokeIfActiveAsync(session, () => StatusMessage = session.StatusMessage);
            var (cues, _) = await SpeechBatchApiClient.BatchTranscribeSpeechToCuesAsync(
                contentUrl, locale, subscription, token,
                status => { session.StatusMessage = status; PostIfActive(session, () => StatusMessage = status); },
                splitOptions);

            return cues;
        }

        // ── 总结生成 ──────────────────────────────────────────

        private Task GenerateSummaryAsync()
        {
            var session = _activeSession;
            return session != null ? GenerateSummarySessionAsync(session) : Task.CompletedTask;
        }

        private async Task GenerateSummarySessionAsync(AudioFileSession session)
        {
            if (session.Segments.Count == 0) return;

            var config = _configProvider();
            if (!TryBuildTextRuntimeConfig(config, out var runtimeRequest, out var endpoint, out var errorMessage))
            {
                session.StatusMessage = $"总结未启动：{errorMessage}";
                await InvokeIfActiveAsync(session, () => StatusMessage = session.StatusMessage);
                return;
            }

            var token = session.ResetCts();

            session.IsGenerating = true;
            session.SummaryMarkdown = "";
            session.StatusMessage = "正在生成总结...";
            await InvokeIfActiveAsync(session, () =>
            {
                IsGenerating = true;
                SummaryMarkdown = "";
                StatusMessage = session.StatusMessage;
                RaiseAllCommandsCanExecuteChanged();
            });

            try
            {
                var aiService = await CreateAuthenticatedInsightServiceAsync(runtimeRequest, endpoint, token);
                var transcript = FormatTranscriptForAi(session.Segments);

                const string systemPrompt = "你是一个专业的音频内容分析助手。请根据转录文本生成简洁的 Markdown 总结。\n\n## 概要\n用一段话概括核心内容（50-100字），标注关键时间范围。\n\n## 关键要点\n提炼 3-5 个最重要的发现或观点，每条一句话，标注 [MM:SS]。\n\n## 行动建议\n如果有值得跟进的建议或结论，列出 2-3 条。\n\n## 关键词\n提取 3-5 个关键词。\n\n注意：时间戳格式统一用 [MM:SS]，内容要简洁，不要重复。";
                var userContent = $"以下是音频转录内容：\n\n{transcript}";

                var sb = new StringBuilder();
                await aiService.StreamChatAsync(
                    runtimeRequest, systemPrompt, userContent,
                    chunk =>
                    {
                        sb.Append(chunk);
                        var text = sb.ToString();
                        session.SummaryMarkdown = text;
                        PostIfActive(session, () => SummaryMarkdown = text);
                    },
                    token, AiChatProfile.Summary,
                    enableReasoning: runtimeRequest.SummaryEnableReasoning);

                session.SummaryMarkdown = sb.ToString();
                session.StatusMessage = "总结生成完成";
                await InvokeIfActiveAsync(session, () =>
                {
                    SummaryMarkdown = session.SummaryMarkdown;
                    StatusMessage = session.StatusMessage;
                });

                // 保存到生命周期数据库
                SaveSummaryToLifecycle(session);

                // 总结后自动生成思维导图
                await GenerateMindMapSessionAsync(session);
            }
            catch (OperationCanceledException)
            {
                session.StatusMessage = "总结生成已取消";
                await InvokeIfActiveAsync(session, () => StatusMessage = session.StatusMessage);
            }
            catch (Exception ex)
            {
                session.SummaryMarkdown += $"\n\n---\n**错误**: {ex.Message}";
                session.StatusMessage = $"总结生成失败：{ex.Message}";
                await InvokeIfActiveAsync(session, () =>
                {
                    SummaryMarkdown = session.SummaryMarkdown;
                    StatusMessage = session.StatusMessage;
                });
            }
            finally
            {
                session.IsGenerating = false;
                await InvokeIfActiveAsync(session, () =>
                {
                    IsGenerating = false;
                    RaiseAllCommandsCanExecuteChanged();
                });
            }
        }

        // ── 思维导图生成 ──────────────────────────────────────

        private Task GenerateMindMapAsync()
        {
            var session = _activeSession;
            return session != null ? GenerateMindMapSessionAsync(session) : Task.CompletedTask;
        }

        private async Task GenerateMindMapSessionAsync(AudioFileSession session)
        {
            if (session.Segments.Count == 0) return;

            var config = _configProvider();
            if (!TryBuildTextRuntimeConfig(config, out var runtimeRequest, out var endpoint, out _))
                return;

            var token = session.ResetCts();

            session.IsGenerating = true;
            session.StatusMessage = "正在生成思维导图...";
            await InvokeIfActiveAsync(session, () =>
            {
                IsGenerating = true;
                StatusMessage = session.StatusMessage;
                RaiseAllCommandsCanExecuteChanged();
            });

            try
            {
                var aiService = await CreateAuthenticatedInsightServiceAsync(runtimeRequest, endpoint, token);
                var transcript = FormatTranscriptForAi(session.Segments);

                const string systemPrompt = "你是一个结构化分析专家。根据音频转录文本生成思维导图的 JSON 结构。\n你必须严格输出有效 JSON，不要输出任何其他内容（不要 markdown 代码块标记）。\n格式：\n{\"label\":\"主题\",\"children\":[{\"label\":\"分支1\",\"children\":[{\"label\":\"要点1\"},{\"label\":\"要点2\"}]}]}\n层级不超过 3 层，每个分支不超过 5 个子节点。";
                var userContent = $"请根据以下转录内容生成思维导图结构：\n\n{transcript}";

                var sb = new StringBuilder();
                await aiService.StreamChatAsync(
                    runtimeRequest, systemPrompt, userContent,
                    chunk => sb.Append(chunk),
                    token, AiChatProfile.Quick);

                var json = sb.ToString().Trim();
                if (json.StartsWith("```"))
                {
                    var firstNewline = json.IndexOf('\n');
                    if (firstNewline > 0) json = json[(firstNewline + 1)..];
                    if (json.EndsWith("```")) json = json[..^3];
                    json = json.Trim();
                }

                var root = ParseMindMapJson(json);
                session.MindMapRoot = root;
                session.StatusMessage = "思维导图生成完成";
                await InvokeIfActiveAsync(session, () =>
                {
                    MindMapRoot = root;
                    StatusMessage = session.StatusMessage;
                });

                // 保存到生命周期数据库
                var audioItemId = _pipeline.EnsureAudioItem(session.FilePath);
                _pipeline.SaveStageContent(audioItemId, AudioLifecycleStage.MindMap, json);
                ControlPanel.RefreshLifecycleStatus();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                session.StatusMessage = $"思维导图生成失败：{ex.Message}";
                await InvokeIfActiveAsync(session, () => StatusMessage = session.StatusMessage);
            }
            finally
            {
                session.IsGenerating = false;
                await InvokeIfActiveAsync(session, () =>
                {
                    IsGenerating = false;
                    RaiseAllCommandsCanExecuteChanged();
                });
            }
        }

        private static MindMapNode? ParseMindMapJson(string json)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                return ParseMindMapElement(doc.RootElement, 0);
            }
            catch
            {
                return new MindMapNode { Title = "解析失败" };
            }
        }

        private static MindMapNode ParseMindMapElement(System.Text.Json.JsonElement element, int level)
        {
            var node = new MindMapNode
            {
                Title = element.TryGetProperty("label", out var lbl) ? lbl.GetString() ?? ""
                      : element.TryGetProperty("title", out var ttl) ? ttl.GetString() ?? "" : ""
            };

            if (element.TryGetProperty("children", out var children) && children.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var child in children.EnumerateArray())
                    node.Children.Add(ParseMindMapElement(child, level + 1));
            }

            return node;
        }

        // ── 顿悟生成 ──────────────────────────────────────────

        private async Task GenerateInsightAsync()
        {
            var session = _activeSession;
            if (session == null) return;
            await GenerateAiContentSessionAsync(session, "顿悟",
                "你是一个深度思考专家。根据音频转录内容，提供深层洞察和顿悟。\n请从以下角度分析：\n1. **隐含假设**：说话者可能不自知的假设\n2. **潜在矛盾**：观点之间的冲突\n3. **深层模式**：反复出现的主题或思维模式\n4. **未说出的内容**：重要但被忽略的方面\n5. **关联启发**：与其他领域的联系\n\n以 Markdown 格式输出，标注时间戳 [HH:MM:SS]。",
                (s, result) =>
                {
                    s.InsightMarkdown = result;
                    var audioItemId = _pipeline.EnsureAudioItem(s.FilePath);
                    _pipeline.SaveStageContent(audioItemId, AudioLifecycleStage.Insight, result);
                    ControlPanel.RefreshLifecycleStatus();
                },
                text => InsightMarkdown = text);
        }

        // ── 播客生成 ──────────────────────────────────────────

        private async Task GeneratePodcastAsync()
        {
            var session = _activeSession;
            if (session == null) return;
            await GenerateAiContentSessionAsync(session, "播客",
                "你是一个播客脚本编写专家。根据音频转录内容，生成一段适合播客的对话内容改写。\n\n严格使用以下格式，每行一句：\n发言人 A：[主持人台词]\n发言人 B：[嘉宾台词]\n\n要求：\n1. 对话总轮次控制在 40 轮以内（A 和 B 各约 20 轮）\n2. 每轮发言控制在 200 字以内\n3. 口语化、自然过渡\n4. 不要加 Markdown 格式、括号注释或舞台指导\n5. 第一行必须是发言人 A 的开场白\n6. 突出有趣的细节和故事",
                (s, result) =>
                {
                    s.PodcastMarkdown = result;
                    // 台本完整生成后才保存到生命周期数据库
                    var audioItemId = _pipeline.EnsureAudioItem(s.FilePath);
                    _pipeline.SaveStageContent(audioItemId, AudioLifecycleStage.PodcastScript, result);
                    ControlPanel.RefreshLifecycleStatus();
                    // 台本完整就绪后才触发 TTS 合成（不在流式期间触发）
                    if (ControlPanel.VoicesLoaded && ControlPanel.SpeakerProfiles.Any(p => p.Voice != null))
                        _ = ControlPanel.SynthesizePodcastAsync(result);
                },
                text => PodcastMarkdown = text);
        }

        // ── 深度研究生成 ──────────────────────────────────────

        private async Task GenerateResearchAsync()
        {
            var session = _activeSession;
            if (session == null) return;
            await GenerateResearchSessionAsync(session);
        }

        private async Task GenerateResearchSessionAsync(AudioFileSession session)
        {
            if (session.Segments.Count == 0) return;

            var config = _configProvider();
            if (!TryBuildTextRuntimeConfig(config, out var runtimeRequest, out var endpoint, out var errorMessage))
            {
                session.StatusMessage = $"研究未启动：{errorMessage}";
                await InvokeIfActiveAsync(session, () => StatusMessage = session.StatusMessage);
                return;
            }

            var token = session.ResetCts();

            session.IsGenerating = true;
            session.CurrentResearchPhase = ResearchPhase.GeneratingOutline;
            session.StatusMessage = "正在规划研究方向...";
            session.ResearchTopics = new List<ResearchTopic>();
            session.ResearchReportMarkdown = "";
            await InvokeIfActiveAsync(session, () =>
            {
                IsGenerating = true;
                CurrentResearchPhase = ResearchPhase.GeneratingOutline;
                StatusMessage = session.StatusMessage;
                ResearchTopics.Clear();
                ResearchReportMarkdown = "";
                RaiseAllCommandsCanExecuteChanged();
            });

            try
            {
                var aiService = await CreateAuthenticatedInsightServiceAsync(runtimeRequest, endpoint, token);
                var transcript = FormatTranscriptForAi(session.Segments);

                // Phase 1: 生成研究课题
                var planSb = new StringBuilder();
                await aiService.StreamChatAsync(
                    runtimeRequest,
                    "你是一个学术研究规划专家。根据音频转录内容，提出 3-5 个值得深入研究的课题。每个课题一行，格式为纯文本标题。不要编号，不要其他格式。",
                    $"请根据以下内容提出研究课题：\n\n{transcript}",
                    chunk => planSb.Append(chunk),
                    token, AiChatProfile.Quick);

                var topicLines = planSb.ToString()
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(l => !string.IsNullOrWhiteSpace(l) && l.Length > 2)
                    .Take(5)
                    .ToList();

                var topics = new List<ResearchTopic>();
                foreach (var line in topicLines)
                {
                    var title = line.TrimStart('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.', '、', '-', ' ');
                    if (string.IsNullOrWhiteSpace(title)) title = line;
                    topics.Add(new ResearchTopic { Title = title, IsSelected = true });
                }
                session.ResearchTopics = topics;
                session.CurrentResearchPhase = ResearchPhase.GeneratingReport;
                session.StatusMessage = "正在生成深度研究报告...";
                await InvokeIfActiveAsync(session, () =>
                {
                    ResearchTopics.Clear();
                    foreach (var t in topics) ResearchTopics.Add(t);
                    CurrentResearchPhase = ResearchPhase.GeneratingReport;
                    StatusMessage = session.StatusMessage;
                });

                // Phase 2: 生成研究报告
                var selectedTopics = string.Join("\n", topicLines);
                var reportSb = new StringBuilder();
                await aiService.StreamChatAsync(
                    runtimeRequest,
                    "你是一个深度研究分析师。用户提供了音频转录内容和研究课题列表。请针对每个课题展开深度分析，包括核心论点和支撑证据、不同视角和反驳、与现有知识体系的关联、进一步研究建议。以 Markdown 格式输出，使用标题分隔各课题。引用时标注 [HH:MM:SS]。",
                    $"研究课题：\n{selectedTopics}\n\n音频转录内容：\n{transcript}",
                    chunk =>
                    {
                        reportSb.Append(chunk);
                        var text = reportSb.ToString();
                        session.ResearchReportMarkdown = text;
                        PostIfActive(session, () => ResearchReportMarkdown = text);
                    },
                    token, AiChatProfile.Summary,
                    enableReasoning: runtimeRequest.SummaryEnableReasoning);

                session.ResearchReportMarkdown = reportSb.ToString();
                session.CurrentResearchPhase = ResearchPhase.ReportReady;
                session.StatusMessage = "深度研究完成";
                await InvokeIfActiveAsync(session, () =>
                {
                    ResearchReportMarkdown = session.ResearchReportMarkdown;
                    CurrentResearchPhase = ResearchPhase.ReportReady;
                    StatusMessage = session.StatusMessage;
                });

                // 保存到生命周期数据库
                var audioItemId = _pipeline.EnsureAudioItem(session.FilePath);
                _pipeline.SaveStageContent(audioItemId, AudioLifecycleStage.Research, session.ResearchReportMarkdown);
                ControlPanel.RefreshLifecycleStatus();
            }
            catch (OperationCanceledException)
            {
                session.CurrentResearchPhase = ResearchPhase.Idle;
                session.StatusMessage = "研究已取消";
                await InvokeIfActiveAsync(session, () =>
                {
                    CurrentResearchPhase = ResearchPhase.Idle;
                    StatusMessage = session.StatusMessage;
                });
            }
            catch (Exception ex)
            {
                session.ResearchReportMarkdown += $"\n\n---\n**错误**: {ex.Message}";
                session.CurrentResearchPhase = ResearchPhase.Idle;
                session.StatusMessage = $"研究失败：{ex.Message}";
                await InvokeIfActiveAsync(session, () =>
                {
                    ResearchReportMarkdown = session.ResearchReportMarkdown;
                    CurrentResearchPhase = ResearchPhase.Idle;
                    StatusMessage = session.StatusMessage;
                });
            }
            finally
            {
                session.IsGenerating = false;
                await InvokeIfActiveAsync(session, () =>
                {
                    IsGenerating = false;
                    RaiseAllCommandsCanExecuteChanged();
                });
            }
        }

        // ── 通用 AI 内容生成 ──────────────────────────────────

        private async Task GenerateAiContentSessionAsync(
            AudioFileSession session, string label, string systemPrompt,
            Action<AudioFileSession, string> onComplete, Action<string> uiSetter)
        {
            if (session.Segments.Count == 0) return;

            var config = _configProvider();
            if (!TryBuildTextRuntimeConfig(config, out var runtimeRequest, out var endpoint, out var errorMessage))
            {
                session.StatusMessage = $"{label}未启动：{errorMessage}";
                await InvokeIfActiveAsync(session, () => StatusMessage = session.StatusMessage);
                return;
            }

            var token = session.ResetCts();

            session.IsGenerating = true;
            session.StatusMessage = $"正在生成{label}...";
            await InvokeIfActiveAsync(session, () =>
            {
                IsGenerating = true;
                uiSetter("");
                StatusMessage = session.StatusMessage;
                RaiseAllCommandsCanExecuteChanged();
            });

            try
            {
                var aiService = await CreateAuthenticatedInsightServiceAsync(runtimeRequest, endpoint, token);
                var transcript = FormatTranscriptForAi(session.Segments);
                var userContent = $"以下是音频转录内容：\n\n{transcript}";

                var sb = new StringBuilder();
                await aiService.StreamChatAsync(
                    runtimeRequest, systemPrompt, userContent,
                    chunk =>
                    {
                        sb.Append(chunk);
                        var text = sb.ToString();
                        // 流式期间仅更新内存和 UI，不做持久化/下游触发
                        PostIfActive(session, () => uiSetter(text));
                    },
                    token, AiChatProfile.Quick);

                var result = sb.ToString();
                // 生成完成后执行完整回调（持久化 + 下游触发）
                onComplete(session, result);
                session.StatusMessage = $"{label}生成完成";
                await InvokeIfActiveAsync(session, () =>
                {
                    uiSetter(result);
                    StatusMessage = session.StatusMessage;
                });
            }
            catch (OperationCanceledException)
            {
                session.StatusMessage = $"{label}已取消";
                await InvokeIfActiveAsync(session, () => StatusMessage = session.StatusMessage);
            }
            catch (Exception ex)
            {
                session.StatusMessage = $"{label}失败：{ex.Message}";
                await InvokeIfActiveAsync(session, () => StatusMessage = session.StatusMessage);
            }
            finally
            {
                session.IsGenerating = false;
                await InvokeIfActiveAsync(session, () =>
                {
                    IsGenerating = false;
                    RaiseAllCommandsCanExecuteChanged();
                });
            }
        }

        private void RaiseAllCommandsCanExecuteChanged()
        {
            ((RelayCommand)TranscribeCommand).RaiseCanExecuteChanged();
            ((RelayCommand)GenerateSummaryCommand).RaiseCanExecuteChanged();
            ((RelayCommand)GenerateInsightCommand).RaiseCanExecuteChanged();
            ((RelayCommand)GenerateResearchCommand).RaiseCanExecuteChanged();
            ((RelayCommand)GeneratePodcastCommand).RaiseCanExecuteChanged();
            ((RelayCommand)StopGenerationCommand).RaiseCanExecuteChanged();
        }

        // ── 队列事件处理 ──────────────────────────────────────

        /// <summary>
        /// 任务状态变更事件处理 — 当队列中的任务完成/失败时，
        /// 从 DB 刷新对应阶段的内容到 UI。
        /// </summary>
        private void OnTaskStatusChanged(TaskStatusChangedEvent e)
        {
            // 仅处理与当前音频相关的事件
            if (_currentAudioItemId == null || e.AudioItemId != _currentAudioItemId)
                return;

            var session = _activeSession;
            if (session == null) return;

            // 刷新阶段状态
            RefreshStageStates(e.AudioItemId);

            // 任务完成时，从 DB 重新加载内容到 UI
            if (e.NewStatus == AudioTaskStatus.Completed)
            {
                RefreshStageContentFromDb(session, e.AudioItemId, e.Stage);
            }
            else if (e.NewStatus == AudioTaskStatus.Failed)
            {
                StatusMessage = $"{e.Stage} 失败：{e.ErrorMessage}";
            }
        }

        /// <summary>
        /// 任务进度更新事件处理 — 实时更新底部状态栏显示详细进度。
        /// </summary>
        private void OnTaskProgressUpdated(TaskProgressEvent e)
        {
            if (_currentAudioItemId == null || e.AudioItemId != _currentAudioItemId)
                return;

            StatusMessage = e.ProgressMessage;
        }

        /// <summary>
        /// 刷新各阶段的 UI 状态（Empty/Processing/Ready）。
        /// </summary>
        private void RefreshStageStates(string audioItemId)
        {
            var completedStages = new HashSet<AudioLifecycleStage>(
                _pipeline.GetCompletedStages(audioItemId));

            // 查询活跃任务
            var activeStages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_queueService != null)
            {
                var activeTasks = _queueService.Query(new AudioTaskQueryFilter
                {
                    AudioItemId = audioItemId,
                    Status = AudioTaskStatus.Pending,
                    Limit = 20
                });
                foreach (var t in activeTasks)
                    activeStages.Add(t.Stage);

                var runningTasks = _queueService.Query(new AudioTaskQueryFilter
                {
                    AudioItemId = audioItemId,
                    Status = AudioTaskStatus.Running,
                    Limit = 20
                });
                foreach (var t in runningTasks)
                    activeStages.Add(t.Stage);
            }

            TranscribeState = GetStageState(AudioLifecycleStage.Transcribed, completedStages, activeStages);
            SummaryState = GetStageState(AudioLifecycleStage.Summarized, completedStages, activeStages);
            MindMapState = GetStageState(AudioLifecycleStage.MindMap, completedStages, activeStages);
            InsightState = GetStageState(AudioLifecycleStage.Insight, completedStages, activeStages);
            PodcastState = GetStageState(AudioLifecycleStage.PodcastScript, completedStages, activeStages);
            ResearchState = GetStageState(AudioLifecycleStage.Research, completedStages, activeStages);
        }

        private static StageContentState GetStageState(
            AudioLifecycleStage stage,
            HashSet<AudioLifecycleStage> completed,
            HashSet<string> active)
        {
            if (completed.Contains(stage))
                return StageContentState.Ready;
            if (active.Contains(stage.ToString()))
                return StageContentState.Processing;
            return StageContentState.Empty;
        }

        /// <summary>
        /// 从 DB 重新加载指定阶段的内容到当前会话和 UI。
        /// </summary>
        private void RefreshStageContentFromDb(AudioFileSession session, string audioItemId, AudioLifecycleStage stage)
        {
            switch (stage)
            {
                case AudioLifecycleStage.Transcribed:
                    var transcriptionJson = _pipeline.TryLoadCachedContent(audioItemId, AudioLifecycleStage.Transcribed);
                    if (!string.IsNullOrWhiteSpace(transcriptionJson))
                    {
                        try
                        {
                            var dtos = System.Text.Json.JsonSerializer.Deserialize<List<TranscriptSegmentDto>>(transcriptionJson);
                            if (dtos != null && dtos.Count > 0)
                            {
                                session.Segments = dtos.Select(d => new TranscriptSegment
                                {
                                    Speaker = d.Speaker ?? "",
                                    SpeakerIndex = d.SpeakerIndex,
                                    StartTime = new TimeSpan(d.StartTimeTicks),
                                    Text = d.Text ?? "",
                                }).ToList();
                                SyncSegmentsFromSession(session);
                                StatusMessage = $"转录完成：{session.Segments.Count} 个段落";
                                RaiseAllCommandsCanExecuteChanged();
                            }
                        }
                        catch { /* 兼容旧格式 */ }
                    }
                    break;

                case AudioLifecycleStage.Summarized:
                    var summary = _pipeline.TryLoadCachedContent(audioItemId, AudioLifecycleStage.Summarized);
                    if (!string.IsNullOrWhiteSpace(summary))
                    {
                        session.SummaryMarkdown = summary;
                        SummaryMarkdown = summary;
                        StatusMessage = "总结生成完成";
                    }
                    break;

                case AudioLifecycleStage.MindMap:
                    var mindMapJson = _pipeline.TryLoadCachedContent(audioItemId, AudioLifecycleStage.MindMap);
                    if (!string.IsNullOrWhiteSpace(mindMapJson))
                    {
                        session.MindMapRoot = ParseMindMapJson(mindMapJson);
                        MindMapRoot = session.MindMapRoot;
                        StatusMessage = "思维导图生成完成";
                    }
                    break;

                case AudioLifecycleStage.Insight:
                    var insight = _pipeline.TryLoadCachedContent(audioItemId, AudioLifecycleStage.Insight);
                    if (!string.IsNullOrWhiteSpace(insight))
                    {
                        session.InsightMarkdown = insight;
                        InsightMarkdown = insight;
                        StatusMessage = "顿悟生成完成";
                    }
                    break;

                case AudioLifecycleStage.PodcastScript:
                    var podcast = _pipeline.TryLoadCachedContent(audioItemId, AudioLifecycleStage.PodcastScript);
                    if (!string.IsNullOrWhiteSpace(podcast))
                    {
                        session.PodcastMarkdown = podcast;
                        PodcastMarkdown = podcast;
                        StatusMessage = "播客台本生成完成";
                    }
                    break;

                case AudioLifecycleStage.Research:
                    var research = _pipeline.TryLoadCachedContent(audioItemId, AudioLifecycleStage.Research);
                    if (!string.IsNullOrWhiteSpace(research))
                    {
                        session.ResearchReportMarkdown = research;
                        ResearchReportMarkdown = research;
                        session.CurrentResearchPhase = ResearchPhase.ReportReady;
                        CurrentResearchPhase = ResearchPhase.ReportReady;
                        StatusMessage = "深度研究完成";
                    }
                    break;
            }

            ControlPanel.RefreshLifecycleStatus();
        }

        // ── 生命周期缓存恢复 ──────────────────────────────────

        /// <summary>
        /// 从数据库恢复已缓存的内容到会话，避免重新生成。
        /// 每个字段独立判断：只在会话中该字段为空时才从 DB 恢复。
        /// </summary>
        private void TryRestoreFromLifecycleCache(AudioFileSession session, string audioItemId)
        {
            // 恢复转录段落
            if (session.Segments.Count == 0)
            {
                var transcriptionJson = _pipeline.TryLoadCachedContent(audioItemId, AudioLifecycleStage.Transcribed);
                if (!string.IsNullOrWhiteSpace(transcriptionJson))
                {
                    try
                    {
                        var dtos = System.Text.Json.JsonSerializer.Deserialize<List<TranscriptSegmentDto>>(transcriptionJson);
                        if (dtos != null && dtos.Count > 0)
                        {
                            session.Segments = dtos.Select(d => new TranscriptSegment
                            {
                                Speaker = d.Speaker ?? "",
                                SpeakerIndex = d.SpeakerIndex,
                                StartTime = new TimeSpan(d.StartTimeTicks),
                                Text = d.Text ?? "",
                            }).ToList();
                        }
                    }
                    catch { /* 兼容旧格式 */ }
                }
            }

            if (string.IsNullOrWhiteSpace(session.SummaryMarkdown))
            {
                var summary = _pipeline.TryLoadCachedContent(audioItemId, AudioLifecycleStage.Summarized);
                if (!string.IsNullOrWhiteSpace(summary)) session.SummaryMarkdown = summary;
            }

            if (session.MindMapRoot == null)
            {
                var mindMapJson = _pipeline.TryLoadCachedContent(audioItemId, AudioLifecycleStage.MindMap);
                if (!string.IsNullOrWhiteSpace(mindMapJson)) session.MindMapRoot = ParseMindMapJson(mindMapJson);
            }

            if (string.IsNullOrWhiteSpace(session.InsightMarkdown))
            {
                var insight = _pipeline.TryLoadCachedContent(audioItemId, AudioLifecycleStage.Insight);
                if (!string.IsNullOrWhiteSpace(insight)) session.InsightMarkdown = insight;
            }

            if (string.IsNullOrWhiteSpace(session.PodcastMarkdown))
            {
                var podcast = _pipeline.TryLoadCachedContent(audioItemId, AudioLifecycleStage.PodcastScript);
                if (!string.IsNullOrWhiteSpace(podcast)) session.PodcastMarkdown = podcast;
            }

            if (string.IsNullOrWhiteSpace(session.ResearchReportMarkdown))
            {
                var research = _pipeline.TryLoadCachedContent(audioItemId, AudioLifecycleStage.Research);
                if (!string.IsNullOrWhiteSpace(research)) session.ResearchReportMarkdown = research;
            }
        }

        /// <summary>
        /// 将当前会话中的总结结果保存到生命周期数据库。
        /// 在 GenerateSummarySessionAsync 完成后调用。
        /// </summary>
        private void SaveSummaryToLifecycle(AudioFileSession session)
        {
            if (string.IsNullOrWhiteSpace(session.SummaryMarkdown)) return;
            var audioItemId = _pipeline.EnsureAudioItem(session.FilePath);
            _pipeline.SaveStageContent(audioItemId, AudioLifecycleStage.Summarized, session.SummaryMarkdown);
            ControlPanel.RefreshLifecycleStatus();
        }

        /// <summary>
        /// 将转录完成事件通知到生命周期系统。
        /// </summary>
        private void SaveTranscriptionToLifecycle(AudioFileSession session)
        {
            var audioItemId = _pipeline.EnsureAudioItem(session.FilePath);
            // 保存完整转录段落到生命周期数据库（JSON 序列化）
            var segmentDtos = session.Segments.Select(s => new
            {
                s.Speaker,
                s.SpeakerIndex,
                StartTimeTicks = s.StartTime.Ticks,
                s.Text,
            }).ToList();
            var json = System.Text.Json.JsonSerializer.Serialize(segmentDtos);
            _pipeline.SaveStageContent(audioItemId, AudioLifecycleStage.Transcribed, json);
            ControlPanel.RefreshLifecycleStatus();
        }

        // ── 自动补齐 ─────────────────────────────────────────

        private async void OnAutoFillMissingRequested()
        {
            var session = _activeSession;
            if (session == null || session.Segments.Count == 0) return;
            await AutoFillDownstreamAsync(session);
        }

        /// <summary>
        /// 转录完成后并发填充所有下游内容。
        /// 阶段一：总结 + 思维导图（有依赖）；
        /// 阶段二：顿悟 / 研究 / 播客 并发。
        /// </summary>
        private async Task AutoFillDownstreamAsync(AudioFileSession session)
        {
            var audioItemId = _pipeline.EnsureAudioItem(session.FilePath);
            var completed = _pipeline.GetCompletedStages(audioItemId);

            // 阶段一：总结 + 思维导图（思维导图由总结自动链式触发）
            if (!completed.Contains(AudioLifecycleStage.Summarized))
            {
                await GenerateSummarySessionAsync(session);
            }
            else if (!completed.Contains(AudioLifecycleStage.MindMap))
            {
                await GenerateMindMapSessionAsync(session);
            }

            // 刷新 completed 列表
            completed = _pipeline.GetCompletedStages(audioItemId);

            // 阶段二：独立阶段并发执行
            var tasks = new List<Task>();

            if (!completed.Contains(AudioLifecycleStage.Insight))
                tasks.Add(GenerateInsightAsync());

            if (!completed.Contains(AudioLifecycleStage.Research))
                tasks.Add(GenerateResearchAsync());

            if (!completed.Contains(AudioLifecycleStage.PodcastScript))
                tasks.Add(GeneratePodcastAsync());

            if (tasks.Count > 0)
                await Task.WhenAll(tasks);
        }

        public void Dispose()
        {
            ControlPanel.AutoFillMissingRequested -= OnAutoFillMissingRequested;
            ControlPanel.PodcastAudioSynthesized -= OnPodcastAudioSynthesized;
            if (_eventBus != null)
            {
                _eventBus.TaskStatusChanged -= OnTaskStatusChanged;
                _eventBus.TaskProgressUpdated -= OnTaskProgressUpdated;
            }
            foreach (var session in _sessions.Values)
                session.Dispose();
            _sessions.Clear();
            _activeSession = null;
            Playback.Dispose();
            PodcastPlayback.Dispose();
        }

        private void OnPodcastAudioSynthesized(string path)
        {
            PodcastAudioPath = path;
            HasPodcastAudioFile = true;
            PodcastPlayback.LoadAudio(path);
        }

        /// <summary>转录段落序列化 DTO</summary>
        private sealed class TranscriptSegmentDto
        {
            public string? Speaker { get; set; }
            public int SpeakerIndex { get; set; }
            public long StartTimeTicks { get; set; }
            public string? Text { get; set; }
        }
    }
}
