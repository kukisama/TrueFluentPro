using System.Collections.Generic;

namespace TrueFluentPro.Services.Storage
{
    /// <summary>翻译历史仓储：translation_history 表。</summary>
    public interface ITranslationHistoryRepository
    {
        void Insert(TranslationRecord record);
        List<TranslationRecord> List(int limit = 50, int offset = 0);
        int Count();
        void SoftDelete(string id);
    }
}
