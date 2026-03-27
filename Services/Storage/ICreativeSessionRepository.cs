using System.Collections.Generic;

namespace TrueFluentPro.Services.Storage
{
    /// <summary>创作会话仓储：sessions 表的 CRUD 与分页查询。</summary>
    public interface ICreativeSessionRepository
    {
        void Upsert(SessionRecord record);
        SessionRecord? GetById(string id);
        List<SessionRecord> List(int limit = 30, int offset = 0, bool includeDeleted = false);
        int Count(bool includeDeleted = false);
        void SoftDelete(string id);
        void Restore(string id);
        void UpdateCounts(string id, int messageCount, int taskCount, int assetCount);
        void UpdateLastAccessed(string id);
    }
}
