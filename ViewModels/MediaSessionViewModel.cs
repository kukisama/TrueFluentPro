using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using TrueFluentPro.Models;
using TrueFluentPro.Services;

namespace TrueFluentPro.ViewModels
{
    /// <summary>
    /// 单会话 ViewModel — 聊天交互+任务管理
    /// </summary>
    public class MediaSessionViewModel : ViewModelBase
    {
        private readonly AiConfig _aiConfig;
        private readonly MediaGenConfig _genConfig;
        private readonly List<AiEndpoint> _endpoints;
        private readonly IModelRuntimeResolver _modelRuntimeResolver;
        private readonly IAzureTokenProviderStore _azureTokenProviderStore;
        private readonly IAiImageGenService _imageService;
        private readonly IAiVideoGenService _videoService;
        private readonly Action _onTaskCountChanged;
        private readonly Action<MediaSessionViewModel>? _onRequestSave;

        // 网页搜索配置（可由外部更新）
        private string _webSearchProviderId = "bing";
        private int _webSearchMaxResults = 5;
        private string _webSearchMcpEndpoint = "";
        private string _webSearchMcpToolName = "web_search";
        private string _webSearchMcpApiKey = "";
        /// <summary>搜索进度回调目标消息（搜索期间指向 aiMessage，结束后 null）</summary>
        private ChatMessageViewModel? _searchProgressMessage;
        private CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _videoFrameBackfillLock = new(1, 1);
        private const int MaxReferenceImageCount = 8;
        private readonly List<ChatMessageViewModel> _allMessages = new();

        // ── 会话级参数（仅内存，不回写配置文件）──
        private string _imageSize = "1024x1024";
        private string _imageQuality = "medium";
        private string _imageFormat = "png";
        private int _imageCount = 1;
        private string _videoAspectRatio = "16:9";
        private string _videoResolution = "720p";
        private int _videoSeconds = 5;
        private int _videoVariants = 1;
        private bool _imageParamsActivated;
        private bool _videoParamsActivated;

        public string SessionId { get; }

        private string _sessionName;
        public string SessionName
        {
            get => _sessionName;
            set => SetProperty(ref _sessionName, value);
        }

        public string SessionDirectory { get; }

        private MediaSessionSourceInfo? _sourceInfo;
        public MediaSessionSourceInfo? SourceInfo
        {
            get => _sourceInfo;
            private set
            {
                if (SetProperty(ref _sourceInfo, value))
                {
                    OnPropertyChanged(nameof(HasSourceInfo));
                    OnPropertyChanged(nameof(SourceSummaryText));
                }
            }
        }

        public bool HasSourceInfo => SourceInfo != null;

        public string SourceSummaryText => BuildSourceSummary(SourceInfo);

        public List<MediaAssetRecord> AssetCatalog { get; } = new();

        /// <summary>
        /// 记录该会话上次“非底部”滚动位置；为空表示采用默认到底部行为。
        /// </summary>
        public double? LastNonBottomScrollOffsetY { get; set; }

        /// <summary>
        /// 记录上次视口锚点对应的消息索引（仅内存）。
        /// </summary>
        public int? LastScrollAnchorMessageIndex { get; set; }

        /// <summary>
        /// 记录上次滚动比例（0~1，仅内存）。
        /// </summary>
        public double? LastScrollAnchorRatio { get; set; }

        /// <summary>
        /// 记录保存滚动位置时的可滚动最大值（Extent-Viewport，仅内存）。
        /// 用于恢复时判断是否需要从“绝对像素”切换为“比例恢复”。
        /// </summary>
        public double? LastScrollSavedMaxY { get; set; }

        /// <summary>
        /// 记录锚点消息在视口中的 Y 偏移（像素，仅内存）。
        /// </summary>
        public double? LastScrollAnchorViewportOffsetY { get; set; }

        /// <summary>
        /// 是否已从 session.json 加载完整消息与任务内容。
        /// </summary>
        public bool IsContentLoaded { get; set; }

        /// <summary>
        /// 是否已执行过“历史视频补帧”初始化（仅需一次）。
        /// </summary>
        public bool HasBackfilledVideoFrames { get; set; }

        /// <summary>
        /// 是否已执行过“中断视频任务恢复”扫描（仅需一次）。
        /// </summary>
        public bool HasResumedInterruptedVideoTasks { get; set; }

        private bool _isActiveSession;
        public bool IsActiveSession
        {
            get => _isActiveSession;
            set => SetProperty(ref _isActiveSession, value);
        }

        // --- 聊天记录 ---
        public ObservableCollection<ChatMessageViewModel> Messages { get; } = new();

        public IReadOnlyList<ChatMessageViewModel> AllMessages => _allMessages;

        public int TotalMessageCount => _allMessages.Count;

        // --- 进行中的任务 ---
        public ObservableCollection<MediaGenTask> RunningTasks { get; } = new();

        /// <summary>
        /// 所有任务历史（含已完成、失败、取消的）
        /// </summary>
        public List<MediaGenTask> TaskHistory { get; } = new();

        public int RunningTaskCount => RunningTasks.Count;

        public bool IsBusy => RunningTasks.Count > 0;

        public bool HasBadge => RunningTasks.Count > 0;

        public ObservableCollection<string> ReferenceImagePaths { get; } = new();

        public string? ReferenceImagePath => ReferenceImagePaths.FirstOrDefault();

        public int ReferenceImageCount => ReferenceImagePaths.Count;

        public bool HasReferenceImage => ReferenceImagePaths.Count > 0;

        public bool CanAddMoreReferenceImages => ReferenceImagePaths.Count < MaxReferenceImageCount;

        public bool IsVideoReferenceLimitExceeded => IsVideoMode && ReferenceImagePaths.Count > 1;

        public string VideoReferenceLimitHint => $"Sora 当前仅支持 1 张参考图，已选择 {ReferenceImagePaths.Count} 张";

        private bool _isReferenceImageSizeValid = true;
        public bool IsReferenceImageSizeValid
        {
            get => _isReferenceImageSizeValid;
            private set
            {
                if (SetProperty(ref _isReferenceImageSizeValid, value))
                {
                    OnPropertyChanged(nameof(IsReferenceImageSizeMismatch));
                    OnPropertyChanged(nameof(IsReferenceImageSizeMatched));
                }
            }
        }

        private string _referenceImageValidationHint = string.Empty;
        public string ReferenceImageValidationHint
        {
            get => _referenceImageValidationHint;
            private set => SetProperty(ref _referenceImageValidationHint, value);
        }

        private int _targetVideoWidth;
        public int TargetVideoWidth
        {
            get => _targetVideoWidth;
            private set => SetProperty(ref _targetVideoWidth, value);
        }

        private int _targetVideoHeight;
        public int TargetVideoHeight
        {
            get => _targetVideoHeight;
            private set => SetProperty(ref _targetVideoHeight, value);
        }

        public bool IsReferenceImageSizeMismatch =>
            IsVideoMode && HasReferenceImage && !IsReferenceImageSizeValid;

        public bool IsReferenceImageSizeMatched =>
            IsVideoMode && HasReferenceImage && IsReferenceImageSizeValid;

        // --- 输入区 ---
        private string _promptText = "";
        public string PromptText
        {
            get => _promptText;
            set
            {
                if (SetProperty(ref _promptText, value))
                    ((RelayCommand)GenerateCommand).RaiseCanExecuteChanged();
            }
        }

        private MediaGenType _selectedType = MediaGenType.Text;
        public MediaGenType SelectedType
        {
            get => _selectedType;
            set
            {
                if (SetProperty(ref _selectedType, value))
                {
                    if (value == MediaGenType.Video && !_videoParamsActivated)
                        ActivateVideoParams();

                    OnPropertyChanged(nameof(IsImageMode));
                    OnPropertyChanged(nameof(IsVideoMode));
                    OnPropertyChanged(nameof(IsTextMode));
                    OnPropertyChanged(nameof(IsVideoReferenceLimitExceeded));
                    OnPropertyChanged(nameof(VideoReferenceLimitHint));
                    OnPropertyChanged(nameof(IsReferenceImageSizeMismatch));
                    OnPropertyChanged(nameof(IsReferenceImageSizeMatched));
                    ReevaluateReferenceImageValidation();
                    ((RelayCommand)GenerateCommand).RaiseCanExecuteChanged();
                }
            }
        }

        public bool IsImageMode
        {
            get => SelectedType == MediaGenType.Image;
            set { if (value) SelectedType = MediaGenType.Image; else OnPropertyChanged(); }
        }

        public bool IsVideoMode
        {
            get => SelectedType == MediaGenType.Video;
            set { if (value) SelectedType = MediaGenType.Video; else OnPropertyChanged(); }
        }

        public bool IsTextMode
        {
            get => SelectedType == MediaGenType.Text;
            set { if (value) SelectedType = MediaGenType.Text; else OnPropertyChanged(); }
        }

        public MediaGenConfig GenConfig => _genConfig;

        // ── 会话级生成参数（供 AXAML ComboBox 直接绑定）──
        public string ImageSize
        {
            get => _imageSize;
            set => SetProperty(ref _imageSize, value);
        }
        public string ImageQuality
        {
            get => _imageQuality;
            set => SetProperty(ref _imageQuality, value);
        }
        public string ImageFormat
        {
            get => _imageFormat;
            set => SetProperty(ref _imageFormat, value);
        }
        public int ImageCount
        {
            get => _imageCount;
            set => SetProperty(ref _imageCount, value);
        }
        public string VideoAspectRatio
        {
            get => _videoAspectRatio;
            set
            {
                if (SetProperty(ref _videoAspectRatio, value))
                    ReevaluateReferenceImageValidation();
            }
        }
        public string VideoResolution
        {
            get => _videoResolution;
            set
            {
                if (SetProperty(ref _videoResolution, value))
                    ReevaluateReferenceImageValidation();
            }
        }
        public int VideoSeconds
        {
            get => _videoSeconds;
            set => SetProperty(ref _videoSeconds, value);
        }
        public int VideoVariants
        {
            get => _videoVariants;
            set => SetProperty(ref _videoVariants, value);
        }

        // --- 状态指示 ---
        private string _statusText = "就绪";
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        private bool _isGenerating;
        public bool IsGenerating
        {
            get => _isGenerating;
            set
            {
                if (SetProperty(ref _isGenerating, value))
                {
                    ((RelayCommand)GenerateCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)CancelCommand).RaiseCanExecuteChanged();
                }
            }
        }

        // --- 文本聊天流式状态 ---
        private CancellationTokenSource? _chatCts;
        private readonly Stopwatch _chatFlushThrottle = new();
        private const int ChatFlushIntervalMs = 80;

        /// <summary>字符级平滑流式动画器 — 参考 Cherry Studio 的 useSmoothStream</summary>
        private Controls.Markdown.SmoothStreamingAnimator? _streamAnimator;

        private bool _isChatStreaming;
        public bool IsChatStreaming
        {
            get => _isChatStreaming;
            set
            {
                if (SetProperty(ref _isChatStreaming, value))
                {
                    ((RelayCommand)GenerateCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)StopChatCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)RegenerateMessageCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)DeleteMessageCommand).RaiseCanExecuteChanged();
                }
            }
        }

        private bool _enableReasoning;
        public bool EnableReasoning
        {
            get => _enableReasoning;
            set => SetProperty(ref _enableReasoning, value);
        }

        private bool _enableWebSearch;
        /// <summary>是否启用 Web 搜索增强</summary>
        public bool EnableWebSearch
        {
            get => _enableWebSearch;
            set => SetProperty(ref _enableWebSearch, value);
        }

        /// <summary>从全局配置同步网页搜索引擎设置</summary>
        public void UpdateWebSearchConfig(string providerId, int maxResults,
            bool enableIntentAnalysis = true, bool enableResultCompression = false,
            string mcpEndpoint = "", string mcpToolName = "web_search", string mcpApiKey = "")
        {
            _webSearchProviderId = providerId;
            _webSearchMaxResults = maxResults;
            _webSearchMcpEndpoint = mcpEndpoint;
            _webSearchMcpToolName = mcpToolName;
            _webSearchMcpApiKey = mcpApiKey;
        }

        // --- 命令 ---
        public ICommand GenerateCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand OpenFileCommand { get; }
        public ICommand DeleteMessageCommand { get; }
        public ICommand RemoveReferenceImageCommand { get; }
        public ICommand CopyMessageCommand { get; }
        public ICommand RegenerateMessageCommand { get; }
        public ICommand StopChatCommand { get; }

        // --- 对话功能增强命令 ---
        public ICommand EditMessageCommand { get; }
        public ICommand SaveEditCommand { get; }
        public ICommand CancelEditCommand { get; }
        public ICommand SendEditCommand { get; }
        public ICommand ForkFromMessageCommand { get; }
        public ICommand ExportConversationCommand { get; }

        // --- 对话搜索 ---
        private readonly Controls.Markdown.DialogSearchEngine _searchEngine = new();

        private string _searchQuery = "";
        public string SearchQuery
        {
            get => _searchQuery;
            set
            {
                if (SetProperty(ref _searchQuery, value))
                    ExecuteSearch();
            }
        }

        private bool _isSearchVisible;
        public bool IsSearchVisible
        {
            get => _isSearchVisible;
            set => SetProperty(ref _isSearchVisible, value);
        }

        private string _searchStatus = "";
        public string SearchStatus
        {
            get => _searchStatus;
            set => SetProperty(ref _searchStatus, value);
        }

        // --- 快捷短语 ---
        public ObservableCollection<Models.QuickPhrase> QuickPhrases { get; } = new(new[]
        {
            new Models.QuickPhrase { Title = "翻译", Content = "请帮我翻译以下内容为中文：\n" },
            new Models.QuickPhrase { Title = "总结", Content = "请帮我总结以下内容的要点：\n" },
            new Models.QuickPhrase { Title = "代码审查", Content = "请帮我审查以下代码，指出问题和改进建议：\n" },
            new Models.QuickPhrase { Title = "解释", Content = "请用通俗易懂的语言解释以下概念：\n" },
        });

        // --- 参数选项 ---
        public List<string> ImageSizeOptions { get; } = new()
        {
            "1024x1024", "1024x1536", "1536x1024"
        };

        public List<string> ImageQualityOptions { get; } = new()
        {
            "low", "medium", "high"
        };

        public List<string> ImageFormatOptions { get; } = new()
        {
            "png", "jpeg"
        };

        public List<int> ImageCountOptions { get; } = new()
        {
            1, 2, 3, 4, 5
        };

        public List<string> VideoAspectRatioOptions { get; private set; } = new()
        {
            "1:1", "16:9", "9:16"
        };

        public List<string> VideoResolutionOptions { get; private set; } = new()
        {
            "480p", "720p", "1080p"
        };

        public List<int> VideoDurationOptions { get; private set; } = new()
        {
            5, 10, 15, 20
        };

        public List<int> VideoCountOptions { get; private set; } = new()
        {
            1, 2
        };

        public MediaSessionViewModel(
            string sessionId,
            string sessionName,
            string sessionDirectory,
            AiConfig aiConfig,
            MediaGenConfig genConfig,
            List<AiEndpoint> endpoints,
            IModelRuntimeResolver modelRuntimeResolver,
            IAzureTokenProviderStore azureTokenProviderStore,
            IAiImageGenService imageService,
            IAiVideoGenService videoService,
            Action onTaskCountChanged,
            Action<MediaSessionViewModel>? onRequestSave = null)
        {
            SessionId = sessionId;
            _sessionName = sessionName;
            SessionDirectory = sessionDirectory;
            _aiConfig = aiConfig;
            _genConfig = genConfig;
            _endpoints = endpoints;
            _modelRuntimeResolver = modelRuntimeResolver;
            _azureTokenProviderStore = azureTokenProviderStore;
            _imageService = imageService;
            _videoService = videoService;
            _onTaskCountChanged = onTaskCountChanged;
            _onRequestSave = onRequestSave;

            // 会话参数延迟注入：在会话首次被选中 / 首次切到视频模式时，
            // 从全局 genConfig 快照最新值，避免使用创建时的过期配置。

            GenerateCommand = new RelayCommand(
                _ => Generate(),
                _ => CanGenerateNow());

            CancelCommand = new RelayCommand(
                _ => CancelAll(),
                _ => IsGenerating);

            OpenFileCommand = new RelayCommand(
                param => OpenFile(param as string));

            DeleteMessageCommand = new RelayCommand(
                param => DeleteMessage(param as ChatMessageViewModel),
                param => param is ChatMessageViewModel m && !m.IsLoading);

            RemoveReferenceImageCommand = new RelayCommand(
                p => RemoveReferenceImage(p as string),
                _ => HasReferenceImage);

            CopyMessageCommand = new RelayCommand(
                param => CopyMessage(param as ChatMessageViewModel));

            RegenerateMessageCommand = new RelayCommand(
                param => RegenerateMessageAsync(param as ChatMessageViewModel),
                param => param is ChatMessageViewModel m && m.IsAssistant && !m.IsLoading && !IsChatStreaming);

            StopChatCommand = new RelayCommand(
                _ => StopChatStreaming(),
                _ => IsChatStreaming);

            // --- 对话功能增强命令 ---
            EditMessageCommand = new RelayCommand(
                param => EditMessage(param as ChatMessageViewModel),
                param => param is ChatMessageViewModel m && m.IsUser && !m.IsLoading && !IsChatStreaming);

            SaveEditCommand = new RelayCommand(
                param => SaveEdit(param as ChatMessageViewModel));

            CancelEditCommand = new RelayCommand(
                param => CancelEdit(param as ChatMessageViewModel));

            SendEditCommand = new RelayCommand(
                param => SendEdit(param as ChatMessageViewModel),
                _ => !IsChatStreaming);

            ForkFromMessageCommand = new RelayCommand(
                param => ForkFromMessage(param as ChatMessageViewModel),
                param => param is ChatMessageViewModel m && !m.IsLoading);

            ExportConversationCommand = new RelayCommand(
                _ => ExportConversation(),
                _ => _allMessages.Count > 0);

            // 根据当前 API 模式初始化视频参数选项
            RefreshVideoParameterOptions();

            RunningTasks.CollectionChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(RunningTaskCount));
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(HasBadge));
                _onTaskCountChanged?.Invoke();
            };

            ReferenceImagePaths.CollectionChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(ReferenceImagePath));
                OnPropertyChanged(nameof(ReferenceImageCount));
                OnPropertyChanged(nameof(HasReferenceImage));
                OnPropertyChanged(nameof(CanAddMoreReferenceImages));
                OnPropertyChanged(nameof(IsVideoReferenceLimitExceeded));
                OnPropertyChanged(nameof(VideoReferenceLimitHint));
                OnPropertyChanged(nameof(IsReferenceImageSizeMismatch));
                OnPropertyChanged(nameof(IsReferenceImageSizeMatched));
                ReevaluateReferenceImageValidation();
                (RemoveReferenceImageCommand as RelayCommand)?.RaiseCanExecuteChanged();
                ((RelayCommand)GenerateCommand).RaiseCanExecuteChanged();
            };

            ReevaluateReferenceImageValidation();
        }

        private bool CanGenerateNow()
        {
            if (string.IsNullOrWhiteSpace(PromptText))
                return false;

            if (IsTextMode)
                return !IsChatStreaming;

            if (IsGenerating)
                return false;

            if (IsVideoMode && ReferenceImagePaths.Count > 1)
                return false;

            if (IsVideoMode && HasReferenceImage && !IsReferenceImageSizeValid)
                return false;

            return true;
        }

        private async System.Threading.Tasks.Task ConfigureScopedServiceAuthAsync(
            AiMediaServiceBase service,
            ModelRuntimeResolution runtime,
            CancellationToken ct)
        {
            service.SetTokenProvider(null);

            if (runtime.AzureAuthMode != AzureAuthMode.AAD)
                return;

            var provider = await _azureTokenProviderStore.GetAuthenticatedProviderAsync(
                runtime.ProfileKey,
                runtime.AzureTenantId,
                runtime.AzureClientId,
                ct);
            if (provider == null)
            {
                throw new InvalidOperationException("AAD 登录已失效，请先在设置中重新登录当前媒体模型对应终结点。");
            }

            service.SetTokenProvider(provider);
        }

        private async System.Threading.Tasks.Task<AiImageGenService> CreateScopedImageServiceAsync(ModelRuntimeResolution runtime, CancellationToken ct)
        {
            var service = new AiImageGenService();
            await ConfigureScopedServiceAuthAsync(service, runtime, ct);
            return service;
        }

        private async System.Threading.Tasks.Task<AiVideoGenService> CreateScopedVideoServiceAsync(ModelRuntimeResolution runtime, CancellationToken ct)
        {
            var service = new AiVideoGenService();
            await ConfigureScopedServiceAuthAsync(service, runtime, ct);
            return service;
        }

        /// <summary>
        /// 删除一条聊天记录（只删除记录，不删除磁盘上的媒体文件）。
        /// </summary>
        public void DeleteMessage(ChatMessageViewModel? message)
        {
            if (message == null)
                return;
            if (message.IsLoading)
                return;

            if (RemoveMessageInternal(message))
            {
                _onRequestSave?.Invoke(this);
            }
        }

        public void ReplaceAllMessages(IEnumerable<ChatMessageViewModel>? messages)
        {
            var snapshot = messages?.ToList() ?? new List<ChatMessageViewModel>();

            _allMessages.Clear();
            Messages.Clear();

            _allMessages.AddRange(snapshot);

            for (var i = 0; i < _allMessages.Count; i++)
            {
                Messages.Add(_allMessages[i]);
            }

            UpdateMessageWindowState();
        }

        public void SetSourceInfo(MediaSessionSourceInfo? sourceInfo, bool requestSave = true)
        {
            SourceInfo = CloneSourceInfo(sourceInfo);
            if (requestSave)
            {
                _onRequestSave?.Invoke(this);
            }
        }

        public void ReplaceAssetCatalog(IEnumerable<MediaAssetRecord>? assets)
        {
            AssetCatalog.Clear();
            if (assets != null)
            {
                foreach (var asset in assets)
                {
                    AssetCatalog.Add(CloneAssetRecord(asset));
                }
            }

            OnPropertyChanged(nameof(AssetCatalog));
        }

        public void AppendMessage(ChatMessageViewModel message)
        {
            _allMessages.Add(message);
            Messages.Add(message);
            UpdateMessageWindowState();
        }

        public void ClearLoadedContent()
        {
            Messages.Clear();
            _allMessages.Clear();
            TaskHistory.Clear();
            UpdateMessageWindowState();
        }

        private bool RemoveMessageInternal(ChatMessageViewModel message)
        {
            var removed = false;
            var fullIndex = _allMessages.IndexOf(message);
            if (fullIndex >= 0)
            {
                _allMessages.RemoveAt(fullIndex);
                removed = true;
            }

            var visibleIndex = Messages.IndexOf(message);
            if (visibleIndex >= 0)
            {
                Messages.RemoveAt(visibleIndex);
                removed = true;
            }

            if (Messages.Count == 0 && _allMessages.Count > 0)
            {
                ReplaceAllMessages(_allMessages.ToList());
                return true;
            }

            if (removed)
            {
                UpdateMessageWindowState();
            }

            return removed;
        }

        private void UpdateMessageWindowState()
        {
            OnPropertyChanged(nameof(AllMessages));
            OnPropertyChanged(nameof(TotalMessageCount));
        }


        /// <summary>
        /// 首次切换到视频模式时，从全局配置重新快照视频参数。
        /// 这样在 Settings 中修改的视频参数能在首次切换时生效。
        /// </summary>
        /// <summary>
        /// 会话首次被选中时调用，从全局配置注入图片参数。
        /// </summary>
        public void ActivateOnFirstSelection()
        {
            if (!_imageParamsActivated)
                ActivateImageParams();
        }

        private void ActivateImageParams()
        {
            _imageParamsActivated = true;
            ImageSize = string.IsNullOrWhiteSpace(_genConfig.ImageSize) ? "1024x1024" : _genConfig.ImageSize;
            ImageQuality = string.IsNullOrWhiteSpace(_genConfig.ImageQuality) ? "medium" : _genConfig.ImageQuality;
            ImageFormat = string.IsNullOrWhiteSpace(_genConfig.ImageFormat) ? "png" : _genConfig.ImageFormat;
            ImageCount = _genConfig.ImageCount > 0 ? _genConfig.ImageCount : 1;
        }

        private void ActivateVideoParams()
        {
            _videoParamsActivated = true;
            VideoAspectRatio = _genConfig.VideoAspectRatio ?? "16:9";
            VideoResolution = _genConfig.VideoResolution ?? "720p";
            VideoSeconds = _genConfig.VideoSeconds > 0 ? _genConfig.VideoSeconds : 5;
            VideoVariants = _genConfig.VideoVariants > 0 ? _genConfig.VideoVariants : 1;
            RefreshVideoParameterOptions();
        }

        /// <summary>
        /// 根据当前 VideoApiMode（sora / sora-2）刷新视频参数选项。
        /// sora: 全参数（1:1/16:9/9:16，480p/720p/1080p，5/10/15/20秒，1/2数量）
        /// sora-2: 仅 16:9/9:16，720p，4/8/12秒，无数量选择
        /// </summary>
        public void RefreshVideoParameterOptions()
        {
            var profile = VideoCapabilityResolver.ResolveProfile(_genConfig.VideoApiMode, _genConfig.VideoModelRef?.ModelId ?? string.Empty);

            VideoAspectRatioOptions = profile.AspectRatioOptions.ToList();
            VideoResolutionOptions = profile.ResolutionOptions.ToList();
            VideoDurationOptions = profile.DurationOptions.ToList();
            VideoCountOptions = profile.CountOptions.ToList();

            OnPropertyChanged(nameof(VideoAspectRatioOptions));
            OnPropertyChanged(nameof(VideoResolutionOptions));
            OnPropertyChanged(nameof(VideoDurationOptions));
            OnPropertyChanged(nameof(VideoCountOptions));

            // 确保当前选中值在新选项中有效
            if (!VideoAspectRatioOptions.Contains(VideoAspectRatio))
                VideoAspectRatio = VideoAspectRatioOptions[0];
            if (!VideoResolutionOptions.Contains(VideoResolution))
                VideoResolution = VideoResolutionOptions[0];
            if (!VideoDurationOptions.Contains(VideoSeconds))
                VideoSeconds = VideoDurationOptions[0];
            if (!VideoCountOptions.Contains(VideoVariants))
                VideoVariants = VideoCountOptions[0];

            ReevaluateReferenceImageValidation();
        }

        public void Generate()
        {
            if (string.IsNullOrWhiteSpace(PromptText)) return;

            var prompt = PromptText.Trim();
            PromptText = "";

            if (SelectedType == MediaGenType.Text)
            {
                SendTextChatAsync(prompt);
                return;
            }

            if (IsGenerating)
            {
                StatusText = "当前会话已有生成中，请先新建一个会话再继续。";
                return;
            }

            // 添加用户消息
            AppendMessage(new ChatMessageViewModel(new MediaChatMessage
            {
                Role = "user",
                Text = prompt,
                ContentType = SelectedType == MediaGenType.Image ? "image" : "video",
                Timestamp = DateTime.Now
            }));

            if (SelectedType == MediaGenType.Image)
            {
                GenerateImage(prompt);
            }
            else
            {
                GenerateVideo(prompt);
            }
        }

        private void GenerateImage(string prompt)
        {
            var task = new MediaGenTask
            {
                Type = MediaGenType.Image,
                Status = MediaGenStatus.Running,
                Prompt = prompt,
                HasReferenceInput = HasReferenceImage
            };

            RunningTasks.Add(task);
            TaskHistory.Add(task);
            IsGenerating = true;
            StatusText = "生成图片中...";

            var loadingMessage = new ChatMessageViewModel(new MediaChatMessage
            {
                Role = "assistant",
                Text = "已提交提示词，生成中...",
                Timestamp = DateTime.Now
            })
            { IsLoading = true };
            AppendMessage(loadingMessage);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            // 启动计时器显示
            var timer = new System.Threading.Timer(_ =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (loadingMessage.IsLoading)
                    {
                        var elapsed = stopwatch.Elapsed;
                        loadingMessage.Text = $"生成中... 已耗时 {elapsed.TotalSeconds:F0} 秒";
                    }
                });
            }, null, 1000, 1000);

            if (!TryResolveImageRuntime(out var imageRuntime, out var imageError) || imageRuntime == null)
            {
                stopwatch.Stop();
                timer.Dispose();
                loadingMessage.IsLoading = false;
                loadingMessage.Text = imageError;
                task.Status = MediaGenStatus.Failed;
                task.ErrorMessage = imageError;
                RunningTasks.Remove(task);
                UpdateGeneratingState();
                StatusText = imageError;
                _onRequestSave?.Invoke(this);
                return;
            }

            // 构建有效的配置（覆盖参数优先）
            var effectiveConfig = new MediaGenConfig
            {
                ImageModel = imageRuntime.ModelId,
                ImageSize = ImageSize,
                ImageQuality = ImageQuality,
                ImageFormat = ImageFormat,
                ImageCount = ImageCount
            };

            var imageConfig = imageRuntime.CreateRequestConfig();

            var ct = _cts.Token;

            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var imageService = await CreateScopedImageServiceAsync(imageRuntime, ct);
                    var result = await imageService.GenerateAndSaveImagesAsync(
                        imageConfig, prompt, effectiveConfig, SessionDirectory, ct,
                        ReferenceImagePaths.ToList(),
                        p => Dispatcher.UIThread.Post(() =>
                        {
                            task.Progress = p;
                            var elapsed = stopwatch.Elapsed.TotalSeconds;
                            var phaseText = p switch
                            {
                                < 50 => $"等待服务端生成... 已耗时 {elapsed:F0}s",
                                < 96 => $"下载图片数据中... {p}%  已耗时 {elapsed:F0}s",
                                < 100 => $"解析并保存中... 已耗时 {elapsed:F0}s",
                                _ => "图片生成完成"
                            };
                            StatusText = phaseText;
                            // 同步到聊天气泡
                            if (loadingMessage.IsLoading)
                                loadingMessage.Text = phaseText;
                        }));

                    var totalSeconds = stopwatch.Elapsed.TotalSeconds;
                    stopwatch.Stop();
                    timer.Dispose();

                    Dispatcher.UIThread.Post(() =>
                    {
                        task.Status = MediaGenStatus.Completed;
                        task.ResultFilePath = result.FilePaths.FirstOrDefault();

                        loadingMessage.IsLoading = false;
                        loadingMessage.GenerateSeconds = result.GenerateSeconds;
                        loadingMessage.DownloadSeconds = result.DownloadSeconds;
                        loadingMessage.Text = $"已生成 {result.FilePaths.Count} 张图片 (生成 {result.GenerateSeconds:F1}s + 下载 {result.DownloadSeconds:F1}s = 总计 {totalSeconds:F1}s)";
                        loadingMessage.MediaPaths.Clear();
                        foreach (var path in result.FilePaths)
                        {
                            loadingMessage.MediaPaths.Add(path);
                        }

                        // 图片生成完成后清空参考图（避免下一次误用）
                        ClearReferenceImage(silent: true);

                        RunningTasks.Remove(task);
                        UpdateGeneratingState();
                        _onRequestSave?.Invoke(this);
                    });
                }
                catch (OperationCanceledException)
                {
                    stopwatch.Stop();
                    timer.Dispose();
                    Dispatcher.UIThread.Post(() =>
                    {
                        task.Status = MediaGenStatus.Cancelled;
                        loadingMessage.IsLoading = false;
                        loadingMessage.Text = "已取消生成";
                        RunningTasks.Remove(task);
                        UpdateGeneratingState();
                        StatusText = "已取消";
                        _onRequestSave?.Invoke(this);
                    });
                }
                catch (Exception ex)
                {
                    stopwatch.Stop();
                    timer.Dispose();
                    var elapsedSec = stopwatch.Elapsed.TotalSeconds;
                    var logPath = PathManager.Instance.GetLogFile("image_http_debug.log");
                    Dispatcher.UIThread.Post(() =>
                    {
                        task.Status = MediaGenStatus.Failed;
                        task.ErrorMessage = $"{ex.Message}\n日志: {logPath}";

                        loadingMessage.IsLoading = false;
                        loadingMessage.Text = $"❌ 图片生成失败 (耗时 {elapsedSec:F1}秒): {ex.Message}\n日志: {logPath}";

                        RunningTasks.Remove(task);
                        UpdateGeneratingState();
                        StatusText = $"图片生成失败，请查看日志：{logPath}";
                        _onRequestSave?.Invoke(this);
                    });
                }
            }, ct);
        }

        private void GenerateVideo(string prompt)
        {
            if (HasReferenceImage && !IsReferenceImageSizeValid)
            {
                StatusText = string.IsNullOrWhiteSpace(ReferenceImageValidationHint)
                    ? "参考图尺寸不符合当前视频参数，请先裁切"
                    : ReferenceImageValidationHint;
                return;
            }

            var task = new MediaGenTask
            {
                Type = MediaGenType.Video,
                Status = MediaGenStatus.Running,
                Prompt = prompt,
                HasReferenceInput = HasReferenceImage
            };

            RunningTasks.Add(task);
            TaskHistory.Add(task);
            IsGenerating = true;
            StatusText = "创建视频任务中...";

            var loadingMessage = new ChatMessageViewModel(new MediaChatMessage
            {
                Role = "assistant",
                Text = "生成中 0秒",
                Timestamp = DateTime.Now
            })
            { IsLoading = true };
            AppendMessage(loadingMessage);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            string currentApiStatus = ""; // 记录当前 API 返回的状态
            bool isDownloading = false;   // 标记是否已进入下载阶段
            double recordedGenerateSeconds = 0;
            var downloadStopwatch = new System.Diagnostics.Stopwatch();
            var timer = new System.Threading.Timer(_ =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (loadingMessage.IsLoading)
                    {
                        if (isDownloading)
                        {
                            // 阶段3：succeeded 后下载中
                            loadingMessage.Text = $"生成进度 succeeded 耗时{recordedGenerateSeconds:F0}秒，下载中 {downloadStopwatch.Elapsed.TotalSeconds:F0}秒";
                        }
                        else if (!string.IsNullOrEmpty(currentApiStatus))
                        {
                            // 阶段2：有状态了
                            loadingMessage.Text = $"生成进度 {currentApiStatus} {stopwatch.Elapsed.TotalSeconds:F0}秒";
                        }
                        else
                        {
                            // 阶段1：刚提交
                            loadingMessage.Text = $"生成中 {stopwatch.Elapsed.TotalSeconds:F0}秒";
                        }
                    }
                });
            }, null, 1000, 1000);

            if (!TryGetCurrentVideoTargetSize(out var videoWidth, out var videoHeight))
            {
                task.Status = MediaGenStatus.Failed;
                task.ErrorMessage = "当前视频参数组合无可用尺寸映射";
                RunningTasks.Remove(task);
                UpdateGeneratingState();
                StatusText = "视频参数无效，请调整比例/分辨率";
                return;
            }

            if (!TryResolveVideoRuntime(out var videoRuntime, out var videoError) || videoRuntime == null)
            {
                stopwatch.Stop();
                timer.Dispose();
                loadingMessage.IsLoading = false;
                loadingMessage.Text = videoError;
                task.Status = MediaGenStatus.Failed;
                task.ErrorMessage = videoError;
                RunningTasks.Remove(task);
                UpdateGeneratingState();
                StatusText = videoError;
                _onRequestSave?.Invoke(this);
                return;
            }

            var effectiveConfig = new MediaGenConfig
            {
                VideoModel = videoRuntime.ModelId,
                VideoApiMode = _genConfig.VideoApiMode,
                VideoWidth = videoWidth,
                VideoHeight = videoHeight,
                VideoSeconds = VideoSeconds,
                VideoVariants = VideoVariants,
                VideoPollIntervalMs = _genConfig.VideoPollIntervalMs
            };

            // 记录该任务创建时的模式，方便重启/恢复时走同一路径。
            task.RemoteVideoApiMode = effectiveConfig.VideoApiMode;

            var videoConfig = videoRuntime.CreateRequestConfig();

            var randomId = Guid.NewGuid().ToString("N")[..8];
            var outputPath = Path.Combine(SessionDirectory, $"vid_001_{randomId}.mp4");

            var ct = _cts.Token;

            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var videoService = await CreateScopedVideoServiceAsync(videoRuntime, ct);
                    var (_, generateSec, downloadSec, downloadUrl) = await videoService.GenerateVideoAsync(
                        videoConfig, prompt, effectiveConfig, outputPath, ct,
                        ReferenceImagePath,
                        p => Dispatcher.UIThread.Post(() =>
                        {
                            task.Progress = p;
                            StatusText = p < 100 ? $"生成视频中... {p}%" : "视频生成完成";
                        }),
                        videoId => Dispatcher.UIThread.Post(() =>
                        {
                            task.RemoteVideoId = videoId;
                            StatusText = $"视频任务已创建，等待生成... (ID: {videoId})";
                            _onRequestSave?.Invoke(this);
                        }),
                        status => Dispatcher.UIThread.Post(() =>
                        {
                            currentApiStatus = status;
                            StatusText = $"视频状态: {status}";
                        }),
                        genId => Dispatcher.UIThread.Post(() =>
                        {
                            task.RemoteGenerationId = genId;

                            // 拿到 generationId 就可以提前构造并写入下载 URL（不等到真正下载完成）
                            if (!string.IsNullOrWhiteSpace(task.RemoteVideoId))
                            {
                                var candidates = videoService.BuildDownloadCandidateUrls(
                                    videoConfig,
                                    task.RemoteVideoId,
                                    genId,
                                    effectiveConfig.VideoApiMode);
                                var preferred = candidates.Count > 0 ? candidates[0] : null;
                                if (!string.IsNullOrWhiteSpace(preferred))
                                {
                                    task.RemoteDownloadUrl = preferred;
                                }
                            }
                            _onRequestSave?.Invoke(this);
                        }),
                        genSeconds => Dispatcher.UIThread.Post(() =>
                        {
                            // succeeded → 记录生成耗时，切换到下载阶段
                            recordedGenerateSeconds = genSeconds;
                            task.GenerateSeconds = genSeconds;
                            isDownloading = true;
                            downloadStopwatch.Start();
                            _onRequestSave?.Invoke(this);
                        }));

                    var frameResult = await VideoFrameExtractorService.TryExtractFirstAndLastFrameAsync(outputPath, ct);

                    Dispatcher.UIThread.Post(() =>
                    {
                        task.Status = MediaGenStatus.Completed;
                        task.ResultFilePath = outputPath;
                        task.GenerateSeconds = generateSec;
                        task.DownloadSeconds = downloadSec;
                        task.RemoteDownloadUrl = downloadUrl;

                        stopwatch.Stop();
                        timer.Dispose();

                        loadingMessage.IsLoading = false;
                        loadingMessage.GenerateSeconds = generateSec;
                        loadingMessage.DownloadSeconds = downloadSec;
                        var totalSec = generateSec + downloadSec;
                        loadingMessage.Text = $"✅ 视频已生成（AI生成 {generateSec:F1}s + 下载 {downloadSec:F1}s = 总计 {totalSec:F1}s）";
                        loadingMessage.MediaPaths.Clear();
                        if (!string.IsNullOrWhiteSpace(frameResult.FirstFramePath))
                        {
                            loadingMessage.MediaPaths.Add(frameResult.FirstFramePath);
                        }
                        if (!string.IsNullOrWhiteSpace(frameResult.LastFramePath))
                        {
                            loadingMessage.MediaPaths.Add(frameResult.LastFramePath);
                        }
                        if (loadingMessage.MediaPaths.Count == 0)
                        {
                            loadingMessage.MediaPaths.Add(outputPath);
                        }

                        RunningTasks.Remove(task);
                        UpdateGeneratingState();
                        _onRequestSave?.Invoke(this);
                    });
                }
                catch (OperationCanceledException)
                {
                    stopwatch.Stop();
                    timer.Dispose();
                    Dispatcher.UIThread.Post(() =>
                    {
                        task.Status = MediaGenStatus.Cancelled;
                        loadingMessage.IsLoading = false;
                        loadingMessage.Text = "已取消生成";
                        RunningTasks.Remove(task);
                        UpdateGeneratingState();
                        StatusText = "已取消";
                        _onRequestSave?.Invoke(this);
                    });
                }
                catch (Exception ex)
                {
                    stopwatch.Stop();
                    timer.Dispose();
                    var elapsedSec = stopwatch.Elapsed.TotalSeconds;
                    var logPath = AiVideoGenService.GetVideoDebugLogPath();
                    Dispatcher.UIThread.Post(() =>
                    {
                        task.Status = MediaGenStatus.Failed;
                        task.ErrorMessage = $"{ex.Message}\n日志: {logPath}";

                        loadingMessage.IsLoading = false;
                        loadingMessage.Text = $"❌ 视频生成失败 (耗时 {elapsedSec:F1}秒): {ex.Message}\n日志: {logPath}";

                        RunningTasks.Remove(task);
                        UpdateGeneratingState();
                        StatusText = $"视频生成失败，请查看日志：{logPath}";
                        _onRequestSave?.Invoke(this);
                    });
                }
            }, ct);
        }

        /// <summary>
        /// 恢复一个中断的视频任务（基于 RemoteVideoId 继续轮询+下载）
        /// 若 RemoteGenerationId 已存在，则跳过轮询直接下载。
        /// </summary>
        public void ResumeVideoTask(MediaGenTask task)
        {
            if (string.IsNullOrEmpty(task.RemoteVideoId))
            {
                task.Status = MediaGenStatus.Failed;
                task.ErrorMessage = "无法恢复：缺少 RemoteVideoId";
                return;
            }

            task.Status = MediaGenStatus.Running;
            RunningTasks.Add(task);
            // 如果 TaskHistory 已包含此 task（从持久化恢复的情况），不重复添加
            if (!TaskHistory.Contains(task))
                TaskHistory.Add(task);
            IsGenerating = true;
            StatusText = $"恢复视频任务... (ID: {task.RemoteVideoId})";

            if (!TryResolveVideoRuntime(out var videoRuntime, out var videoError) || videoRuntime == null)
            {
                task.Status = MediaGenStatus.Failed;
                task.ErrorMessage = videoError;
                StatusText = videoError;
                RunningTasks.Remove(task);
                UpdateGeneratingState();
                return;
            }

            var videoConfig = videoRuntime.CreateRequestConfig();
            var randomId = Guid.NewGuid().ToString("N")[..8];
            var outputPath = task.ResultFilePath
                ?? Path.Combine(SessionDirectory, $"vid_resume_{randomId}.mp4");

            var effectiveConfig = new MediaGenConfig
            {
                VideoPollIntervalMs = _genConfig.VideoPollIntervalMs
            };

            var apiMode = task.RemoteVideoApiMode ?? _genConfig.VideoApiMode;

            // 添加恢复中的提示消息
            var loadingMessage = new ChatMessageViewModel(new MediaChatMessage
            {
                Role = "assistant",
                Text = $"生成中 0秒 (恢复 ID: {task.RemoteVideoId})",
                Timestamp = DateTime.Now
            })
            { IsLoading = true };
            AppendMessage(loadingMessage);


            var ct = _cts.Token;
            var videoId = task.RemoteVideoId;
            var existingGenId = task.RemoteGenerationId;

            // 如果已知 generationId，提前写入 RemoteDownloadUrl（不等到真正下载完成）
            if (!string.IsNullOrEmpty(existingGenId))
            {
                var candidates = _videoService.BuildDownloadCandidateUrls(
                    videoConfig,
                    videoId,
                    existingGenId,
                    apiMode);
                var preferred = candidates.Count > 0 ? candidates[0] : null;
                if (!string.IsNullOrWhiteSpace(preferred))
                {
                    task.RemoteDownloadUrl = preferred;
                    _onRequestSave?.Invoke(this);
                }
            }

            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                var generateSw = System.Diagnostics.Stopwatch.StartNew();
                var downloadSw = new System.Diagnostics.Stopwatch();
                bool isDownloadPhase = false;
                double recordedGenSec = 0;
                try
                {
                    var videoService = await CreateScopedVideoServiceAsync(videoRuntime, ct);
                    string? generationId = existingGenId;
                    // 用 Timer 定时刷新显示文字（生成+下载阶段都可用）
                    string currentStatus = "";
                    using var pollTimer = new System.Threading.Timer(_ =>
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (loadingMessage.IsLoading)
                            {
                                if (isDownloadPhase)
                                {
                                    loadingMessage.Text = $"生成进度 succeeded 耗时{recordedGenSec:F0}秒，下载中 {downloadSw.Elapsed.TotalSeconds:F0}秒";
                                }
                                else if (!string.IsNullOrEmpty(currentStatus))
                                {
                                    loadingMessage.Text = $"生成进度 {currentStatus} {generateSw.Elapsed.TotalSeconds:F0}秒";
                                }
                                else
                                {
                                    loadingMessage.Text = $"生成中 {generateSw.Elapsed.TotalSeconds:F0}秒";
                                }
                            }
                        });
                    }, null, 1000, 1000);

                    // 如果已有 generationId，跳过轮询直接下载
                    if (!string.IsNullOrEmpty(generationId))
                    {
                        generateSw.Stop(); // 无需生成等待
                        isDownloadPhase = true;
                        downloadSw.Start();
                        Dispatcher.UIThread.Post(() =>
                        {
                            loadingMessage.Text = $"生成进度 succeeded 耗时0秒，下载中 0秒";
                            StatusText = "跳过轮询，直接下载视频...";
                        });
                    }
                    else
                    {
                        var retryCount = 0;
                        const int maxRetries = 3;
                        while (!ct.IsCancellationRequested)
                        {
                            try
                            {
                                var (status, progress, genId, failureReason) = await videoService.PollStatusDetailsAsync(
                                    videoConfig, videoId, ct, apiMode);
                                Dispatcher.UIThread.Post(() =>
                                {
                                    task.Progress = progress;
                                    currentStatus = status;
                                    StatusText = $"视频状态: {status}";
                                });
                                retryCount = 0;

                                if (!string.IsNullOrWhiteSpace(genId))
                                {
                                    generationId = genId;
                                    Dispatcher.UIThread.Post(() =>
                                    {
                                        task.RemoteGenerationId = genId;

                                        // 解析到 generationId 就立刻构造并写入下载 URL，便于恢复
                                        var candidates = videoService.BuildDownloadCandidateUrls(
                                            videoConfig,
                                            videoId,
                                            genId,
                                            apiMode);
                                        var preferred = candidates.Count > 0 ? candidates[0] : null;
                                        if (!string.IsNullOrWhiteSpace(preferred))
                                        {
                                            task.RemoteDownloadUrl = preferred;
                                        }
                                        _onRequestSave?.Invoke(this);
                                    });
                                }

                                if (status is "succeeded" or "completed" or "success")
                                {
                                    generateSw.Stop();
                                    recordedGenSec = generateSw.Elapsed.TotalSeconds;
                                    Dispatcher.UIThread.Post(() =>
                                    {
                                        task.GenerateSeconds = recordedGenSec;
                                        _onRequestSave?.Invoke(this);
                                    });
                                    break;
                                }
                                if (status is "failed" or "error" or "cancelled" or "canceled")
                                {
                                    var detail = string.IsNullOrWhiteSpace(failureReason)
                                        ? status
                                        : $"{status} ({failureReason})";
                                    throw new InvalidOperationException($"视频生成失败: {detail}");
                                }

                                await System.Threading.Tasks.Task.Delay(
                                    effectiveConfig.VideoPollIntervalMs, ct);
                            }
                            catch (HttpRequestException) when (retryCount < maxRetries)
                            {
                                retryCount++;
                                await System.Threading.Tasks.Task.Delay(
                                    effectiveConfig.VideoPollIntervalMs, ct);
                            }
                        }

                        ct.ThrowIfCancellationRequested();
                        // 进入下载阶段
                        isDownloadPhase = true;
                        downloadSw.Start();
                        Dispatcher.UIThread.Post(() =>
                        {
                            loadingMessage.Text = $"生成进度 succeeded 耗时{recordedGenSec:F0}秒，下载中 0秒";
                            StatusText = "下载视频中...";
                        });
                    }

                    var dlUrl = await videoService.DownloadVideoAsync(videoConfig, videoId, outputPath, ct,
                        generationId, apiMode);
                    downloadSw.Stop();

                    var frameResult = await VideoFrameExtractorService.TryExtractFirstAndLastFrameAsync(outputPath, ct);

                    var genSec = recordedGenSec > 0 ? recordedGenSec : generateSw.Elapsed.TotalSeconds;
                    var dlSec = downloadSw.Elapsed.TotalSeconds;

                    Dispatcher.UIThread.Post(() =>
                    {
                        task.Status = MediaGenStatus.Completed;
                        task.ResultFilePath = outputPath;
                        task.GenerateSeconds = genSec;
                        task.DownloadSeconds = dlSec;
                        task.RemoteDownloadUrl = dlUrl;

                        loadingMessage.IsLoading = false;
                        loadingMessage.GenerateSeconds = genSec;
                        loadingMessage.DownloadSeconds = dlSec;
                        var totalSec = genSec + dlSec;
                        loadingMessage.Text = $"✅ 视频已恢复（等待 {genSec:F1}s + 下载 {dlSec:F1}s = 总计 {totalSec:F1}s）";
                        loadingMessage.MediaPaths.Clear();
                        if (!string.IsNullOrWhiteSpace(frameResult.FirstFramePath))
                        {
                            loadingMessage.MediaPaths.Add(frameResult.FirstFramePath);
                        }
                        if (!string.IsNullOrWhiteSpace(frameResult.LastFramePath))
                        {
                            loadingMessage.MediaPaths.Add(frameResult.LastFramePath);
                        }
                        if (loadingMessage.MediaPaths.Count == 0)
                        {
                            loadingMessage.MediaPaths.Add(outputPath);
                        }

                        RunningTasks.Remove(task);
                        UpdateGeneratingState();
                        _onRequestSave?.Invoke(this);
                    });
                }
                catch (OperationCanceledException)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        task.Status = MediaGenStatus.Cancelled;
                        loadingMessage.IsLoading = false;
                        loadingMessage.Text = "已取消恢复";
                        RunningTasks.Remove(task);
                        UpdateGeneratingState();
                        _onRequestSave?.Invoke(this);
                    });
                }
                catch (Exception ex)
                {
                    var logPath = AiVideoGenService.GetVideoDebugLogPath();
                    Dispatcher.UIThread.Post(() =>
                    {
                        task.Status = MediaGenStatus.Failed;
                        task.ErrorMessage = $"{ex.Message}\n日志: {logPath}";
                        loadingMessage.IsLoading = false;
                        loadingMessage.Text = $"❌ 视频恢复失败: {ex.Message}\n日志: {logPath}";
                        RunningTasks.Remove(task);
                        UpdateGeneratingState();
                        StatusText = $"视频恢复失败，请查看日志：{logPath}";
                        _onRequestSave?.Invoke(this);
                    });
                }
            }, ct);
        }

        private void UpdateGeneratingState()
        {
            IsGenerating = RunningTasks.Count > 0;
            if (!IsGenerating)
                StatusText = "就绪";
        }

        public void CancelAll()
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = new CancellationTokenSource();

            foreach (var t in RunningTasks.ToList())
            {
                t.Status = MediaGenStatus.Cancelled;
            }

            RunningTasks.Clear();
            IsGenerating = false;
            StatusText = "已取消所有任务";
        }

        public async System.Threading.Tasks.Task<bool> SetReferenceImageFromFileAsync(string sourcePath, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                return false;
            }

            if (!CanAddMoreReferenceImages)
            {
                StatusText = $"最多仅支持 {MaxReferenceImageCount} 张参考图";
                return false;
            }

            var ext = Path.GetExtension(sourcePath);
            if (string.IsNullOrWhiteSpace(ext))
            {
                ext = ".png";
            }

            var refsDir = Path.Combine(SessionDirectory, "refs");
            Directory.CreateDirectory(refsDir);

            var targetPath = Path.Combine(refsDir, $"reference_{Guid.NewGuid():N}{ext}");
            await using (var source = File.OpenRead(sourcePath))
            await using (var target = File.Create(targetPath))
            {
                await source.CopyToAsync(target, ct);
            }

            AddReferenceImagePath(targetPath);
            StatusText = $"已添加参考图（{ReferenceImagePaths.Count}/{MaxReferenceImageCount}）";
            _onRequestSave?.Invoke(this);
            return true;
        }

        public async System.Threading.Tasks.Task<bool> SetReferenceImageFromBytesAsync(byte[] bytes, string extension = ".png", CancellationToken ct = default)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return false;
            }

            if (!CanAddMoreReferenceImages)
            {
                StatusText = $"最多仅支持 {MaxReferenceImageCount} 张参考图";
                return false;
            }

            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".png";
            }

            if (!extension.StartsWith('.'))
            {
                extension = "." + extension;
            }

            var refsDir = Path.Combine(SessionDirectory, "refs");
            Directory.CreateDirectory(refsDir);

            var targetPath = Path.Combine(refsDir, $"reference_{Guid.NewGuid():N}{extension}");
            await File.WriteAllBytesAsync(targetPath, bytes, ct);

            AddReferenceImagePath(targetPath);
            StatusText = $"已添加参考图（{ReferenceImagePaths.Count}/{MaxReferenceImageCount}）";
            _onRequestSave?.Invoke(this);
            return true;
        }

        public void ClearReferenceImage(bool silent = false)
        {
            DeleteReferenceImageFiles();
            ReferenceImagePaths.Clear();
            if (!silent)
            {
                StatusText = "已移除参考图";
            }
            _onRequestSave?.Invoke(this);
            (RemoveReferenceImageCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private void RemoveReferenceImage(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                ClearReferenceImage();
                return;
            }

            var existing = ReferenceImagePaths.FirstOrDefault(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
                return;

            DeleteReferenceImageFile(existing);
            ReferenceImagePaths.Remove(existing);
            StatusText = "已移除参考图";
            _onRequestSave?.Invoke(this);
            (RemoveReferenceImageCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private void AddReferenceImagePath(string path)
        {
            if (ReferenceImagePaths.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
                return;

            ReferenceImagePaths.Add(path);
            (RemoveReferenceImageCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        public bool TryGetCurrentVideoTargetSize(out int width, out int height)
        {
            return VideoCapabilityResolver.TryResolveSize(
                _genConfig.VideoApiMode,
                _genConfig.VideoModelRef?.ModelId ?? string.Empty,
                VideoAspectRatio,
                VideoResolution,
                out width,
                out height);
        }

        public void NotifyReferenceImageUpdated(string path)
        {
            var index = -1;
            for (var i = 0; i < ReferenceImagePaths.Count; i++)
            {
                if (string.Equals(ReferenceImagePaths[i], path, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    break;
                }
            }

            if (index >= 0)
            {
                var existing = ReferenceImagePaths[index];
                ReferenceImagePaths.RemoveAt(index);
                ReferenceImagePaths.Insert(index, existing);
            }

            ReevaluateReferenceImageValidation();
            _onRequestSave?.Invoke(this);
            ((RelayCommand)GenerateCommand).RaiseCanExecuteChanged();
        }

        private void ReevaluateReferenceImageValidation()
        {
            if (!TryGetCurrentVideoTargetSize(out var targetW, out var targetH))
            {
                TargetVideoWidth = 0;
                TargetVideoHeight = 0;

                if (IsVideoMode)
                {
                    IsReferenceImageSizeValid = false;
                    ReferenceImageValidationHint = "当前视频参数组合无可用尺寸映射";
                }
                else
                {
                    IsReferenceImageSizeValid = true;
                    ReferenceImageValidationHint = string.Empty;
                }

                ((RelayCommand)GenerateCommand).RaiseCanExecuteChanged();
                return;
            }

            TargetVideoWidth = targetW;
            TargetVideoHeight = targetH;

            if (!IsVideoMode || !HasReferenceImage)
            {
                IsReferenceImageSizeValid = true;
                ReferenceImageValidationHint = string.Empty;
                ((RelayCommand)GenerateCommand).RaiseCanExecuteChanged();
                return;
            }

            var primaryPath = ReferenceImagePath;
            if (string.IsNullOrWhiteSpace(primaryPath) || !File.Exists(primaryPath))
            {
                IsReferenceImageSizeValid = false;
                ReferenceImageValidationHint = "参考图不存在，请重新添加";
                ((RelayCommand)GenerateCommand).RaiseCanExecuteChanged();
                return;
            }

            if (!ImageCropService.TryGetImageSize(primaryPath, out var imageW, out var imageH))
            {
                IsReferenceImageSizeValid = false;
                ReferenceImageValidationHint = "无法读取参考图尺寸，请更换图片";
                ((RelayCommand)GenerateCommand).RaiseCanExecuteChanged();
                return;
            }

            var isMatch = imageW == targetW && imageH == targetH;
            IsReferenceImageSizeValid = isMatch;
            ReferenceImageValidationHint = isMatch
                ? $"参考图尺寸已匹配：{targetW}×{targetH}"
                : $"当前参考图 {imageW}×{imageH}，目标 {targetW}×{targetH}，点击缩略图进行裁切";

            ((RelayCommand)GenerateCommand).RaiseCanExecuteChanged();
        }

        private void DeleteReferenceImageFiles()
        {
            foreach (var path in ReferenceImagePaths.ToList())
            {
                DeleteReferenceImageFile(path);
            }
        }

        private static void DeleteReferenceImageFile(string? path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // ignore cleanup errors
            }
        }

        /// <summary>
        /// 会话激活时，自动为历史“视频已生成/视频已恢复”消息补齐首帧和尾帧。
        /// </summary>
        public async System.Threading.Tasks.Task BackfillVideoFramesForExistingMessagesAsync(CancellationToken cancellationToken = default)
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            if (!await _videoFrameBackfillLock.WaitAsync(0, cancellationToken))
            {
                return;
            }

            try
            {
                var candidates = _allMessages
                    .Where(IsTargetVideoMessage)
                    .ToList();

                var changed = false;
                foreach (var message in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var mediaPaths = message.MediaPaths.ToList();
                    if (mediaPaths.Count == 0)
                    {
                        continue;
                    }

                    var hasFirst = mediaPaths.Any(VideoFrameExtractorService.IsFirstFrameImagePath);
                    var hasLast = mediaPaths.Any(VideoFrameExtractorService.IsLastFrameImagePath);
                    if (hasFirst && hasLast)
                    {
                        continue;
                    }

                    var videoPath = ResolveVideoPathForMessage(mediaPaths);
                    if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
                    {
                        continue;
                    }

                    var frameResult = await VideoFrameExtractorService
                        .TryExtractFirstAndLastFrameAsync(videoPath, cancellationToken)
                        .ConfigureAwait(false);

                    if (string.IsNullOrWhiteSpace(frameResult.FirstFramePath)
                        && string.IsNullOrWhiteSpace(frameResult.LastFramePath))
                    {
                        continue;
                    }

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        message.MediaPaths.Clear();
                        if (!string.IsNullOrWhiteSpace(frameResult.FirstFramePath))
                        {
                            message.MediaPaths.Add(frameResult.FirstFramePath);
                        }

                        if (!string.IsNullOrWhiteSpace(frameResult.LastFramePath))
                        {
                            message.MediaPaths.Add(frameResult.LastFramePath);
                        }

                        if (message.MediaPaths.Count == 0)
                        {
                            message.MediaPaths.Add(videoPath);
                        }
                    });

                    changed = true;
                }

                if (changed)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => _onRequestSave?.Invoke(this));
                }
            }
            catch (OperationCanceledException)
            {
                // 忽略取消
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"历史视频补帧失败: {ex.Message}");
            }
            finally
            {
                _videoFrameBackfillLock.Release();
            }
        }

        private static bool IsTargetVideoMessage(ChatMessageViewModel message)
        {
            if (message.Role == "user" || message.IsLoading)
            {
                return false;
            }

            if (message.MediaPaths.Count == 0)
            {
                return false;
            }

            var text = message.Text ?? string.Empty;
            return text.Contains("视频已生成", StringComparison.Ordinal)
                || text.Contains("视频已恢复", StringComparison.Ordinal);
        }

        private static string? ResolveVideoPathForMessage(IReadOnlyList<string> mediaPaths)
        {
            foreach (var path in mediaPaths)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                if (Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(path))
                {
                    return path;
                }

                if (VideoFrameExtractorService.TryResolveVideoPathFromFirstFrame(path, out var fromFirst)
                    && File.Exists(fromFirst))
                {
                    return fromFirst;
                }
            }

            return null;
        }

        private void OpenFile(string? filePath)
        {
            if (VideoFrameExtractorService.TryResolveVideoPathFromFirstFrame(filePath, out var videoPath))
            {
                filePath = videoPath;
            }

            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = filePath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"打开文件失败: {ex.Message}");
            }
        }

        // ── Web 搜索辅助（Cherry Studio 风格） ──────────────

        private sealed record WebSearchContext(
            string FormattedContext,
            IReadOnlyList<Services.WebSearch.WebSearchResult> RawResults,
            string SearchQuery,
            string ProviderId);

        /// <summary>
        /// Cherry 风格搜索：LLM 意图分析 → 多查询并行搜索 → JSON 引用格式化。
        /// 失败时不抛异常，返回空结果。
        /// </summary>
        private async Task<WebSearchContext> ExecuteWebSearchAsync(
            string prompt,
            AiChatRequestConfig runtimeRequest,
            AzureTokenProvider? tokenProvider,
            CancellationToken ct)
        {
            var empty = new WebSearchContext("", [], "", "");
            if (!_enableWebSearch) return empty;

            try
            {
                var factory = new Services.WebSearch.WebSearchProviderFactory();
                var provider = factory.Create(_webSearchProviderId,
                    _webSearchMcpEndpoint, _webSearchMcpToolName, _webSearchMcpApiKey);

                // 搜索进度回调 — 分阶段更新 AI 消息文本
                var searchProgress = new Services.WebSearch.SearchAgentService.SearchProgress
                {
                    OnIntentAnalyzed = queries =>
                    {
                        var q = string.Join("、", queries);
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            _searchProgressMessage!.Text = $"🔍 正在搜索「{q}」...");
                    },
                    OnSearchCompleted = count =>
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            _searchProgressMessage!.Text = $"📄 找到 {count} 条结果，正在读取网页内容...");
                    },
                    OnFetchingContent = () =>
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            _searchProgressMessage!.Text = "📖 正在提取网页正文...");
                    }
                };

                // Cherry 风格：取最近一条 assistant 回复作为对话上下文
                string? lastAssistantReply = null;
                for (int i = _allMessages.Count - 1; i >= 0; i--)
                {
                    if (_allMessages[i].Role == "assistant" &&
                        !string.IsNullOrEmpty(_allMessages[i].Text))
                    {
                        lastAssistantReply = _allMessages[i].Text;
                        break;
                    }
                }

                var agent = new Services.WebSearch.SearchAgentService();
                var agentResult = await agent.RunAsync(
                    prompt, lastAssistantReply, provider,
                    runtimeRequest, tokenProvider, ct,
                    maxResults: _webSearchMaxResults,
                    progress: searchProgress);

                if (!agentResult.NeedsSearch) return empty;
                if (agentResult.Results.Count == 0) return empty;

                var displayQuery = string.Join("」「", agentResult.AllQueries);
                var formatted = Services.WebSearch.SearchAgentService
                    .FormatContext(agentResult, prompt);

                return new WebSearchContext(
                    formatted, agentResult.Results, displayQuery, _webSearchProviderId);
            }
            catch
            {
                return empty;
            }
        }

        /// <summary>挂载搜索结果引用和搜索过程摘要到 AI 消息</summary>
        private static void AttachSearchResults(ChatMessageViewModel aiMessage, WebSearchContext webSearch)
        {
            if (webSearch.RawResults.Count == 0) return;

            var citations = new List<Services.WebSearch.SearchCitation>();
            for (int i = 0; i < webSearch.RawResults.Count; i++)
                citations.Add(Services.WebSearch.SearchCitation.FromResult(webSearch.RawResults[i], i + 1));
            aiMessage.Citations = citations;

            // Cherry 风格搜索摘要
            var queries = webSearch.SearchQuery.Split("」「");
            var summary = $"🔍 搜索「{webSearch.SearchQuery}」→ 返回 {webSearch.RawResults.Count} 条结果";
            aiMessage.SearchSummary = summary;
        }

        // ── 文本聊天 ───────────────────────────────────────

        private async void SendTextChatAsync(string prompt)
        {
            if (IsChatStreaming) return;

            // 添加用户消息
            AppendMessage(new ChatMessageViewModel(new MediaChatMessage
            {
                Role = "user",
                Text = prompt,
                ContentType = "text",
                Timestamp = DateTime.Now
            }));

            // 构建运行时配置
            if (!TryBuildChatRuntimeConfig(out var runtimeRequest, out var endpoint, out var errorText))
            {
                AppendMessage(new ChatMessageViewModel(new MediaChatMessage
                {
                    Role = "assistant",
                    Text = errorText,
                    ContentType = "text",
                    Timestamp = DateTime.Now
                }));
                _onRequestSave?.Invoke(this);
                return;
            }

            // AAD 令牌
            AzureTokenProvider? tokenProvider = null;
            if (runtimeRequest.AzureAuthMode == AzureAuthMode.AAD)
            {
                tokenProvider = await _azureTokenProviderStore.GetAuthenticatedProviderAsync(
                    endpoint != null ? $"endpoint_{endpoint.Id}" : "ai",
                    runtimeRequest.AzureTenantId,
                    runtimeRequest.AzureClientId);
            }

            // 推理诊断
            var reasoningRequested = _enableReasoning;

            // AI 占位消息
            var aiMessage = new ChatMessageViewModel(new MediaChatMessage
            {
                Role = "assistant",
                Text = "",
                ContentType = "text",
                Timestamp = DateTime.Now
            }) { IsLoading = true, IsStreamingDone = false };
            AppendMessage(aiMessage);

            _chatCts?.Cancel();
            _chatCts = new CancellationTokenSource();
            var ct = _chatCts.Token;
            IsChatStreaming = true;

            // Web 搜索增强（Cherry 风格：分阶段显示搜索进度）
            if (_enableWebSearch)
                aiMessage.Text = "🔍 正在分析搜索意图...";
            _searchProgressMessage = aiMessage;
            var webSearch = await ExecuteWebSearchAsync(prompt, runtimeRequest, tokenProvider, ct);
            _searchProgressMessage = null;
            if (_enableWebSearch && webSearch.RawResults.Count > 0)
            {
                aiMessage.Text = $"✅ 找到 {webSearch.RawResults.Count} 条结果，生成中...";
                // 提前挂载引用，用户可在流式过程中查看来源
                AttachSearchResults(aiMessage, webSearch);
            }

            var runtimeService = new AiInsightService(tokenProvider);
            var sb = new StringBuilder();
            var reasoningSb = new StringBuilder();
            _chatFlushThrottle.Restart();

            // 字符级平滑流式动画器：AI token 进入队列，按帧节奏追加到 UI
            _streamAnimator?.Dispose();
            _streamAnimator = new Controls.Markdown.SmoothStreamingAnimator(displayedText =>
            {
                aiMessage.Text = displayedText;
            });

            // 构建多轮上下文
            var contextContent = BuildMultiTurnContent(prompt);
            if (!string.IsNullOrEmpty(webSearch.FormattedContext))
                contextContent = webSearch.FormattedContext + "\n\n---\n\n" + contextContent;

            try
            {
                await runtimeService.StreamChatAsync(
                    runtimeRequest,
                    "你是一个有帮助的AI助手。请用中文回答用户的问题。",
                    contextContent,
                    chunk =>
                    {
                        sb.Append(chunk);
                        // 主文本通过动画器实现字符级平滑追加
                        _streamAnimator?.AppendToken(chunk);
                        // Reasoning 仍用节流刷新
                        ThrottledFlushReasoning(aiMessage, reasoningSb);
                    },
                    ct,
                    AiChatProfile.Summary,
                    enableReasoning: _enableReasoning,
                    onOutcome: outcome =>
                    {
                        if (outcome.PromptTokens.HasValue || outcome.CompletionTokens.HasValue)
                        {
                            Dispatcher.UIThread.Post(() =>
                            {
                                aiMessage.PromptTokens = outcome.PromptTokens;
                                aiMessage.CompletionTokens = outcome.CompletionTokens;
                            });
                        }
                    },
                    onReasoningChunk: reasoningChunk =>
                    {
                        reasoningSb.Append(reasoningChunk);
                        ThrottledFlushReasoning(aiMessage, reasoningSb);
                    });

                // 最终刷新：停止动画器，直接设置完整文本
                _streamAnimator?.EndStream();
                Dispatcher.UIThread.Post(() =>
                {
                    aiMessage.Text = sb.ToString();
                    aiMessage.ReasoningText = reasoningSb.ToString();
                    aiMessage.IsLoading = false;
                    aiMessage.IsStreamingDone = true;
                    aiMessage.ReasoningStatusText = BuildReasoningStatusText(
                        reasoningRequested, reasoningSb.Length > 0);
                    // 流式完成后自动收起思考区，让用户聚焦答案
                    if (aiMessage.HasReasoning)
                        aiMessage.IsReasoningExpanded = false;

                    // P1: 挂载搜索引用来源 + 搜索摘要
                    AttachSearchResults(aiMessage, webSearch);
                });
            }
            catch (OperationCanceledException)
            {
                _streamAnimator?.EndStream();
                Dispatcher.UIThread.Post(() =>
                {
                    aiMessage.Text = sb.ToString();
                    aiMessage.ReasoningText = reasoningSb.ToString();
                    aiMessage.IsLoading = false;
                    aiMessage.IsStreamingDone = true;
                });
            }
            catch (Exception ex)
            {
                _streamAnimator?.EndStream();
                Dispatcher.UIThread.Post(() =>
                {
                    aiMessage.Text = sb.Length > 0
                        ? sb + $"\n\n---\n**错误**: {ex.Message}"
                        : $"**错误**: {ex.Message}";
                    aiMessage.ReasoningText = reasoningSb.ToString();
                    aiMessage.IsLoading = false;
                    aiMessage.IsStreamingDone = true;
                });
            }
            finally
            {
                IsChatStreaming = false;
                _chatFlushThrottle.Stop();
                _streamAnimator?.Dispose();
                _streamAnimator = null;
                _onRequestSave?.Invoke(this);
            }
        }

        /// <summary>Reasoning 文本的节流刷新（主文本由 SmoothStreamingAnimator 处理）</summary>
        private void ThrottledFlushReasoning(ChatMessageViewModel aiMessage, StringBuilder reasoningSb)
        {
            if (_chatFlushThrottle.ElapsedMilliseconds < ChatFlushIntervalMs)
                return;
            _chatFlushThrottle.Restart();
            var reasoning = reasoningSb.ToString();
            var hasNewReasoning = reasoning.Length > 0;
            Dispatcher.UIThread.Post(() =>
            {
                aiMessage.ReasoningText = reasoning;
                // 首次收到推理内容时自动展开
                if (hasNewReasoning && !aiMessage.IsReasoningExpanded)
                    aiMessage.IsReasoningExpanded = true;
            });
        }

        private static string BuildReasoningStatusText(bool requested, bool hasContent)
        {
            if (!requested)
                return "💡 思考：关闭（点击底部 ❓ 开启）";
            if (hasContent)
                return "✅ 思考：已收到推理内容";
            return "⚠️ 思考：已请求但服务端未返回推理内容（模型可能不支持 reasoning）";
        }

        private string BuildMultiTurnContent(string currentPrompt)
        {
            var textMessages = _allMessages
                .Where(m => m.IsTextContent && !string.IsNullOrWhiteSpace(m.Text))
                .TakeLast(20)
                .ToList();

            // 排除刚刚加入的用户消息（已在 textMessages 里）和占位 AI 消息
            if (textMessages.Count <= 1)
                return currentPrompt;

            var sb = new StringBuilder();
            // 不包含最后一条（那是刚发的 user prompt）
            foreach (var msg in textMessages.Take(textMessages.Count - 1))
            {
                var label = msg.IsUser ? "用户" : "AI";
                sb.AppendLine($"[{label}]: {msg.Text}");
                sb.AppendLine();
            }
            sb.AppendLine($"[用户]: {currentPrompt}");
            return sb.ToString();
        }

        private bool TryBuildChatRuntimeConfig(
            out AiChatRequestConfig runtimeRequest,
            out AiEndpoint? endpoint,
            out string errorMessage)
        {
            runtimeRequest = new AiChatRequestConfig();
            endpoint = null;
            errorMessage = "";

            var reference = _aiConfig.ReviewModelRef
                         ?? _aiConfig.SummaryModelRef
                         ?? _aiConfig.QuickModelRef
                         ?? _aiConfig.InsightModelRef;

            if (_modelRuntimeResolver.TryResolve(BuildRuntimeConfig(), reference, ModelCapability.Text, out var runtime, out var resolveError)
                && runtime != null)
            {
                endpoint = runtime.Endpoint;
                runtimeRequest = runtime.CreateChatRequest(_aiConfig.SummaryEnableReasoning);
                return true;
            }

            errorMessage = string.IsNullOrWhiteSpace(resolveError)
                ? "未配置文本模型，请在设置中选择复盘/洞察模型。"
                : resolveError;
            return false;
        }

        private void CopyMessage(ChatMessageViewModel? message)
        {
            if (message == null || string.IsNullOrEmpty(message.Text)) return;
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    var clipboard = Avalonia.Application.Current?.ApplicationLifetime is
                        Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                        ? desktop.MainWindow?.Clipboard
                        : null;
                    if (clipboard != null)
                        await clipboard.SetTextAsync(message.Text);
                }
                catch { /* ignore */ }
            });
        }

        private void RegenerateMessageAsync(ChatMessageViewModel? message)
        {
            if (message == null || !message.IsAssistant || IsChatStreaming) return;

            // 找到该 AI 消息前面最近的用户消息
            var idx = _allMessages.IndexOf(message);
            if (idx < 0) return;

            string? userPrompt = null;
            for (int i = idx - 1; i >= 0; i--)
            {
                if (_allMessages[i].IsUser && _allMessages[i].IsTextContent)
                {
                    userPrompt = _allMessages[i].Text;
                    break;
                }
            }
            if (string.IsNullOrWhiteSpace(userPrompt)) return;

            // 移除该 AI 消息
            RemoveMessageInternal(message);

            // 重新发送
            SendTextChatAsync(userPrompt);
        }

        private void StopChatStreaming()
        {
            _chatCts?.Cancel();
        }

        private bool TryResolveImageRuntime(out ModelRuntimeResolution? runtime, out string errorMessage)
        {
            return _modelRuntimeResolver.TryResolve(BuildRuntimeConfig(), _genConfig.ImageModelRef, ModelCapability.Image, out runtime, out errorMessage);
        }

        private bool TryResolveVideoRuntime(out ModelRuntimeResolution? runtime, out string errorMessage)
        {
            return _modelRuntimeResolver.TryResolve(BuildRuntimeConfig(), _genConfig.VideoModelRef, ModelCapability.Video, out runtime, out errorMessage);
        }

        private AiEndpoint? ResolveEndpoint(ModelReference? reference)
        {
            if (reference == null || _endpoints == null) return null;
            return _endpoints.FirstOrDefault(e => e.Id == reference.EndpointId && e.IsEnabled);
        }

        private AzureSpeechConfig BuildRuntimeConfig()
            => new()
            {
                AiConfig = _aiConfig,
                MediaGenConfig = _genConfig,
                Endpoints = _endpoints
            };

        private static string BuildSourceSummary(MediaSessionSourceInfo? sourceInfo)
        {
            if (sourceInfo == null)
            {
                return string.Empty;
            }

            var sessionName = !string.IsNullOrWhiteSpace(sourceInfo.SourceSessionName)
                ? sourceInfo.SourceSessionName
                : sourceInfo.SourceSessionDirectoryName;
            var assetName = string.IsNullOrWhiteSpace(sourceInfo.SourceAssetFileName)
                ? "上一个结果"
                : sourceInfo.SourceAssetFileName;

            return string.Equals(sourceInfo.ReferenceRole, "VideoLastFrame", StringComparison.OrdinalIgnoreCase)
                ? $"衍生自“{sessionName}”的视频尾帧：{assetName}"
                : $"衍生自“{sessionName}”的图片：{assetName}";
        }

        private static MediaSessionSourceInfo? CloneSourceInfo(MediaSessionSourceInfo? sourceInfo)
        {
            if (sourceInfo == null)
            {
                return null;
            }

            return new MediaSessionSourceInfo
            {
                SourceSessionId = sourceInfo.SourceSessionId,
                SourceSessionName = sourceInfo.SourceSessionName,
                SourceSessionDirectoryName = sourceInfo.SourceSessionDirectoryName,
                SourceAssetId = sourceInfo.SourceAssetId,
                SourceAssetKind = sourceInfo.SourceAssetKind,
                SourceAssetFileName = sourceInfo.SourceAssetFileName,
                SourceAssetPath = sourceInfo.SourceAssetPath,
                SourcePreviewPath = sourceInfo.SourcePreviewPath,
                ReferenceRole = sourceInfo.ReferenceRole
            };
        }

        private static MediaAssetRecord CloneAssetRecord(MediaAssetRecord asset)
        {
            return new MediaAssetRecord
            {
                AssetId = asset.AssetId,
                GroupId = asset.GroupId,
                Kind = asset.Kind,
                Workflow = asset.Workflow,
                FileName = asset.FileName,
                FilePath = asset.FilePath,
                PreviewPath = asset.PreviewPath,
                PromptText = asset.PromptText,
                CreatedAt = asset.CreatedAt,
                ModifiedAt = asset.ModifiedAt,
                DerivedFromSessionId = asset.DerivedFromSessionId,
                DerivedFromSessionName = asset.DerivedFromSessionName,
                DerivedFromAssetId = asset.DerivedFromAssetId,
                DerivedFromAssetFileName = asset.DerivedFromAssetFileName,
                DerivedFromAssetKind = asset.DerivedFromAssetKind,
                DerivedFromReferenceRole = asset.DerivedFromReferenceRole
            };
        }

        // ── 消息编辑 ────────────────────────────────────────

        /// <summary>
        /// 编辑用户消息：进入原地编辑模式，显示 TextBox 和操作按钮。
        /// </summary>
        private void EditMessage(ChatMessageViewModel? message)
        {
            if (message == null || !message.IsUser || IsChatStreaming) return;

            message.EditText = message.Text;
            message.IsEditing = true;
        }

        /// <summary>保存编辑：仅更新文本，不重发。</summary>
        private void SaveEdit(ChatMessageViewModel? message)
        {
            if (message == null) return;
            message.Text = message.EditText;
            message.IsEditing = false;
            _onRequestSave?.Invoke(this);
        }

        /// <summary>取消编辑：恢复原文。</summary>
        private void CancelEdit(ChatMessageViewModel? message)
        {
            if (message == null) return;
            message.IsEditing = false;
        }

        /// <summary>发送编辑：更新文本，删除后续消息，并重新生成。</summary>
        private void SendEdit(ChatMessageViewModel? message)
        {
            if (message == null || IsChatStreaming) return;

            message.Text = message.EditText;
            message.IsEditing = false;

            // 删除该消息之后的所有消息
            var idx = _allMessages.IndexOf(message);
            if (idx < 0) return;

            var toRemove = _allMessages.Skip(idx + 1).ToList();
            foreach (var m in toRemove)
                RemoveMessageInternal(m);

            _onRequestSave?.Invoke(this);

            // 以编辑后的消息文本重新发送（不重复添加用户消息）
            ResendTextChatAsync(message.Text);
        }

        /// <summary>基于已存在的用户消息重新发起文本聊天（不重新添加用户消息）。</summary>
        private async void ResendTextChatAsync(string prompt)
        {
            if (IsChatStreaming) return;

            if (!TryBuildChatRuntimeConfig(out var runtimeRequest, out var endpoint, out var errorText))
            {
                AppendMessage(new ChatMessageViewModel(new MediaChatMessage
                {
                    Role = "assistant",
                    Text = errorText,
                    ContentType = "text",
                    Timestamp = DateTime.Now
                }));
                _onRequestSave?.Invoke(this);
                return;
            }

            AzureTokenProvider? tokenProvider = null;
            if (runtimeRequest.AzureAuthMode == AzureAuthMode.AAD)
            {
                tokenProvider = await _azureTokenProviderStore.GetAuthenticatedProviderAsync(
                    endpoint != null ? $"endpoint_{endpoint.Id}" : "ai",
                    runtimeRequest.AzureTenantId,
                    runtimeRequest.AzureClientId);
            }

            var reasoningRequested = _enableReasoning;
            var aiMessage = new ChatMessageViewModel(new MediaChatMessage
            {
                Role = "assistant",
                Text = "",
                ContentType = "text",
                Timestamp = DateTime.Now
            }) { IsLoading = true, IsStreamingDone = false };
            AppendMessage(aiMessage);

            _chatCts?.Cancel();
            _chatCts = new CancellationTokenSource();
            var ct = _chatCts.Token;
            IsChatStreaming = true;

            var runtimeService = new AiInsightService(tokenProvider);
            var sb = new StringBuilder();
            var reasoningSb = new StringBuilder();
            _chatFlushThrottle.Restart();

            _streamAnimator?.Dispose();
            _streamAnimator = new Controls.Markdown.SmoothStreamingAnimator(displayedText =>
            {
                aiMessage.Text = displayedText;
            });

            // Web 搜索增强（Cherry 风格：分阶段显示搜索进度）
            if (_enableWebSearch)
                aiMessage.Text = "🔍 正在分析搜索意图...";
            _searchProgressMessage = aiMessage;
            var webSearch = await ExecuteWebSearchAsync(prompt, runtimeRequest, tokenProvider, ct);
            _searchProgressMessage = null;
            if (_enableWebSearch && webSearch.RawResults.Count > 0)
            {
                aiMessage.Text = $"✅ 找到 {webSearch.RawResults.Count} 条结果，生成中...";
                AttachSearchResults(aiMessage, webSearch);
            }

            var contextContent = BuildMultiTurnContent(prompt);
            if (!string.IsNullOrEmpty(webSearch.FormattedContext))
                contextContent = webSearch.FormattedContext + "\n\n---\n\n" + contextContent;

            try
            {
                await runtimeService.StreamChatAsync(
                    runtimeRequest,
                    "你是一个有帮助的AI助手。请用中文回答用户的问题。",
                    contextContent,
                    chunk =>
                    {
                        sb.Append(chunk);
                        _streamAnimator?.AppendToken(chunk);
                        ThrottledFlushReasoning(aiMessage, reasoningSb);
                    },
                    ct,
                    AiChatProfile.Summary,
                    enableReasoning: _enableReasoning,
                    onOutcome: outcome =>
                    {
                        if (outcome.PromptTokens.HasValue || outcome.CompletionTokens.HasValue)
                        {
                            Dispatcher.UIThread.Post(() =>
                            {
                                aiMessage.PromptTokens = outcome.PromptTokens;
                                aiMessage.CompletionTokens = outcome.CompletionTokens;
                            });
                        }
                    },
                    onReasoningChunk: reasoningChunk =>
                    {
                        reasoningSb.Append(reasoningChunk);
                        ThrottledFlushReasoning(aiMessage, reasoningSb);
                    });

                _streamAnimator?.EndStream();
                Dispatcher.UIThread.Post(() =>
                {
                    aiMessage.Text = sb.ToString();
                    aiMessage.ReasoningText = reasoningSb.ToString();
                    aiMessage.IsLoading = false;
                    aiMessage.IsStreamingDone = true;
                    aiMessage.ReasoningStatusText = BuildReasoningStatusText(
                        reasoningRequested, reasoningSb.Length > 0);
                    if (aiMessage.HasReasoning)
                        aiMessage.IsReasoningExpanded = false;

                    // 挂载搜索引用来源 + 搜索摘要
                    AttachSearchResults(aiMessage, webSearch);
                });
            }
            catch (OperationCanceledException)
            {
                _streamAnimator?.EndStream();
                Dispatcher.UIThread.Post(() =>
                {
                    aiMessage.Text = sb.ToString();
                    aiMessage.ReasoningText = reasoningSb.ToString();
                    aiMessage.IsLoading = false;
                    aiMessage.IsStreamingDone = true;
                });
            }
            catch (Exception ex)
            {
                _streamAnimator?.EndStream();
                Dispatcher.UIThread.Post(() =>
                {
                    aiMessage.Text = sb.Length > 0
                        ? sb + $"\n\n---\n**错误**: {ex.Message}"
                        : $"**错误**: {ex.Message}";
                    aiMessage.ReasoningText = reasoningSb.ToString();
                    aiMessage.IsLoading = false;
                    aiMessage.IsStreamingDone = true;
                });
            }
            finally
            {
                IsChatStreaming = false;
                _chatFlushThrottle.Stop();
                _streamAnimator?.Dispose();
                _streamAnimator = null;
                _onRequestSave?.Invoke(this);
            }
        }

        // ── 对话分支（Fork）─────────────────────────────────

        /// <summary>
        /// 事件：请求从当前消息创建对话分支。
        /// 宿主应用订阅此事件以创建新 Session。
        /// </summary>
        public event Action<MediaSessionViewModel, ChatMessageViewModel>? ForkRequested;

        /// <summary>
        /// 从指定消息处创建对话分支。
        /// 触发 ForkRequested 事件，由宿主应用实际创建新 Session。
        /// </summary>
        private void ForkFromMessage(ChatMessageViewModel? message)
        {
            if (message == null) return;
            ForkRequested?.Invoke(this, message);
        }

        /// <summary>
        /// 获取从开头到指定消息（含）的所有消息，用于分支。
        /// </summary>
        public IReadOnlyList<ChatMessageViewModel> GetMessagesUpTo(ChatMessageViewModel message)
        {
            var idx = _allMessages.IndexOf(message);
            if (idx < 0) return Array.Empty<ChatMessageViewModel>();
            return _allMessages.Take(idx + 1).ToList();
        }

        // ── 对话导出 ────────────────────────────────────────

        /// <summary>
        /// 导出当前对话为 Markdown 文件。
        /// </summary>
        private async void ExportConversation()
        {
            if (_allMessages.Count == 0) return;

            try
            {
                var messages = _allMessages.Cast<Controls.Markdown.IDialogMessage>().ToList();
                var markdown = Controls.Markdown.DialogExporter.ExportToMarkdown(messages, SessionName);

                // 使用保存文件对话框
                var desktop = Avalonia.Application.Current?.ApplicationLifetime
                    as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
                var mainWindow = desktop?.MainWindow;
                if (mainWindow == null) return;

                var storage = mainWindow.StorageProvider;
                var file = await storage.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
                {
                    Title = "导出对话",
                    SuggestedFileName = $"{SessionName}_{DateTime.Now:yyyyMMdd_HHmmss}.md",
                    DefaultExtension = "md",
                    FileTypeChoices = new[]
                    {
                        new Avalonia.Platform.Storage.FilePickerFileType("Markdown") { Patterns = new[] { "*.md" } },
                        new Avalonia.Platform.Storage.FilePickerFileType("纯文本") { Patterns = new[] { "*.txt" } },
                        new Avalonia.Platform.Storage.FilePickerFileType("JSON") { Patterns = new[] { "*.json" } },
                    }
                });

                if (file != null)
                {
                    var filePath = file.Path.LocalPath;
                    string content;
                    if (filePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        content = Controls.Markdown.DialogExporter.ExportToJson(messages, SessionName);
                    else if (filePath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                        content = Controls.Markdown.DialogExporter.ExportToPlainText(messages, SessionName);
                    else
                        content = markdown;

                    await System.IO.File.WriteAllTextAsync(filePath, content, System.Text.Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ExportConversation] 导出失败: {ex.Message}");
            }
        }

        // ── 对话搜索 ────────────────────────────────────────

        /// <summary>切换搜索栏可见性</summary>
        public void ToggleSearch()
        {
            IsSearchVisible = !IsSearchVisible;
            if (!IsSearchVisible)
            {
                SearchQuery = "";
                _searchEngine.Clear();
                SearchStatus = "";
            }
        }

        private void ExecuteSearch()
        {
            if (string.IsNullOrWhiteSpace(_searchQuery))
            {
                _searchEngine.Clear();
                SearchStatus = "";
                return;
            }

            _searchEngine.SetMessages(_allMessages.Cast<Controls.Markdown.IDialogMessage>().ToList());
            var count = _searchEngine.Search(_searchQuery);
            SearchStatus = count > 0
                ? $"{_searchEngine.CurrentIndex + 1}/{count}"
                : "无匹配";
        }

        /// <summary>跳转到下一个搜索匹配</summary>
        public void SearchNext()
        {
            var match = _searchEngine.NavigateNext();
            if (match != null)
                SearchStatus = $"{_searchEngine.CurrentIndex + 1}/{_searchEngine.MatchCount}";
        }

        /// <summary>跳转到上一个搜索匹配</summary>
        public void SearchPrevious()
        {
            var match = _searchEngine.NavigatePrevious();
            if (match != null)
                SearchStatus = $"{_searchEngine.CurrentIndex + 1}/{_searchEngine.MatchCount}";
        }

    }

    /// <summary>
    /// 聊天消息 ViewModel
    /// </summary>
    public class ChatMessageViewModel : ViewModelBase, Controls.Markdown.IDialogMessage
    {
        public string Role { get; }

        private string _text;
        public string Text
        {
            get => _text;
            set => SetProperty(ref _text, value);
        }

        public ObservableCollection<string> MediaPaths { get; }

        public DateTime Timestamp { get; }

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set => SetProperty(ref _isLoading, value);
        }

        /// <summary>推理/思考过程文本</summary>
        private string _reasoningText = "";
        public string ReasoningText
        {
            get => _reasoningText;
            set
            {
                if (SetProperty(ref _reasoningText, value))
                    OnPropertyChanged(nameof(HasReasoning));
            }
        }

        private bool _isReasoningExpanded;
        public bool IsReasoningExpanded
        {
            get => _isReasoningExpanded;
            set => SetProperty(ref _isReasoningExpanded, value);
        }

        /// <summary>消息内容类型：text / image / video</summary>
        public string ContentType { get; }

        /// <summary>服务端生成耗时（秒）</summary>
        public double? GenerateSeconds { get; set; }
        /// <summary>下载传输耗时（秒）</summary>
        public double? DownloadSeconds { get; set; }

        private bool _isStreamingDone = true;
        /// <summary>流式输出完成标记：完成后切换到 Markdown 渲染</summary>
        public bool IsStreamingDone
        {
            get => _isStreamingDone;
            set => SetProperty(ref _isStreamingDone, value);
        }

        // ── Token 用量 ──────────────────────────────────
        private int? _promptTokens;
        /// <summary>Token 用量：输入 Token 数</summary>
        public int? PromptTokens
        {
            get => _promptTokens;
            set
            {
                if (SetProperty(ref _promptTokens, value))
                {
                    OnPropertyChanged(nameof(HasTokenUsage));
                    OnPropertyChanged(nameof(TokenUsageText));
                }
            }
        }

        private int? _completionTokens;
        /// <summary>Token 用量：输出 Token 数</summary>
        public int? CompletionTokens
        {
            get => _completionTokens;
            set
            {
                if (SetProperty(ref _completionTokens, value))
                {
                    OnPropertyChanged(nameof(HasTokenUsage));
                    OnPropertyChanged(nameof(TokenUsageText));
                }
            }
        }

        /// <summary>是否有 Token 用量数据</summary>
        public bool HasTokenUsage => _promptTokens.HasValue || _completionTokens.HasValue;

        /// <summary>Token 用量显示文本</summary>
        public string TokenUsageText
        {
            get
            {
                if (!HasTokenUsage) return "";
                var prompt = _promptTokens?.ToString() ?? "?";
                var completion = _completionTokens?.ToString() ?? "?";
                return $"⚡ 输入 {prompt} / 输出 {completion} tokens";
            }
        }

        // ── 编辑状态 ────────────────────────────────────
        private bool _isEditing;
        /// <summary>消息是否处于编辑模式</summary>
        public bool IsEditing
        {
            get => _isEditing;
            set
            {
                if (SetProperty(ref _isEditing, value))
                    OnPropertyChanged(nameof(IsNotEditing));
            }
        }

        /// <summary>反向绑定：非编辑模式</summary>
        public bool IsNotEditing => !_isEditing;

        private string _editText = "";
        /// <summary>编辑中的临时文本</summary>
        public string EditText
        {
            get => _editText;
            set => SetProperty(ref _editText, value);
        }

        public bool IsUser => Role == "user";
        public bool IsAssistant => Role != "user";
        public bool HasMedia => MediaPaths.Count > 0;
        public bool HasReasoning => !string.IsNullOrEmpty(_reasoningText);
        public bool IsTextContent => ContentType == "text";

        private string _reasoningStatusText = "";
        /// <summary>推理诊断状态文本，仅完成后显示</summary>
        public string ReasoningStatusText
        {
            get => _reasoningStatusText;
            set
            {
                if (SetProperty(ref _reasoningStatusText, value))
                    OnPropertyChanged(nameof(HasReasoningStatus));
            }
        }
        public bool HasReasoningStatus => !string.IsNullOrEmpty(_reasoningStatusText);

        // ── 搜索引用来源 ────────────────────────────────
        private IReadOnlyList<Services.WebSearch.SearchCitation> _citations = [];
        public IReadOnlyList<Services.WebSearch.SearchCitation> Citations
        {
            get => _citations;
            set
            {
                if (SetProperty(ref _citations, value))
                    OnPropertyChanged(nameof(HasCitations));
            }
        }
        public bool HasCitations => _citations.Count > 0;
        public string CitationsSummary => _citations.Count > 0 ? $"🌐 {_citations.Count} 个来源" : "";

        /// <summary>搜索过程摘要（关键词 + 引擎 + 结果数），显示在引用面板上方</summary>
        private string _searchSummary = "";
        public string SearchSummary
        {
            get => _searchSummary;
            set
            {
                if (SetProperty(ref _searchSummary, value))
                    OnPropertyChanged(nameof(HasSearchSummary));
            }
        }
        public bool HasSearchSummary => !string.IsNullOrEmpty(_searchSummary);

        public string TimestampText => Timestamp.ToString("yyyy-MM-dd HH:mm:ss");

        // ── IDialogMessage 显式实现 ─────────────────────
        IReadOnlyList<string> Controls.Markdown.IDialogMessage.MediaPaths => new List<string>(MediaPaths);

        public ChatMessageViewModel(MediaChatMessage message)
        {
            Role = message.Role;
            _text = message.Text;
            _reasoningText = message.ReasoningText ?? "";
            ContentType = InferContentType(message);
            MediaPaths = new ObservableCollection<string>(message.MediaPaths);
            Timestamp = message.Timestamp;
            GenerateSeconds = message.GenerateSeconds;
            DownloadSeconds = message.DownloadSeconds;
            _promptTokens = message.PromptTokens;
            _completionTokens = message.CompletionTokens;
            _searchSummary = message.SearchSummary ?? "";

            // 恢复搜索引用来源
            if (message.Citations is { Count: > 0 })
            {
                _citations = message.Citations.Select(c => new Services.WebSearch.SearchCitation
                {
                    Number = c.Number,
                    Title = c.Title,
                    Url = c.Url,
                    Snippet = c.Snippet,
                    Hostname = c.Hostname
                }).ToList();
            }

            MediaPaths.CollectionChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(HasMedia));
            };
        }

        private static string InferContentType(MediaChatMessage msg)
        {
            if (!string.IsNullOrEmpty(msg.ContentType))
                return msg.ContentType;
            return msg.MediaPaths.Count > 0 ? "image" : "text";
        }
    }
}
