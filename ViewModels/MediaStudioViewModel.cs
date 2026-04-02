using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using TrueFluentPro.Services.WebSearch;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using TrueFluentPro.Services.Storage;

namespace TrueFluentPro.ViewModels
{
    /// <summary>
    /// Media Studio 主 ViewModel — 管理会话列表、全局状态
    /// </summary>
    public class MediaStudioViewModel : ViewModelBase, IDisposable
    {
        private readonly AiConfig _aiConfig;
        private readonly MediaGenConfig _genConfig;
        private readonly List<AiEndpoint> _endpoints;
        private readonly IModelRuntimeResolver _modelRuntimeResolver;
        private readonly IAzureTokenProviderStore _azureTokenProviderStore;
        private readonly ConfigurationService _configurationService;
        private readonly Func<AzureSpeechConfig> _configProvider;
        private readonly Action<AzureSpeechConfig>? _onGlobalConfigUpdated;
        private readonly AiImageGenService _imageService = new();
        private readonly AiVideoGenService _videoService = new();

        // 网页搜索配置
        private string _webSearchProviderId = "bing";
        private WebSearchTriggerMode _webSearchTriggerMode = WebSearchTriggerMode.Auto;
        private int _webSearchMaxResults = 5;
        private bool _webSearchEnableIntentAnalysis = true;
        private bool _webSearchEnableResultCompression;
        private bool _webSearchDebugMode;
        private string _webSearchMcpEndpoint = "";
        private string _webSearchMcpToolName = "web_search";
        private string _webSearchMcpApiKey = "";

        private AzureTokenProvider _imageTokenProvider;
        private AzureTokenProvider _videoTokenProvider;
        private readonly string _studioDirectory;

        // SQLite 写入去重：指纹 + 任务状态追踪
        private readonly Dictionary<string, (int MsgCount, int TaskCount, int Completed, int Active, int Terminal, int AssetCount, string? Name)> _sqliteFingerprints = new();
        private readonly Dictionary<string, Dictionary<string, string>> _persistedTaskStates = new();
        private readonly Dictionary<string, long> _sessionAccessTicks = new();
        private long _sessionAccessCounter;

        /// <summary>View 层报告的聊天区域视口高度，用于计算首批渲染条数。</summary>
        public double ChatViewportHeight { get; set; } = MediaSessionViewModel.DefaultViewportHeight;

        // 会话内容异步加载取消令牌（防止快速连点竞态）
        private CancellationTokenSource? _sessionLoadCts;

        // --- 会话管理 ---
        public ObservableCollection<MediaSessionViewModel> Sessions { get; } = new();

        private MediaSessionViewModel? _currentSession;
        public MediaSessionViewModel? CurrentSession
        {
            get => _currentSession;
            set
            {
                if (SetProperty(ref _currentSession, value))
                {
                    OnPropertyChanged(nameof(HasCurrentSession));

                    foreach (var session in Sessions)
                        session.IsActiveSession = ReferenceEquals(session, value);

                    (DeleteSessionCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (RenameSessionCommand as RelayCommand)?.RaiseCanExecuteChanged();

                    if (value != null)
                    {
                        MarkSessionAccessed(value);
                        value.ActivateOnFirstSelection();
                        _ = EnsureSessionLoadedAsync(value);
                        EnforceLoadedSessionLimit(value);
                    }
                }
            }
        }

        public bool HasCurrentSession => CurrentSession != null;

        // --- 全局状态 ---
        private int _activeTaskCount;
        public int ActiveTaskCount
        {
            get => _activeTaskCount;
            set
            {
                if (SetProperty(ref _activeTaskCount, value))
                    OnPropertyChanged(nameof(HasActiveTasks));
            }
        }

        public bool HasActiveTasks => ActiveTaskCount > 0;

        private string _statusText = "就绪";
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        public string CurrentWebSearchProviderId => _webSearchProviderId;

        public string CurrentWebSearchProviderDisplayName => WebSearchProviderFactory.AvailableProviders
            .FirstOrDefault(p => string.Equals(p.Id, _webSearchProviderId, StringComparison.OrdinalIgnoreCase)).DisplayName
            ?? "Bing 国际版";

        // --- 命令 ---
        public ICommand NewSessionCommand { get; }
        public ICommand DeleteSessionCommand { get; }
        public ICommand RenameSessionCommand { get; }

        public MediaStudioViewModel(
            AiConfig aiConfig,
            MediaGenConfig genConfig,
            List<AiEndpoint> endpoints,
            IModelRuntimeResolver modelRuntimeResolver,
            IAzureTokenProviderStore azureTokenProviderStore,
            ConfigurationService configurationService,
            Func<AzureSpeechConfig> configProvider,
            Action<AzureSpeechConfig>? onGlobalConfigUpdated = null)
        {
            _aiConfig = aiConfig;
            _genConfig = genConfig;
            _endpoints = endpoints;
            _modelRuntimeResolver = modelRuntimeResolver;
            _azureTokenProviderStore = azureTokenProviderStore;
            _configurationService = configurationService;
            _configProvider = configProvider;
            _onGlobalConfigUpdated = onGlobalConfigUpdated;
            _imageTokenProvider = _azureTokenProviderStore.GetProvider("media-image");
            _videoTokenProvider = _azureTokenProviderStore.GetProvider("media-video");

            _imageService.SetTokenProvider(_imageTokenProvider);
            _videoService.SetTokenProvider(_videoTokenProvider);

            // 从全局配置初始化网页搜索设置（构造时 configProvider 已可用）
            var initialConfig = configProvider();
            _webSearchProviderId = initialConfig.WebSearchProviderId;
            _webSearchTriggerMode = initialConfig.WebSearchTriggerMode;
            _webSearchMaxResults = initialConfig.WebSearchMaxResults;
            _webSearchEnableIntentAnalysis = initialConfig.WebSearchEnableIntentAnalysis;
            _webSearchEnableResultCompression = initialConfig.WebSearchEnableResultCompression;
            _webSearchDebugMode = initialConfig.WebSearchDebugMode;
            _webSearchMcpEndpoint = initialConfig.WebSearchMcpEndpoint;
            _webSearchMcpToolName = initialConfig.WebSearchMcpToolName;
            _webSearchMcpApiKey = initialConfig.WebSearchMcpApiKey;

            var sessionsPath = PathManager.Instance.SessionsPath;
            _studioDirectory = Path.Combine(sessionsPath, "media-studio");
            Directory.CreateDirectory(_studioDirectory);

            NewSessionCommand = new RelayCommand(_ => CreateNewSession());
            DeleteSessionCommand = new RelayCommand(
                p => DeleteCurrentSession(p as MediaSessionViewModel),
                p => (p as MediaSessionViewModel ?? CurrentSession) != null);
            RenameSessionCommand = new RelayCommand(
                _ => { },  // 由 View 处理弹窗逻辑
                _ => CurrentSession != null);

            // 加载现有会话（磁盘 I/O 移到后台线程）
            _ = LoadSessionsAsync();

            _ = TrySilentLoginForMediaAsync();
        }

        public void UpdateConfiguration(AiConfig aiConfig, MediaGenConfig genConfig, List<AiEndpoint> endpoints,
            string? webSearchProviderId = null, WebSearchTriggerMode? webSearchTriggerMode = null, int? webSearchMaxResults = null,
            bool? webSearchEnableIntentAnalysis = null, bool? webSearchEnableResultCompression = null,
            string? webSearchMcpEndpoint = null, string? webSearchMcpToolName = null, string? webSearchMcpApiKey = null,
            bool? webSearchDebugMode = null)
        {
            CopyAiConfig(aiConfig, _aiConfig);
            CopyMediaGenConfig(genConfig, _genConfig);

            _endpoints.Clear();
            _endpoints.AddRange(endpoints ?? new List<AiEndpoint>());

            if (webSearchProviderId is not null) _webSearchProviderId = webSearchProviderId;
            if (webSearchTriggerMode is not null) _webSearchTriggerMode = webSearchTriggerMode.Value;
            if (webSearchMaxResults is not null) _webSearchMaxResults = webSearchMaxResults.Value;
            if (webSearchEnableIntentAnalysis is not null) _webSearchEnableIntentAnalysis = webSearchEnableIntentAnalysis.Value;
            if (webSearchEnableResultCompression is not null) _webSearchEnableResultCompression = webSearchEnableResultCompression.Value;
            if (webSearchDebugMode is not null) _webSearchDebugMode = webSearchDebugMode.Value;
            if (webSearchMcpEndpoint is not null) _webSearchMcpEndpoint = webSearchMcpEndpoint;
            if (webSearchMcpToolName is not null) _webSearchMcpToolName = webSearchMcpToolName;
            if (webSearchMcpApiKey is not null) _webSearchMcpApiKey = webSearchMcpApiKey;

            // 同步网页搜索配置到所有会话
            foreach (var session in Sessions)
                session.UpdateWebSearchConfig(_webSearchProviderId, _webSearchTriggerMode, _webSearchMaxResults,
                    _webSearchEnableIntentAnalysis, _webSearchEnableResultCompression,
                    _webSearchMcpEndpoint, _webSearchMcpToolName, _webSearchMcpApiKey,
                    _webSearchDebugMode);

            OnPropertyChanged(nameof(CurrentWebSearchProviderId));
            OnPropertyChanged(nameof(CurrentWebSearchProviderDisplayName));

            _ = TrySilentLoginForMediaAsync();
        }

        public async Task SelectWebSearchProviderAsync(string providerId, bool enableForCurrentSession = true)
        {
            var normalized = WebSearchProviderFactory.NormalizeProviderId(providerId);
            _webSearchProviderId = normalized;

            foreach (var session in Sessions)
            {
                session.UpdateWebSearchConfig(_webSearchProviderId, _webSearchTriggerMode, _webSearchMaxResults,
                    _webSearchEnableIntentAnalysis, _webSearchEnableResultCompression,
                    _webSearchMcpEndpoint, _webSearchMcpToolName, _webSearchMcpApiKey,
                    _webSearchDebugMode);
            }

            if (enableForCurrentSession && CurrentSession != null)
            {
                CurrentSession.EnableWebSearch = true;
            }

            _genConfig.DefaultEnableStudioWebSearch = enableForCurrentSession;

            var config = _configProvider();
            config.WebSearchProviderId = normalized;
            config.MediaGenConfig.DefaultEnableStudioWebSearch = enableForCurrentSession;
            await _configurationService.SaveConfigAsync(config);
            _onGlobalConfigUpdated?.Invoke(config);

            OnPropertyChanged(nameof(CurrentWebSearchProviderId));
            OnPropertyChanged(nameof(CurrentWebSearchProviderDisplayName));
            StatusText = $"已切换联网搜索引擎：{CurrentWebSearchProviderDisplayName}";
        }

        public async Task DisableWebSearchAsync()
        {
            if (CurrentSession != null)
            {
                CurrentSession.EnableWebSearch = false;
            }

            _genConfig.DefaultEnableStudioWebSearch = false;
            var config = _configProvider();
            config.MediaGenConfig.DefaultEnableStudioWebSearch = false;
            await _configurationService.SaveConfigAsync(config);
            _onGlobalConfigUpdated?.Invoke(config);
            StatusText = "已关闭当前会话联网搜索";
        }

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
            target.DefaultEnableStudioReasoning = source.DefaultEnableStudioReasoning;
            target.DefaultEnableStudioWebSearch = source.DefaultEnableStudioWebSearch;
            target.MaxLoadedSessionsInMemory = source.MaxLoadedSessionsInMemory;
            target.OutputDirectory = source.OutputDirectory;
        }

        private static ModelReference? CloneReference(ModelReference? reference)
        {
            if (reference == null)
                return null;

            return new ModelReference
            {
                EndpointId = reference.EndpointId,
                ModelId = reference.ModelId
            };
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
                    _imageTokenProvider = provider;
                    _imageService.SetTokenProvider(_imageTokenProvider);
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
                    _videoTokenProvider = provider;
                    _videoService.SetTokenProvider(_videoTokenProvider);
                }
            }
        }

        private static string GetEndpointProfileKey(AiEndpoint endpoint) => $"endpoint_{endpoint.Id}";

        public void CreateNewSession()
        {
            var sessionId = Guid.NewGuid().ToString("N")[..8];
            var sessionDir = Path.Combine(_studioDirectory, $"session_{sessionId}");
            var defaultName = AllocateNextDefaultSessionName();

            var session = new MediaSessionViewModel(
                sessionId, defaultName,
                sessionDir, _aiConfig, _genConfig, _endpoints,
                _modelRuntimeResolver,
                _azureTokenProviderStore,
                _imageService, _videoService,
                OnSessionTaskCountChanged,
                s => SaveSessionMeta(s),
                App.Services.GetRequiredService<IStoragePathResolver>());

            session.UpdateWebSearchConfig(_webSearchProviderId, _webSearchTriggerMode, _webSearchMaxResults,
                _webSearchEnableIntentAnalysis, _webSearchEnableResultCompression,
                _webSearchMcpEndpoint, _webSearchMcpToolName, _webSearchMcpApiKey,
                _webSearchDebugMode);
            session.IsContentLoaded = true;
            session.ForkRequested += HandleForkRequested;

            Sessions.Add(session);
            CurrentSession = session;
            UpdateActiveTaskCount();
            SaveSessionMeta(session);
        }

        private void HandleForkRequested(MediaSessionViewModel sourceSession, ChatMessageViewModel forkPoint)
        {
            var messagesToCopy = sourceSession.GetMessagesUpTo(forkPoint);
            if (messagesToCopy.Count == 0) return;

            var sessionId = Guid.NewGuid().ToString("N")[..8];
            var sessionDir = Path.Combine(_studioDirectory, $"session_{sessionId}");
            var defaultName = $"{sourceSession.SessionName} (分支)";

            var newSession = new MediaSessionViewModel(
                sessionId, defaultName,
                sessionDir, _aiConfig, _genConfig, _endpoints,
                _modelRuntimeResolver,
                _azureTokenProviderStore,
                _imageService, _videoService,
                OnSessionTaskCountChanged,
                s => SaveSessionMeta(s),
                App.Services.GetRequiredService<IStoragePathResolver>());

            newSession.UpdateWebSearchConfig(_webSearchProviderId, _webSearchTriggerMode, _webSearchMaxResults,
                _webSearchEnableIntentAnalysis, _webSearchEnableResultCompression,
                _webSearchMcpEndpoint, _webSearchMcpToolName, _webSearchMcpApiKey,
                _webSearchDebugMode);
            newSession.EnableReasoning = sourceSession.EnableReasoning;
            newSession.EnableWebSearch = sourceSession.EnableWebSearch;
            newSession.IsContentLoaded = true;
            newSession.ForkRequested += HandleForkRequested;

            // 将源消息复制到新会话
            var clonedMessages = messagesToCopy.Select(m => new ChatMessageViewModel(new MediaChatMessage
            {
                Role = m.Role,
                Text = m.Text,
                ContentType = m.ContentType,
                ReasoningText = m.ReasoningText,
                MediaPaths = m.MediaPaths.ToList(),
                Timestamp = m.Timestamp,
                GenerateSeconds = m.GenerateSeconds,
                DownloadSeconds = m.DownloadSeconds,
                PromptTokens = m.PromptTokens,
                CompletionTokens = m.CompletionTokens,
                Attachments = m.Attachments.Count > 0
                    ? m.Attachments.Select(a => new ChatAttachmentInfo
                    {
                        Type = a.Type,
                        FileName = a.FileName,
                        FilePath = a.FilePath,
                        FileSize = a.FileSize,
                    }).ToList()
                    : null,
            })).ToList();

            newSession.ReplaceAllMessages(clonedMessages, ChatViewportHeight);

            Sessions.Add(newSession);
            CurrentSession = newSession;
            UpdateActiveTaskCount();
            SaveSessionMeta(newSession);
        }

        public void DeleteCurrentSession(MediaSessionViewModel? targetSession = null)
        {
            var session = targetSession ?? CurrentSession;
            if (session == null)
            {
                return;
            }

            EnsureSessionLoadedSync(session);
            session.CancelAll();
            session.ForkRequested -= HandleForkRequested;

            MarkSessionDeleted(session);

            var idx = Sessions.IndexOf(session);
            Sessions.Remove(session);
            _sessionAccessTicks.Remove(session.SessionId);

            if (Sessions.Count > 0)
                CurrentSession = Sessions[Math.Min(idx, Sessions.Count - 1)];
            else
                CurrentSession = null;

            UpdateActiveTaskCount();
        }



        private string AllocateNextDefaultSessionName()
        {
            var usedNames = Sessions
                .Select(s => s.SessionName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var maxUsed = Sessions
                .Select(s => TryParseDefaultSessionNumber(s.SessionName))
                .Where(n => n.HasValue)
                .Select(n => n!.Value)
                .DefaultIfEmpty(0)
                .Max();

            var number = Math.Max(1, maxUsed + 1);
            string candidate;
            do
            {
                candidate = $"会话 {number}";
                number++;
            }
            while (usedNames.Contains(candidate));

            return candidate;
        }

        private static int? TryParseDefaultSessionNumber(string? name)
        {
            const string prefix = "会话 ";
            if (string.IsNullOrWhiteSpace(name) || !name.StartsWith(prefix, StringComparison.Ordinal))
            {
                return null;
            }

            var numberPart = name[prefix.Length..].Trim();
            return int.TryParse(numberPart, out var number) && number > 0
                ? number
                : null;
        }

        private void MarkSessionDeleted(MediaSessionViewModel session)
        {
            try
            {
                var switches = App.Services.GetRequiredService<SqliteFeatureSwitches>();
                if (!switches.IsReady) return;

                var sessionRepo = App.Services.GetRequiredService<ICreativeSessionRepository>();
                sessionRepo.SoftDelete(session.SessionId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"标记会话删除失败: {ex.Message}");
            }
        }

        public void RenameCurrentSession(string newName)
        {
            if (CurrentSession == null) return;
            CurrentSession.SessionName = newName;
            SaveSessionMeta(CurrentSession);
        }

        public void CancelAll()
        {
            foreach (var session in Sessions)
            {
                session.CancelAll();
            }
        }

        private void OnSessionTaskCountChanged()
        {
            Dispatcher.UIThread.Post(UpdateActiveTaskCount);
        }

        private void UpdateActiveTaskCount()
        {
            ActiveTaskCount = Sessions.Sum(s => s.RunningTaskCount);
            StatusText = ActiveTaskCount > 0
                ? $"活跃任务: {ActiveTaskCount}"
                : "就绪";
        }

        // --- 持久化 ---

        private async Task LoadSessionsAsync()
        {
            var switches = App.Services.GetRequiredService<SqliteFeatureSwitches>();
            if (!switches.IsReady)
            {
                // SQLite 尚未就绪，延迟加载
                await Task.Delay(100);
                if (!switches.IsReady) { CreateNewSession(); return; }
            }

            await LoadSessionsFromSqliteAsync();
        }

        private async Task LoadSessionsFromSqliteAsync()
        {
            var sessionRepo = App.Services.GetRequiredService<ICreativeSessionRepository>();
            var paths = App.Services.GetRequiredService<IStoragePathResolver>();

            var records = await Task.Run(() =>
            {
                return sessionRepo.List(limit: 200, sessionType: "media-studio")
                    .ToArray();
            }).ConfigureAwait(false);

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var rec in records)
                {
                    var dir = paths.ToAbsolutePath(rec.DirectoryPath);

                    var session = new MediaSessionViewModel(
                        rec.Id, rec.Name, dir,
                        _aiConfig, _genConfig, _endpoints,
                        _modelRuntimeResolver, _azureTokenProviderStore,
                        _imageService, _videoService,
                        OnSessionTaskCountChanged,
                        s => SaveSessionMeta(s),
                        paths);
                    session.UpdateWebSearchConfig(_webSearchProviderId, _webSearchTriggerMode, _webSearchMaxResults,
                        _webSearchEnableIntentAnalysis, _webSearchEnableResultCompression,
                        _webSearchMcpEndpoint, _webSearchMcpToolName, _webSearchMcpApiKey,
                        _webSearchDebugMode);
                    session.IsContentLoaded = false;
                    session.ForkRequested += HandleForkRequested;
                    Sessions.Add(session);
                }

                if (Sessions.Count == 0)
                {
                    CreateNewSession();
                }
                else
                {
                    CurrentSession = Sessions[0];
                }

                SqliteDebugLogger.LogRead("sessions", "P3-load-list", Sessions.Count);
            });
        }

        private async Task EnsureSessionLoadedAsync(MediaSessionViewModel session)
        {
            if (session.IsContentLoaded)
                return;

            // 取消上一次正在进行的加载（快速连点场景）
            _sessionLoadCts?.Cancel();
            var cts = _sessionLoadCts = new CancellationTokenSource();
            var ct = cts.Token;

            try
            {
                await LoadSessionFromSqliteAsync(session, ct);
            }
            catch (OperationCanceledException) { /* 被新选择取消，正常 */ }
        }

        /// <summary>同步版本，仅用于删除等必须即时准备数据的场景。</summary>
        private void EnsureSessionLoadedSync(MediaSessionViewModel session)
        {
            if (session.IsContentLoaded)
                return;
            var (msgs, tasks) = LoadSessionData(session.SessionId);
            ApplySessionData(session, msgs, tasks);
        }

        private void MarkSessionAccessed(MediaSessionViewModel session)
        {
            _sessionAccessTicks[session.SessionId] = ++_sessionAccessCounter;
        }

        private void EnforceLoadedSessionLimit(MediaSessionViewModel keepSession)
        {
            var maxLoaded = Math.Clamp(_genConfig.MaxLoadedSessionsInMemory, 1, 64);
            var loadedSessions = Sessions
                .Where(s => s.IsContentLoaded)
                .ToList();

            if (loadedSessions.Count <= maxLoaded)
            {
                return;
            }

            var evictionCandidates = loadedSessions
                .Where(s => !ReferenceEquals(s, keepSession))
                .Where(CanUnloadSession)
                .OrderBy(s => _sessionAccessTicks.GetValueOrDefault(s.SessionId, long.MinValue))
                .ToList();

            foreach (var candidate in evictionCandidates)
            {
                if (loadedSessions.Count <= maxLoaded)
                {
                    break;
                }

                SaveSessionMeta(candidate);
                candidate.UnloadLoadedContent();
                loadedSessions.Remove(candidate);
                Debug.WriteLine($"[SessionCache] 卸载普通会话缓存 {candidate.SessionId} ({candidate.SessionName})");
            }
        }

        private static bool CanUnloadSession(MediaSessionViewModel session)
        {
            if (!session.IsContentLoaded)
            {
                return false;
            }

            if (session.RunningTaskCount > 0 || session.IsGenerating || session.IsChatStreaming)
            {
                return false;
            }

            return true;
        }

        public void SaveSessionMeta(MediaSessionViewModel session)
        {
            if (!session.IsContentLoaded) return;

            try
            {
                WriteSqliteSession(session, "media-studio");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"保存会话元数据失败: {ex.Message}");
            }
        }

        public void SaveAllSessions()
        {
            foreach (var session in Sessions)
            {
                SaveSessionMeta(session);
            }
        }

        public void Dispose()
        {
            _sessionLoadCts?.Cancel();
            _sessionLoadCts?.Dispose();
            SaveAllSessions();
            CancelAll();
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

        private async Task LoadSessionFromSqliteAsync(MediaSessionViewModel session, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var sessionId = session.SessionId;

                // ── 后台线程：DB 查询 + 对象构建 ──────────────────
                var (loadedMessages, taskItems) = await Task.Run(
                    () => LoadSessionData(sessionId, ct), ct).ConfigureAwait(false);

                ct.ThrowIfCancellationRequested();

                // ── 回到 UI 线程：一次性刷入 ────────────────────
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    // 加载完成时 CurrentSession 可能已变（用户快速连点），此时放弃
                    if (!ReferenceEquals(CurrentSession, session))
                        return;

                    ApplySessionData(session, loadedMessages, taskItems);
                });

                sw.Stop();
                Debug.WriteLine($"[SQLite] P4 异步加载会话 {session.SessionId}: messages={loadedMessages.Count}, tasks={taskItems.Count}, {sw.ElapsedMilliseconds}ms");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SQLite] P4 加载失败 {session.SessionId}: {ex.Message}");
                await Dispatcher.UIThread.InvokeAsync(() => session.IsContentLoaded = true);
            }
        }

        /// <summary>纯数据加载（无 UI 依赖，可在任意线程调用）。</summary>
        private (List<ChatMessageViewModel> Messages, List<MediaGenTask> Tasks) LoadSessionData(
            string sessionId, CancellationToken ct = default)
        {
            var msgRepo = App.Services.GetRequiredService<ISessionMessageRepository>();
            var contentRepo = App.Services.GetRequiredService<ISessionContentRepository>();
            var paths = App.Services.GetRequiredService<IStoragePathResolver>();

            ct.ThrowIfCancellationRequested();

            // P1: 一次 bundle 查询替代 N+1
            var bundle = msgRepo.GetSessionBundle(sessionId);

            ct.ThrowIfCancellationRequested();

            var msgs = new List<ChatMessageViewModel>(bundle.Messages.Count);
            foreach (var rec in bundle.Messages)
            {
                bundle.MediaRefs.TryGetValue(rec.Id, out var mediaRefs);
                bundle.Citations.TryGetValue(rec.Id, out var citations);
                bundle.Attachments.TryGetValue(rec.Id, out var attachmentRecs);

                var msg = new MediaChatMessage
                {
                    Role = rec.Role,
                    Text = rec.Text,
                    ContentType = rec.ContentType,
                    ReasoningText = rec.ReasoningText,
                    Timestamp = rec.Timestamp,
                    GenerateSeconds = rec.GenerateSeconds,
                    DownloadSeconds = rec.DownloadSeconds,
                    PromptTokens = rec.PromptTokens,
                    CompletionTokens = rec.CompletionTokens,
                    SearchSummary = rec.SearchSummary ?? "",
                    MediaPaths = mediaRefs?.Select(r => paths.ToAbsolutePath(r.MediaPath)).ToList() ?? new(),
                    Citations = citations is { Count: > 0 }
                        ? citations.Select(c => new MediaChatCitation
                        {
                            Number = c.CitationNumber,
                            Title = c.Title,
                            Url = c.Url,
                            Snippet = c.Snippet,
                            Hostname = c.Hostname,
                        }).ToList()
                        : null,
                    Attachments = attachmentRecs is { Count: > 0 }
                        ? attachmentRecs.Select(a => new ChatAttachmentInfo
                        {
                            Type = a.AttachmentType,
                            FileName = a.FileName,
                            FilePath = a.FilePath,
                            FileSize = a.FileSize,
                        }).ToList()
                        : null,
                };
                msgs.Add(new ChatMessageViewModel(msg));
            }

            ct.ThrowIfCancellationRequested();

            var taskRecords = contentRepo.GetSessionTasks(sessionId);
            var tasks = taskRecords.Select(t => new MediaGenTask
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
            }).ToList();

            SqliteDebugLogger.LogLazyLoad(sessionId, msgs.Count > 0 ? 1 : 0, msgs.Count);
            return (msgs, tasks);
        }

        /// <summary>将加载好的数据应用到会话（必须在 UI 线程调用）。</summary>
        private void ApplySessionData(
            MediaSessionViewModel session,
            List<ChatMessageViewModel> messages,
            List<MediaGenTask> tasks)
        {
            session.ReplaceAllMessages(messages, ChatViewportHeight);

            session.TaskHistory.Clear();
            foreach (var t in tasks)
                session.TaskHistory.Add(t);

            session.IsContentLoaded = true;

            _sqliteFingerprints[session.SessionId] = (
                session.TotalMessageCount,
                session.TaskHistory.Count,
                session.TaskHistory.Count(t => t.Status == MediaGenStatus.Completed),
                session.TaskHistory.Count(t => t.Status is MediaGenStatus.Running or MediaGenStatus.Queued),
                session.TaskHistory.Count(t => t.Status is MediaGenStatus.Failed or MediaGenStatus.Cancelled),
                session.AssetCatalog.Count,
                session.SessionName);
            _persistedTaskStates[session.SessionId] = session.TaskHistory
                .ToDictionary(t => t.Id, t => t.Status.ToString());
        }

        private void WriteSqliteSession(MediaSessionViewModel session, string sessionType)
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
                    session.AssetCatalog.Count,
                    session.SessionName);
                if (_sqliteFingerprints.TryGetValue(session.SessionId, out var last) && last == fp)
                    return;

                var sessionRepo = App.Services.GetRequiredService<ICreativeSessionRepository>();
                var msgRepo = App.Services.GetRequiredService<ISessionMessageRepository>();
                var contentRepo = App.Services.GetRequiredService<ISessionContentRepository>();
                var paths = App.Services.GetRequiredService<IStoragePathResolver>();

                var existingRec = sessionRepo.GetById(session.SessionId);
                sessionRepo.Upsert(new SessionRecord
                {
                    Id = session.SessionId,
                    SessionType = sessionType,
                    Name = session.SessionName,
                    DirectoryPath = paths.ToRelativePath(session.SessionDirectory),
                    CanvasMode = existingRec?.CanvasMode ?? "",
                    MediaKind = existingRec?.MediaKind ?? "",
                    CreatedAt = existingRec?.CreatedAt ?? DateTime.Now,
                    UpdatedAt = DateTime.Now,
                    MessageCount = session.TotalMessageCount,
                    TaskCount = session.TaskHistory.Count,
                    AssetCount = session.AssetCatalog.Count,
                    LatestMessagePreview = session.AllMessages.LastOrDefault()?.Text?.Length > 60
                        ? session.AllMessages.Last().Text[..60]
                        : session.AllMessages.LastOrDefault()?.Text,
                });

                // 消息增量写入：只写新增的
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

                    // 写入消息附件
                    if (m.Attachments.Count > 0)
                    {
                        var attachments = m.Attachments.Select((a, idx) => new AttachmentRecord
                        {
                            AttachmentType = a.Type,
                            FileName = a.FileName,
                            FilePath = a.FilePath,
                            FileSize = a.FileSize,
                            SortOrder = idx,
                        }).ToList();
                        msgRepo.InsertAttachments(msgId, attachments);
                    }
                }

                // 任务增量 upsert：只写新增或状态变化的 task
                var knownTasks = _persistedTaskStates.GetValueOrDefault(session.SessionId);
                var newTaskStates = new Dictionary<string, string>();
                foreach (var t in session.TaskHistory)
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

                _sqliteFingerprints[session.SessionId] = fp;
                SqliteDebugLogger.LogWrite("sessions", session.SessionId, $"P2-write type={sessionType}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SQLite] P2 写入失败: {ex.Message}");
            }
        }

    }
}
