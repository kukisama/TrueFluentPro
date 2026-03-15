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
using TrueFluentPro.Models;
using TrueFluentPro.Services;

namespace TrueFluentPro.ViewModels
{
    /// <summary>
    /// 媒体中心 v2 —— 创作者工作区。
    /// 不再优先背负旧会话馆的历史浏览心智，而是围绕“当前画布 / 当前结果 / 继续衍生”组织页面。
    /// </summary>
    public class MediaCenterV2ViewModel : ViewModelBase, IDisposable
    {
        private static readonly JsonSerializerOptions SaveJsonOptions = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

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
                    OnPropertyChanged(nameof(WorkspaceName));
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
                    OnPropertyChanged(nameof(CanvasPreviewMetaText));
                    OnPropertyChanged(nameof(CanvasPlaceholderTitle));
                    OnPropertyChanged(nameof(CanvasPlaceholderDescription));
                    OnPropertyChanged(nameof(CurrentModeBadgeText));
                }
            }
        }

        public bool HasWorkspace => WorkspaceSession != null;
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
                    if (SelectedWorkspaceTab != null && SelectedWorkspaceTab.MediaKind != value)
                    {
                        SelectedWorkspaceTab.MediaKind = value;
                    }

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
                    OnPropertyChanged(nameof(CanvasPreviewMetaText));
                    OnPropertyChanged(nameof(CanvasPlaceholderTitle));
                    OnPropertyChanged(nameof(CanvasPlaceholderDescription));
                    ApplyWorkflowToWorkspace();
                    RebuildResultCollections(selectLatest: false);
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
            && (CanvasMode == CreatorCanvasMode.Edit || MediaKind == CreatorMediaKind.Video || WorkspaceSession.HasReferenceImage);

        public bool CanAttachReferenceImages => WorkspaceSession != null
            && WorkspaceSession.ReferenceImageCount < GetReferenceImageLimit();

        public string SelectedPreviewPath => SelectedAsset?.PreviewPath ?? string.Empty;
        public bool IsSelectedPreviewVideo => SelectedAsset?.IsVideo == true;
        public string CanvasPreviewPath => ResolveCanvasPreviewPath();
        public bool HasCanvasPreview => !string.IsNullOrWhiteSpace(CanvasPreviewPath);
        public bool HasNoCanvasPreview => !HasCanvasPreview;
        public bool IsCanvasPreviewVideo => CanvasMode != CreatorCanvasMode.Edit && SelectedAsset?.IsVideo == true;
        public string CanvasPreviewMetaText => ResolveCanvasPreviewMetaText();
        public string CurrentModeBadgeText => $"{(IsEditMode ? "编辑" : "绘制")} · {(IsVideoKind ? "视频" : "图片")}";
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

        private void LoadExistingWorkspaces()
        {
            var directories = Directory.GetDirectories(_workspaceRootDirectory, "workspace_*")
                .OrderByDescending(GetWorkspaceDirectorySortKey)
                .ToList();

            var loadedTabs = new List<MediaWorkspaceTabViewModel>();
            foreach (var directory in directories)
            {
                var snapshot = LoadWorkspaceSnapshot(directory);
                if (snapshot.IsDeleted)
                {
                    continue;
                }

                var tab = CreateWorkspaceTab(
                    directory,
                    Path.GetFileName(directory),
                    true,
                    snapshot.CanvasMode ?? _canvasMode,
                    snapshot.MediaKind ?? _mediaKind,
                    snapshot);

                if (!ShouldRestoreWorkspace(tab))
                {
                    tab.Dispose();
                    continue;
                }

                loadedTabs.Add(tab);
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

        private static DateTime GetWorkspaceDirectorySortKey(string directory)
        {
            var metaPath = Path.Combine(directory, "workspace.json");
            if (File.Exists(metaPath))
            {
                return File.GetLastWriteTime(metaPath);
            }

            return Directory.GetLastWriteTime(directory);
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
            _isSubmittingNewWorkspace = true;
            OnPropertyChanged(nameof(CanSubmitPrompt));
            (SubmitPromptCommand as RelayCommand)?.RaiseCanExecuteChanged();

            try
            {
                var tab = CreateNewWorkspaceCore(copyFromCurrentSession: true, selectNewWorkspace: true);
                await CopyReferenceImagesAsync(sourceSession, tab.Session);

                tab.Session.PromptText = prompt;
                DraftPromptText = string.Empty;
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
            WorkspaceSnapshot? snapshotOverride = null)
        {
            var snapshot = snapshotOverride ?? (loadFromDisk
                ? LoadWorkspaceSnapshot(directory)
                : default);
            var sessionId = Guid.NewGuid().ToString("N")[..8];
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

        private static WorkspaceSnapshot LoadWorkspaceSnapshot(string directory)
        {
            try
            {
                var metaPath = Path.Combine(directory, "workspace.json");
                if (!File.Exists(metaPath))
                {
                    return default;
                }

                var json = File.ReadAllText(metaPath);
                var data = JsonSerializer.Deserialize<MediaGenSession>(json);
                if (data == null)
                {
                    return default;
                }

                return new WorkspaceSnapshot(
                    data.Name,
                    ParseCanvasMode(data.CanvasMode),
                    ParseMediaKind(data.MediaKind),
                    data.IsDeleted);
            }
            catch
            {
                return default;
            }
        }

        private static CreatorCanvasMode? ParseCanvasMode(string? value)
        {
            return Enum.TryParse<CreatorCanvasMode>(value, true, out var mode) ? mode : null;
        }

        private static CreatorMediaKind? ParseMediaKind(string? value)
        {
            return Enum.TryParse<CreatorMediaKind>(value, true, out var kind) ? kind : null;
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
                case nameof(MediaSessionViewModel.ReferenceImageCount):
                case nameof(MediaSessionViewModel.HasReferenceImage):
                case nameof(MediaSessionViewModel.CanAddMoreReferenceImages):
                case nameof(MediaSessionViewModel.IsVideoMode):
                case nameof(MediaSessionViewModel.IsImageMode):
                    OnPropertyChanged(nameof(CanAttachReferenceImages));
                    OnPropertyChanged(nameof(ReferenceSummaryText));
                    OnPropertyChanged(nameof(IsReferencePanelVisible));
                    OnPropertyChanged(nameof(CanvasPreviewPath));
                    OnPropertyChanged(nameof(HasCanvasPreview));
                    OnPropertyChanged(nameof(HasNoCanvasPreview));
                    OnPropertyChanged(nameof(IsCanvasPreviewVideo));
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
                .Where(g => MediaKind == CreatorMediaKind.Video ? g.Kind == CreatorAssetKind.Video : g.Kind == CreatorAssetKind.Image)
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

                foreach (var asset in assets)
                {
                    asset.Group = group;
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

                pendingGroup.Items.Add(new MediaCreatorResultAsset
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
                });

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
                var metaPath = Path.Combine(session.SessionDirectory, "workspace.json");
                Directory.CreateDirectory(session.SessionDirectory);
                var tab = WorkspaceTabs.FirstOrDefault(candidate => ReferenceEquals(candidate.Session, session));

                var data = new MediaGenSession
                {
                    Id = session.SessionId,
                    Name = session.SessionName,
                    IsDeleted = isDeleted,
                    CanvasMode = (tab?.CanvasMode ?? CanvasMode).ToString(),
                    MediaKind = (tab?.MediaKind ?? MediaKind).ToString(),
                    Messages = session.AllMessages.Select(m => new MediaChatMessage
                    {
                        Role = m.Role,
                        Text = m.Text,
                        MediaPaths = m.MediaPaths
                            .Select(p => ConvertPathForSave(p, session.SessionDirectory))
                            .Where(p => !string.IsNullOrWhiteSpace(p))
                            .ToList(),
                        Timestamp = m.Timestamp,
                        GenerateSeconds = m.GenerateSeconds,
                        DownloadSeconds = m.DownloadSeconds
                    }).ToList(),
                    Tasks = session.TaskHistory
                        .Where(t => ShouldPersistTask(t))
                        .Select(t => new MediaGenTask
                        {
                            Id = t.Id,
                            Type = t.Type,
                            Status = t.Status,
                            Prompt = t.Prompt,
                            Progress = t.Progress,
                            ResultFilePath = ConvertPathForSave(t.ResultFilePath, session.SessionDirectory),
                            ErrorMessage = t.ErrorMessage,
                            HasReferenceInput = t.HasReferenceInput,
                            CreatedAt = t.CreatedAt,
                            RemoteVideoId = t.RemoteVideoId,
                            RemoteVideoApiMode = t.RemoteVideoApiMode,
                            RemoteGenerationId = t.RemoteGenerationId,
                            GenerateSeconds = t.GenerateSeconds,
                            DownloadSeconds = t.DownloadSeconds,
                            RemoteDownloadUrl = t.RemoteDownloadUrl
                        }).ToList()
                };

                File.WriteAllText(metaPath, JsonSerializer.Serialize(data, SaveJsonOptions));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MediaCenterV2] Save workspace meta failed: {ex.Message}");
            }
        }

        private void LoadWorkspaceMeta(MediaSessionViewModel session)
        {
            try
            {
                var metaPath = Path.Combine(session.SessionDirectory, "workspace.json");
                if (!File.Exists(metaPath))
                {
                    return;
                }

                var json = File.ReadAllText(metaPath);
                var data = JsonSerializer.Deserialize<MediaGenSession>(json);
                if (data == null)
                {
                    return;
                }

                var messages = (data.Messages ?? new List<MediaChatMessage>())
                    .Select(m => new ChatMessageViewModel(new MediaChatMessage
                    {
                        Role = m.Role,
                        Text = m.Text,
                        Timestamp = m.Timestamp,
                        GenerateSeconds = m.GenerateSeconds,
                        DownloadSeconds = m.DownloadSeconds,
                        MediaPaths = m.MediaPaths
                            .Select(p => ResolveStoredPathForLoad(p, session.SessionDirectory))
                            .Where(p => !string.IsNullOrWhiteSpace(p))
                            .ToList()
                    }))
                    .ToList();

                session.ReplaceAllMessages(messages);
                session.TaskHistory.Clear();
                foreach (var task in data.Tasks ?? new List<MediaGenTask>())
                {
                    task.ResultFilePath = ResolveStoredPathForLoad(task.ResultFilePath, session.SessionDirectory);
                    if (!ShouldRestoreTask(task))
                    {
                        continue;
                    }

                    session.TaskHistory.Add(task);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MediaCenterV2] Load workspace meta failed: {ex.Message}");
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
            SaveWorkspaceMeta(tab.Session);

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
        public string SecondaryText => IsPending
            ? $"{PendingStatusText} · {SessionName} · {CreatedAt:yyyy-MM-dd HH:mm}"
            : $"{WorkflowBadgeText} · {SessionName} · {CreatedAt:yyyy-MM-dd HH:mm}";
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

        public string MetaSummaryText => Group == null
            ? (IsPending ? PendingStatusText : $"{KindText} · {TimestampText}")
            : (IsPending ? $"{PendingStatusText} · {Group.SecondaryText}" : $"{KindText} · {Group.SecondaryText}");

        public string PromptSummaryText => Group?.PromptSummaryText
            ?? (string.IsNullOrWhiteSpace(PromptText) ? "未记录提示词" : Compact(PromptText, 220));

        private static string Compact(string text, int maxLength)
        {
            var singleLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return singleLine.Length > maxLength ? singleLine[..maxLength] + "..." : singleLine;
        }
    }
}
