using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace TrueFluentPro.Services.Storage
{
    public sealed class AudioLibraryRepository : IAudioLibraryRepository
    {
        private readonly ISqliteDbService _db;

        public AudioLibraryRepository(ISqliteDbService db) => _db = db;

        public void Upsert(AudioItemRecord r)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO audio_library_items (
    id, file_path, file_name, directory_path, file_size, duration_ms,
    processing_state, processing_badge_text, processing_detail_text,
    created_at, updated_at, is_deleted
) VALUES (
    @id, @fp, @fn, @dp, @fs, @dur,
    @ps, @pbt, @pdt,
    @cat, @uat, @del
) ON CONFLICT(id) DO UPDATE SET
    file_path=excluded.file_path, file_name=excluded.file_name,
    directory_path=excluded.directory_path, file_size=excluded.file_size,
    duration_ms=excluded.duration_ms, processing_state=excluded.processing_state,
    processing_badge_text=excluded.processing_badge_text,
    processing_detail_text=excluded.processing_detail_text,
    updated_at=excluded.updated_at, is_deleted=excluded.is_deleted;";

            cmd.Parameters.AddWithValue("@id", r.Id);
            cmd.Parameters.AddWithValue("@fp", r.FilePath);
            cmd.Parameters.AddWithValue("@fn", r.FileName);
            cmd.Parameters.AddWithValue("@dp", r.DirectoryPath);
            cmd.Parameters.AddWithValue("@fs", r.FileSize);
            cmd.Parameters.AddWithValue("@dur", Db.Val(r.DurationMs));
            cmd.Parameters.AddWithValue("@ps", r.ProcessingState);
            cmd.Parameters.AddWithValue("@pbt", Db.Val(r.ProcessingBadgeText));
            cmd.Parameters.AddWithValue("@pdt", Db.Val(r.ProcessingDetailText));
            cmd.Parameters.AddWithValue("@cat", Db.Ts(r.CreatedAt));
            cmd.Parameters.AddWithValue("@uat", Db.Ts(r.UpdatedAt));
            cmd.Parameters.AddWithValue("@del", Db.BoolInt(r.IsDeleted));
            cmd.ExecuteNonQuery();

            SqliteDebugLogger.LogWrite("audio_library_items", r.Id, "upsert");
        }

        public AudioItemRecord? GetById(string id)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM audio_library_items WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", id);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadRow(r) : null;
        }

        public AudioItemRecord? GetByFilePath(string filePath)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM audio_library_items WHERE file_path = @fp;";
            cmd.Parameters.AddWithValue("@fp", filePath);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadRow(r) : null;
        }

        public List<AudioItemRecord> List(int limit = 100, int offset = 0, bool includeDeleted = false)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = includeDeleted
                ? "SELECT * FROM audio_library_items ORDER BY updated_at DESC LIMIT @limit OFFSET @offset;"
                : "SELECT * FROM audio_library_items WHERE is_deleted = 0 ORDER BY updated_at DESC LIMIT @limit OFFSET @offset;";
            cmd.Parameters.AddWithValue("@limit", limit);
            cmd.Parameters.AddWithValue("@offset", offset);

            var list = new List<AudioItemRecord>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(ReadRow(r));
            SqliteDebugLogger.LogRead("audio_library_items", $"limit={limit} offset={offset}", list.Count);
            return list;
        }

        public List<AudioItemRecord> Search(string keyword, int limit = 100)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT * FROM audio_library_items
WHERE is_deleted = 0 AND file_name LIKE @kw
ORDER BY updated_at DESC LIMIT @limit;";
            cmd.Parameters.AddWithValue("@kw", $"%{keyword}%");
            cmd.Parameters.AddWithValue("@limit", limit);

            var list = new List<AudioItemRecord>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(ReadRow(r));
            SqliteDebugLogger.LogRead("audio_library_items", $"search='{keyword}'", list.Count);
            return list;
        }

        public int Count(bool includeDeleted = false)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = includeDeleted
                ? "SELECT COUNT(*) FROM audio_library_items;"
                : "SELECT COUNT(*) FROM audio_library_items WHERE is_deleted = 0;";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        public void SoftDelete(string id)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE audio_library_items SET is_deleted = 1, updated_at = @t WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@t", Db.Ts(DateTime.Now));
            cmd.ExecuteNonQuery();
            SqliteDebugLogger.LogWrite("audio_library_items", id, "soft-delete");
        }

        // ── 字幕 ──────────────────────────────────────

        public void UpsertSubtitle(SubtitleRecord r)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO subtitle_assets (id, audio_item_id, subtitle_type, file_path, file_name, cue_count, updated_at)
VALUES (@id, @aid, @type, @fp, @fn, @cue, @uat)
ON CONFLICT(id) DO UPDATE SET
    subtitle_type=excluded.subtitle_type, file_path=excluded.file_path,
    file_name=excluded.file_name, cue_count=excluded.cue_count, updated_at=excluded.updated_at;";
            cmd.Parameters.AddWithValue("@id", r.Id);
            cmd.Parameters.AddWithValue("@aid", r.AudioItemId);
            cmd.Parameters.AddWithValue("@type", r.SubtitleType);
            cmd.Parameters.AddWithValue("@fp", r.FilePath);
            cmd.Parameters.AddWithValue("@fn", r.FileName);
            cmd.Parameters.AddWithValue("@cue", Db.Val(r.CueCount));
            cmd.Parameters.AddWithValue("@uat", Db.Ts(r.UpdatedAt));
            cmd.ExecuteNonQuery();
            SqliteDebugLogger.LogWrite("subtitle_assets", r.Id, "upsert");
        }

        public List<SubtitleRecord> GetSubtitles(string audioItemId)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM subtitle_assets WHERE audio_item_id = @aid;";
            cmd.Parameters.AddWithValue("@aid", audioItemId);

            var list = new List<SubtitleRecord>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new SubtitleRecord
                {
                    Id = Db.Str(r["id"]),
                    AudioItemId = Db.Str(r["audio_item_id"]),
                    SubtitleType = Db.Str(r["subtitle_type"]),
                    FilePath = Db.Str(r["file_path"]),
                    FileName = Db.Str(r["file_name"]),
                    CueCount = Db.NInt(r["cue_count"]),
                    UpdatedAt = Db.Dt(r["updated_at"]),
                });
            }
            return list;
        }

        public void DeleteSubtitle(string id)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM subtitle_assets WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }

        // ── 内部读取 ──────────────────────────────────

        private static AudioItemRecord ReadRow(SqliteDataReader r)
        {
            return new AudioItemRecord
            {
                Id = Db.Str(r["id"]),
                FilePath = Db.Str(r["file_path"]),
                FileName = Db.Str(r["file_name"]),
                DirectoryPath = Db.Str(r["directory_path"]),
                FileSize = Db.Long(r["file_size"]),
                DurationMs = Db.NInt(r["duration_ms"]),
                ProcessingState = Db.Str(r["processing_state"]),
                ProcessingBadgeText = Db.NStr(r["processing_badge_text"]),
                ProcessingDetailText = Db.NStr(r["processing_detail_text"]),
                CreatedAt = Db.Dt(r["created_at"]),
                UpdatedAt = Db.Dt(r["updated_at"]),
                IsDeleted = Db.Bool(r["is_deleted"]),
            };
        }
    }
}
