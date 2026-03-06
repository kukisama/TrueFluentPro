using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
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
        private readonly AiImageGenService _imageService;
        private readonly AiVideoGenService _videoService;
        private readonly Action _onTaskCountChanged;
        private readonly Action<MediaSessionViewModel>? _onRequestSave;
        private CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _videoFrameBackfillLock = new(1, 1);
        private const int MaxReferenceImageCount = 8;

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

        private MediaGenType _selectedType = MediaGenType.Image;
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
            set { if (value) SelectedType = MediaGenType.Image; }
        }

        public bool IsVideoMode
        {
            get => SelectedType == MediaGenType.Video;
            set { if (value) SelectedType = MediaGenType.Video; }
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

        // --- 命令 ---
        public ICommand GenerateCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand OpenFileCommand { get; }
        public ICommand DeleteMessageCommand { get; }
        public ICommand RemoveReferenceImageCommand { get; }

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
            AiImageGenService imageService,
            AiVideoGenService videoService,
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
            if (IsGenerating || string.IsNullOrWhiteSpace(PromptText))
                return false;

            if (IsVideoMode && ReferenceImagePaths.Count > 1)
                return false;

            if (IsVideoMode && HasReferenceImage && !IsReferenceImageSizeValid)
                return false;

            return true;
        }

        private static async System.Threading.Tasks.Task ConfigureScopedServiceAuthAsync(
            AiMediaServiceBase service,
            ModelRuntimeResolution runtime,
            CancellationToken ct)
        {
            service.SetTokenProvider(null);

            if (runtime.AzureAuthMode != AzureAuthMode.AAD)
                return;

            var provider = new AzureTokenProvider(runtime.ProfileKey);
            await provider.TrySilentLoginAsync(runtime.AzureTenantId, runtime.AzureClientId, ct);
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

            if (Messages.Contains(message))
            {
                Messages.Remove(message);
                _onRequestSave?.Invoke(this);
            }
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

            // 添加用户消息
            Messages.Add(new ChatMessageViewModel(new MediaChatMessage
            {
                Role = "user",
                Text = prompt,
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
                Prompt = prompt
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
            Messages.Add(loadingMessage);

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
                Prompt = prompt
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
            Messages.Add(loadingMessage);

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
            Messages.Add(loadingMessage);


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
                var candidates = Messages
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

    }

    /// <summary>
    /// 聊天消息 ViewModel
    /// </summary>
    public class ChatMessageViewModel : ViewModelBase
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

        /// <summary>服务端生成耗时（秒）</summary>
        public double? GenerateSeconds { get; set; }
        /// <summary>下载传输耗时（秒）</summary>
        public double? DownloadSeconds { get; set; }

        public bool IsUser => Role == "user";
        public bool IsAssistant => Role != "user";
        public bool HasMedia => MediaPaths.Count > 0;

        public string TimestampText => Timestamp.ToString("HH:mm:ss");

        public ChatMessageViewModel(MediaChatMessage message)
        {
            Role = message.Role;
            _text = message.Text;
            MediaPaths = new ObservableCollection<string>(message.MediaPaths);
            Timestamp = message.Timestamp;
            GenerateSeconds = message.GenerateSeconds;
            DownloadSeconds = message.DownloadSeconds;

            MediaPaths.CollectionChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(HasMedia));
            };
        }
    }
}
