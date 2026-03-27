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
    }
}
