using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using TrueFluentPro.Services.WebSearch;
using TrueFluentPro.Models;
using TrueFluentPro.Services;

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
        private string _webSearchMcpEndpoint = "";
        private string _webSearchMcpToolName = "web_search";
        private string _webSearchMcpApiKey = "";

        private AzureTokenProvider _imageTokenProvider;
        private AzureTokenProvider _videoTokenProvider;
        private readonly string _studioDirectory;
        private readonly string _indexFilePath;
        private MediaStudioIndex _sessionIndex = new();


        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

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
                        value.ActivateOnFirstSelection();
                        EnsureSessionLoaded(value);
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

            var sessionsPath = PathManager.Instance.SessionsPath;
            _studioDirectory = Path.Combine(sessionsPath, "media-studio");
            _indexFilePath = Path.Combine(_studioDirectory, "media-studio.index.json");
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
            string? webSearchMcpEndpoint = null, string? webSearchMcpToolName = null, string? webSearchMcpApiKey = null)
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
            if (webSearchMcpEndpoint is not null) _webSearchMcpEndpoint = webSearchMcpEndpoint;
            if (webSearchMcpToolName is not null) _webSearchMcpToolName = webSearchMcpToolName;
            if (webSearchMcpApiKey is not null) _webSearchMcpApiKey = webSearchMcpApiKey;

            // 同步网页搜索配置到所有会话
            foreach (var session in Sessions)
                session.UpdateWebSearchConfig(_webSearchProviderId, _webSearchTriggerMode, _webSearchMaxResults,
                    _webSearchEnableIntentAnalysis, _webSearchEnableResultCompression,
                    _webSearchMcpEndpoint, _webSearchMcpToolName, _webSearchMcpApiKey);

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
                    _webSearchMcpEndpoint, _webSearchMcpToolName, _webSearchMcpApiKey);
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
            Directory.CreateDirectory(sessionDir);
            var defaultName = AllocateNextDefaultSessionName();

            var session = new MediaSessionViewModel(
                sessionId, defaultName,
                sessionDir, _aiConfig, _genConfig, _endpoints,
                _modelRuntimeResolver,
                _azureTokenProviderStore,
                _imageService, _videoService,
                OnSessionTaskCountChanged,
                s => SaveSessionMeta(s));

            session.UpdateWebSearchConfig(_webSearchProviderId, _webSearchTriggerMode, _webSearchMaxResults,
                _webSearchEnableIntentAnalysis, _webSearchEnableResultCompression,
                _webSearchMcpEndpoint, _webSearchMcpToolName, _webSearchMcpApiKey);
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
            Directory.CreateDirectory(sessionDir);
            var defaultName = $"{sourceSession.SessionName} (分支)";

            var newSession = new MediaSessionViewModel(
                sessionId, defaultName,
                sessionDir, _aiConfig, _genConfig, _endpoints,
                _modelRuntimeResolver,
                _azureTokenProviderStore,
                _imageService, _videoService,
                OnSessionTaskCountChanged,
                s => SaveSessionMeta(s));

            newSession.UpdateWebSearchConfig(_webSearchProviderId, _webSearchTriggerMode, _webSearchMaxResults,
                _webSearchEnableIntentAnalysis, _webSearchEnableResultCompression,
                _webSearchMcpEndpoint, _webSearchMcpToolName, _webSearchMcpApiKey);
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
                CompletionTokens = m.CompletionTokens
            })).ToList();

            newSession.ReplaceAllMessages(clonedMessages);

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

            EnsureSessionLoaded(session);
            session.CancelAll();
            session.ForkRequested -= HandleForkRequested;

            MarkSessionDeleted(session);
            MarkSessionDeletedInIndex(session.SessionId);

            var idx = Sessions.IndexOf(session);
            Sessions.Remove(session);

            if (Sessions.Count > 0)
                CurrentSession = Sessions[Math.Min(idx, Sessions.Count - 1)];
            else
                CurrentSession = null;

            UpdateActiveTaskCount();
        }



        private string AllocateNextDefaultSessionName()
        {
            EnsureNextSessionNumberInitialized();

            var usedNames = _sessionIndex.Sessions
                .Select(s => s.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var number = Math.Max(1, _sessionIndex.NextSessionNumber);
            string candidate;
            do
            {
                candidate = $"会话 {number}";
                number++;
            }
            while (usedNames.Contains(candidate));

            _sessionIndex.NextSessionNumber = number;
            return candidate;
        }

        private void EnsureNextSessionNumberInitialized()
        {
            var maxUsed = _sessionIndex.Sessions
                .Select(s => TryParseDefaultSessionNumber(s.Name))
                .Where(n => n.HasValue)
                .Select(n => n!.Value)
                .DefaultIfEmpty(0)
                .Max();

            if (_sessionIndex.NextSessionNumber <= maxUsed)
            {
                _sessionIndex.NextSessionNumber = maxUsed + 1;
            }

            if (_sessionIndex.NextSessionNumber < 1)
            {
                _sessionIndex.NextSessionNumber = 1;
            }
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
                var metaPath = Path.Combine(session.SessionDirectory, "session.json");
                if (File.Exists(metaPath))
                {
                    var json = File.ReadAllText(metaPath);
                    var sessionData = JsonSerializer.Deserialize<MediaGenSession>(json);
                    if (sessionData != null)
                    {
                        sessionData.IsDeleted = true;
                        File.WriteAllText(metaPath, JsonSerializer.Serialize(sessionData, JsonOptions));
                        return;
                    }
                }

                // 不存在或无法反序列化时，兜底写入最小删除标记
                var fallback = new MediaGenSession
                {
                    Id = session.SessionId,
                    Name = session.SessionName,
                    IsDeleted = true
                };
                File.WriteAllText(metaPath, JsonSerializer.Serialize(fallback, JsonOptions));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"标记会话删除失败: {ex.Message}");
            }
        }

        private void MarkSessionDeletedInIndex(string sessionId)
        {
            var entry = _sessionIndex.Sessions.FirstOrDefault(s => s.Id == sessionId);
            if (entry == null)
            {
                return;
            }

            entry.IsDeleted = true;
            entry.UpdatedAt = DateTime.Now;
            SaveSessionIndex();
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
            // 磁盘 I/O 在后台线程
            var loadedEntries = await Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(_studioDirectory)) return Array.Empty<(MediaSessionIndexItem entry, string dir)>();
                    LoadOrRebuildSessionIndex();

                    var entries = new List<(MediaSessionIndexItem entry, string dir)>();
                    foreach (var entry in _sessionIndex.Sessions.Where(s => !s.IsDeleted))
                    {
                        var dir = ResolveSessionDirectory(entry);
                        var metaPath = Path.Combine(dir, "session.json");
                        if (!File.Exists(metaPath))
                        {
                            entry.IsDeleted = true;
                            entry.UpdatedAt = DateTime.Now;
                            continue;
                        }
                        entries.Add((entry, dir));
                    }
                    SaveSessionIndex();
                    return entries.ToArray();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"加载会话索引失败: {ex.Message}");
                    return Array.Empty<(MediaSessionIndexItem entry, string dir)>();
                }
            }).ConfigureAwait(false);

            // UI 操作回到主线程
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var (entry, dir) in loadedEntries)
                {
                    var session = new MediaSessionViewModel(
                        entry.Id, entry.Name, dir,
                        _aiConfig, _genConfig, _endpoints,
                        _modelRuntimeResolver, _azureTokenProviderStore,
                        _imageService, _videoService,
                        OnSessionTaskCountChanged,
                        s => SaveSessionMeta(s));
                    session.UpdateWebSearchConfig(_webSearchProviderId, _webSearchTriggerMode, _webSearchMaxResults,
                        _webSearchEnableIntentAnalysis, _webSearchEnableResultCompression,
                        _webSearchMcpEndpoint, _webSearchMcpToolName, _webSearchMcpApiKey);
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
            });
        }

        private void LoadOrRebuildSessionIndex()
        {
            if (!File.Exists(_indexFilePath))
            {
                RebuildSessionIndexFromDisk();
                SaveSessionIndex();
                return;
            }

            try
            {
                var json = File.ReadAllText(_indexFilePath);
                _sessionIndex = JsonSerializer.Deserialize<MediaStudioIndex>(json) ?? new MediaStudioIndex();
                // 确保索引始终按最近更新排序（JSON 文件可能保留了旧的追加顺序）
                _sessionIndex.Sessions = _sessionIndex.Sessions
                    .OrderByDescending(s => s.UpdatedAt)
                    .ToList();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"读取会话索引失败，改为重建: {ex.Message}");
                RebuildSessionIndexFromDisk();
                SaveSessionIndex();
                return;
            }

            if (NeedRebuildIndexFromDisk())
            {
                RebuildSessionIndexFromDisk();
                SaveSessionIndex();
            }

            EnsureNextSessionNumberInitialized();
        }

        private bool NeedRebuildIndexFromDisk()
        {
            try
            {
                var diskDirs = Directory.GetDirectories(_studioDirectory, "session_*")
                    .Select(Path.GetFileName)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var indexedDirs = _sessionIndex.Sessions
                    .Select(s => string.IsNullOrWhiteSpace(s.DirectoryName) ? $"session_{s.Id}" : s.DirectoryName)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                return !diskDirs.SetEquals(indexedDirs);
            }
            catch
            {
                return true;
            }
        }

        private void RebuildSessionIndexFromDisk()
        {
            var rebuilt = new List<MediaSessionIndexItem>();

            foreach (var dir in Directory.GetDirectories(_studioDirectory, "session_*"))
            {
                var metaPath = Path.Combine(dir, "session.json");
                if (!File.Exists(metaPath))
                {
                    continue;
                }

                try
                {
                    var json = File.ReadAllText(metaPath);
                    var sessionData = JsonSerializer.Deserialize<MediaGenSession>(json);
                    if (sessionData == null)
                    {
                        continue;
                    }

                    rebuilt.Add(new MediaSessionIndexItem
                    {
                        Id = sessionData.Id,
                        Name = string.IsNullOrWhiteSpace(sessionData.Name) ? "新会话" : sessionData.Name,
                        DirectoryName = Path.GetFileName(dir) ?? $"session_{sessionData.Id}",
                        IsDeleted = sessionData.IsDeleted,
                        MessageCount = sessionData.Messages?.Count ?? 0,
                        TaskCount = sessionData.Tasks?.Count ?? 0,
                        UpdatedAt = File.GetLastWriteTime(metaPath)
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"重建索引时读取会话失败 {dir}: {ex.Message}");
                }
            }

            _sessionIndex = new MediaStudioIndex
            {
                Version = 1,
                Sessions = rebuilt
                    .OrderByDescending(s => s.UpdatedAt)
                    .ToList()
            };
        }

        private void EnsureSessionLoaded(MediaSessionViewModel session)
        {
            if (session.IsContentLoaded)
            {
                return;
            }

            // 保留已有的滚动位置快照（LRU 淘汰后重新加载时恢复原位置）

            var sw = Stopwatch.StartNew();

            var metaPath = Path.Combine(session.SessionDirectory, "session.json");
            if (!File.Exists(metaPath))
            {
                session.IsContentLoaded = true;
                sw.Stop();
                Debug.WriteLine($"加载会话 {session.SessionId}: session.json 不存在，mark loaded，{sw.ElapsedMilliseconds}ms");
                return;
            }

            try
            {
                var json = File.ReadAllText(metaPath);
                var sessionData = JsonSerializer.Deserialize<MediaGenSession>(json);
                if (sessionData == null)
                {
                    session.IsContentLoaded = true;
                    return;
                }

                var loadedMessages = new List<ChatMessageViewModel>();
                if (sessionData.Messages != null)
                {
                    foreach (var msg in sessionData.Messages)
                    {
                        var normalizedMessage = new MediaChatMessage
                        {
                            Role = msg.Role,
                            Text = msg.Text,
                            ContentType = msg.ContentType,
                            ReasoningText = msg.ReasoningText,
                            Timestamp = msg.Timestamp,
                            GenerateSeconds = msg.GenerateSeconds,
                            DownloadSeconds = msg.DownloadSeconds,
                            PromptTokens = msg.PromptTokens,
                            CompletionTokens = msg.CompletionTokens,
                            Citations = msg.Citations,
                            SearchSummary = msg.SearchSummary,
                            MediaPaths = msg.MediaPaths?
                                .Select(p => ResolveStoredPathForLoad(p, session.SessionDirectory))
                                .Where(p => !string.IsNullOrWhiteSpace(p))
                                .ToList() ?? new List<string>()
                        };

                        loadedMessages.Add(new ChatMessageViewModel(normalizedMessage));
                    }
                }

                session.ReplaceAllMessages(loadedMessages);

                session.TaskHistory.Clear();
                if (sessionData.Tasks != null)
                {
                    foreach (var task in sessionData.Tasks)
                    {
                        task.ResultFilePath = ResolveStoredPathForLoad(task.ResultFilePath, session.SessionDirectory);
                        session.TaskHistory.Add(task);
                    }
                }

                session.IsContentLoaded = true;
                UpdateSessionIndexFromSession(session, saveIndex: true);
                sw.Stop();
                Debug.WriteLine($"加载会话 {session.SessionId}: messages={session.Messages.Count}, tasks={session.TaskHistory.Count}, {sw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"按需加载会话失败 {session.SessionId}: {ex.Message}");
                session.IsContentLoaded = true;
                sw.Stop();
                Debug.WriteLine($"加载会话 {session.SessionId}: 失败后标记为已加载，{sw.ElapsedMilliseconds}ms");
            }
        }

        private string ResolveSessionDirectory(MediaSessionIndexItem entry)
            => Path.Combine(
                _studioDirectory,
                string.IsNullOrWhiteSpace(entry.DirectoryName) ? $"session_{entry.Id}" : entry.DirectoryName);

        public void SaveSessionMeta(MediaSessionViewModel session)
        {
            try
            {
                if (session.IsContentLoaded)
                {
                    var metaPath = Path.Combine(session.SessionDirectory, "session.json");
                    Directory.CreateDirectory(session.SessionDirectory);

                    var data = new MediaGenSession
                    {
                        Id = session.SessionId,
                        Name = session.SessionName,
                        Messages = session.AllMessages.Select(m => new MediaChatMessage
                        {
                            Role = m.Role,
                            Text = m.Text,
                            ContentType = m.ContentType,
                            ReasoningText = m.ReasoningText,
                            MediaPaths = m.MediaPaths
                                .Select(p => ConvertPathForSave(p, session.SessionDirectory))
                                .Where(p => !string.IsNullOrWhiteSpace(p))
                                .ToList(),
                            Timestamp = m.Timestamp,
                            GenerateSeconds = m.GenerateSeconds,
                            DownloadSeconds = m.DownloadSeconds,
                            PromptTokens = m.PromptTokens,
                            CompletionTokens = m.CompletionTokens,
                            Citations = m.Citations?.Select(c => new MediaChatCitation
                            {
                                Number = c.Number,
                                Title = c.Title,
                                Url = c.Url,
                                Snippet = c.Snippet,
                                Hostname = c.Hostname
                            }).ToList(),
                            SearchSummary = m.SearchSummary
                        }).ToList(),
                        Tasks = session.TaskHistory.Select(t => new MediaGenTask
                        {
                            Id = t.Id,
                            Type = t.Type,
                            Status = t.Status,
                            Prompt = t.Prompt,
                            Progress = t.Progress,
                            ResultFilePath = ConvertPathForSave(t.ResultFilePath, session.SessionDirectory),
                            ErrorMessage = t.ErrorMessage,
                            CreatedAt = t.CreatedAt,
                            RemoteVideoId = t.RemoteVideoId,
                            RemoteVideoApiMode = t.RemoteVideoApiMode,
                            RemoteGenerationId = t.RemoteGenerationId,
                            GenerateSeconds = t.GenerateSeconds,
                            DownloadSeconds = t.DownloadSeconds,
                            RemoteDownloadUrl = t.RemoteDownloadUrl
                        }).ToList()
                    };

                    File.WriteAllText(metaPath, JsonSerializer.Serialize(data, JsonOptions));
                }

                UpdateSessionIndexFromSession(session, saveIndex: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"保存会话元数据失败: {ex.Message}");
            }
        }

        private void UpdateSessionIndexFromSession(MediaSessionViewModel session, bool saveIndex)
        {
            var entry = _sessionIndex.Sessions.FirstOrDefault(s => s.Id == session.SessionId);
            if (entry == null)
            {
                entry = new MediaSessionIndexItem
                {
                    Id = session.SessionId,
                    DirectoryName = Path.GetFileName(session.SessionDirectory) ?? $"session_{session.SessionId}"
                };
                _sessionIndex.Sessions.Add(entry);
            }

            entry.Name = session.SessionName;
            entry.IsDeleted = false;
            entry.DirectoryName = Path.GetFileName(session.SessionDirectory) ?? entry.DirectoryName;
            if (session.IsContentLoaded)
            {
                entry.MessageCount = session.TotalMessageCount;
                entry.TaskCount = session.TaskHistory.Count;
            }
            entry.UpdatedAt = DateTime.Now;

            if (saveIndex)
            {
                SaveSessionIndex();
            }
        }

        private void SaveSessionIndex()
        {
            try
            {
                Directory.CreateDirectory(_studioDirectory);
                File.WriteAllText(_indexFilePath, JsonSerializer.Serialize(_sessionIndex, JsonOptions));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"保存会话索引失败: {ex.Message}");
            }
        }

        public void SaveAllSessions()
        {
            foreach (var session in Sessions)
            {
                SaveSessionMeta(session);
            }

            SaveSessionIndex();
        }

        public void Dispose()
        {
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
                    var candidate = Path.GetFullPath(Path.Combine(sessionDirectory, value.Replace('/', Path.DirectorySeparatorChar)));
                    return candidate;
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
                    Path.Combine(sessionDirectory, "outputs", fileName)
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
    }
}
