using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace TrueFluentPro.Services.Storage
{
    /// <summary>
    /// 音频生命周期仓储：管理 audio_lifecycle 表，
    /// 跟踪每条音频各阶段的处理状态和缓存内容。
    /// </summary>
    public interface IAudioLifecycleRepository
    {
        void Upsert(AudioLifecycleRecord record);
        AudioLifecycleRecord? Get(string audioItemId, string stage);
        List<AudioLifecycleRecord> GetAllStages(string audioItemId);
        void Delete(string audioItemId, string stage);
        void DeleteAllStages(string audioItemId);
        void MarkStale(string audioItemId, string? stage = null);
    }

    public sealed class AudioLifecycleRepository : IAudioLifecycleRepository
    {
        private readonly ISqliteDbService _db;

        public AudioLifecycleRepository(ISqliteDbService db) => _db = db;

        public void Upsert(AudioLifecycleRecord r)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO audio_lifecycle (
    id, audio_item_id, stage, content_json, file_path, is_stale,
    generated_at, updated_at
) VALUES (
    @id, @aid, @stage, @cj, @fp, @stale,
    @gat, @uat
) ON CONFLICT(audio_item_id, stage) DO UPDATE SET
    content_json=excluded.content_json, file_path=excluded.file_path,
    is_stale=excluded.is_stale, generated_at=excluded.generated_at,
    updated_at=excluded.updated_at;";

            cmd.Parameters.AddWithValue("@id", r.Id);
            cmd.Parameters.AddWithValue("@aid", r.AudioItemId);
            cmd.Parameters.AddWithValue("@stage", r.Stage);
            cmd.Parameters.AddWithValue("@cj", Db.Val(r.ContentJson));
            cmd.Parameters.AddWithValue("@fp", Db.Val(r.FilePath));
            cmd.Parameters.AddWithValue("@stale", Db.BoolInt(r.IsStale));
            cmd.Parameters.AddWithValue("@gat", Db.Ts(r.GeneratedAt));
            cmd.Parameters.AddWithValue("@uat", Db.Ts(r.UpdatedAt));
            cmd.ExecuteNonQuery();

            SqliteDebugLogger.LogWrite("audio_lifecycle", r.Id, $"upsert stage={r.Stage}");
        }

        public AudioLifecycleRecord? Get(string audioItemId, string stage)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM audio_lifecycle WHERE audio_item_id = @aid AND stage = @s;";
            cmd.Parameters.AddWithValue("@aid", audioItemId);
            cmd.Parameters.AddWithValue("@s", stage);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadRow(r) : null;
        }

        public List<AudioLifecycleRecord> GetAllStages(string audioItemId)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM audio_lifecycle WHERE audio_item_id = @aid ORDER BY generated_at;";
            cmd.Parameters.AddWithValue("@aid", audioItemId);

            var list = new List<AudioLifecycleRecord>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(ReadRow(r));
            return list;
        }

        public void Delete(string audioItemId, string stage)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM audio_lifecycle WHERE audio_item_id = @aid AND stage = @s;";
            cmd.Parameters.AddWithValue("@aid", audioItemId);
            cmd.Parameters.AddWithValue("@s", stage);
            cmd.ExecuteNonQuery();
        }

        public void DeleteAllStages(string audioItemId)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM audio_lifecycle WHERE audio_item_id = @aid;";
            cmd.Parameters.AddWithValue("@aid", audioItemId);
            cmd.ExecuteNonQuery();
        }

        public void MarkStale(string audioItemId, string? stage = null)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            if (stage != null)
            {
                cmd.CommandText = "UPDATE audio_lifecycle SET is_stale = 1, updated_at = @t WHERE audio_item_id = @aid AND stage = @s;";
                cmd.Parameters.AddWithValue("@s", stage);
            }
            else
            {
                cmd.CommandText = "UPDATE audio_lifecycle SET is_stale = 1, updated_at = @t WHERE audio_item_id = @aid;";
            }
            cmd.Parameters.AddWithValue("@aid", audioItemId);
            cmd.Parameters.AddWithValue("@t", Db.Ts(DateTime.Now));
            cmd.ExecuteNonQuery();
        }

        private static AudioLifecycleRecord ReadRow(SqliteDataReader r) => new()
        {
            Id = Db.Str(r["id"]),
            AudioItemId = Db.Str(r["audio_item_id"]),
            Stage = Db.Str(r["stage"]),
            ContentJson = Db.NStr(r["content_json"]),
            FilePath = Db.NStr(r["file_path"]),
            IsStale = Db.Bool(r["is_stale"]),
            GeneratedAt = Db.Dt(r["generated_at"]),
            UpdatedAt = Db.Dt(r["updated_at"]),
        };
    }
}
