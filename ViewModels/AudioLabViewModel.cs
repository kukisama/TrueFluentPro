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
        private CancellationTokenSource? _generationCts;

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

        // ── 深度研究 ──────────────────────────────────────────
        public ObservableCollection<ResearchTopic> ResearchTopics { get; } = new();

        [ObservableProperty]
        private string _researchReportMarkdown = "";

        [ObservableProperty]
        private ResearchPhase _currentResearchPhase = ResearchPhase.Idle;

        // ── 生成状态 ──────────────────────────────────────────
        [ObservableProperty]
        private bool _isGenerating;

        [ObservableProperty]
        private string _statusMessage = "就绪";

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

        public AudioLabViewModel(
            IAiInsightService aiInsightService,
            IAzureTokenProviderStore azureTokenProviderStore,
            IModelRuntimeResolver modelRuntimeResolver,
            ISpeechResourceRuntimeResolver speechResourceRuntimeResolver,
            IAiAudioTranscriptionService aiAudioTranscriptionService,
            Func<AzureSpeechConfig> configProvider)
        {
            _aiInsightService = aiInsightService;
            _azureTokenProviderStore = azureTokenProviderStore;
            _modelRuntimeResolver = modelRuntimeResolver;
            _speechResourceRuntimeResolver = speechResourceRuntimeResolver;
            _aiAudioTranscriptionService = aiAudioTranscriptionService;
            _configProvider = configProvider;

            Playback = new AudioLabPlaybackViewModel(
                () => Segments,
                seg => CurrentSegment = seg,
                () => CurrentSegment);

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
            StopGenerationCommand = new RelayCommand(_ => _generationCts?.Cancel(), _ => IsGenerating);
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

            CurrentFilePath = filePath;
            CurrentFileName = Path.GetFileName(filePath);
            BreadcrumbText = $"听析中心 / {CurrentFileName}";
            StatusMessage = $"已加载: {CurrentFileName}";

            Playback.LoadAudio(filePath);

            // 清空旧数据
            SummaryMarkdown = "";
            Segments.Clear();
            AutoTags.Clear();
            MindMapRoot = null;
            InsightMarkdown = "";
            PodcastMarkdown = "";
            ResearchTopics.Clear();
            ResearchReportMarkdown = "";
            CurrentResearchPhase = ResearchPhase.Idle;
            SelectedTab = AudioLabTabKind.Summary;

            RaiseAllCommandsCanExecuteChanged();

            // 自动开始转录
            _ = TranscribeAsync();
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

            var ai = config.AiConfig;
            if (ai == null)
            {
                errorMessage = "AI 配置不存在，请先在设置中配置 AI 模型。";
                return false;
            }

            var reference = SelectTextModelReference(ai);
            if (_modelRuntimeResolver.TryResolve(config, reference, ModelCapability.Text, out var runtime, out var resolveError)
                && runtime != null)
            {
                endpoint = runtime.Endpoint;
                runtimeRequest = runtime.CreateChatRequest(ai.SummaryEnableReasoning);
                return true;
            }

            errorMessage = string.IsNullOrWhiteSpace(resolveError) ? "未配置可用的文本模型" : resolveError;
            return false;
        }

        private bool TryResolveAiSpeechRuntime(
            AzureSpeechConfig config,
            out ModelRuntimeResolution? runtime,
            out string errorMessage)
        {
            runtime = null;
            if (!_speechResourceRuntimeResolver.TryResolveActive(config, SpeechCapability.BatchSpeechToText, out var resolution, out errorMessage)
                || resolution == null)
            {
                return false;
            }

            if (!resolution.IsAiSpeech || resolution.AiRuntime == null)
            {
                errorMessage = $"当前语音资源「{resolution.Resource.Name}」不是可用的 AI 语音连接器。";
                return false;
            }

            runtime = resolution.AiRuntime;
            return true;
        }

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

        private string FormatTranscriptForAi()
        {
            if (Segments.Count == 0) return "(暂无转录内容)";

            var sb = new StringBuilder();
            foreach (var seg in Segments)
            {
                var time = seg.StartTime.ToString(@"hh\:mm\:ss");
                sb.AppendLine($"[{time}] {seg.Speaker}: {seg.Text}");
            }
            return sb.ToString();
        }

        // ── 转录 ─────────────────────────────────────────────

        private async Task TranscribeAsync()
        {
            if (string.IsNullOrWhiteSpace(CurrentFilePath) || !File.Exists(CurrentFilePath))
                return;

            var config = _configProvider();

            if (!TryResolveAiSpeechRuntime(config, out var runtime, out var errorMessage) || runtime == null)
            {
                StatusMessage = $"转录未启动：{errorMessage}";
                return;
            }

            _generationCts?.Cancel();
            _generationCts = new CancellationTokenSource();
            var token = _generationCts.Token;

            IsGenerating = true;
            StatusMessage = "正在转录音频...";
            RaiseAllCommandsCanExecuteChanged();

            try
            {
                var splitOptions = new BatchSubtitleSplitOptions
                {
                    EnableSentenceSplit = true,
                    SplitOnComma = false,
                    MaxChars = 80,
                    MaxDurationSeconds = 15,
                    PauseSplitMs = 600
                };

                var result = await _aiAudioTranscriptionService.TranscribeAsync(
                    runtime, CurrentFilePath, config.SourceLanguage, splitOptions, token);

                if (result.Cues.Count == 0)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        StatusMessage = "转录完成，但未识别到内容。";
                    });
                    return;
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    LoadTranscriptFromCues(result.Cues);
                    StatusMessage = $"转录完成：{Segments.Count} 个段落";
                    RaiseAllCommandsCanExecuteChanged();
                });

                // 转录完成后自动生成总结
                await GenerateSummaryAsync();
            }
            catch (OperationCanceledException)
            {
                await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = "转录已取消");
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = $"转录失败：{ex.Message}");
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    IsGenerating = false;
                    RaiseAllCommandsCanExecuteChanged();
                });
            }
        }

        // ── 总结生成 ──────────────────────────────────────────

        private async Task GenerateSummaryAsync()
        {
            if (Segments.Count == 0) return;

            var config = _configProvider();
            if (!TryBuildTextRuntimeConfig(config, out var runtimeRequest, out var endpoint, out var errorMessage))
            {
                await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = $"总结未启动：{errorMessage}");
                return;
            }

            _generationCts?.Cancel();
            _generationCts = new CancellationTokenSource();
            var token = _generationCts.Token;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsGenerating = true;
                SummaryMarkdown = "";
                StatusMessage = "正在生成总结...";
                RaiseAllCommandsCanExecuteChanged();
            });

            try
            {
                var aiService = await CreateAuthenticatedInsightServiceAsync(runtimeRequest, endpoint, token);
                var transcript = FormatTranscriptForAi();

                const string systemPrompt = "你是一个专业的音频内容分析助手。根据转录文本生成结构化 Markdown 总结。\n请输出：\n1. **概要**：一段话概括核心内容\n2. **关键要点**：提炼 3-7 个最重要的要点\n3. **行动项**：如果有需要跟进的事项\n4. **关键词标签**：提取 3-5 个关键词\n\n引用内容时标注时间戳，格式为 [HH:MM:SS]。";
                var userContent = $"以下是音频转录内容：\n\n{transcript}";

                var sb = new StringBuilder();
                await aiService.StreamChatAsync(
                    runtimeRequest, systemPrompt, userContent,
                    chunk =>
                    {
                        sb.Append(chunk);
                        var text = sb.ToString();
                        Dispatcher.UIThread.Post(() => SummaryMarkdown = text);
                    },
                    token, AiChatProfile.Summary,
                    enableReasoning: runtimeRequest.SummaryEnableReasoning);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    SummaryMarkdown = sb.ToString();
                    StatusMessage = "总结生成完成";
                    _ = GenerateMindMapAsync();
                });
            }
            catch (OperationCanceledException)
            {
                await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = "总结生成已取消");
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    SummaryMarkdown += $"\n\n---\n**错误**: {ex.Message}";
                    StatusMessage = $"总结生成失败：{ex.Message}";
                });
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    IsGenerating = false;
                    RaiseAllCommandsCanExecuteChanged();
                });
            }
        }

        // ── 思维导图生成 ──────────────────────────────────────

        private async Task GenerateMindMapAsync()
        {
            if (Segments.Count == 0) return;

            var config = _configProvider();
            if (!TryBuildTextRuntimeConfig(config, out var runtimeRequest, out var endpoint, out _))
                return;

            _generationCts?.Cancel();
            _generationCts = new CancellationTokenSource();
            var token = _generationCts.Token;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsGenerating = true;
                StatusMessage = "正在生成思维导图...";
                RaiseAllCommandsCanExecuteChanged();
            });

            try
            {
                var aiService = await CreateAuthenticatedInsightServiceAsync(runtimeRequest, endpoint, token);
                var transcript = FormatTranscriptForAi();

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
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    MindMapRoot = root;
                    StatusMessage = "思维导图生成完成";
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                    StatusMessage = $"思维导图生成失败：{ex.Message}");
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
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
            await GenerateAiContentAsync(
                "顿悟",
                "你是一个深度思考专家。根据音频转录内容，提供深层洞察和顿悟。\n请从以下角度分析：\n1. **隐含假设**：说话者可能不自知的假设\n2. **潜在矛盾**：观点之间的冲突\n3. **深层模式**：反复出现的主题或思维模式\n4. **未说出的内容**：重要但被忽略的方面\n5. **关联启发**：与其他领域的联系\n\n以 Markdown 格式输出，标注时间戳 [HH:MM:SS]。",
                text => InsightMarkdown = text);
        }

        // ── 播客生成 ──────────────────────────────────────────

        private async Task GeneratePodcastAsync()
        {
            await GenerateAiContentAsync(
                "播客",
                "你是一个播客脚本编写专家。根据音频转录内容，生成一段适合播客的内容改写。\n要求：\n1. 用对话体、口语化风格重新组织内容\n2. 添加适当的过渡语和解说\n3. 突出有趣的细节和故事\n4. 提供开场引言和结尾总结\n5. 标注适合插入音效或停顿的位置\n\n以 Markdown 格式输出。",
                text => PodcastMarkdown = text);
        }

        // ── 深度研究生成 ──────────────────────────────────────

        private async Task GenerateResearchAsync()
        {
            if (Segments.Count == 0) return;

            var config = _configProvider();
            if (!TryBuildTextRuntimeConfig(config, out var runtimeRequest, out var endpoint, out var errorMessage))
            {
                await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = $"研究未启动：{errorMessage}");
                return;
            }

            _generationCts?.Cancel();
            _generationCts = new CancellationTokenSource();
            var token = _generationCts.Token;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsGenerating = true;
                CurrentResearchPhase = ResearchPhase.GeneratingOutline;
                StatusMessage = "正在规划研究方向...";
                ResearchTopics.Clear();
                ResearchReportMarkdown = "";
                RaiseAllCommandsCanExecuteChanged();
            });

            try
            {
                var aiService = await CreateAuthenticatedInsightServiceAsync(runtimeRequest, endpoint, token);
                var transcript = FormatTranscriptForAi();

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

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    foreach (var line in topicLines)
                    {
                        var title = line.TrimStart('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.', '、', '-', ' ');
                        if (string.IsNullOrWhiteSpace(title)) title = line;
                        ResearchTopics.Add(new ResearchTopic { Title = title, IsSelected = true });
                    }
                    CurrentResearchPhase = ResearchPhase.GeneratingReport;
                    StatusMessage = "正在生成深度研究报告...";
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
                        Dispatcher.UIThread.Post(() => ResearchReportMarkdown = text);
                    },
                    token, AiChatProfile.Summary,
                    enableReasoning: runtimeRequest.SummaryEnableReasoning);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ResearchReportMarkdown = reportSb.ToString();
                    CurrentResearchPhase = ResearchPhase.ReportReady;
                    StatusMessage = "深度研究完成";
                });
            }
            catch (OperationCanceledException)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    CurrentResearchPhase = ResearchPhase.Idle;
                    StatusMessage = "研究已取消";
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ResearchReportMarkdown += $"\n\n---\n**错误**: {ex.Message}";
                    CurrentResearchPhase = ResearchPhase.Idle;
                    StatusMessage = $"研究失败：{ex.Message}";
                });
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    IsGenerating = false;
                    RaiseAllCommandsCanExecuteChanged();
                });
            }
        }

        // ── 通用 AI 内容生成 ──────────────────────────────────

        private async Task GenerateAiContentAsync(string label, string systemPrompt, Action<string> resultSetter)
        {
            if (Segments.Count == 0) return;

            var config = _configProvider();
            if (!TryBuildTextRuntimeConfig(config, out var runtimeRequest, out var endpoint, out var errorMessage))
            {
                await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = $"{label}未启动：{errorMessage}");
                return;
            }

            _generationCts?.Cancel();
            _generationCts = new CancellationTokenSource();
            var token = _generationCts.Token;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsGenerating = true;
                resultSetter("");
                StatusMessage = $"正在生成{label}...";
                RaiseAllCommandsCanExecuteChanged();
            });

            try
            {
                var aiService = await CreateAuthenticatedInsightServiceAsync(runtimeRequest, endpoint, token);
                var transcript = FormatTranscriptForAi();
                var userContent = $"以下是音频转录内容：\n\n{transcript}";

                var sb = new StringBuilder();
                await aiService.StreamChatAsync(
                    runtimeRequest, systemPrompt, userContent,
                    chunk =>
                    {
                        sb.Append(chunk);
                        var text = sb.ToString();
                        Dispatcher.UIThread.Post(() => resultSetter(text));
                    },
                    token, AiChatProfile.Quick);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    resultSetter(sb.ToString());
                    StatusMessage = $"{label}生成完成";
                });
            }
            catch (OperationCanceledException)
            {
                await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = $"{label}已取消");
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                    StatusMessage = $"{label}失败：{ex.Message}");
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
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

        public void Dispose()
        {
            _generationCts?.Cancel();
            _generationCts?.Dispose();
            Playback.Dispose();
        }
    }
}
