using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace TrueFluentPro.Services.Storage
{
    public sealed class CreativeSessionRepository : ICreativeSessionRepository
    {
        private readonly ISqliteDbService _db;

        public CreativeSessionRepository(ISqliteDbService db) => _db = db;

        public void Upsert(SessionRecord r)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO sessions (
    id, session_type, name, directory_path, canvas_mode, media_kind,
    is_deleted, created_at, updated_at, last_accessed_at,
    source_session_id, source_session_name, source_session_directory_name,
    source_asset_id, source_asset_kind, source_asset_file_name,
    source_asset_path, source_preview_path, source_reference_role,
    message_count, task_count, asset_count, latest_message_preview,
    legacy_source_path, import_batch_id, imported_at, is_legacy_import
) VALUES (
    @id, @session_type, @name, @directory_path, @canvas_mode, @media_kind,
    @is_deleted, @created_at, @updated_at, @last_accessed_at,
    @source_session_id, @source_session_name, @source_session_directory_name,
    @source_asset_id, @source_asset_kind, @source_asset_file_name,
    @source_asset_path, @source_preview_path, @source_reference_role,
    @message_count, @task_count, @asset_count, @latest_message_preview,
    @legacy_source_path, @import_batch_id, @imported_at, @is_legacy_import
) ON CONFLICT(id) DO UPDATE SET
    session_type=excluded.session_type, name=excluded.name,
    directory_path=excluded.directory_path, canvas_mode=excluded.canvas_mode,
    media_kind=excluded.media_kind, is_deleted=excluded.is_deleted,
    updated_at=excluded.updated_at, last_accessed_at=excluded.last_accessed_at,
    source_session_id=excluded.source_session_id, source_session_name=excluded.source_session_name,
    source_session_directory_name=excluded.source_session_directory_name,
    source_asset_id=excluded.source_asset_id, source_asset_kind=excluded.source_asset_kind,
    source_asset_file_name=excluded.source_asset_file_name,
    source_asset_path=excluded.source_asset_path, source_preview_path=excluded.source_preview_path,
    source_reference_role=excluded.source_reference_role,
    message_count=excluded.message_count, task_count=excluded.task_count,
    asset_count=excluded.asset_count, latest_message_preview=excluded.latest_message_preview,
    legacy_source_path=excluded.legacy_source_path, import_batch_id=excluded.import_batch_id,
    imported_at=excluded.imported_at, is_legacy_import=excluded.is_legacy_import;";

            cmd.Parameters.AddWithValue("@id", r.Id);
            cmd.Parameters.AddWithValue("@session_type", r.SessionType);
            cmd.Parameters.AddWithValue("@name", r.Name);
            cmd.Parameters.AddWithValue("@directory_path", r.DirectoryPath);
            cmd.Parameters.AddWithValue("@canvas_mode", r.CanvasMode);
            cmd.Parameters.AddWithValue("@media_kind", r.MediaKind);
            cmd.Parameters.AddWithValue("@is_deleted", Db.BoolInt(r.IsDeleted));
            cmd.Parameters.AddWithValue("@created_at", Db.Ts(r.CreatedAt));
            cmd.Parameters.AddWithValue("@updated_at", Db.Ts(r.UpdatedAt));
            cmd.Parameters.AddWithValue("@last_accessed_at", Db.Val(r.LastAccessedAt));
            cmd.Parameters.AddWithValue("@source_session_id", Db.Val(r.SourceSessionId));
            cmd.Parameters.AddWithValue("@source_session_name", Db.Val(r.SourceSessionName));
            cmd.Parameters.AddWithValue("@source_session_directory_name", Db.Val(r.SourceSessionDirectoryName));
            cmd.Parameters.AddWithValue("@source_asset_id", Db.Val(r.SourceAssetId));
            cmd.Parameters.AddWithValue("@source_asset_kind", Db.Val(r.SourceAssetKind));
            cmd.Parameters.AddWithValue("@source_asset_file_name", Db.Val(r.SourceAssetFileName));
            cmd.Parameters.AddWithValue("@source_asset_path", Db.Val(r.SourceAssetPath));
            cmd.Parameters.AddWithValue("@source_preview_path", Db.Val(r.SourcePreviewPath));
            cmd.Parameters.AddWithValue("@source_reference_role", Db.Val(r.SourceReferenceRole));
            cmd.Parameters.AddWithValue("@message_count", r.MessageCount);
            cmd.Parameters.AddWithValue("@task_count", r.TaskCount);
            cmd.Parameters.AddWithValue("@asset_count", r.AssetCount);
            cmd.Parameters.AddWithValue("@latest_message_preview", Db.Val(r.LatestMessagePreview));
            cmd.Parameters.AddWithValue("@legacy_source_path", Db.Val(r.LegacySourcePath));
            cmd.Parameters.AddWithValue("@import_batch_id", Db.Val(r.ImportBatchId));
            cmd.Parameters.AddWithValue("@imported_at", Db.Val(r.ImportedAt));
            cmd.Parameters.AddWithValue("@is_legacy_import", Db.BoolInt(r.IsLegacyImport));
            cmd.ExecuteNonQuery();

            SqliteDebugLogger.LogWrite("sessions", r.Id, "upsert");
        }

        public SessionRecord? GetById(string id)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM sessions WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", id);

            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            SqliteDebugLogger.LogRead("sessions", $"id={id}", 1);
            return ReadRow(r);
        }

        public List<SessionRecord> List(int limit = 30, int offset = 0, bool includeDeleted = false)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = includeDeleted
                ? "SELECT * FROM sessions ORDER BY updated_at DESC LIMIT @limit OFFSET @offset;"
                : "SELECT * FROM sessions WHERE is_deleted = 0 ORDER BY updated_at DESC LIMIT @limit OFFSET @offset;";
            cmd.Parameters.AddWithValue("@limit", limit);
            cmd.Parameters.AddWithValue("@offset", offset);

            var list = new List<SessionRecord>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(ReadRow(r));

            SqliteDebugLogger.LogRead("sessions", $"limit={limit} offset={offset}", list.Count);
            return list;
        }

        public int Count(bool includeDeleted = false)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = includeDeleted
                ? "SELECT COUNT(*) FROM sessions;"
                : "SELECT COUNT(*) FROM sessions WHERE is_deleted = 0;";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        public void SoftDelete(string id)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE sessions SET is_deleted = 1, updated_at = @t WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@t", Db.Ts(DateTime.Now));
            cmd.ExecuteNonQuery();
            SqliteDebugLogger.LogWrite("sessions", id, "soft-delete");
        }

        public void Restore(string id)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE sessions SET is_deleted = 0, updated_at = @t WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@t", Db.Ts(DateTime.Now));
            cmd.ExecuteNonQuery();
            SqliteDebugLogger.LogWrite("sessions", id, "restore");
        }

        public void UpdateCounts(string id, int messageCount, int taskCount, int assetCount)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
UPDATE sessions SET message_count = @mc, task_count = @tc, asset_count = @ac, updated_at = @t
WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@mc", messageCount);
            cmd.Parameters.AddWithValue("@tc", taskCount);
            cmd.Parameters.AddWithValue("@ac", assetCount);
            cmd.Parameters.AddWithValue("@t", Db.Ts(DateTime.Now));
            cmd.ExecuteNonQuery();
            SqliteDebugLogger.LogWrite("sessions", id, $"update-counts msg={messageCount} task={taskCount} asset={assetCount}");
        }

        public void UpdateLastAccessed(string id)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE sessions SET last_accessed_at = @t WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@t", Db.Ts(DateTime.Now));
            cmd.ExecuteNonQuery();
        }

        private static SessionRecord ReadRow(SqliteDataReader r)
        {
            return new SessionRecord
            {
                Id = Db.Str(r["id"]),
                SessionType = Db.Str(r["session_type"]),
                Name = Db.Str(r["name"]),
                DirectoryPath = Db.Str(r["directory_path"]),
                CanvasMode = Db.Str(r["canvas_mode"]),
                MediaKind = Db.Str(r["media_kind"]),
                IsDeleted = Db.Bool(r["is_deleted"]),
                CreatedAt = Db.Dt(r["created_at"]),
                UpdatedAt = Db.Dt(r["updated_at"]),
                LastAccessedAt = Db.NDt(r["last_accessed_at"]),
                SourceSessionId = Db.NStr(r["source_session_id"]),
                SourceSessionName = Db.NStr(r["source_session_name"]),
                SourceSessionDirectoryName = Db.NStr(r["source_session_directory_name"]),
                SourceAssetId = Db.NStr(r["source_asset_id"]),
                SourceAssetKind = Db.NStr(r["source_asset_kind"]),
                SourceAssetFileName = Db.NStr(r["source_asset_file_name"]),
                SourceAssetPath = Db.NStr(r["source_asset_path"]),
                SourcePreviewPath = Db.NStr(r["source_preview_path"]),
                SourceReferenceRole = Db.NStr(r["source_reference_role"]),
                MessageCount = Db.Int(r["message_count"]),
                TaskCount = Db.Int(r["task_count"]),
                AssetCount = Db.Int(r["asset_count"]),
                LatestMessagePreview = Db.NStr(r["latest_message_preview"]),
                LegacySourcePath = Db.NStr(r["legacy_source_path"]),
                ImportBatchId = Db.NStr(r["import_batch_id"]),
                ImportedAt = Db.NDt(r["imported_at"]),
                IsLegacyImport = Db.Bool(r["is_legacy_import"]),
            };
        }
    }
}
