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
        private readonly AiImageGenService _imageService = new();
        private readonly AiVideoGenService _videoService = new();
        private AzureTokenProvider _imageTokenProvider = new("media-image");
        private AzureTokenProvider _videoTokenProvider = new("media-video");
        private readonly string _studioDirectory;
        private readonly string _indexFilePath;
        private MediaStudioIndex _sessionIndex = new();
        private readonly LinkedList<string> _loadedSessionLru = new();
        private readonly Dictionary<string, LinkedListNode<string>> _loadedSessionLruNodes = new(StringComparer.OrdinalIgnoreCase);

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
                    {
                        session.IsActiveSession = ReferenceEquals(session, value);
                    }

                    (DeleteSessionCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (RenameSessionCommand as RelayCommand)?.RaiseCanExecuteChanged();

                    if (value != null)
                    {
                        value.ActivateOnFirstSelection();
                        var switchSw = Stopwatch.StartNew();
                        var wasLoaded = value.IsContentLoaded;
                        EnsureSessionLoaded(value);
                        TouchLoadedSession(value);
                        EnforceLoadedSessionCacheLimit(currentSessionId: value.SessionId);
                        switchSw.Stop();
                        Debug.WriteLine($"切换会话 {value.SessionId}: loadedBefore={wasLoaded}, loadedNow={value.IsContentLoaded}, messages={value.Messages.Count}, tasks={value.TaskHistory.Count}, ensureMs={switchSw.ElapsedMilliseconds}");

                        if (!value.HasBackfilledVideoFrames)
                        {
                            value.HasBackfilledVideoFrames = true;
                            _ = value.BackfillVideoFramesForExistingMessagesAsync();
                        }

                        if (!value.HasResumedInterruptedVideoTasks)
                        {
                            value.HasResumedInterruptedVideoTasks = true;
                            ResumeInterruptedVideoTasksForSession(value);
                        }
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

        // --- 命令 ---
        public ICommand NewSessionCommand { get; }
        public ICommand DeleteSessionCommand { get; }
        public ICommand RenameSessionCommand { get; }

        public MediaStudioViewModel(AiConfig aiConfig, MediaGenConfig genConfig, List<AiEndpoint> endpoints, IModelRuntimeResolver modelRuntimeResolver)
        {
            _aiConfig = aiConfig;
            _genConfig = genConfig;
            _endpoints = endpoints;
            _modelRuntimeResolver = modelRuntimeResolver;

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

            // 加载现有会话
            LoadSessions();

            // 若无会话，自动创建一个
            if (Sessions.Count == 0)
            {
                CreateNewSession();
            }

            _ = TrySilentLoginForMediaAsync();
        }

        public void UpdateConfiguration(AiConfig aiConfig, MediaGenConfig genConfig, List<AiEndpoint> endpoints)
        {
            CopyAiConfig(aiConfig, _aiConfig);
            CopyMediaGenConfig(genConfig, _genConfig);

            _endpoints.Clear();
            _endpoints.AddRange(endpoints ?? new List<AiEndpoint>());

            _ = TrySilentLoginForMediaAsync();
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
                return null;

            return new ModelReference
            {
                EndpointId = reference.EndpointId,
                ModelId = reference.ModelId
            };
        }

        /// <summary>
        /// 恢复单会话中 Running 状态且有 RemoteVideoId 的视频任务
        /// </summary>
        private static void ResumeInterruptedVideoTasksForSession(MediaSessionViewModel session)
        {
            var tasksToResume = session.TaskHistory
                .Where(t => t.Type == MediaGenType.Video
                    && t.Status == MediaGenStatus.Running
                    && !string.IsNullOrEmpty(t.RemoteVideoId))
                .ToList();

            foreach (var task in tasksToResume)
            {
                Debug.WriteLine($"恢复视频任务: {task.Id}, VideoId: {task.RemoteVideoId}");
                session.ResumeVideoTask(task);
            }
        }

        private async Task TrySilentLoginForMediaAsync()
        {
            var runtimeConfig = BuildRuntimeConfig();

            if (_modelRuntimeResolver.TryResolve(runtimeConfig, _genConfig.ImageModelRef, ModelCapability.Image, out var imageRuntime, out _)
                && imageRuntime != null
                && imageRuntime.AzureAuthMode == AzureAuthMode.AAD)
            {
                _imageTokenProvider = new AzureTokenProvider(imageRuntime.ProfileKey);
                _imageService.SetTokenProvider(_imageTokenProvider);
                await _imageTokenProvider.TrySilentLoginAsync(imageRuntime.AzureTenantId, imageRuntime.AzureClientId);
            }

            if (_modelRuntimeResolver.TryResolve(runtimeConfig, _genConfig.VideoModelRef, ModelCapability.Video, out var videoRuntime, out _)
                && videoRuntime != null
                && videoRuntime.AzureAuthMode == AzureAuthMode.AAD)
            {
                _videoTokenProvider = new AzureTokenProvider(videoRuntime.ProfileKey);
                _videoService.SetTokenProvider(_videoTokenProvider);
                await _videoTokenProvider.TrySilentLoginAsync(videoRuntime.AzureTenantId, videoRuntime.AzureClientId);
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
                _imageService, _videoService,
                OnSessionTaskCountChanged,
                s => SaveSessionMeta(s));

            session.IsContentLoaded = true;
            ResetSessionScrollSnapshot(session);

            Sessions.Add(session);
            CurrentSession = session;
            UpdateActiveTaskCount();
            SaveSessionMeta(session);
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

            // 标记 session.json 为已删除，下次不再加载（保留目录与历史文件）
            MarkSessionDeleted(session);
            MarkSessionDeletedInIndex(session.SessionId);

            var idx = Sessions.IndexOf(session);
            Sessions.Remove(session);

            if (Sessions.Count > 0)
            {
                CurrentSession = Sessions[Math.Min(idx, Sessions.Count - 1)];
            }
            else
            {
                CurrentSession = null;
            }

            UpdateActiveTaskCount();
            RemoveFromLoadedSessionLru(session.SessionId);
        }

        private int GetMaxLoadedSessionsInMemory()
        {
            var configured = _genConfig.MaxLoadedSessionsInMemory;
            if (configured <= 0)
            {
                return 8;
            }

            return Math.Clamp(configured, 1, 64);
        }

        private void TouchLoadedSession(MediaSessionViewModel session)
        {
            if (!session.IsContentLoaded)
            {
                return;
            }

            if (_loadedSessionLruNodes.TryGetValue(session.SessionId, out var existingNode))
            {
                _loadedSessionLru.Remove(existingNode);
            }

            var node = _loadedSessionLru.AddLast(session.SessionId);
            _loadedSessionLruNodes[session.SessionId] = node;
        }

        private void RemoveFromLoadedSessionLru(string sessionId)
        {
            if (!_loadedSessionLruNodes.TryGetValue(sessionId, out var node))
            {
                return;
            }

            _loadedSessionLru.Remove(node);
            _loadedSessionLruNodes.Remove(sessionId);
        }

        private void EnforceLoadedSessionCacheLimit(string? currentSessionId)
        {
            var maxLoaded = GetMaxLoadedSessionsInMemory();
            var loadedCount = Sessions.Count(s => s.IsContentLoaded);

            if (loadedCount <= maxLoaded)
            {
                return;
            }

            var guard = _loadedSessionLru.Count + 8;
            while (loadedCount > maxLoaded && _loadedSessionLru.Count > 0 && guard-- > 0)
            {
                var node = _loadedSessionLru.First;
                if (node == null)
                {
                    break;
                }

                _loadedSessionLru.RemoveFirst();
                _loadedSessionLruNodes.Remove(node.Value);

                var session = Sessions.FirstOrDefault(s => s.SessionId.Equals(node.Value, StringComparison.OrdinalIgnoreCase));
                if (session == null)
                {
                    continue;
                }

                if (!session.IsContentLoaded)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(currentSessionId)
                    && session.SessionId.Equals(currentSessionId, StringComparison.OrdinalIgnoreCase))
                {
                    TouchLoadedSession(session);
                    continue;
                }

                if (session.RunningTaskCount > 0)
                {
                    TouchLoadedSession(session);
                    continue;
                }

                session.Messages.Clear();
                session.TaskHistory.Clear();
                session.LastNonBottomScrollOffsetY = null;
                session.LastScrollAnchorRatio = null;
                session.LastScrollAnchorMessageIndex = null;
                session.LastScrollAnchorViewportOffsetY = null;
                session.IsContentLoaded = false;
                loadedCount--;
                Debug.WriteLine($"会话缓存淘汰: {session.SessionId}");
            }
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

        private void LoadSessions()
        {
            try
            {
                if (!Directory.Exists(_studioDirectory)) return;

                LoadOrRebuildSessionIndex();

                foreach (var entry in _sessionIndex.Sessions.Where(s => !s.IsDeleted))
                {
                    var dir = ResolveSessionDirectory(entry);
                    var metaPath = Path.Combine(dir, "session.json");
                    if (!File.Exists(metaPath))
                    {
                        // 索引与真实文件不一致：标记删除并略过
                        entry.IsDeleted = true;
                        entry.UpdatedAt = DateTime.Now;
                        continue;
                    }

                    var session = new MediaSessionViewModel(
                        entry.Id,
                        entry.Name,
                        dir,
                        _aiConfig,
                        _genConfig,
                        _endpoints,
                        _modelRuntimeResolver,
                        _imageService,
                        _videoService,
                        OnSessionTaskCountChanged,
                        s => SaveSessionMeta(s));

                    session.IsContentLoaded = false;
                    ResetSessionScrollSnapshot(session);

                    Sessions.Add(session);
                }

                SaveSessionIndex();

                if (Sessions.Count > 0)
                {
                    CurrentSession = Sessions[0];
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载会话列表失败: {ex.Message}");
            }
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

            // 旧版本可能在文件中出现过滚动相关字段；
            // 即使存在，也统一忽略，首次加载一律按“无位置快照”处理（默认到底部）。
            ResetSessionScrollSnapshot(session);

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

                session.Messages.Clear();
                if (sessionData.Messages != null)
                {
                    foreach (var msg in sessionData.Messages)
                    {
                        var normalizedMessage = new MediaChatMessage
                        {
                            Role = msg.Role,
                            Text = msg.Text,
                            Timestamp = msg.Timestamp,
                            GenerateSeconds = msg.GenerateSeconds,
                            DownloadSeconds = msg.DownloadSeconds,
                            MediaPaths = msg.MediaPaths?
                                .Select(p => ResolveStoredPathForLoad(p, session.SessionDirectory))
                                .Where(p => !string.IsNullOrWhiteSpace(p))
                                .ToList() ?? new List<string>()
                        };

                        session.Messages.Add(new ChatMessageViewModel(normalizedMessage));
                    }
                }

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
                TouchLoadedSession(session);
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

        private static void ResetSessionScrollSnapshot(MediaSessionViewModel session)
        {
            session.LastNonBottomScrollOffsetY = null;
            session.LastScrollAnchorRatio = null;
            session.LastScrollSavedMaxY = null;
            session.LastScrollAnchorMessageIndex = null;
            session.LastScrollAnchorViewportOffsetY = null;
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
                        Messages = session.Messages.Select(m => new MediaChatMessage
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
                entry.MessageCount = session.Messages.Count;
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
