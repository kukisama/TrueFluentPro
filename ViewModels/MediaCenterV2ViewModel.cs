using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using TrueFluentPro.Services.Storage;

namespace TrueFluentPro.ViewModels
{
    /// <summary>
    /// 媒体中心 v2 —— 创作者工作区。
    /// 不再优先背负旧会话馆的历史浏览心智，而是围绕“当前画布 / 当前结果 / 继续衍生”组织页面。
    /// </summary>
    public class MediaCenterV2ViewModel : ViewModelBase, IDisposable
    {
        private readonly AiConfig _aiConfig = new();
        private readonly MediaGenConfig _genConfig = new();
        private readonly List<AiEndpoint> _endpoints = new();
        private readonly IModelRuntimeResolver _modelRuntimeResolver;
        private readonly IAzureTokenProviderStore _azureTokenProviderStore;
        private readonly AiImageGenService _imageService = new();
        private readonly AiVideoGenService _videoService = new();
        private readonly string _workspaceRootDirectory;

        private readonly ObservableCollection<MediaWorkspaceTabViewModel> _workspaceTabs = new();
        private readonly ObservableCollection<MediaCreatorResultGroup> _resultGroups = new();
        private readonly ObservableCollection<MediaCreatorResultAsset> _resultRailItems = new();
        private readonly Dictionary<ChatMessageViewModel, NotifyCollectionChangedEventHandler> _messageMediaHandlers = new();
        private readonly Dictionary<MediaGenTask, PropertyChangedEventHandler> _taskHandlers = new();

        // SQLite 写入去重：指纹 + 任务状态追踪
        private readonly Dictionary<string, (int MsgCount, int TaskCount, int Completed, int Active, int Terminal, int AssetCount, string? Name, string? Canvas, string? Kind, bool Deleted)> _sqliteFingerprints = new();
        private readonly Dictionary<string, Dictionary<string, string>> _persistedTaskStates = new();

        private MediaSessionViewModel? _workspaceSession;
        private MediaWorkspaceTabViewModel? _selectedWorkspaceTab;
        private MediaCreatorResultGroup? _selectedGroup;
        private MediaCreatorResultAsset? _selectedAsset;
        private CreatorCanvasMode _canvasMode = CreatorCanvasMode.Draw;
        private CreatorMediaKind _mediaKind = CreatorMediaKind.Image;
        private string _draftPromptText = string.Empty;
        private bool _isSubmittingNewWorkspace;
        private string _statusText = "正在初始化创作工作区...";
        private bool _disposed;

        public MediaCenterV2ViewModel(
            AiConfig aiConfig,
            MediaGenConfig genConfig,
            List<AiEndpoint> endpoints,
            IModelRuntimeResolver modelRuntimeResolver,
            IAzureTokenProviderStore azureTokenProviderStore)
        {
            _modelRuntimeResolver = modelRuntimeResolver;
            _azureTokenProviderStore = azureTokenProviderStore;

            CopyAiConfig(aiConfig, _aiConfig);
            CopyMediaGenConfig(genConfig, _genConfig);
            _endpoints.AddRange(endpoints ?? new List<AiEndpoint>());

            var sessionsPath = PathManager.Instance.SessionsPath;
            _workspaceRootDirectory = Path.Combine(sessionsPath, "media-center-v2");
            Directory.CreateDirectory(_workspaceRootDirectory);

            NewWorkspaceCommand = new RelayCommand(_ => CreateNewWorkspace());
            SubmitPromptCommand = new RelayCommand(
                _ => _ = SubmitPromptToNewWorkspaceAsync(),
                _ => CanSubmitPrompt);
            SelectWorkspaceTabCommand = new RelayCommand(
                p => SelectWorkspaceTab(p as MediaWorkspaceTabViewModel),
                p => p is MediaWorkspaceTabViewModel);
            RemoveWorkspaceCommand = new RelayCommand(
                p => RemoveWorkspace(p as MediaWorkspaceTabViewModel),
                p => p is MediaWorkspaceTabViewModel);
            SelectAssetCommand = new RelayCommand(
                p => SelectAsset(p as MediaCreatorResultAsset),
                p => p is MediaCreatorResultAsset);
            SelectPreviousAssetCommand = new RelayCommand(
                _ => SelectAdjacentAsset(-1),
                _ => CanSelectPreviousAsset);
            SelectNextAssetCommand = new RelayCommand(
                _ => SelectAdjacentAsset(1),
                _ => CanSelectNextAsset);
            OpenSelectedAssetCommand = new RelayCommand(
                _ => OpenSelectedAsset(),
                _ => SelectedAsset is { IsPending: false } && File.Exists(SelectedAsset.FilePath));
            OpenSelectedAssetInExplorerCommand = new RelayCommand(
                _ => OpenSelectedAssetInExplorer(),
                _ => SelectedAsset is { IsPending: false } && File.Exists(SelectedAsset.FilePath));
            OpenWorkspaceFolderCommand = new RelayCommand(
                _ => OpenWorkspaceFolder(),
                _ => WorkspaceSession != null && Directory.Exists(WorkspaceSession.SessionDirectory));
            RefreshWorkspaceCommand = new RelayCommand(_ => RebuildResultCollections(selectLatest: false));
            EditAssetCommand = new RelayCommand(
                p => _ = EditAssetAsync(p as MediaCreatorResultAsset),
                p => p is MediaCreatorResultAsset { IsPending: false });

            _ = InitializeAsync();
        }

        public MediaSessionViewModel? WorkspaceSession
        {
            get => _workspaceSession;
            private set
            {
                if (SetProperty(ref _workspaceSession, value))
                {
                    OnPropertyChanged(nameof(HasWorkspace));
                    OnPropertyChanged(nameof(HasWorkspaceLineage));
                    OnPropertyChanged(nameof(WorkspaceName));
                    OnPropertyChanged(nameof(WorkspaceLineageText));
                    OnPropertyChanged(nameof(WorkspaceSubtitle));
                    OnPropertyChanged(nameof(WorkspaceStatusSummary));
                    OnPropertyChanged(nameof(CurrentEndpointDisplayName));
                    OnPropertyChanged(nameof(CurrentModelDisplayName));
                    OnPropertyChanged(nameof(CanAttachReferenceImages));
                    OnPropertyChanged(nameof(ReferenceSummaryText));
                    OnPropertyChanged(nameof(IsReferencePanelVisible));
                    OnPropertyChanged(nameof(CanvasPreviewPath));
                    OnPropertyChanged(nameof(HasCanvasPreview));
                    OnPropertyChanged(nameof(HasNoCanvasPreview));
                    OnPropertyChanged(nameof(IsCanvasPreviewVideo));
                    OnPropertyChanged(nameof(IsCanvasPreviewImage));
                    OnPropertyChanged(nameof(CanvasPreviewMetaText));
                    OnPropertyChanged(nameof(CanvasPlaceholderTitle));
                    OnPropertyChanged(nameof(CanvasPlaceholderDescription));
                    OnPropertyChanged(nameof(CurrentModeBadgeText));
                }
            }
        }

        public bool HasWorkspace => WorkspaceSession != null;
        public bool HasWorkspaceLineage => WorkspaceSession?.HasSourceInfo == true;
        public ObservableCollection<MediaWorkspaceTabViewModel> WorkspaceTabs => _workspaceTabs;
        public ObservableCollection<MediaCreatorResultGroup> ResultGroups => _resultGroups;
        public ObservableCollection<MediaCreatorResultAsset> ResultRailItems => _resultRailItems;

        public string DraftPromptText
        {
            get => _draftPromptText;
            set
            {
                if (SetProperty(ref _draftPromptText, value))
                {
                    OnPropertyChanged(nameof(CanSubmitPrompt));
                    (SubmitPromptCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public bool CanSubmitPrompt => !_isSubmittingNewWorkspace && !string.IsNullOrWhiteSpace(DraftPromptText);

        public MediaWorkspaceTabViewModel? SelectedWorkspaceTab
        {
            get => _selectedWorkspaceTab;
            private set
            {
                if (SetProperty(ref _selectedWorkspaceTab, value))
                {
                    foreach (var tab in WorkspaceTabs)
                    {
                        tab.IsSelected = ReferenceEquals(tab, value);
                    }
                }
            }
        }

        public MediaCreatorResultGroup? SelectedGroup
        {
            get => _selectedGroup;
            private set
            {
                if (SetProperty(ref _selectedGroup, value))
                {
                    OnPropertyChanged(nameof(HasSelectedGroup));
                    OnPropertyChanged(nameof(SelectedGroupPromptText));
                    OnPropertyChanged(nameof(SelectedGroupMetaText));
                    OnPropertyChanged(nameof(SelectedGroupResultSummary));
                    OnPropertyChanged(nameof(SelectedGroupSuggestedNextText));
                    OnPropertyChanged(nameof(SelectedGroupWorkflowText));
                }
            }
        }

        public MediaCreatorResultAsset? SelectedAsset
        {
            get => _selectedAsset;
            private set
            {
                if (SetProperty(ref _selectedAsset, value))
                {
                    UpdateSelectedState(value);
                    OnPropertyChanged(nameof(HasSelectedAsset));
                        OnPropertyChanged(nameof(HasNoSelectedAsset));
                    OnPropertyChanged(nameof(SelectedPreviewPath));
                    OnPropertyChanged(nameof(IsSelectedPreviewVideo));
                    OnPropertyChanged(nameof(SelectedAssetCounterText));
                    OnPropertyChanged(nameof(SelectedAssetMetaText));
                    OnPropertyChanged(nameof(SelectedAssetPromptText));
                    OnPropertyChanged(nameof(CanSelectPreviousAsset));
                    OnPropertyChanged(nameof(CanSelectNextAsset));
                    OnPropertyChanged(nameof(WorkspaceStatusSummary));
                    OnPropertyChanged(nameof(CanvasPreviewPath));
                    OnPropertyChanged(nameof(HasCanvasPreview));
                    OnPropertyChanged(nameof(HasNoCanvasPreview));
                    OnPropertyChanged(nameof(IsCanvasPreviewVideo));
                    OnPropertyChanged(nameof(IsCanvasPreviewImage));
                    OnPropertyChanged(nameof(CanvasPreviewMetaText));
                    OnPropertyChanged(nameof(CanvasPlaceholderTitle));
                    OnPropertyChanged(nameof(CanvasPlaceholderDescription));
                    (SelectPreviousAssetCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (SelectNextAssetCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (OpenSelectedAssetCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (OpenSelectedAssetInExplorerCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public bool HasSelectedGroup => SelectedGroup != null;
        public bool HasSelectedAsset => SelectedAsset != null;
        public bool HasNoSelectedAsset => SelectedAsset == null;

        public CreatorCanvasMode CanvasMode
        {
            get => _canvasMode;
            set
            {
                if (SetProperty(ref _canvasMode, value))
                {
                    if (SelectedWorkspaceTab != null && SelectedWorkspaceTab.CanvasMode != value)
                    {
                        SelectedWorkspaceTab.CanvasMode = value;
                    }

                    OnPropertyChanged(nameof(IsDrawMode));
                    OnPropertyChanged(nameof(IsEditMode));
                    OnPropertyChanged(nameof(CurrentCanvasTitle));
                    OnPropertyChanged(nameof(CurrentCanvasDescription));
                    OnPropertyChanged(nameof(PromptWatermark));
                    OnPropertyChanged(nameof(ReferenceSummaryText));
                    OnPropertyChanged(nameof(IsReferencePanelVisible));
                    OnPropertyChanged(nameof(CurrentModeBadgeText));
                    OnPropertyChanged(nameof(ModeAccentBackground));
                    OnPropertyChanged(nameof(ModeAccentBorderBrush));
                    OnPropertyChanged(nameof(CanvasPreviewPath));
                    OnPropertyChanged(nameof(HasCanvasPreview));
                    OnPropertyChanged(nameof(HasNoCanvasPreview));
                    OnPropertyChanged(nameof(IsCanvasPreviewVideo));
                    OnPropertyChanged(nameof(IsCanvasPreviewImage));
                    OnPropertyChanged(nameof(CanvasPreviewMetaText));
                    OnPropertyChanged(nameof(CanvasPlaceholderTitle));
                    OnPropertyChanged(nameof(CanvasPlaceholderDescription));
                }
            }
        }

        public CreatorMediaKind MediaKind
        {
            get => _mediaKind;
            set
            {
                if (SetProperty(ref _mediaKind, value))
                {
                    // 注意：切换 MediaKind 只改变视图层面的筛选/参数展示，
                    // 不写回 SelectedWorkspaceTab.MediaKind，避免改变会话的固有类型

                    OnPropertyChanged(nameof(IsImageKind));
                    OnPropertyChanged(nameof(IsVideoKind));
                    OnPropertyChanged(nameof(CurrentCanvasTitle));
                    OnPropertyChanged(nameof(CurrentCanvasDescription));
                    OnPropertyChanged(nameof(PromptWatermark));
                    OnPropertyChanged(nameof(CurrentEndpointDisplayName));
                    OnPropertyChanged(nameof(CurrentModelDisplayName));
                    OnPropertyChanged(nameof(IsReferencePanelVisible));
                    OnPropertyChanged(nameof(CanAttachReferenceImages));
                    OnPropertyChanged(nameof(ReferenceSummaryText));
                    OnPropertyChanged(nameof(CurrentModeBadgeText));
                    OnPropertyChanged(nameof(CanvasPreviewPath));
                    OnPropertyChanged(nameof(HasCanvasPreview));
                    OnPropertyChanged(nameof(HasNoCanvasPreview));
                    OnPropertyChanged(nameof(IsCanvasPreviewVideo));
                    OnPropertyChanged(nameof(IsCanvasPreviewImage));
                    OnPropertyChanged(nameof(CanvasPreviewMetaText));
                    OnPropertyChanged(nameof(CanvasPlaceholderTitle));
                    OnPropertyChanged(nameof(CanvasPlaceholderDescription));
                    ApplyWorkflowToWorkspace();
                    RebuildResultCollections(selectLatest: false);
                    SyncCanvasModeWithReferenceState();
                }
            }
        }

        public bool IsDrawMode
        {
            get => CanvasMode == CreatorCanvasMode.Draw;
            set
            {
                if (value)
                {
                    CanvasMode = CreatorCanvasMode.Draw;
                }
            }
        }

        public bool IsEditMode
        {
            get => CanvasMode == CreatorCanvasMode.Edit;
            set
            {
                if (value)
                {
                    CanvasMode = CreatorCanvasMode.Edit;
                }
            }
        }

        public bool IsImageKind
        {
            get => MediaKind == CreatorMediaKind.Image;
            set
            {
                if (value)
                {
                    MediaKind = CreatorMediaKind.Image;
                }
            }
        }

        public bool IsVideoKind
        {
            get => MediaKind == CreatorMediaKind.Video;
            set
            {
                if (value)
                {
                    MediaKind = CreatorMediaKind.Video;
                }
            }
        }

        public string WorkspaceName => WorkspaceSession?.SessionName ?? "创作工作区";

        public string WorkspaceSubtitle
        {
            get
            {
                if (WorkspaceSession == null)
                {
                    return "正在准备画布...";
                }

                var directoryName = Path.GetFileName(WorkspaceSession.SessionDirectory);
                return $"{directoryName} · 结果 {ResultRailItems.Count} 个";
            }
        }

        public string WorkspaceLineageText => WorkspaceSession?.SourceSummaryText ?? string.Empty;

        public string CurrentCanvasTitle => (MediaKind, CanvasMode) switch
        {
            (CreatorMediaKind.Image, CreatorCanvasMode.Draw) => "生图",
            (CreatorMediaKind.Image, CreatorCanvasMode.Edit) => "改图",
            (CreatorMediaKind.Video, CreatorCanvasMode.Draw) => "生视频",
            _ => "图生视频"
        };

        public string CurrentCanvasDescription => (MediaKind, CanvasMode) switch
        {
            (CreatorMediaKind.Image, CreatorCanvasMode.Draw) => "直接描述你想得到的画面，重点放在主体、风格、镜头与材质。",
            (CreatorMediaKind.Image, CreatorCanvasMode.Edit) => "先给一张或多张参考图，再告诉它你想改哪里。",
            (CreatorMediaKind.Video, CreatorCanvasMode.Draw) => "把它当镜头脚本来写：主体、动作、景别、运镜、节奏。",
            _ => "当前先以参考图驱动的视频创作路径为主，适合做图生视频。"
        };

        public string PromptWatermark => (MediaKind, CanvasMode) switch
        {
            (CreatorMediaKind.Image, CreatorCanvasMode.Draw) => "描述你想生成的图片，例如：金色宫廷里的兔子国王，巴洛克油画风格，细节华丽",
            (CreatorMediaKind.Image, CreatorCanvasMode.Edit) => "描述你想如何改这张图，例如：把主角换成小乌龟，保留青背带与红背心",
            (CreatorMediaKind.Video, CreatorCanvasMode.Draw) => "描述你想生成的视频镜头，例如：夜雨街头，镜头缓慢推进，霓虹倒影闪烁",
            _ => "描述参考图要发生的动作或镜头变化，例如：兔子升龙拳把小乌龟打飞，火焰拖尾，街机夸张感"
        };

        public string CurrentEndpointDisplayName
        {
            get
            {
                var endpoint = ResolveCurrentEndpoint();
                return endpoint == null ? "未配置" : endpoint.Name;
            }
        }

        public string CurrentModelDisplayName
        {
            get
            {
                var reference = ResolveCurrentModelReference();
                return reference == null || string.IsNullOrWhiteSpace(reference.ModelId)
                    ? "未配置"
                    : reference.ModelId;
            }
        }

        public string ReferenceSummaryText
        {
            get
            {
                if (WorkspaceSession == null)
                {
                    return "还没有工作区。";
                }

                if (WorkspaceSession.ReferenceImageCount == 0)
                {
                    return CanvasMode == CreatorCanvasMode.Edit || MediaKind == CreatorMediaKind.Video
                        ? "建议先放参考图，再开始当前工作流。"
                        : "当前是纯提示词路径；如需改图或图生视频，可以先放参考图。";
                }

                return $"已挂载 {WorkspaceSession.ReferenceImageCount} 张参考图。点击缩略图可在视频模式下裁切到目标尺寸。";
            }
        }

        public bool IsReferencePanelVisible => WorkspaceSession != null
            && WorkspaceSession.HasReferenceImage;

        public bool CanAttachReferenceImages => WorkspaceSession != null
            && WorkspaceSession.ReferenceImageCount < GetReferenceImageLimit();

        public string SelectedPreviewPath => SelectedAsset?.PreviewPath ?? string.Empty;
        public bool IsSelectedPreviewVideo => SelectedAsset?.IsVideo == true;
        public string CanvasPreviewPath => ResolveCanvasPreviewPath();
        public bool HasCanvasPreview => !string.IsNullOrWhiteSpace(CanvasPreviewPath);
        public bool HasNoCanvasPreview => !HasCanvasPreview;
        public bool IsCanvasPreviewVideo => CanvasMode != CreatorCanvasMode.Edit && SelectedAsset?.IsVideo == true;
        public bool IsCanvasPreviewImage => HasCanvasPreview && !IsCanvasPreviewVideo;
        public string CanvasPreviewMetaText => ResolveCanvasPreviewMetaText();
        public string CurrentModeBadgeText => CurrentCanvasTitle;
        public string ModeAccentBackground => IsEditMode ? "#1F5B3A17" : "#1F164A8A";
        public string ModeAccentBorderBrush => IsEditMode ? "#D88A5A" : "#5DA9FF";
        public string CanvasPlaceholderTitle => SelectedAsset?.IsPending == true
            ? SelectedAsset.PendingStatusText
            : IsEditMode
                ? "先放参考图，再开始改造"
                : "这里是你的创作舞台";
        public string CanvasPlaceholderDescription => SelectedAsset?.IsPending == true
            ? $"提示词：{SelectedAsset.PromptSummaryText}"
            : IsEditMode
                ? "编辑模式会优先围绕参考图工作。为避免误操作，这里不会继续拿上一组结果当作当前输入。"
                : "先在底部写提示词，右侧结果轨会不断积累新结果；中间永远聚焦你当前想看的那一个。";
        public string SelectedGroupPromptText => SelectedGroup?.PromptSummaryText ?? "先生成一组结果，右侧结果轨会把它们挂出来。";
        public string SelectedGroupMetaText => SelectedGroup?.SecondaryText ?? "当前还没有结果组。";
        public string SelectedGroupResultSummary => SelectedGroup?.ResultSummaryText ?? "暂无结果";
        public string SelectedGroupSuggestedNextText => SelectedGroup?.SuggestedNextStepText ?? CurrentCanvasDescription;
        public string SelectedGroupWorkflowText => SelectedGroup?.WorkflowDisplayText ?? CurrentModeBadgeText;
        public string SelectedAssetMetaText => SelectedAsset?.MetaSummaryText ?? "选中图片或视频后，这里会告诉你它来自哪一组结果。";
        public string SelectedAssetPromptText => SelectedAsset?.PromptSummaryText ?? CurrentCanvasDescription;

        public string SelectedAssetCounterText
        {
            get
            {
                if (SelectedAsset?.Group == null)
                {
                    return string.Empty;
                }

                var group = SelectedAsset.Group;
                var index = group.Items.IndexOf(SelectedAsset);
                if (index < 0)
                {
                    return string.Empty;
                }

                return $"{index + 1} / {group.Items.Count}";
            }
        }

        public bool CanSelectPreviousAsset => GetSelectedGroupIndex() > 0;
        public bool CanSelectNextAsset => GetSelectedGroupIndex() >= 0 && SelectedGroup != null && GetSelectedGroupIndex() < SelectedGroup.Items.Count - 1;

        public string WorkspaceStatusSummary
        {
            get
            {
                if (WorkspaceSession == null)
                {
                    return StatusText;
                }

                if (!string.IsNullOrWhiteSpace(StatusText) && StatusText != "就绪")
                {
                    return StatusText;
                }

                return SelectedAsset != null
                    ? $"当前聚焦：{SelectedAsset.FileName}"
                    : $"当前画布有 {ResultRailItems.Count} 个结果";
            }
        }

        public string StatusText
        {
            get => _statusText;
                private set
                {
                    if (SetProperty(ref _statusText, value))
                    {
                        OnPropertyChanged(nameof(WorkspaceStatusSummary));
                    }
                }
        }

        public ICommand NewWorkspaceCommand { get; }
        public ICommand SubmitPromptCommand { get; }
        public ICommand SelectWorkspaceTabCommand { get; }
        public ICommand SelectAssetCommand { get; }
        public ICommand RemoveWorkspaceCommand { get; }
        public ICommand SelectPreviousAssetCommand { get; }
        public ICommand SelectNextAssetCommand { get; }
        public ICommand OpenSelectedAssetCommand { get; }
        public ICommand OpenSelectedAssetInExplorerCommand { get; }
        public ICommand OpenWorkspaceFolderCommand { get; }
        public ICommand RefreshWorkspaceCommand { get; }
        public ICommand EditAssetCommand { get; }

        private async Task InitializeAsync()
        {
            await TrySilentLoginForMediaAsync();
            LoadExistingWorkspaces();
        }

        public void UpdateConfiguration(AiConfig aiConfig, MediaGenConfig genConfig, List<AiEndpoint> endpoints)
        {
            CopyAiConfig(aiConfig, _aiConfig);
            CopyMediaGenConfig(genConfig, _genConfig);
            _endpoints.Clear();
            _endpoints.AddRange(endpoints ?? new List<AiEndpoint>());

            ApplyWorkflowToWorkspace();
            OnPropertyChanged(nameof(CurrentEndpointDisplayName));
            OnPropertyChanged(nameof(CurrentModelDisplayName));
            _ = TrySilentLoginForMediaAsync();
        }

        /// <summary>右侧面板最多加载的工作区数。超出部分不加载，节省内存和启动时间。</summary>
        private const int MaxLoadedWorkspaces = 20;

        private void LoadExistingWorkspaces()
        {
            var switches = App.Services.GetRequiredService<SqliteFeatureSwitches>();
            if (!switches.IsReady)
            {
                // SQLite 后台初始化未完成，暂时创建空白画布
                CreateNewWorkspace();
                return;
            }

            var sessionRepo = App.Services.GetRequiredService<ICreativeSessionRepository>();
            var paths = App.Services.GetRequiredService<IStoragePathResolver>();
            var records = sessionRepo.List(limit: MaxLoadedWorkspaces * 3, sessionType: "media-center-v2");

            var loadedTabs = new List<MediaWorkspaceTabViewModel>();
            foreach (var rec in records)
            {
                var directory = paths.ToAbsolutePath(rec.DirectoryPath);
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                var canvasMode = Enum.TryParse<CreatorCanvasMode>(rec.CanvasMode, true, out var cm) ? cm : _canvasMode;
                var mediaKind = Enum.TryParse<CreatorMediaKind>(rec.MediaKind, true, out var mk) ? mk : _mediaKind;

                var tab = CreateWorkspaceTab(
                    directory,
                    rec.Name,
                    true,
                    canvasMode,
                    mediaKind,
                    existingSessionId: rec.Id);

                if (!ShouldRestoreWorkspace(tab))
                {
                    tab.Dispose();
                    continue;
                }

                loadedTabs.Add(tab);
                if (loadedTabs.Count >= MaxLoadedWorkspaces)
                {
                    break;
                }
            }

            if (loadedTabs.Count == 0)
            {
                CreateNewWorkspace();
                return;
            }

            foreach (var tab in loadedTabs)
            {
                WorkspaceTabs.Add(tab);
            }

            SelectWorkspaceTab(loadedTabs[0]);
            StatusText = loadedTabs.Count == 1 ? "已载入最近画布" : $"已恢复 {loadedTabs.Count} 个历史会话";
        }

        public void CreateNewWorkspace()
        {
            CreateNewWorkspaceCore(copyFromCurrentSession: true, selectNewWorkspace: true);
            StatusText = "已创建新画布";
        }

        private MediaWorkspaceTabViewModel CreateNewWorkspaceCore(bool copyFromCurrentSession, bool selectNewWorkspace)
        {
            if (WorkspaceSession != null)
            {
                SaveWorkspaceMeta(WorkspaceSession);
            }

            var workspaceId = Guid.NewGuid().ToString("N")[..8];
            var workspaceDirectory = Path.Combine(_workspaceRootDirectory, $"workspace_{DateTime.Now:yyyyMMdd_HHmmss}_{workspaceId}");
            Directory.CreateDirectory(workspaceDirectory);

            var tab = CreateWorkspaceTab(
                workspaceDirectory,
                $"{CurrentCanvasTitle} {DateTime.Now:MM-dd HH:mm}",
                false,
                _canvasMode,
                _mediaKind);

            if (copyFromCurrentSession && WorkspaceSession != null)
            {
                CopyWorkspaceDraft(WorkspaceSession, tab.Session);
            }

            WorkspaceTabs.Insert(0, tab);
            if (selectNewWorkspace)
            {
                SelectWorkspaceTab(tab);
            }

            return tab;
        }

        private async Task SubmitPromptToNewWorkspaceAsync()
        {
            if (!CanSubmitPrompt)
            {
                return;
            }

            var prompt = DraftPromptText.Trim();
            var sourceSession = WorkspaceSession;
            var sourceTab = SelectedWorkspaceTab;
            _isSubmittingNewWorkspace = true;
            OnPropertyChanged(nameof(CanSubmitPrompt));
            (SubmitPromptCommand as RelayCommand)?.RaiseCanExecuteChanged();

            try
            {
                DraftPromptText = string.Empty;

                if (CanSubmitInCurrentWorkspace(sourceSession, sourceTab))
                {
                    sourceTab!.MediaKind = _mediaKind;
                    sourceSession!.PromptText = prompt;
                    sourceSession.Generate();
                    StatusText = sourceSession.HasSourceInfo
                        ? "已在当前编辑会话提交生成"
                        : "已在当前会话提交生成";
                    return;
                }

                var tab = CreateNewWorkspaceCore(copyFromCurrentSession: true, selectNewWorkspace: true);
                // 新建的 workspace 继承当前 MediaKind 作为固有属性
                tab.MediaKind = _mediaKind;
                await CopyReferenceImagesAsync(sourceSession, tab.Session);

                tab.Session.PromptText = prompt;
                tab.Session.Generate();
                StatusText = "已新建会话并提交生成";
            }
            finally
            {
                _isSubmittingNewWorkspace = false;
                OnPropertyChanged(nameof(CanSubmitPrompt));
                (SubmitPromptCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        private static bool CanSubmitInCurrentWorkspace(MediaSessionViewModel? session, MediaWorkspaceTabViewModel? tab)
        {
            if (session == null || tab == null)
            {
                return false;
            }

            if (session.IsGenerating)
            {
                return false;
            }

            return session.TotalMessageCount == 0
                && session.TaskHistory.Count == 0
                && session.RunningTaskCount == 0;
        }

        private void RemoveWorkspace(MediaWorkspaceTabViewModel? tab)
        {
            if (tab == null)
            {
                return;
            }

            MarkWorkspaceDeleted(tab.Session);

            var wasSelected = ReferenceEquals(tab, SelectedWorkspaceTab);
            var removeIndex = WorkspaceTabs.IndexOf(tab);

            if (wasSelected)
            {
                DetachWorkspaceSubscriptions();
                WorkspaceSession = null;
                SelectedWorkspaceTab = null;
            }

            WorkspaceTabs.Remove(tab);
            tab.Dispose();

            if (WorkspaceTabs.Count == 0)
            {
                CreateNewWorkspace();
                StatusText = "已从列表移除会话，原始文件已保留";
                return;
            }

            if (wasSelected)
            {
                var nextIndex = Math.Clamp(removeIndex, 0, WorkspaceTabs.Count - 1);
                SelectWorkspaceTab(WorkspaceTabs[nextIndex]);
            }

            StatusText = "已从列表移除会话，原始文件已保留";
        }

        private MediaWorkspaceTabViewModel CreateWorkspaceTab(
            string directory,
            string nameOrDirectory,
            bool loadFromDisk,
            CreatorCanvasMode defaultCanvasMode,
            CreatorMediaKind defaultMediaKind,
            WorkspaceSnapshot? snapshotOverride = null,
            string? existingSessionId = null)
        {
            var snapshot = snapshotOverride ?? (loadFromDisk
                ? default
                : default);
            var sessionId = existingSessionId ?? Guid.NewGuid().ToString("N")[..8];
            var sessionName = loadFromDisk
                ? snapshot.Name ?? nameOrDirectory
                : nameOrDirectory;

            var session = new MediaSessionViewModel(
                sessionId,
                sessionName,
                directory,
                _aiConfig,
                _genConfig,
                _endpoints,
                _modelRuntimeResolver,
                _azureTokenProviderStore,
                _imageService,
                _videoService,
                OnWorkspaceTaskCountChanged,
                SaveWorkspaceMeta);

            session.IsContentLoaded = true;
            session.ActivateOnFirstSelection();

            if (loadFromDisk)
            {
                LoadWorkspaceMeta(session);
            }

            var tab = new MediaWorkspaceTabViewModel(
                session,
                snapshot.CanvasMode ?? defaultCanvasMode,
                snapshot.MediaKind ?? defaultMediaKind,
                OnWorkspaceTabStateChanged);

            ApplyWorkflowToSession(session, tab.MediaKind);
            return tab;
        }

        private static bool ShouldRestoreWorkspace(MediaWorkspaceTabViewModel tab)
        {
            if (tab.HasCompletedResult)
            {
                return true;
            }

            if (tab.MediaKind != CreatorMediaKind.Video)
            {
                return false;
            }

            return tab.Session.TaskHistory.Any(IsResumableVideoTask);
        }

        private static bool IsResumableVideoTask(MediaGenTask task)
        {
            if (task.Type != MediaGenType.Video)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(task.ResultFilePath) && File.Exists(task.ResultFilePath))
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(task.RemoteVideoId)
                || !string.IsNullOrWhiteSpace(task.RemoteGenerationId)
                || !string.IsNullOrWhiteSpace(task.RemoteDownloadUrl);
        }

        private void SelectWorkspaceTab(MediaWorkspaceTabViewModel? tab)
        {
            if (tab == null)
            {
                return;
            }

            if (ReferenceEquals(SelectedWorkspaceTab, tab) && ReferenceEquals(WorkspaceSession, tab.Session))
            {
                return;
            }

            AttachWorkspace(tab);
        }

        private void AttachWorkspace(MediaWorkspaceTabViewModel tab)
        {
            DetachWorkspaceSubscriptions();

            SelectedWorkspaceTab = tab;
            WorkspaceSession = tab.Session;
            ApplySelectedWorkspaceState(tab);
            SyncCanvasModeWithReferenceState();
            WorkspaceSession.PropertyChanged += WorkspaceSession_PropertyChanged;
            WorkspaceSession.Messages.CollectionChanged += WorkspaceMessages_CollectionChanged;
            WorkspaceSession.RunningTasks.CollectionChanged += WorkspaceRunningTasks_CollectionChanged;

            foreach (var message in WorkspaceSession.Messages)
            {
                SubscribeMessageMediaPaths(message);
            }

            foreach (var task in WorkspaceSession.RunningTasks)
            {
                SubscribeTask(task);
            }

            RebuildResultCollections(selectLatest: true);
            OnWorkspaceTaskCountChanged();
            OnPropertyChanged(nameof(WorkspaceStatusSummary));
            (OpenWorkspaceFolderCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private void ApplySelectedWorkspaceState(MediaWorkspaceTabViewModel tab)
        {
            var canvasChanged = _canvasMode != tab.CanvasMode;
            var mediaChanged = _mediaKind != tab.MediaKind;

            _canvasMode = tab.CanvasMode;
            _mediaKind = tab.MediaKind;

            if (canvasChanged)
            {
                OnPropertyChanged(nameof(CanvasMode));
                OnPropertyChanged(nameof(IsDrawMode));
                OnPropertyChanged(nameof(IsEditMode));
            }

            if (mediaChanged)
            {
                OnPropertyChanged(nameof(MediaKind));
                OnPropertyChanged(nameof(IsImageKind));
                OnPropertyChanged(nameof(IsVideoKind));
                OnPropertyChanged(nameof(CurrentEndpointDisplayName));
                OnPropertyChanged(nameof(CurrentModelDisplayName));
            }

            if (canvasChanged || mediaChanged)
            {
                OnPropertyChanged(nameof(CurrentCanvasTitle));
                OnPropertyChanged(nameof(CurrentCanvasDescription));
                OnPropertyChanged(nameof(PromptWatermark));
                OnPropertyChanged(nameof(ReferenceSummaryText));
                OnPropertyChanged(nameof(IsReferencePanelVisible));
                OnPropertyChanged(nameof(CanAttachReferenceImages));
                OnPropertyChanged(nameof(CurrentModeBadgeText));
                OnPropertyChanged(nameof(ModeAccentBackground));
                OnPropertyChanged(nameof(ModeAccentBorderBrush));
                OnPropertyChanged(nameof(CanvasPreviewPath));
                OnPropertyChanged(nameof(HasCanvasPreview));
                OnPropertyChanged(nameof(HasNoCanvasPreview));
                OnPropertyChanged(nameof(IsCanvasPreviewVideo));
                    OnPropertyChanged(nameof(IsCanvasPreviewImage));
                OnPropertyChanged(nameof(CanvasPreviewMetaText));
                OnPropertyChanged(nameof(CanvasPlaceholderTitle));
                OnPropertyChanged(nameof(CanvasPlaceholderDescription));
                ApplyWorkflowToWorkspace();
            }
        }

        private void DetachWorkspaceSubscriptions()
        {
            if (WorkspaceSession != null)
            {
                WorkspaceSession.PropertyChanged -= WorkspaceSession_PropertyChanged;
                WorkspaceSession.Messages.CollectionChanged -= WorkspaceMessages_CollectionChanged;
                WorkspaceSession.RunningTasks.CollectionChanged -= WorkspaceRunningTasks_CollectionChanged;
            }

            foreach (var pair in _messageMediaHandlers.ToList())
            {
                pair.Key.MediaPaths.CollectionChanged -= pair.Value;
            }

            _messageMediaHandlers.Clear();

            foreach (var pair in _taskHandlers.ToList())
            {
                pair.Key.PropertyChanged -= pair.Value;
            }

            _taskHandlers.Clear();
        }

        private void WorkspaceRunningTasks_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (var task in e.OldItems.OfType<MediaGenTask>())
                {
                    UnsubscribeTask(task);
                }
            }

            if (e.NewItems != null)
            {
                foreach (var task in e.NewItems.OfType<MediaGenTask>())
                {
                    SubscribeTask(task);
                }
            }

            RebuildResultCollections(selectLatest: true);
        }

        private void SubscribeTask(MediaGenTask task)
        {
            if (_taskHandlers.ContainsKey(task))
            {
                return;
            }

            PropertyChangedEventHandler handler = (_, args) =>
            {
                if (args.PropertyName is nameof(MediaGenTask.Status) or nameof(MediaGenTask.Progress) or nameof(MediaGenTask.ErrorMessage))
                {
                    RebuildResultCollections(selectLatest: false);
                }
            };

            task.PropertyChanged += handler;
            _taskHandlers[task] = handler;
        }

        private void UnsubscribeTask(MediaGenTask task)
        {
            if (!_taskHandlers.TryGetValue(task, out var handler))
            {
                return;
            }

            task.PropertyChanged -= handler;
            _taskHandlers.Remove(task);
        }

        private void WorkspaceSession_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(MediaSessionViewModel.SessionName):
                    OnPropertyChanged(nameof(WorkspaceName));
                    break;
                case nameof(MediaSessionViewModel.StatusText):
                    StatusText = WorkspaceSession?.StatusText ?? "就绪";
                    OnPropertyChanged(nameof(WorkspaceStatusSummary));
                    break;
                case nameof(MediaSessionViewModel.SourceInfo):
                    OnPropertyChanged(nameof(HasWorkspaceLineage));
                    OnPropertyChanged(nameof(WorkspaceLineageText));
                    OnPropertyChanged(nameof(WorkspaceSubtitle));
                    break;
                case nameof(MediaSessionViewModel.ReferenceImageCount):
                case nameof(MediaSessionViewModel.HasReferenceImage):
                case nameof(MediaSessionViewModel.CanAddMoreReferenceImages):
                case nameof(MediaSessionViewModel.IsVideoMode):
                case nameof(MediaSessionViewModel.IsImageMode):
                    SyncCanvasModeWithReferenceState();
                    OnPropertyChanged(nameof(CanAttachReferenceImages));
                    OnPropertyChanged(nameof(ReferenceSummaryText));
                    OnPropertyChanged(nameof(IsReferencePanelVisible));
                    OnPropertyChanged(nameof(CanvasPreviewPath));
                    OnPropertyChanged(nameof(HasCanvasPreview));
                    OnPropertyChanged(nameof(HasNoCanvasPreview));
                    OnPropertyChanged(nameof(IsCanvasPreviewVideo));
                    OnPropertyChanged(nameof(IsCanvasPreviewImage));
                    OnPropertyChanged(nameof(CanvasPreviewMetaText));
                    OnPropertyChanged(nameof(CanvasPlaceholderTitle));
                    OnPropertyChanged(nameof(CanvasPlaceholderDescription));
                    break;
                case nameof(MediaSessionViewModel.RunningTaskCount):
                case nameof(MediaSessionViewModel.IsGenerating):
                    OnPropertyChanged(nameof(WorkspaceSubtitle));
                    break;
            }
        }

        private void WorkspaceMessages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (var item in e.OldItems.OfType<ChatMessageViewModel>())
                {
                    UnsubscribeMessageMediaPaths(item);
                }
            }

            if (e.NewItems != null)
            {
                foreach (var item in e.NewItems.OfType<ChatMessageViewModel>())
                {
                    SubscribeMessageMediaPaths(item);
                }
            }

            var selectLatest = e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset;
            RebuildResultCollections(selectLatest);
        }

        private void SubscribeMessageMediaPaths(ChatMessageViewModel message)
        {
            if (_messageMediaHandlers.ContainsKey(message))
            {
                return;
            }

            NotifyCollectionChangedEventHandler handler = (_, args) =>
            {
                var selectLatest = args.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset;
                RebuildResultCollections(selectLatest);
            };

            message.MediaPaths.CollectionChanged += handler;
            _messageMediaHandlers[message] = handler;
        }

        private void UnsubscribeMessageMediaPaths(ChatMessageViewModel message)
        {
            if (!_messageMediaHandlers.TryGetValue(message, out var handler))
            {
                return;
            }

            message.MediaPaths.CollectionChanged -= handler;
            _messageMediaHandlers.Remove(message);
        }

        private void ApplyWorkflowToWorkspace()
        {
            if (WorkspaceSession == null)
            {
                return;
            }

            ApplyWorkflowToSession(WorkspaceSession, MediaKind);
        }

        private static void ApplyWorkflowToSession(MediaSessionViewModel session, CreatorMediaKind mediaKind)
        {
            if (mediaKind == CreatorMediaKind.Image)
            {
                session.IsImageMode = true;
            }
            else
            {
                session.IsVideoMode = true;
            }
        }

        private static void CopyWorkspaceDraft(MediaSessionViewModel source, MediaSessionViewModel target)
        {
            target.IsImageMode = source.IsImageMode;
            target.IsVideoMode = source.IsVideoMode;

            target.ImageSize = source.ImageSize;
            target.ImageQuality = source.ImageQuality;
            target.ImageFormat = source.ImageFormat;
            target.ImageCount = source.ImageCount;

            target.VideoAspectRatio = source.VideoAspectRatio;
            target.VideoResolution = source.VideoResolution;
            target.VideoSeconds = source.VideoSeconds;
            target.VideoVariants = source.VideoVariants;
            target.RefreshVideoParameterOptions();
        }

        private static async Task CopyReferenceImagesAsync(MediaSessionViewModel? source, MediaSessionViewModel target)
        {
            if (source == null || source.ReferenceImageCount == 0)
            {
                return;
            }

            foreach (var path in source.ReferenceImagePaths)
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    continue;
                }

                await target.SetReferenceImageFromFileAsync(path);
            }
        }

        private void OnWorkspaceTaskCountChanged()
        {
            OnPropertyChanged(nameof(WorkspaceSubtitle));
            OnPropertyChanged(nameof(WorkspaceStatusSummary));
        }

        private void RebuildResultCollections(bool selectLatest)
        {
            if (WorkspaceSession == null)
            {
                _resultGroups.Clear();
                _resultRailItems.Clear();
                SelectedGroup = null;
                SelectedAsset = null;
                return;
            }

            var previousAssetId = SelectedAsset?.AssetId;
            var groups = ExtractResultGroups(WorkspaceSession)
                .ToList();
            var assets = groups
                .Select(g => g.Items.FirstOrDefault())
                .Where(a => a != null)
                .Cast<MediaCreatorResultAsset>()
                .ToList();

            _resultGroups.Clear();
            foreach (var group in groups)
            {
                _resultGroups.Add(group);
            }

            _resultRailItems.Clear();
            foreach (var asset in assets)
            {
                _resultRailItems.Add(asset);
            }

            OnPropertyChanged(nameof(WorkspaceSubtitle));
            OnPropertyChanged(nameof(WorkspaceStatusSummary));

            MediaCreatorResultAsset? targetAsset = null;
            if (!selectLatest && !string.IsNullOrWhiteSpace(previousAssetId))
            {
                targetAsset = assets.FirstOrDefault(a => string.Equals(a.AssetId, previousAssetId, StringComparison.OrdinalIgnoreCase));
            }

            targetAsset ??= assets.FirstOrDefault();
            SelectAsset(targetAsset);
        }

        private IEnumerable<MediaCreatorResultGroup> ExtractResultGroups(MediaSessionViewModel session)
        {
            var messages = session.Messages.ToList();
            var groups = new List<MediaCreatorResultGroup>();

            for (var i = 0; i < messages.Count; i++)
            {
                var message = messages[i];
                if (!message.IsAssistant || message.MediaPaths.Count == 0)
                {
                    continue;
                }

                var assets = BuildAssetsFromMessage(message, session.SessionDirectory, session.SessionName, i);
                if (assets.Count == 0)
                {
                    continue;
                }

                var promptInfo = FindNearestPromptInfo(messages, i);
                var groupKind = assets.Any(a => a.Kind == CreatorAssetKind.Video)
                    ? CreatorAssetKind.Video
                    : CreatorAssetKind.Image;
                var matchedTask = FindMatchingTask(session, promptInfo.Text, groupKind, message.Timestamp);

                var group = new MediaCreatorResultGroup
                {
                    GroupId = $"group_{message.Timestamp:yyyyMMddHHmmss}_{i}",
                    PromptText = promptInfo.Text,
                    CreatedAt = message.Timestamp,
                    SessionName = session.SessionName,
                    Kind = groupKind,
                    Workflow = matchedTask?.HasReferenceInput == true || promptInfo.HasReferenceInput
                        ? CreatorWorkflowKind.Edit
                        : CreatorWorkflowKind.Create,
                };

                ApplySessionLineage(session, group);

                foreach (var asset in assets)
                {
                    asset.Group = group;
                    ApplyAssetLineage(session, group, asset);
                    group.Items.Add(asset);
                }

                groups.Add(group);
            }

            foreach (var task in session.TaskHistory
                         .Where(t => t.Status is MediaGenStatus.Running or MediaGenStatus.Queued)
                         .OrderByDescending(t => t.CreatedAt))
            {
                var taskKind = task.Type == MediaGenType.Video ? CreatorAssetKind.Video : CreatorAssetKind.Image;
                var pendingGroup = new MediaCreatorResultGroup
                {
                    GroupId = $"pending_{task.Id}",
                    PromptText = task.Prompt,
                    CreatedAt = task.CreatedAt,
                    SessionName = session.SessionName,
                    Kind = taskKind,
                    Workflow = task.HasReferenceInput ? CreatorWorkflowKind.Edit : CreatorWorkflowKind.Create,
                    IsPending = true,
                    PendingStatusText = BuildPendingStatusText(task)
                };

                ApplySessionLineage(session, pendingGroup);

                var pendingAsset = new MediaCreatorResultAsset
                {
                    AssetId = $"pending_asset_{task.Id}",
                    Group = pendingGroup,
                    Kind = taskKind,
                    SessionName = session.SessionName,
                    ModifiedAt = task.CreatedAt,
                    FileName = pendingGroup.PendingStatusText,
                    IsPending = true,
                    PendingStatusText = pendingGroup.PendingStatusText,
                    PromptText = task.Prompt
                };

                ApplyAssetLineage(session, pendingGroup, pendingAsset);
                pendingGroup.Items.Add(pendingAsset);

                groups.Add(pendingGroup);
            }

            return groups.OrderByDescending(g => g.CreatedAt);
        }

        private static MediaGenTask? FindMatchingTask(MediaSessionViewModel session, string prompt, CreatorAssetKind kind, DateTime messageTime)
        {
            var targetType = kind == CreatorAssetKind.Video ? MediaGenType.Video : MediaGenType.Image;
            return session.TaskHistory
                .Where(t => t.Type == targetType && string.Equals(t.Prompt?.Trim(), prompt?.Trim(), StringComparison.Ordinal))
                .OrderBy(t => Math.Abs((t.CreatedAt - messageTime).TotalSeconds))
                .FirstOrDefault();
        }

        private static string BuildPendingStatusText(MediaGenTask task)
        {
            var mediaText = task.Type == MediaGenType.Video ? "视频" : "图片";
            var workflowText = task.HasReferenceInput ? "修改" : "生成";
            return $"正在{mediaText}{workflowText}";
        }

        private void SyncCanvasModeWithReferenceState()
        {
            if (WorkspaceSession == null)
            {
                return;
            }

            var targetMode = WorkspaceSession.HasReferenceImage
                ? CreatorCanvasMode.Edit
                : CreatorCanvasMode.Draw;

            if (_canvasMode != targetMode)
            {
                CanvasMode = targetMode;
            }
        }

        private static CreatorPromptInfo FindNearestPromptInfo(IReadOnlyList<ChatMessageViewModel> messages, int assistantIndex)
        {
            for (var i = assistantIndex - 1; i >= 0; i--)
            {
                var candidate = messages[i];
                if (candidate.IsUser && !string.IsNullOrWhiteSpace(candidate.Text))
                {
                    return new CreatorPromptInfo(
                        candidate.Text.Trim(),
                        candidate.MediaPaths.Count > 0);
                }
            }

            return new CreatorPromptInfo(string.Empty, false);
        }

        private int GetReferenceImageLimit()
        {
            return MediaKind == CreatorMediaKind.Video ? 1 : 8;
        }

        private string ResolveCanvasPreviewPath()
        {
            if (CanvasMode == CreatorCanvasMode.Edit)
            {
                var referencePath = WorkspaceSession?.ReferenceImagePath;
                return !string.IsNullOrWhiteSpace(referencePath) && File.Exists(referencePath)
                    ? referencePath
                    : string.Empty;
            }

            return SelectedAsset?.PreviewPath ?? string.Empty;
        }

        private string ResolveCanvasPreviewMetaText()
        {
            if (CanvasMode == CreatorCanvasMode.Edit)
            {
                if (WorkspaceSession?.HasReferenceImage == true)
                {
                    return IsVideoKind ? "当前参考图 · 图生视频模式" : "当前参考图 · 改图模式";
                }

                return string.Empty;
            }

            return SelectedAsset?.MetaSummaryText ?? string.Empty;
        }

        private List<MediaCreatorResultAsset> BuildAssetsFromMessage(ChatMessageViewModel message, string sessionDirectory, string sessionName, int order)
        {
            var results = new List<MediaCreatorResultAsset>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in message.MediaPaths)
            {
                var resolved = ResolveStoredPathForLoad(path, sessionDirectory);
                if (string.IsNullOrWhiteSpace(resolved))
                {
                    continue;
                }

                if (VideoFrameExtractorService.IsLastFrameImagePath(resolved))
                {
                    continue;
                }

                if (VideoFrameExtractorService.TryResolveVideoPathFromFirstFrame(resolved, out var videoPath))
                {
                    if (File.Exists(videoPath) && seen.Add(videoPath))
                    {
                        results.Add(CreateVideoAsset(videoPath, resolved, sessionName, message.Timestamp, order));
                    }
                    continue;
                }

                if (IsVideoFile(resolved) && File.Exists(resolved) && seen.Add(resolved))
                {
                    results.Add(CreateVideoAsset(resolved, FindAdjacentVideoPreview(resolved), sessionName, message.Timestamp, order));
                    continue;
                }

                if (IsImageFile(resolved) && File.Exists(resolved) && seen.Add(resolved))
                {
                    results.Add(CreateImageAsset(resolved, sessionName, message.Timestamp, order));
                }
            }

            return results;
        }

        private static MediaCreatorResultAsset CreateImageAsset(string filePath, string sessionName, DateTime timestamp, int order)
        {
            var info = new FileInfo(filePath);
            return new MediaCreatorResultAsset
            {
                AssetId = $"img_{timestamp:yyyyMMddHHmmss}_{order}_{Path.GetFileNameWithoutExtension(filePath)}",
                FilePath = filePath,
                PreviewPath = filePath,
                FileName = info.Name,
                Kind = CreatorAssetKind.Image,
                FileSize = info.Exists ? info.Length : 0,
                ModifiedAt = info.Exists ? info.LastWriteTime : timestamp,
                SessionName = sessionName,
            };
        }

        private static MediaCreatorResultAsset CreateVideoAsset(string filePath, string? previewPath, string sessionName, DateTime timestamp, int order)
        {
            var info = new FileInfo(filePath);
            return new MediaCreatorResultAsset
            {
                AssetId = $"vid_{timestamp:yyyyMMddHHmmss}_{order}_{Path.GetFileNameWithoutExtension(filePath)}",
                FilePath = filePath,
                PreviewPath = !string.IsNullOrWhiteSpace(previewPath) && File.Exists(previewPath) ? previewPath : filePath,
                FileName = info.Name,
                Kind = CreatorAssetKind.Video,
                FileSize = info.Exists ? info.Length : 0,
                ModifiedAt = info.Exists ? info.LastWriteTime : timestamp,
                SessionName = sessionName,
            };
        }

        private static string? FindAdjacentVideoPreview(string videoPath)
        {
            var prefix = Path.Combine(
                Path.GetDirectoryName(videoPath) ?? string.Empty,
                Path.GetFileNameWithoutExtension(videoPath));

            var candidates = new[]
            {
                prefix + ".first.png",
                prefix + ".last.png",
                prefix + "_first_frame.png",
                prefix + "_first_frame.jpg"
            };

            return candidates.FirstOrDefault(File.Exists);
        }

        public void SelectAsset(MediaCreatorResultAsset? asset)
        {
            SelectedGroup = asset?.Group;
            SelectedAsset = asset;
        }

        private void SelectAdjacentAsset(int delta)
        {
            if (SelectedGroup == null || SelectedAsset == null)
            {
                return;
            }

            var index = SelectedGroup.Items.IndexOf(SelectedAsset);
            if (index < 0)
            {
                return;
            }

            var nextIndex = index + delta;
            if (nextIndex < 0 || nextIndex >= SelectedGroup.Items.Count)
            {
                return;
            }

            SelectAsset(SelectedGroup.Items[nextIndex]);
        }

        private int GetSelectedGroupIndex()
        {
            if (SelectedGroup == null || SelectedAsset == null)
            {
                return -1;
            }

            return SelectedGroup.Items.IndexOf(SelectedAsset);
        }

        private void UpdateSelectedState(MediaCreatorResultAsset? selected)
        {
            foreach (var group in ResultGroups)
            {
                group.IsSelected = ReferenceEquals(group, selected?.Group);
                foreach (var asset in group.Items)
                {
                    asset.IsSelected = ReferenceEquals(asset, selected);
                }
            }
        }

        public IReadOnlyList<string> GetSelectedGroupImagePaths()
        {
            return SelectedGroup?.Items
                .Where(a => a.Kind == CreatorAssetKind.Image && File.Exists(a.FilePath))
                .Select(a => a.FilePath)
                .ToList()
                ?? new List<string>();
        }

        public void OpenSelectedAsset()
        {
            if (SelectedAsset == null || !File.Exists(SelectedAsset.FilePath))
            {
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = SelectedAsset.FilePath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MediaCenterV2] Open asset error: {ex.Message}");
            }
        }

        public void OpenSelectedAssetInExplorer()
        {
            if (SelectedAsset == null || !File.Exists(SelectedAsset.FilePath))
            {
                return;
            }

            try
            {
                var dir = Path.GetDirectoryName(SelectedAsset.FilePath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = dir,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MediaCenterV2] Open explorer error: {ex.Message}");
            }
        }

        public void OpenWorkspaceFolder()
        {
            if (WorkspaceSession == null || !Directory.Exists(WorkspaceSession.SessionDirectory))
            {
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = WorkspaceSession.SessionDirectory,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MediaCenterV2] Open workspace folder error: {ex.Message}");
            }
        }

        /// <summary>
        /// 将指定资产作为参考图，创建一个新的创作工作区。
        /// 文案上仍保留“编辑”，但实际走的是“新建 + 参考图”的创作路径。
        /// 图片直接引用；视频则使用目录中的尾帧图片。
        /// </summary>
        public async Task EditAssetAsync(MediaCreatorResultAsset? asset)
        {
            if (asset == null || asset.IsPending || WorkspaceSession == null)
            {
                return;
            }

            string referenceImagePath;

            if (asset.IsVideo)
            {
                // 视频：查找尾帧图片
                var videoPath = asset.FilePath;
                if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
                {
                    StatusText = "视频文件不存在";
                    return;
                }

                var lastFramePath = Path.Combine(
                    Path.GetDirectoryName(videoPath) ?? string.Empty,
                    Path.GetFileNameWithoutExtension(videoPath) + ".last.png");

                if (!File.Exists(lastFramePath))
                {
                    StatusText = "尾帧图片未找到，正在尝试提取...";
                    var result = await VideoFrameExtractorService
                        .TryExtractFirstAndLastFrameAsync(videoPath);
                    if (string.IsNullOrWhiteSpace(result.LastFramePath) || !File.Exists(result.LastFramePath))
                    {
                        StatusText = "无法提取视频尾帧，编辑失败";
                        return;
                    }

                    lastFramePath = result.LastFramePath;
                }

                referenceImagePath = lastFramePath;
            }
            else
            {
                // 图片：直接引用
                referenceImagePath = asset.FilePath;
                if (string.IsNullOrWhiteSpace(referenceImagePath) || !File.Exists(referenceImagePath))
                {
                    StatusText = "图片文件不存在";
                    return;
                }
            }

            var targetMediaKind = asset.IsVideo ? CreatorMediaKind.Video : CreatorMediaKind.Image;

            if (_mediaKind != targetMediaKind)
            {
                MediaKind = targetMediaKind;
            }

            var sourceSession = WorkspaceSession;
            var tab = CreateNewWorkspaceCore(copyFromCurrentSession: true, selectNewWorkspace: true);
            var targetSession = tab.Session;

            if (!ReferenceEquals(sourceSession, targetSession))
            {
                targetSession.ClearReferenceImage(silent: true);
            }

            await targetSession.SetReferenceImageFromFileAsync(referenceImagePath);

            targetSession.SetSourceInfo(new MediaSessionSourceInfo
            {
                SourceSessionId = sourceSession?.SessionId ?? string.Empty,
                SourceSessionName = sourceSession?.SessionName ?? string.Empty,
                SourceSessionDirectoryName = sourceSession == null
                    ? string.Empty
                    : Path.GetFileName(sourceSession.SessionDirectory),
                SourceAssetId = asset.AssetId,
                SourceAssetKind = asset.Kind.ToString(),
                SourceAssetFileName = asset.FileName,
                SourceAssetPath = asset.FilePath,
                SourcePreviewPath = asset.PreviewPath,
                ReferenceRole = asset.IsVideo ? "VideoLastFrame" : "DirectImage"
            });

            StatusText = asset.IsVideo
                ? "已用视频尾帧创建新的图生视频起点"
                : "已用图片创建新的参考图创作起点";
        }

        private async Task TrySilentLoginForMediaAsync()
        {
            var runtimeConfig = BuildRuntimeConfig();

            if (_modelRuntimeResolver.TryResolve(runtimeConfig, _genConfig.ImageModelRef, ModelCapability.Image, out var imageRuntime, out _)
                && imageRuntime != null
                && imageRuntime.AzureAuthMode == AzureAuthMode.AAD)
            {
                var provider = await _azureTokenProviderStore.GetAuthenticatedProviderAsync(
                    imageRuntime.ProfileKey,
                    imageRuntime.AzureTenantId,
                    imageRuntime.AzureClientId);
                if (provider != null)
                {
                    _imageService.SetTokenProvider(provider);
                }
            }

            if (_modelRuntimeResolver.TryResolve(runtimeConfig, _genConfig.VideoModelRef, ModelCapability.Video, out var videoRuntime, out _)
                && videoRuntime != null
                && videoRuntime.AzureAuthMode == AzureAuthMode.AAD)
            {
                var provider = await _azureTokenProviderStore.GetAuthenticatedProviderAsync(
                    videoRuntime.ProfileKey,
                    videoRuntime.AzureTenantId,
                    videoRuntime.AzureClientId);
                if (provider != null)
                {
                    _videoService.SetTokenProvider(provider);
                }
            }
        }

        private void SaveWorkspaceMeta(MediaSessionViewModel session)
        {
            WriteWorkspaceMeta(session, isDeleted: false);
        }

        private void MarkWorkspaceDeleted(MediaSessionViewModel session)
        {
            WriteWorkspaceMeta(session, isDeleted: true);
        }

        private void WriteWorkspaceMeta(MediaSessionViewModel session, bool isDeleted)
        {
            try
            {
                Directory.CreateDirectory(session.SessionDirectory);
                var tab = WorkspaceTabs.FirstOrDefault(candidate => ReferenceEquals(candidate.Session, session));

                var canvasMode = (tab?.CanvasMode ?? CanvasMode).ToString();
                var mediaKind = InferSessionMediaKind(session, tab).ToString();
                var assets = BuildAssetRecordsForSave(session);
                session.ReplaceAssetCatalog(assets);

                WriteSqliteWorkspace(session, canvasMode, mediaKind, assets, isDeleted);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MediaCenterV2] Save workspace meta failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 根据会话实际内容推断 MediaKind：如果有视频任务则为 Video，否则用 tab 的固有类型。
        /// </summary>
        private CreatorMediaKind InferSessionMediaKind(MediaSessionViewModel session, MediaWorkspaceTabViewModel? tab)
        {
            if (session.TaskHistory.Any(t => t.Type == MediaGenType.Video))
            {
                return CreatorMediaKind.Video;
            }

            return tab?.MediaKind ?? _mediaKind;
        }

        private void WriteSqliteWorkspace(MediaSessionViewModel session, string canvasMode, string mediaKind, List<MediaAssetRecord>? assets, bool isDeleted)
        {
            try
            {
                var switches = App.Services.GetRequiredService<SqliteFeatureSwitches>();
                if (!switches.IsReady) return;

                // 指纹去重：数据未变则跳过整个 SQLite 写入
                var fp = (
                    session.TotalMessageCount,
                    session.TaskHistory.Count,
                    session.TaskHistory.Count(t => t.Status == MediaGenStatus.Completed),
                    session.TaskHistory.Count(t => t.Status is MediaGenStatus.Running or MediaGenStatus.Queued),
                    session.TaskHistory.Count(t => t.Status is MediaGenStatus.Failed or MediaGenStatus.Cancelled),
                    assets?.Count ?? 0,
                    session.SessionName,
                    canvasMode,
                    mediaKind,
                    isDeleted);
                if (_sqliteFingerprints.TryGetValue(session.SessionId, out var last) && last == fp)
                    return;

                var sessionRepo = App.Services.GetRequiredService<ICreativeSessionRepository>();
                var msgRepo = App.Services.GetRequiredService<ISessionMessageRepository>();
                var contentRepo = App.Services.GetRequiredService<ISessionContentRepository>();
                var paths = App.Services.GetRequiredService<IStoragePathResolver>();

                var srcInfo = session.SourceInfo;
                sessionRepo.Upsert(new SessionRecord
                {
                    Id = session.SessionId,
                    SessionType = "media-center-v2",
                    Name = session.SessionName,
                    DirectoryPath = paths.ToRelativePath(session.SessionDirectory),
                    CanvasMode = canvasMode,
                    MediaKind = mediaKind,
                    IsDeleted = isDeleted,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now,
                    MessageCount = session.TotalMessageCount,
                    TaskCount = session.TaskHistory.Count,
                    AssetCount = assets?.Count ?? 0,
                    LatestMessagePreview = session.AllMessages.LastOrDefault()?.Text?.Length > 60
                        ? session.AllMessages.Last().Text[..60]
                        : session.AllMessages.LastOrDefault()?.Text,
                    SourceSessionId = srcInfo?.SourceSessionId,
                    SourceSessionName = srcInfo?.SourceSessionName,
                    SourceSessionDirectoryName = srcInfo?.SourceSessionDirectoryName,
                    SourceAssetId = srcInfo?.SourceAssetId,
                    SourceAssetKind = srcInfo?.SourceAssetKind,
                    SourceAssetFileName = srcInfo?.SourceAssetFileName,
                    SourceAssetPath = string.IsNullOrWhiteSpace(srcInfo?.SourceAssetPath) ? null
                        : paths.ToRelativePath(srcInfo.SourceAssetPath),
                    SourcePreviewPath = string.IsNullOrWhiteSpace(srcInfo?.SourcePreviewPath) ? null
                        : paths.ToRelativePath(srcInfo.SourcePreviewPath),
                    SourceReferenceRole = srcInfo?.ReferenceRole,
                });

                // 消息增量写入
                var maxSeq = msgRepo.GetMaxSequence(session.SessionId);
                var allMessages = session.AllMessages;
                for (var i = maxSeq; i < allMessages.Count; i++)
                {
                    var m = allMessages[i];
                    var msgId = Guid.NewGuid().ToString("N")[..8];
                    msgRepo.Insert(new MessageRecord
                    {
                        Id = msgId,
                        SessionId = session.SessionId,
                        SequenceNo = i + 1,
                        Role = m.Role,
                        ContentType = m.ContentType,
                        Text = m.Text,
                        ReasoningText = m.ReasoningText ?? "",
                        PromptTokens = m.PromptTokens,
                        CompletionTokens = m.CompletionTokens,
                        GenerateSeconds = m.GenerateSeconds,
                        DownloadSeconds = m.DownloadSeconds,
                        SearchSummary = m.SearchSummary,
                        Timestamp = m.Timestamp,
                    });

                    if (m.MediaPaths.Count > 0)
                    {
                        var refs = m.MediaPaths.Select((p, idx) => new MediaRefRecord
                        {
                            MediaPath = paths.ToRelativePath(p),
                            MediaKind = Path.GetExtension(p).ToLowerInvariant() switch
                            {
                                ".mp4" or ".webm" or ".mov" => "video",
                                ".mp3" or ".wav" => "audio",
                                _ => "image"
                            },
                            SortOrder = idx,
                        }).ToList();
                        msgRepo.InsertMediaRefs(msgId, refs);
                    }

                    // BUG-1 fix: 写入网页搜索引用
                    if (m.Citations.Count > 0)
                    {
                        var citations = m.Citations.Select(c => new CitationRecord
                        {
                            CitationNumber = c.Number,
                            Title = c.Title,
                            Url = c.Url,
                            Snippet = c.Snippet,
                            Hostname = c.Hostname,
                        }).ToList();
                        msgRepo.InsertCitations(msgId, citations);
                    }
                }

                // 任务增量 upsert：只写新增或状态变化的 task
                var knownTasks = _persistedTaskStates.GetValueOrDefault(session.SessionId);
                var newTaskStates = new Dictionary<string, string>();
                foreach (var t in session.TaskHistory.Where(ShouldPersistTask))
                {
                    var status = t.Status.ToString();
                    newTaskStates[t.Id] = status;
                    if (knownTasks != null && knownTasks.TryGetValue(t.Id, out var old) && old == status)
                        continue;

                    contentRepo.UpsertTask(new TaskRecord
                    {
                        Id = t.Id,
                        SessionId = session.SessionId,
                        TaskType = t.Type.ToString(),
                        Status = status,
                        Prompt = t.Prompt ?? "",
                        Progress = t.Progress,
                        ResultFilePath = string.IsNullOrWhiteSpace(t.ResultFilePath) ? null
                            : paths.ToRelativePath(t.ResultFilePath),
                        ErrorMessage = t.ErrorMessage,
                        HasReferenceInput = t.HasReferenceInput,
                        RemoteVideoId = t.RemoteVideoId,
                        RemoteVideoApiMode = t.RemoteVideoApiMode?.ToString(),
                        RemoteGenerationId = t.RemoteGenerationId,
                        RemoteDownloadUrl = t.RemoteDownloadUrl,
                        GenerateSeconds = t.GenerateSeconds,
                        DownloadSeconds = t.DownloadSeconds,
                        CreatedAt = t.CreatedAt,
                        UpdatedAt = DateTime.Now,
                    });
                }
                _persistedTaskStates[session.SessionId] = newTaskStates;

                // P5: 资产全量 upsert（路径需从 session-relative 转为 workspace-relative）
                foreach (var asset in assets ?? new List<MediaAssetRecord>())
                {
                    contentRepo.UpsertAsset(new AssetRecord
                    {
                        AssetId = asset.AssetId,
                        SessionId = session.SessionId,
                        GroupId = asset.GroupId,
                        Kind = asset.Kind,
                        Workflow = asset.Workflow,
                        FileName = asset.FileName,
                        FilePath = string.IsNullOrWhiteSpace(asset.FilePath) ? ""
                            : paths.ToRelativePath(
                                Path.IsPathRooted(asset.FilePath) ? asset.FilePath
                                : Path.GetFullPath(Path.Combine(session.SessionDirectory, asset.FilePath.Replace('/', Path.DirectorySeparatorChar)))),
                        PreviewPath = string.IsNullOrWhiteSpace(asset.PreviewPath) ? ""
                            : paths.ToRelativePath(
                                Path.IsPathRooted(asset.PreviewPath) ? asset.PreviewPath
                                : Path.GetFullPath(Path.Combine(session.SessionDirectory, asset.PreviewPath.Replace('/', Path.DirectorySeparatorChar)))),
                        PromptText = asset.PromptText,
                        CreatedAt = asset.CreatedAt,
                        ModifiedAt = asset.ModifiedAt,
                        StorageScope = "workspace-relative",
                        DerivedFromSessionId = asset.DerivedFromSessionId,
                        DerivedFromSessionName = asset.DerivedFromSessionName,
                        DerivedFromAssetId = asset.DerivedFromAssetId,
                        DerivedFromAssetFileName = asset.DerivedFromAssetFileName,
                        DerivedFromAssetKind = asset.DerivedFromAssetKind,
                        DerivedFromReferenceRole = asset.DerivedFromReferenceRole,
                    });
                }

                _sqliteFingerprints[session.SessionId] = fp;
                SqliteDebugLogger.LogWrite("sessions", session.SessionId, "P2-workspace-write");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SQLite] P2 workspace write failed: {ex.Message}");
            }
        }

        private void LoadWorkspaceMeta(MediaSessionViewModel session)
        {
            LoadWorkspaceFromSqlite(session);
        }

        /// <summary>P4/P5: 从 SQLite 加载工作区消息、任务、资产。成功返回 true。</summary>
        private bool LoadWorkspaceFromSqlite(MediaSessionViewModel session)
        {
            try
            {
                var sessionRepo = App.Services.GetRequiredService<ICreativeSessionRepository>();
                var rec = sessionRepo.GetById(session.SessionId);
                if (rec == null) return false;

                var msgRepo = App.Services.GetRequiredService<ISessionMessageRepository>();
                var contentRepo = App.Services.GetRequiredService<ISessionContentRepository>();
                var paths = App.Services.GetRequiredService<IStoragePathResolver>();

                // 设置 SourceInfo
                if (!string.IsNullOrWhiteSpace(rec.SourceSessionId))
                {
                    session.SetSourceInfo(new MediaSessionSourceInfo
                    {
                        SourceSessionId = rec.SourceSessionId ?? "",
                        SourceSessionName = rec.SourceSessionName ?? "",
                        SourceSessionDirectoryName = rec.SourceSessionDirectoryName ?? "",
                        SourceAssetId = rec.SourceAssetId ?? "",
                        SourceAssetKind = rec.SourceAssetKind ?? "",
                        SourceAssetFileName = rec.SourceAssetFileName ?? "",
                        SourceAssetPath = string.IsNullOrWhiteSpace(rec.SourceAssetPath) ? "" : paths.ToAbsolutePath(rec.SourceAssetPath),
                        SourcePreviewPath = string.IsNullOrWhiteSpace(rec.SourcePreviewPath) ? "" : paths.ToAbsolutePath(rec.SourcePreviewPath),
                        ReferenceRole = rec.SourceReferenceRole ?? "",
                    }, requestSave: false);
                }

                // P4: 懒加载最近 40 条消息
                var messageRecords = msgRepo.GetLatest(session.SessionId, limit: 40);
                var messages = new List<ChatMessageViewModel>();
                foreach (var mr in messageRecords)
                {
                    var mediaRefs = msgRepo.GetMediaRefs(mr.Id);
                    var citations = msgRepo.GetCitations(mr.Id);

                    messages.Add(new ChatMessageViewModel(new MediaChatMessage
                    {
                        Role = mr.Role,
                        Text = mr.Text,
                        ReasoningText = mr.ReasoningText,
                        Timestamp = mr.Timestamp,
                        GenerateSeconds = mr.GenerateSeconds,
                        DownloadSeconds = mr.DownloadSeconds,
                        SearchSummary = mr.SearchSummary ?? "",
                        PromptTokens = mr.PromptTokens,
                        CompletionTokens = mr.CompletionTokens,
                        MediaPaths = mediaRefs.Select(r => paths.ToAbsolutePath(r.MediaPath)).ToList(),
                        Citations = citations.Count > 0
                            ? citations.Select(c => new MediaChatCitation
                            {
                                Number = c.CitationNumber,
                                Title = c.Title,
                                Url = c.Url,
                                Snippet = c.Snippet,
                                Hostname = c.Hostname,
                            }).ToList()
                            : null,
                    }));
                }
                session.ReplaceAllMessages(messages);

                // 加载任务
                session.TaskHistory.Clear();
                var taskRecords = contentRepo.GetSessionTasks(session.SessionId);
                foreach (var t in taskRecords)
                {
                    var task = new MediaGenTask
                    {
                        Id = t.Id,
                        Type = Enum.TryParse<MediaGenType>(t.TaskType, out var tt) ? tt : MediaGenType.Image,
                        Status = Enum.TryParse<MediaGenStatus>(t.Status, out var ts) ? ts : MediaGenStatus.Completed,
                        Prompt = t.Prompt,
                        Progress = (int)t.Progress,
                        ResultFilePath = string.IsNullOrWhiteSpace(t.ResultFilePath) ? null : paths.ToAbsolutePath(t.ResultFilePath),
                        ErrorMessage = t.ErrorMessage,
                        HasReferenceInput = t.HasReferenceInput,
                        RemoteVideoId = t.RemoteVideoId,
                        RemoteGenerationId = t.RemoteGenerationId,
                        RemoteDownloadUrl = t.RemoteDownloadUrl,
                        GenerateSeconds = t.GenerateSeconds,
                        DownloadSeconds = t.DownloadSeconds,
                        CreatedAt = t.CreatedAt,
                    };
                    if (!ShouldRestoreTask(task)) continue;
                    session.TaskHistory.Add(task);
                }

                // P5: 从资产表加载资产目录
                var assetRecords = contentRepo.GetSessionAssets(session.SessionId, limit: 200);
                var assetList = assetRecords.Select(a => new MediaAssetRecord
                {
                    AssetId = a.AssetId,
                    GroupId = a.GroupId,
                    Kind = a.Kind,
                    Workflow = a.Workflow,
                    FileName = a.FileName,
                    FilePath = paths.ToAbsolutePath(a.FilePath),
                    PreviewPath = string.IsNullOrWhiteSpace(a.PreviewPath) ? "" : paths.ToAbsolutePath(a.PreviewPath),
                    PromptText = a.PromptText,
                    CreatedAt = a.CreatedAt,
                    ModifiedAt = a.ModifiedAt,
                    DerivedFromSessionId = a.DerivedFromSessionId ?? "",
                    DerivedFromSessionName = a.DerivedFromSessionName ?? "",
                    DerivedFromAssetId = a.DerivedFromAssetId ?? "",
                    DerivedFromAssetFileName = a.DerivedFromAssetFileName ?? "",
                    DerivedFromAssetKind = a.DerivedFromAssetKind ?? "",
                    DerivedFromReferenceRole = a.DerivedFromReferenceRole ?? "",
                }).ToList();
                session.ReplaceAssetCatalog(assetList);

                // 预填充指纹 + task 状态，避免 Dispose 时全量重写
                _sqliteFingerprints[session.SessionId] = (
                    session.TotalMessageCount,
                    session.TaskHistory.Count,
                    session.TaskHistory.Count(t => t.Status == MediaGenStatus.Completed),
                    session.TaskHistory.Count(t => t.Status is MediaGenStatus.Running or MediaGenStatus.Queued),
                    session.TaskHistory.Count(t => t.Status is MediaGenStatus.Failed or MediaGenStatus.Cancelled),
                    assetList.Count,
                    session.SessionName,
                    rec.CanvasMode,
                    rec.MediaKind,
                    rec.IsDeleted);
                _persistedTaskStates[session.SessionId] = session.TaskHistory
                    .ToDictionary(t => t.Id, t => t.Status.ToString());

                SqliteDebugLogger.LogRead("workspace", $"session={session.SessionId}", messageRecords.Count);
                Debug.WriteLine($"[SQLite] P4/P5 加载工作区 {session.SessionId}: msgs={messageRecords.Count}, tasks={taskRecords.Count}, assets={assetRecords.Count}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SQLite] LoadWorkspaceFromSqlite failed: {ex.Message}");
                return false;
            }
        }

        private static bool ShouldPersistTask(MediaGenTask task)
        {
            if (task.Type == MediaGenType.Video)
            {
                return true;
            }

            return task.Status == MediaGenStatus.Completed && !string.IsNullOrWhiteSpace(task.ResultFilePath) && File.Exists(task.ResultFilePath);
        }

        private static bool ShouldRestoreTask(MediaGenTask task)
        {
            if (task.Type == MediaGenType.Video)
            {
                return true;
            }

            return task.Status == MediaGenStatus.Completed && !string.IsNullOrWhiteSpace(task.ResultFilePath) && File.Exists(task.ResultFilePath);
        }

        private static string ConvertPathForSave(string? path, string sessionDirectory)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            var value = path.Trim();
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                return value;
            }

            try
            {
                if (!Path.IsPathRooted(value))
                {
                    return value.Replace('\\', '/');
                }

                var fullPath = Path.GetFullPath(value);
                var fullSessionDirectory = Path.GetFullPath(sessionDirectory);
                var sessionPrefix = fullSessionDirectory.EndsWith(Path.DirectorySeparatorChar)
                    ? fullSessionDirectory
                    : fullSessionDirectory + Path.DirectorySeparatorChar;

                if (fullPath.StartsWith(sessionPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var relative = Path.GetRelativePath(fullSessionDirectory, fullPath);
                    return relative.Replace('\\', '/');
                }

                return fullPath;
            }
            catch
            {
                return value;
            }
        }

        private List<MediaAssetRecord> BuildAssetRecordsForSave(MediaSessionViewModel session)
        {
            var results = new List<MediaAssetRecord>();
            foreach (var group in ExtractResultGroups(session))
            {
                foreach (var asset in group.Items.Where(a => !a.IsPending && !string.IsNullOrWhiteSpace(a.FilePath)))
                {
                    results.Add(new MediaAssetRecord
                    {
                        AssetId = asset.AssetId,
                        GroupId = group.GroupId,
                        Kind = asset.Kind.ToString(),
                        Workflow = group.Workflow.ToString(),
                        FileName = asset.FileName,
                        FilePath = ConvertPathForSave(asset.FilePath, session.SessionDirectory),
                        PreviewPath = ConvertPathForSave(asset.PreviewPath, session.SessionDirectory),
                        PromptText = asset.PromptText,
                        CreatedAt = group.CreatedAt,
                        ModifiedAt = asset.ModifiedAt,
                        DerivedFromSessionId = asset.DerivedFromSessionId,
                        DerivedFromSessionName = asset.DerivedFromSessionName,
                        DerivedFromAssetId = asset.DerivedFromAssetId,
                        DerivedFromAssetFileName = asset.DerivedFromAssetFileName,
                        DerivedFromAssetKind = asset.DerivedFromAssetKind,
                        DerivedFromReferenceRole = asset.DerivedFromReferenceRole
                    });
                }
            }

            return results;
        }

        private static void ApplySessionLineage(MediaSessionViewModel session, MediaCreatorResultGroup group)
        {
            if (group.Workflow != CreatorWorkflowKind.Edit || session.SourceInfo == null)
            {
                return;
            }

            group.SourceSessionName = session.SourceInfo.SourceSessionName;
            group.SourceAssetFileName = session.SourceInfo.SourceAssetFileName;
            group.SourceReferenceRole = session.SourceInfo.ReferenceRole;
        }

        private static void ApplyAssetLineage(MediaSessionViewModel session, MediaCreatorResultGroup group, MediaCreatorResultAsset asset)
        {
            if (group.Workflow != CreatorWorkflowKind.Edit || session.SourceInfo == null)
            {
                return;
            }

            asset.DerivedFromSessionId = session.SourceInfo.SourceSessionId;
            asset.DerivedFromSessionName = session.SourceInfo.SourceSessionName;
            asset.DerivedFromAssetId = session.SourceInfo.SourceAssetId;
            asset.DerivedFromAssetFileName = session.SourceInfo.SourceAssetFileName;
            asset.DerivedFromAssetKind = session.SourceInfo.SourceAssetKind;
            asset.DerivedFromReferenceRole = session.SourceInfo.ReferenceRole;
        }

        private static string ResolveStoredPathForLoad(string? storedPath, string sessionDirectory)
        {
            if (string.IsNullOrWhiteSpace(storedPath))
            {
                return string.Empty;
            }

            var value = storedPath.Trim();
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                return value;
            }

            try
            {
                if (!Path.IsPathRooted(value))
                {
                    return Path.GetFullPath(Path.Combine(sessionDirectory, value.Replace('/', Path.DirectorySeparatorChar)));
                }

                var fullPath = Path.GetFullPath(value);
                if (File.Exists(fullPath) || Directory.Exists(fullPath))
                {
                    return fullPath;
                }

                var fileName = Path.GetFileName(fullPath);
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    return fullPath;
                }

                var fallbackCandidates = new[]
                {
                    Path.Combine(sessionDirectory, fileName),
                    Path.Combine(sessionDirectory, "media", fileName),
                    Path.Combine(sessionDirectory, "outputs", fileName),
                    Path.Combine(sessionDirectory, "refs", fileName)
                };

                foreach (var candidate in fallbackCandidates)
                {
                    if (File.Exists(candidate) || Directory.Exists(candidate))
                    {
                        return candidate;
                    }
                }

                return fullPath;
            }
            catch
            {
                return value;
            }
        }

        private static bool IsImageFile(string filePath)
        {
            var ext = Path.GetExtension(filePath);
            return ext is not null && ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
                || ext is not null && ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || ext is not null && ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                || ext is not null && ext.Equals(".webp", StringComparison.OrdinalIgnoreCase)
                || ext is not null && ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
                || ext is not null && ext.Equals(".gif", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsVideoFile(string filePath)
        {
            var ext = Path.GetExtension(filePath);
            return ext is not null && ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
                || ext is not null && ext.Equals(".webm", StringComparison.OrdinalIgnoreCase)
                || ext is not null && ext.Equals(".mov", StringComparison.OrdinalIgnoreCase)
                || ext is not null && ext.Equals(".avi", StringComparison.OrdinalIgnoreCase);
        }

        private AiEndpoint? ResolveCurrentEndpoint()
        {
            var reference = ResolveCurrentModelReference();
            if (reference == null)
            {
                return null;
            }

            return _endpoints.FirstOrDefault(e => e.Id == reference.EndpointId && e.IsEnabled);
        }

        private ModelReference? ResolveCurrentModelReference()
        {
            return MediaKind == CreatorMediaKind.Image ? _genConfig.ImageModelRef : _genConfig.VideoModelRef;
        }

        private AzureSpeechConfig BuildRuntimeConfig() => new()
        {
            AiConfig = _aiConfig,
            MediaGenConfig = _genConfig,
            Endpoints = _endpoints
        };

        private static void CopyAiConfig(AiConfig source, AiConfig target)
        {
            source ??= new AiConfig();
            target.ProfileId = source.ProfileId;
            target.EndpointType = source.EndpointType;
            target.ProviderType = source.ProviderType;
            target.ApiEndpoint = source.ApiEndpoint;
            target.ApiKey = source.ApiKey;
            target.ModelName = source.ModelName;
            target.DeploymentName = source.DeploymentName;
            target.ApiVersion = source.ApiVersion;
            target.AzureAuthMode = source.AzureAuthMode;
            target.ApiKeyHeaderMode = source.ApiKeyHeaderMode;
            target.TextApiProtocolMode = source.TextApiProtocolMode;
            target.ImageApiRouteMode = source.ImageApiRouteMode;
            target.AzureTenantId = source.AzureTenantId;
            target.AzureClientId = source.AzureClientId;
            target.SummaryEnableReasoning = source.SummaryEnableReasoning;
            target.InsightSystemPrompt = source.InsightSystemPrompt;
            target.ReviewSystemPrompt = source.ReviewSystemPrompt;
            target.InsightUserContentTemplate = source.InsightUserContentTemplate;
            target.ReviewUserContentTemplate = source.ReviewUserContentTemplate;
            target.InsightModelRef = CloneReference(source.InsightModelRef);
            target.SummaryModelRef = CloneReference(source.SummaryModelRef);
            target.QuickModelRef = CloneReference(source.QuickModelRef);
            target.ReviewModelRef = CloneReference(source.ReviewModelRef);
            target.ConversationModelRef = CloneReference(source.ConversationModelRef);
            target.IntentModelRef = CloneReference(source.IntentModelRef);
            target.AutoInsightBufferOutput = source.AutoInsightBufferOutput;
            target.PresetButtons = source.PresetButtons?.Select(button => new InsightPresetButton
            {
                Name = button.Name,
                Prompt = button.Prompt
            }).ToList() ?? new List<InsightPresetButton>();
            target.ReviewSheets = source.ReviewSheets?.Select(sheet => new ReviewSheetPreset
            {
                Name = sheet.Name,
                FileTag = sheet.FileTag,
                Prompt = sheet.Prompt
            }).ToList() ?? new List<ReviewSheetPreset>();
        }

        private static void CopyMediaGenConfig(MediaGenConfig source, MediaGenConfig target)
        {
            source ??= new MediaGenConfig();
            target.ImageModelRef = CloneReference(source.ImageModelRef);
            target.VideoModelRef = CloneReference(source.VideoModelRef);
            target.ImageModel = source.ImageModel;
            target.ImageSize = source.ImageSize;
            target.ImageQuality = source.ImageQuality;
            target.ImageFormat = source.ImageFormat;
            target.ImageCount = source.ImageCount;
            target.VideoModel = source.VideoModel;
            target.VideoApiMode = source.VideoApiMode;
            target.VideoWidth = source.VideoWidth;
            target.VideoHeight = source.VideoHeight;
            target.VideoAspectRatio = source.VideoAspectRatio;
            target.VideoResolution = source.VideoResolution;
            target.VideoSeconds = source.VideoSeconds;
            target.VideoVariants = source.VideoVariants;
            target.VideoPollIntervalMs = source.VideoPollIntervalMs;
            target.MaxLoadedSessionsInMemory = source.MaxLoadedSessionsInMemory;
            target.OutputDirectory = source.OutputDirectory;
        }

        private static ModelReference? CloneReference(ModelReference? reference)
        {
            if (reference == null)
            {
                return null;
            }

            return new ModelReference
            {
                EndpointId = reference.EndpointId,
                ModelId = reference.ModelId
            };
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (WorkspaceSession != null)
            {
                SaveWorkspaceMeta(WorkspaceSession);
            }

            DetachWorkspaceSubscriptions();
            foreach (var tab in WorkspaceTabs)
            {
                tab.Dispose();
            }
        }

        private void OnWorkspaceTabStateChanged(MediaWorkspaceTabViewModel tab)
        {
            // 不在 UI 状态通知中触发持久化 —— _onRequestSave 和 Dispose 已覆盖所有数据变更
            if (!ReferenceEquals(tab, SelectedWorkspaceTab))
            {
                return;
            }

            ApplySelectedWorkspaceState(tab);
            OnPropertyChanged(nameof(WorkspaceName));
            OnPropertyChanged(nameof(WorkspaceSubtitle));
            OnPropertyChanged(nameof(WorkspaceStatusSummary));
        }
    }

    public readonly record struct WorkspaceSnapshot(
        string? Name,
        CreatorCanvasMode? CanvasMode,
        CreatorMediaKind? MediaKind,
        bool IsDeleted);

    public enum CreatorCanvasMode
    {
        Draw,
        Edit
    }

    public enum CreatorMediaKind
    {
        Image,
        Video
    }

    public enum CreatorAssetKind
    {
        Image,
        Video
    }

    public enum CreatorWorkflowKind
    {
        Create,
        Edit
    }

    public readonly record struct CreatorPromptInfo(string Text, bool HasReferenceInput);

    public class MediaCreatorResultGroup : ViewModelBase
    {
        private bool _isSelected;

        public string GroupId { get; set; } = string.Empty;
        public string PromptText { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public string SessionName { get; set; } = string.Empty;
        public CreatorAssetKind Kind { get; set; }
        public CreatorWorkflowKind Workflow { get; set; }
        public bool IsPending { get; set; }
        public string PendingStatusText { get; set; } = string.Empty;
        public string SourceSessionName { get; set; } = string.Empty;
        public string SourceAssetFileName { get; set; } = string.Empty;
        public string SourceReferenceRole { get; set; } = string.Empty;
        public ObservableCollection<MediaCreatorResultAsset> Items { get; } = new();

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public string PromptPreviewText => string.IsNullOrWhiteSpace(PromptText)
            ? "未记录提示词"
            : Compact(PromptText, 88);

        public string PromptSummaryText => string.IsNullOrWhiteSpace(PromptText)
            ? "这一组结果没有保留可读提示词，适合直接围绕图像/视频继续创作。"
            : Compact(PromptText, 220);

        public string WorkflowBadgeText => Workflow == CreatorWorkflowKind.Edit ? "修改" : "创建";
        public string WorkflowDisplayText => IsPending ? PendingStatusText : WorkflowBadgeText;
        public bool HasLineage => !string.IsNullOrWhiteSpace(SourceSessionName) || !string.IsNullOrWhiteSpace(SourceAssetFileName);
        public string LineageText => BuildLineageText(SourceSessionName, SourceAssetFileName, SourceReferenceRole);
        public string SecondaryText => IsPending
            ? AppendLineage($"{PendingStatusText} · {SessionName} · {CreatedAt:yyyy-MM-dd HH:mm}")
            : AppendLineage($"{WorkflowBadgeText} · {SessionName} · {CreatedAt:yyyy-MM-dd HH:mm}");
        public string ResultSummaryText => IsPending
            ? "结果仍在写入中，期间你可以继续提交新的创作请求。"
            : Kind == CreatorAssetKind.Video
                ? $"{Items.Count(i => i.Kind == CreatorAssetKind.Video && !i.IsPending)} 个视频结果"
                : $"{Items.Count(i => i.Kind == CreatorAssetKind.Image && !i.IsPending)} 张图片结果";
        public string SuggestedNextStepText => IsPending
            ? "这一项还在生成中。你可以继续切换模式、补提示词或再开新任务，不用等它跑完。"
            : Kind == CreatorAssetKind.Video
                ? "更像导演台：看镜头、改动作、继续迭代视频版本。"
                : "更像选片台：先挑最佳图，再决定继续改图还是拿去图生视频。";

        private static string Compact(string text, int maxLength)
        {
            var singleLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return singleLine.Length > maxLength ? singleLine[..maxLength] + "..." : singleLine;
        }

        private string AppendLineage(string baseText)
        {
            return HasLineage ? $"{baseText} · {LineageText}" : baseText;
        }

        private static string BuildLineageText(string sessionName, string assetFileName, string referenceRole)
        {
            if (string.IsNullOrWhiteSpace(sessionName) && string.IsNullOrWhiteSpace(assetFileName))
            {
                return string.Empty;
            }

            var name = string.IsNullOrWhiteSpace(assetFileName) ? "上一轮结果" : assetFileName;
            var roleText = string.Equals(referenceRole, "VideoLastFrame", StringComparison.OrdinalIgnoreCase)
                ? "尾帧"
                : "图片";
            return $"来源：{sessionName} · {roleText} {name}";
        }
    }

    public class MediaCreatorResultAsset : ViewModelBase
    {
        private bool _isSelected;

        public string AssetId { get; set; } = string.Empty;
        public MediaCreatorResultGroup? Group { get; set; }
        public string FilePath { get; set; } = string.Empty;
        public string PreviewPath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public CreatorAssetKind Kind { get; set; }
        public long FileSize { get; set; }
        public DateTime ModifiedAt { get; set; }
        public string SessionName { get; set; } = string.Empty;
        public bool IsPending { get; set; }
        public string PendingStatusText { get; set; } = string.Empty;
        public string PromptText { get; set; } = string.Empty;
        public string DerivedFromSessionId { get; set; } = string.Empty;
        public string DerivedFromSessionName { get; set; } = string.Empty;
        public string DerivedFromAssetId { get; set; } = string.Empty;
        public string DerivedFromAssetFileName { get; set; } = string.Empty;
        public string DerivedFromAssetKind { get; set; } = string.Empty;
        public string DerivedFromReferenceRole { get; set; } = string.Empty;

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public bool IsImage => Kind == CreatorAssetKind.Image;
        public bool IsVideo => Kind == CreatorAssetKind.Video;
        public bool HasPreview => !IsPending && !string.IsNullOrWhiteSpace(PreviewPath);
        public bool HasNoPreview => !HasPreview;
        public string KindText => IsVideo ? "视频" : "图片";
        public string KindBadgeText => IsPending ? "进行中" : KindText;
        public string RailCaptionText => IsPending ? PendingStatusText : Group?.PromptPreviewText ?? FileName;
        public string PreviewLabel => IsVideo ? "视频首帧" : "图片预览";
        public string TimestampText => ModifiedAt.ToString("yyyy-MM-dd HH:mm");

        public string FileSizeText
        {
            get
            {
                if (FileSize < 1024) return $"{FileSize} B";
                if (FileSize < 1024 * 1024) return $"{FileSize / 1024.0:F1} KB";
                return $"{FileSize / (1024.0 * 1024.0):F1} MB";
            }
        }

        public bool HasLineage => !string.IsNullOrWhiteSpace(DerivedFromSessionName) || !string.IsNullOrWhiteSpace(DerivedFromAssetFileName);
        public string LineageText => BuildLineageText(DerivedFromSessionName, DerivedFromAssetFileName, DerivedFromReferenceRole);

        public string MetaSummaryText => Group == null
            ? AppendLineage(IsPending ? PendingStatusText : $"{KindText} · {TimestampText}")
            : AppendLineage(IsPending ? $"{PendingStatusText} · {Group.SecondaryText}" : $"{KindText} · {Group.SecondaryText}");

        public string PromptSummaryText => Group?.PromptSummaryText
            ?? (string.IsNullOrWhiteSpace(PromptText) ? "未记录提示词" : Compact(PromptText, 220));

        private static string Compact(string text, int maxLength)
        {
            var singleLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return singleLine.Length > maxLength ? singleLine[..maxLength] + "..." : singleLine;
        }

        private string AppendLineage(string baseText)
        {
            return HasLineage ? $"{baseText} · {LineageText}" : baseText;
        }

        private static string BuildLineageText(string sessionName, string assetFileName, string referenceRole)
        {
            if (string.IsNullOrWhiteSpace(sessionName) && string.IsNullOrWhiteSpace(assetFileName))
            {
                return string.Empty;
            }

            var name = string.IsNullOrWhiteSpace(assetFileName) ? "上一轮结果" : assetFileName;
            var roleText = string.Equals(referenceRole, "VideoLastFrame", StringComparison.OrdinalIgnoreCase)
                ? "尾帧"
                : "图片";
            return $"衍生自 {sessionName} · {roleText} {name}";
        }
    }
}
