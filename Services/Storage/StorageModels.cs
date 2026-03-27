using System;

namespace TrueFluentPro.Services.Storage
{
    /// <summary>会话记录（对应 sessions 表）</summary>
    public class SessionRecord
    {
        public string Id { get; set; } = "";
        public string SessionType { get; set; } = "";
        public string Name { get; set; } = "";
        public string DirectoryPath { get; set; } = "";
        public string CanvasMode { get; set; } = "";
        public string MediaKind { get; set; } = "";
        public bool IsDeleted { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? LastAccessedAt { get; set; }
        public string? SourceSessionId { get; set; }
        public string? SourceSessionName { get; set; }
        public string? SourceSessionDirectoryName { get; set; }
        public string? SourceAssetId { get; set; }
        public string? SourceAssetKind { get; set; }
        public string? SourceAssetFileName { get; set; }
        public string? SourceAssetPath { get; set; }
        public string? SourcePreviewPath { get; set; }
        public string? SourceReferenceRole { get; set; }
        public int MessageCount { get; set; }
        public int TaskCount { get; set; }
        public int AssetCount { get; set; }
        public string? LatestMessagePreview { get; set; }
        public string? LegacySourcePath { get; set; }
        public string? ImportBatchId { get; set; }
        public DateTime? ImportedAt { get; set; }
        public bool IsLegacyImport { get; set; }
    }

    /// <summary>消息记录（对应 session_messages 表）</summary>
    public class MessageRecord
    {
        public string Id { get; set; } = "";
        public string SessionId { get; set; } = "";
        public int SequenceNo { get; set; }
        public string Role { get; set; } = "";
        public string ContentType { get; set; } = "";
        public string Text { get; set; } = "";
        public string ReasoningText { get; set; } = "";
        public int? PromptTokens { get; set; }
        public int? CompletionTokens { get; set; }
        public double? GenerateSeconds { get; set; }
        public double? DownloadSeconds { get; set; }
        public string? SearchSummary { get; set; }
        public DateTime Timestamp { get; set; }
        public bool IsDeleted { get; set; }
    }

    /// <summary>消息媒体引用（对应 message_media_refs 表）</summary>
    public class MediaRefRecord
    {
        public long Id { get; set; }
        public string MessageId { get; set; } = "";
        public string MediaPath { get; set; } = "";
        public string MediaKind { get; set; } = "";
        public int SortOrder { get; set; }
        public string? PreviewPath { get; set; }
    }

    /// <summary>消息引用（对应 message_citations 表）</summary>
    public class CitationRecord
    {
        public long Id { get; set; }
        public string MessageId { get; set; } = "";
        public int CitationNumber { get; set; }
        public string Title { get; set; } = "";
        public string Url { get; set; } = "";
        public string Snippet { get; set; } = "";
        public string Hostname { get; set; } = "";
    }

    /// <summary>会话任务（对应 session_tasks 表）</summary>
    public class TaskRecord
    {
        public string Id { get; set; } = "";
        public string SessionId { get; set; } = "";
        public string TaskType { get; set; } = "";
        public string Status { get; set; } = "";
        public string Prompt { get; set; } = "";
        public double Progress { get; set; }
        public string? ResultFilePath { get; set; }
        public string? ErrorMessage { get; set; }
        public bool HasReferenceInput { get; set; }
        public string? RemoteVideoId { get; set; }
        public string? RemoteVideoApiMode { get; set; }
        public string? RemoteGenerationId { get; set; }
        public string? RemoteDownloadUrl { get; set; }
        public double? GenerateSeconds { get; set; }
        public double? DownloadSeconds { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    /// <summary>会话资产（对应 session_assets 表）</summary>
    public class AssetRecord
    {
        public string AssetId { get; set; } = "";
        public string SessionId { get; set; } = "";
        public string GroupId { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Workflow { get; set; } = "";
        public string FileName { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string PreviewPath { get; set; } = "";
        public string PromptText { get; set; } = "";
        public long? FileSize { get; set; }
        public string? MimeType { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public int? DurationMs { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime ModifiedAt { get; set; }
        public string StorageScope { get; set; } = "workspace-relative";
        public string? DerivedFromSessionId { get; set; }
        public string? DerivedFromSessionName { get; set; }
        public string? DerivedFromAssetId { get; set; }
        public string? DerivedFromAssetFileName { get; set; }
        public string? DerivedFromAssetKind { get; set; }
        public string? DerivedFromReferenceRole { get; set; }
    }

    /// <summary>参考图记录（对应 reference_images 表）</summary>
    public class ReferenceImageRecord
    {
        public string Id { get; set; } = "";
        public string SessionId { get; set; } = "";
        public string FilePath { get; set; } = "";
        public int SortOrder { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>音频库项目（对应 audio_library_items 表）</summary>
    public class AudioItemRecord
    {
        public string Id { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string FileName { get; set; } = "";
        public string DirectoryPath { get; set; } = "";
        public long FileSize { get; set; }
        public int? DurationMs { get; set; }
        public string ProcessingState { get; set; } = "None";
        public string? ProcessingBadgeText { get; set; }
        public string? ProcessingDetailText { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public bool IsDeleted { get; set; }
    }

    /// <summary>字幕资产（对应 subtitle_assets 表）</summary>
    public class SubtitleRecord
    {
        public string Id { get; set; } = "";
        public string AudioItemId { get; set; } = "";
        public string SubtitleType { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string FileName { get; set; } = "";
        public int? CueCount { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    /// <summary>翻译历史（对应 translation_history 表）</summary>
    public class TranslationRecord
    {
        public string Id { get; set; } = "";
        public string SourceText { get; set; } = "";
        public string TranslatedText { get; set; } = "";
        public string? SourceLanguage { get; set; }
        public string? TargetLanguage { get; set; }
        public DateTime CreatedAt { get; set; }
        public bool IsDeleted { get; set; }
    }
}
