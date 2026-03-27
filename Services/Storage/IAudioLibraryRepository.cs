using System.Collections.Generic;

namespace TrueFluentPro.Services.Storage
{
    /// <summary>音频库仓储：audio_library_items + subtitle_assets。</summary>
    public interface IAudioLibraryRepository
    {
        void Upsert(AudioItemRecord record);
        AudioItemRecord? GetById(string id);
        AudioItemRecord? GetByFilePath(string filePath);
        List<AudioItemRecord> List(int limit = 100, int offset = 0, bool includeDeleted = false);
        List<AudioItemRecord> Search(string keyword, int limit = 100);
        int Count(bool includeDeleted = false);
        void SoftDelete(string id);

        // ── 字幕 ──
        void UpsertSubtitle(SubtitleRecord record);
        List<SubtitleRecord> GetSubtitles(string audioItemId);
        void DeleteSubtitle(string id);
    }
}
