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

        /// <summary>消息内容类型：text / image / video</summary>
        public string ContentType { get; set; } = "";

        /// <summary>推理/思考过程文本</summary>
        public string ReasoningText { get; set; } = "";

        /// <summary>服务端生成耗时（秒）</summary>
        public double? GenerateSeconds { get; set; }
        /// <summary>下载传输耗时（秒）</summary>
        public double? DownloadSeconds { get; set; }

        /// <summary>Token 用量：输入 Token 数</summary>
        public int? PromptTokens { get; set; }
        /// <summary>Token 用量：输出 Token 数</summary>
        public int? CompletionTokens { get; set; }

        /// <summary>搜索引用来源（JSON 序列化）</summary>
        public List<MediaChatCitation>? Citations { get; set; }

        /// <summary>搜索过程摘要</summary>
        public string SearchSummary { get; set; } = "";

        /// <summary>随消息提交的附件（图片/文本文件）</summary>
        public List<ChatAttachmentInfo>? Attachments { get; set; }
    }

    /// <summary>搜索引用来源（持久化用）</summary>
    public class MediaChatCitation
    {
        public int Number { get; set; }
        public string Title { get; set; } = "";
        public string Url { get; set; } = "";
        public string Snippet { get; set; } = "";
        public string Hostname { get; set; } = "";
    }

    /// <summary>消息附件元数据（持久化用）</summary>
    public class ChatAttachmentInfo
    {
        /// <summary>image / text</summary>
        public string Type { get; set; } = "text";
        /// <summary>显示用文件名</summary>
        public string FileName { get; set; } = "";
        /// <summary>本地文件路径（存储相对路径）</summary>
        public string FilePath { get; set; } = "";
        /// <summary>文件大小（字节）</summary>
        public long FileSize { get; set; }
    }

    /// <summary>
    /// 快捷短语
    /// </summary>
    public class QuickPhrase
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public string Title { get; set; } = "";
        public string Content { get; set; } = "";
        public int SortOrder { get; set; } = 0;
    }
}
