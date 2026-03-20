using System;
using System.Collections.Generic;

namespace TrueFluentPro.Models
{
    /// <summary>
    /// 会话模型（包含聊天记录和任务列表）
    /// </summary>
    public class MediaGenSession
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public string Name { get; set; } = "新会话";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public bool IsDeleted { get; set; } = false;
        public string CanvasMode { get; set; } = "Draw";
        public string MediaKind { get; set; } = "Image";
        public MediaSessionSourceInfo? Source { get; set; }
        public List<MediaChatMessage> Messages { get; set; } = new();
        public List<MediaGenTask> Tasks { get; set; } = new();
        public List<MediaAssetRecord> Assets { get; set; } = new();
    }

    /// <summary>
    /// 记录当前会话是从哪个会话/资产分叉而来。
    /// </summary>
    public class MediaSessionSourceInfo
    {
        public string SourceSessionId { get; set; } = string.Empty;
        public string SourceSessionName { get; set; } = string.Empty;
        public string SourceSessionDirectoryName { get; set; } = string.Empty;
        public string SourceAssetId { get; set; } = string.Empty;
        public string SourceAssetKind { get; set; } = string.Empty;
        public string SourceAssetFileName { get; set; } = string.Empty;
        public string SourceAssetPath { get; set; } = string.Empty;
        public string SourcePreviewPath { get; set; } = string.Empty;
        public string ReferenceRole { get; set; } = string.Empty;
    }

    /// <summary>
    /// 会话内显式资产目录，供后续做血缘、媒体库与跨会话引用。
    /// </summary>
    public class MediaAssetRecord
    {
        public string AssetId { get; set; } = string.Empty;
        public string GroupId { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string Workflow { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public string PreviewPath { get; set; } = string.Empty;
        public string PromptText { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime ModifiedAt { get; set; }
        public string DerivedFromSessionId { get; set; } = string.Empty;
        public string DerivedFromSessionName { get; set; } = string.Empty;
        public string DerivedFromAssetId { get; set; } = string.Empty;
        public string DerivedFromAssetFileName { get; set; } = string.Empty;
        public string DerivedFromAssetKind { get; set; } = string.Empty;
        public string DerivedFromReferenceRole { get; set; } = string.Empty;
    }

    /// <summary>
    /// 聊天消息
    /// </summary>
    public class MediaChatMessage
    {
        public string Role { get; set; } = "user";  // user / assistant / system
        public string Text { get; set; } = "";
        public List<string> MediaPaths { get; set; } = new();
        public DateTime Timestamp { get; set; } = DateTime.Now;

        /// <summary>服务端生成耗时（秒）</summary>
        public double? GenerateSeconds { get; set; }
        /// <summary>下载传输耗时（秒）</summary>
        public double? DownloadSeconds { get; set; }
    }
}
