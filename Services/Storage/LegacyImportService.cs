using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services.Storage
{
    public sealed class LegacyImportService : ILegacyImportService
    {
        private readonly ICreativeSessionRepository _sessionRepo;
        private readonly ISessionMessageRepository _messageRepo;
        private readonly ISessionContentRepository _contentRepo;
        private readonly IAudioLibraryRepository _audioRepo;
        private readonly IStoragePathResolver _paths;

        public string? LastImportBatchId { get; private set; }
        public LegacyImportStats LastStats { get; private set; } = new();

        public LegacyImportService(
            ICreativeSessionRepository sessionRepo,
            ISessionMessageRepository messageRepo,
            ISessionContentRepository contentRepo,
            IAudioLibraryRepository audioRepo,
            IStoragePathResolver paths)
        {
            _sessionRepo = sessionRepo;
            _messageRepo = messageRepo;
            _contentRepo = contentRepo;
            _audioRepo = audioRepo;
            _paths = paths;
        }

        public void ImportAll()
        {
            var batchId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            LastImportBatchId = batchId;
            var stats = new LegacyImportStats();
            LastStats = stats;

            SqliteDebugLogger.LogImport($"===== 开始导入批次 {batchId} =====");

            var sessionsPath = PathManager.Instance.SessionsPath;
            if (!Directory.Exists(sessionsPath))
            {
                SqliteDebugLogger.LogImport("Sessions 目录不存在，跳过导入");
                return;
            }

            // 扫描 media-studio 会话 (session_* 目录 + session.json)
            ImportMediaStudioSessions(sessionsPath, batchId, stats);

            // 扫描 media-center-v2 工作区 (session_* 目录 + workspace.json)
            ImportMediaCenterWorkspaces(sessionsPath, batchId, stats);

            // 扫描音频文件
            ImportAudioFiles(sessionsPath, batchId, stats);

            SqliteDebugLogger.LogImport(
                $"===== 导入完成: sessions={stats.SessionsImported}, messages={stats.MessagesImported}, " +
                $"tasks={stats.TasksImported}, assets={stats.AssetsImported}, " +
                $"audio={stats.AudioFilesImported}, skipped={stats.Skipped}, errors={stats.Errors} =====");
        }

        private void ImportMediaStudioSessions(string sessionsPath, string batchId, LegacyImportStats stats)
        {
            foreach (var dir in Directory.GetDirectories(sessionsPath, "session_*"))
            {
                var metaPath = Path.Combine(dir, "session.json");
                if (!File.Exists(metaPath)) continue;

                // 如果同目录还有 workspace.json，那这个目录属于 media-center-v2
                if (File.Exists(Path.Combine(dir, "workspace.json"))) continue;

                try
                {
                    var json = File.ReadAllText(metaPath);
                    var data = JsonSerializer.Deserialize<MediaGenSession>(json);
                    if (data == null) { stats.Skipped++; continue; }

                    // 检查是否已导入过
                    if (_sessionRepo.GetById(data.Id) != null) { stats.Skipped++; continue; }

                    var dirRelative = _paths.ToRelativePath(dir);

                    _sessionRepo.Upsert(new SessionRecord
                    {
                        Id = data.Id,
                        SessionType = "media-studio",
                        Name = data.Name,
                        DirectoryPath = dirRelative,
                        CanvasMode = data.CanvasMode,
                        MediaKind = data.MediaKind,
                        IsDeleted = data.IsDeleted,
                        CreatedAt = data.CreatedAt,
                        UpdatedAt = File.GetLastWriteTime(metaPath),
                        MessageCount = data.Messages?.Count ?? 0,
                        TaskCount = data.Tasks?.Count ?? 0,
                        AssetCount = data.Assets?.Count ?? 0,
                        LatestMessagePreview = data.Messages?.LastOrDefault()?.Text?.Length > 60
                            ? data.Messages.Last().Text[..60]
                            : data.Messages?.LastOrDefault()?.Text,
                        SourceSessionId = data.Source?.SourceSessionId,
                        SourceSessionName = data.Source?.SourceSessionName,
                        SourceSessionDirectoryName = data.Source?.SourceSessionDirectoryName,
                        SourceAssetId = data.Source?.SourceAssetId,
                        SourceAssetKind = data.Source?.SourceAssetKind,
                        SourceAssetFileName = data.Source?.SourceAssetFileName,
                        SourceAssetPath = data.Source?.SourceAssetPath,
                        SourcePreviewPath = data.Source?.SourcePreviewPath,
                        SourceReferenceRole = data.Source?.ReferenceRole,
                        LegacySourcePath = dirRelative,
                        ImportBatchId = batchId,
                        ImportedAt = DateTime.Now,
                        IsLegacyImport = true,
                    });
                    stats.SessionsImported++;

                    ImportSessionMessages(data.Id, data.Messages, dir, stats);
                    ImportSessionTasks(data.Id, data.Tasks, dir, stats);
                    ImportSessionAssets(data.Id, data.Assets, dir, stats);

                    SqliteDebugLogger.LogImport($"[media-studio] 已导入 {data.Id} ({data.Name})");
                }
                catch (Exception ex)
                {
                    stats.Errors++;
                    SqliteDebugLogger.LogImport($"[media-studio] 导入失败 {dir}: {ex.Message}");
                }
            }
        }

        private void ImportMediaCenterWorkspaces(string sessionsPath, string batchId, LegacyImportStats stats)
        {
            foreach (var dir in Directory.GetDirectories(sessionsPath, "session_*"))
            {
                var metaPath = Path.Combine(dir, "workspace.json");
                if (!File.Exists(metaPath)) continue;

                try
                {
                    var json = File.ReadAllText(metaPath);
                    var data = JsonSerializer.Deserialize<MediaGenSession>(json);
                    if (data == null) { stats.Skipped++; continue; }

                    if (_sessionRepo.GetById(data.Id) != null) { stats.Skipped++; continue; }

                    var dirRelative = _paths.ToRelativePath(dir);

                    _sessionRepo.Upsert(new SessionRecord
                    {
                        Id = data.Id,
                        SessionType = "media-center-v2",
                        Name = data.Name,
                        DirectoryPath = dirRelative,
                        CanvasMode = data.CanvasMode,
                        MediaKind = data.MediaKind,
                        IsDeleted = data.IsDeleted,
                        CreatedAt = data.CreatedAt,
                        UpdatedAt = File.GetLastWriteTime(metaPath),
                        MessageCount = data.Messages?.Count ?? 0,
                        TaskCount = data.Tasks?.Count ?? 0,
                        AssetCount = data.Assets?.Count ?? 0,
                        LatestMessagePreview = data.Messages?.LastOrDefault()?.Text?.Length > 60
                            ? data.Messages.Last().Text[..60]
                            : data.Messages?.LastOrDefault()?.Text,
                        SourceSessionId = data.Source?.SourceSessionId,
                        SourceSessionName = data.Source?.SourceSessionName,
                        SourceSessionDirectoryName = data.Source?.SourceSessionDirectoryName,
                        SourceAssetId = data.Source?.SourceAssetId,
                        SourceAssetKind = data.Source?.SourceAssetKind,
                        SourceAssetFileName = data.Source?.SourceAssetFileName,
                        SourceAssetPath = data.Source?.SourceAssetPath,
                        SourcePreviewPath = data.Source?.SourcePreviewPath,
                        SourceReferenceRole = data.Source?.ReferenceRole,
                        LegacySourcePath = dirRelative,
                        ImportBatchId = batchId,
                        ImportedAt = DateTime.Now,
                        IsLegacyImport = true,
                    });
                    stats.SessionsImported++;

                    ImportSessionMessages(data.Id, data.Messages, dir, stats);
                    ImportSessionTasks(data.Id, data.Tasks, dir, stats);
                    ImportSessionAssets(data.Id, data.Assets, dir, stats);

                    SqliteDebugLogger.LogImport($"[media-center-v2] 已导入 {data.Id} ({data.Name})");
                }
                catch (Exception ex)
                {
                    stats.Errors++;
                    SqliteDebugLogger.LogImport($"[media-center-v2] 导入失败 {dir}: {ex.Message}");
                }
            }
        }

        private void ImportSessionMessages(string sessionId, List<MediaChatMessage>? messages, string sessionDir, LegacyImportStats stats)
        {
            if (messages == null || messages.Count == 0) return;

            for (var i = 0; i < messages.Count; i++)
            {
                var msg = messages[i];
                var msgId = Guid.NewGuid().ToString("N")[..8];

                try
                {
                    _messageRepo.Insert(new MessageRecord
                    {
                        Id = msgId,
                        SessionId = sessionId,
                        SequenceNo = i + 1,
                        Role = msg.Role,
                        ContentType = msg.ContentType,
                        Text = msg.Text,
                        ReasoningText = msg.ReasoningText ?? "",
                        PromptTokens = msg.PromptTokens,
                        CompletionTokens = msg.CompletionTokens,
                        GenerateSeconds = msg.GenerateSeconds,
                        DownloadSeconds = msg.DownloadSeconds,
                        SearchSummary = msg.SearchSummary,
                        Timestamp = msg.Timestamp,
                    });

                    // 导入媒体引用
                    if (msg.MediaPaths is { Count: > 0 })
                    {
                        var refs = msg.MediaPaths.Select((p, idx) => new MediaRefRecord
                        {
                            MediaPath = _paths.ToRelativePath(
                                Path.IsPathRooted(p) ? p : Path.Combine(sessionDir, p.Replace('/', Path.DirectorySeparatorChar))),
                            MediaKind = InferMediaKind(p),
                            SortOrder = idx,
                        }).ToList();
                        _messageRepo.InsertMediaRefs(msgId, refs);
                    }

                    // 导入网页引用
                    if (msg.Citations is { Count: > 0 })
                    {
                        var citations = msg.Citations.Select(c => new CitationRecord
                        {
                            CitationNumber = c.Number,
                            Title = c.Title,
                            Url = c.Url,
                            Snippet = c.Snippet,
                            Hostname = c.Hostname,
                        }).ToList();
                        _messageRepo.InsertCitations(msgId, citations);
                    }

                    stats.MessagesImported++;
                }
                catch (Exception ex)
                {
                    stats.Errors++;
                    SqliteDebugLogger.LogImport($"  消息导入失败 seq={i + 1}: {ex.Message}");
                }
            }
        }

        private void ImportSessionTasks(string sessionId, List<MediaGenTask>? tasks, string sessionDir, LegacyImportStats stats)
        {
            if (tasks == null || tasks.Count == 0) return;

            foreach (var task in tasks)
            {
                try
                {
                    _contentRepo.UpsertTask(new TaskRecord
                    {
                        Id = task.Id,
                        SessionId = sessionId,
                        TaskType = task.Type.ToString(),
                        Status = task.Status.ToString(),
                        Prompt = task.Prompt ?? "",
                        Progress = task.Progress,
                        ResultFilePath = string.IsNullOrWhiteSpace(task.ResultFilePath) ? null
                            : _paths.ToRelativePath(
                                Path.IsPathRooted(task.ResultFilePath)
                                    ? task.ResultFilePath
                                    : Path.Combine(sessionDir, task.ResultFilePath.Replace('/', Path.DirectorySeparatorChar))),
                        ErrorMessage = task.ErrorMessage,
                        HasReferenceInput = task.HasReferenceInput,
                        RemoteVideoId = task.RemoteVideoId,
                        RemoteVideoApiMode = task.RemoteVideoApiMode?.ToString(),
                        RemoteGenerationId = task.RemoteGenerationId,
                        RemoteDownloadUrl = task.RemoteDownloadUrl,
                        GenerateSeconds = task.GenerateSeconds,
                        DownloadSeconds = task.DownloadSeconds,
                        CreatedAt = task.CreatedAt,
                        UpdatedAt = DateTime.Now,
                    });
                    stats.TasksImported++;
                }
                catch (Exception ex)
                {
                    stats.Errors++;
                    SqliteDebugLogger.LogImport($"  任务导入失败 {task.Id}: {ex.Message}");
                }
            }
        }

        private void ImportSessionAssets(string sessionId, List<MediaAssetRecord>? assets, string sessionDir, LegacyImportStats stats)
        {
            if (assets == null || assets.Count == 0) return;

            foreach (var asset in assets)
            {
                try
                {
                    _contentRepo.UpsertAsset(new AssetRecord
                    {
                        AssetId = string.IsNullOrWhiteSpace(asset.AssetId)
                            ? Guid.NewGuid().ToString("N")[..8]
                            : asset.AssetId,
                        SessionId = sessionId,
                        GroupId = asset.GroupId,
                        Kind = asset.Kind,
                        Workflow = asset.Workflow,
                        FileName = asset.FileName,
                        FilePath = _paths.ToRelativePath(
                            Path.IsPathRooted(asset.FilePath)
                                ? asset.FilePath
                                : Path.Combine(sessionDir, asset.FilePath.Replace('/', Path.DirectorySeparatorChar))),
                        PreviewPath = string.IsNullOrWhiteSpace(asset.PreviewPath) ? ""
                            : _paths.ToRelativePath(
                                Path.IsPathRooted(asset.PreviewPath)
                                    ? asset.PreviewPath
                                    : Path.Combine(sessionDir, asset.PreviewPath.Replace('/', Path.DirectorySeparatorChar))),
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
                    stats.AssetsImported++;
                }
                catch (Exception ex)
                {
                    stats.Errors++;
                    SqliteDebugLogger.LogImport($"  资产导入失败 {asset.AssetId}: {ex.Message}");
                }
            }
        }

        private void ImportAudioFiles(string sessionsPath, string batchId, LegacyImportStats stats)
        {
            try
            {
                var audioFiles = Directory.GetFiles(sessionsPath, "*.mp3")
                    .Concat(Directory.GetFiles(sessionsPath, "*.wav"));

                foreach (var file in audioFiles)
                {
                    try
                    {
                        var relativePath = _paths.ToRelativePath(file);
                        if (_audioRepo.GetByFilePath(relativePath) != null) { stats.Skipped++; continue; }

                        var info = new FileInfo(file);
                        _audioRepo.Upsert(new AudioItemRecord
                        {
                            Id = Guid.NewGuid().ToString("N")[..8],
                            FilePath = relativePath,
                            FileName = info.Name,
                            DirectoryPath = _paths.ToRelativePath(info.DirectoryName ?? ""),
                            FileSize = info.Length,
                            ProcessingState = "None",
                            CreatedAt = info.CreationTime,
                            UpdatedAt = info.LastWriteTime,
                        });
                        stats.AudioFilesImported++;
                    }
                    catch (Exception ex)
                    {
                        stats.Errors++;
                        SqliteDebugLogger.LogImport($"  音频导入失败 {file}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                SqliteDebugLogger.LogImport($"音频扫描失败: {ex.Message}");
            }
        }

        private static string InferMediaKind(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".mp4" or ".webm" or ".mov" => "video",
                ".mp3" or ".wav" or ".ogg" => "audio",
                _ => "image",
            };
        }
    }
}
