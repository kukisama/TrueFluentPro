using System.Collections.Generic;

namespace TrueFluentPro.Services.Storage
{
    /// <summary>
    /// 会话消息仓储：session_messages + message_media_refs + message_citations。
    /// 支持窗口化懒加载。
    /// </summary>
    public interface ISessionMessageRepository
    {
        void Insert(MessageRecord record);
        void Update(MessageRecord record);
        MessageRecord? GetById(string id);
        List<MessageRecord> GetLatest(string sessionId, int limit = 40);
        List<MessageRecord> GetBefore(string sessionId, int beforeSequence, int limit = 40);
        int GetCount(string sessionId);

        /// <summary>
        /// 一次查询获取会话的全部消息及其关联的媒体引用、引文、附件，消除 N+1 查询。
        /// </summary>
        SessionMessagesBundle GetSessionBundle(string sessionId);
        int GetMaxSequence(string sessionId);
        void SoftDelete(string id);

        // ── 媒体引用 ──
        void InsertMediaRefs(string messageId, List<MediaRefRecord> refs);
        List<MediaRefRecord> GetMediaRefs(string messageId);
        void DeleteMediaRefs(string messageId);

        // ── 网页引用 ──
        void InsertCitations(string messageId, List<CitationRecord> citations);
        List<CitationRecord> GetCitations(string messageId);
        void DeleteCitations(string messageId);

        // ── 消息附件 ──
        void InsertAttachments(string messageId, List<AttachmentRecord> attachments);
        List<AttachmentRecord> GetAttachments(string messageId);
        void DeleteAttachments(string messageId);
    }

    /// <summary>批量加载结果：消息 + 关联数据已预填充到字典。</summary>
    public sealed class SessionMessagesBundle
    {
        public List<MessageRecord> Messages { get; init; } = new();
        public Dictionary<string, List<MediaRefRecord>> MediaRefs { get; init; } = new();
        public Dictionary<string, List<CitationRecord>> Citations { get; init; } = new();
        public Dictionary<string, List<AttachmentRecord>> Attachments { get; init; } = new();
    }
}
