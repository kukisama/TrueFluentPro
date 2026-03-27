using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace TrueFluentPro.Services.Storage
{
    public sealed class SessionContentRepository : ISessionContentRepository
    {
        private readonly ISqliteDbService _db;

        public SessionContentRepository(ISqliteDbService db) => _db = db;

        // ═══ 任务 ═══════════════════════════════════════════

        public void UpsertTask(TaskRecord r)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO session_tasks (
    id, session_id, task_type, status, prompt, progress,
    result_file_path, error_message, has_reference_input,
    remote_video_id, remote_video_api_mode, remote_generation_id, remote_download_url,
    generate_seconds, download_seconds, created_at, updated_at
) VALUES (
    @id, @sid, @type, @status, @prompt, @progress,
    @result, @error, @has_ref,
    @rvid, @rvmode, @rgen, @rdl,
    @gsec, @dsec, @cat, @uat
) ON CONFLICT(id) DO UPDATE SET
    status=excluded.status, prompt=excluded.prompt, progress=excluded.progress,
    result_file_path=excluded.result_file_path, error_message=excluded.error_message,
    has_reference_input=excluded.has_reference_input,
    remote_video_id=excluded.remote_video_id, remote_video_api_mode=excluded.remote_video_api_mode,
    remote_generation_id=excluded.remote_generation_id, remote_download_url=excluded.remote_download_url,
    generate_seconds=excluded.generate_seconds, download_seconds=excluded.download_seconds,
    updated_at=excluded.updated_at;";

            cmd.Parameters.AddWithValue("@id", r.Id);
            cmd.Parameters.AddWithValue("@sid", r.SessionId);
            cmd.Parameters.AddWithValue("@type", r.TaskType);
            cmd.Parameters.AddWithValue("@status", r.Status);
            cmd.Parameters.AddWithValue("@prompt", r.Prompt);
            cmd.Parameters.AddWithValue("@progress", r.Progress);
            cmd.Parameters.AddWithValue("@result", Db.Val(r.ResultFilePath));
            cmd.Parameters.AddWithValue("@error", Db.Val(r.ErrorMessage));
            cmd.Parameters.AddWithValue("@has_ref", Db.BoolInt(r.HasReferenceInput));
            cmd.Parameters.AddWithValue("@rvid", Db.Val(r.RemoteVideoId));
            cmd.Parameters.AddWithValue("@rvmode", Db.Val(r.RemoteVideoApiMode));
            cmd.Parameters.AddWithValue("@rgen", Db.Val(r.RemoteGenerationId));
            cmd.Parameters.AddWithValue("@rdl", Db.Val(r.RemoteDownloadUrl));
            cmd.Parameters.AddWithValue("@gsec", Db.Val(r.GenerateSeconds));
            cmd.Parameters.AddWithValue("@dsec", Db.Val(r.DownloadSeconds));
            cmd.Parameters.AddWithValue("@cat", Db.Ts(r.CreatedAt));
            cmd.Parameters.AddWithValue("@uat", Db.Ts(r.UpdatedAt));
            cmd.ExecuteNonQuery();

            SqliteDebugLogger.LogWrite("session_tasks", r.Id, $"upsert status={r.Status}");
        }

        public TaskRecord? GetTaskById(string id)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM session_tasks WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", id);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadTask(r) : null;
        }

        public List<TaskRecord> GetSessionTasks(string sessionId)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM session_tasks WHERE session_id = @sid ORDER BY created_at DESC;";
            cmd.Parameters.AddWithValue("@sid", sessionId);

            var list = new List<TaskRecord>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(ReadTask(r));
            SqliteDebugLogger.LogRead("session_tasks", $"session={sessionId}", list.Count);
            return list;
        }

        public void UpdateTaskStatus(string id, string status, double progress, string? errorMessage)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
UPDATE session_tasks SET status=@status, progress=@progress, error_message=@error, updated_at=@t
WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@status", status);
            cmd.Parameters.AddWithValue("@progress", progress);
            cmd.Parameters.AddWithValue("@error", Db.Val(errorMessage));
            cmd.Parameters.AddWithValue("@t", Db.Ts(DateTime.Now));
            cmd.ExecuteNonQuery();
            SqliteDebugLogger.LogWrite("session_tasks", id, $"status→{status}");
        }

        // ═══ 资产 ═══════════════════════════════════════════

        public void UpsertAsset(AssetRecord r)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO session_assets (
    asset_id, session_id, group_id, kind, workflow, file_name, file_path,
    preview_path, prompt_text, file_size, mime_type, width, height, duration_ms,
    created_at, modified_at, storage_scope,
    derived_from_session_id, derived_from_session_name,
    derived_from_asset_id, derived_from_asset_file_name,
    derived_from_asset_kind, derived_from_reference_role
) VALUES (
    @aid, @sid, @gid, @kind, @wf, @fname, @fpath,
    @ppath, @prompt, @fsize, @mime, @w, @h, @dur,
    @cat, @mat, @scope,
    @df_sid, @df_sname,
    @df_aid, @df_afname,
    @df_akind, @df_role
) ON CONFLICT(asset_id) DO UPDATE SET
    group_id=excluded.group_id, kind=excluded.kind, workflow=excluded.workflow,
    file_name=excluded.file_name, file_path=excluded.file_path,
    preview_path=excluded.preview_path, prompt_text=excluded.prompt_text,
    file_size=excluded.file_size, mime_type=excluded.mime_type,
    width=excluded.width, height=excluded.height, duration_ms=excluded.duration_ms,
    modified_at=excluded.modified_at, storage_scope=excluded.storage_scope,
    derived_from_session_id=excluded.derived_from_session_id,
    derived_from_session_name=excluded.derived_from_session_name,
    derived_from_asset_id=excluded.derived_from_asset_id,
    derived_from_asset_file_name=excluded.derived_from_asset_file_name,
    derived_from_asset_kind=excluded.derived_from_asset_kind,
    derived_from_reference_role=excluded.derived_from_reference_role;";

            cmd.Parameters.AddWithValue("@aid", r.AssetId);
            cmd.Parameters.AddWithValue("@sid", r.SessionId);
            cmd.Parameters.AddWithValue("@gid", r.GroupId);
            cmd.Parameters.AddWithValue("@kind", r.Kind);
            cmd.Parameters.AddWithValue("@wf", r.Workflow);
            cmd.Parameters.AddWithValue("@fname", r.FileName);
            cmd.Parameters.AddWithValue("@fpath", r.FilePath);
            cmd.Parameters.AddWithValue("@ppath", r.PreviewPath);
            cmd.Parameters.AddWithValue("@prompt", r.PromptText);
            cmd.Parameters.AddWithValue("@fsize", Db.Val(r.FileSize));
            cmd.Parameters.AddWithValue("@mime", Db.Val(r.MimeType));
            cmd.Parameters.AddWithValue("@w", Db.Val(r.Width));
            cmd.Parameters.AddWithValue("@h", Db.Val(r.Height));
            cmd.Parameters.AddWithValue("@dur", Db.Val(r.DurationMs));
            cmd.Parameters.AddWithValue("@cat", Db.Ts(r.CreatedAt));
            cmd.Parameters.AddWithValue("@mat", Db.Ts(r.ModifiedAt));
            cmd.Parameters.AddWithValue("@scope", r.StorageScope);
            cmd.Parameters.AddWithValue("@df_sid", Db.Val(r.DerivedFromSessionId));
            cmd.Parameters.AddWithValue("@df_sname", Db.Val(r.DerivedFromSessionName));
            cmd.Parameters.AddWithValue("@df_aid", Db.Val(r.DerivedFromAssetId));
            cmd.Parameters.AddWithValue("@df_afname", Db.Val(r.DerivedFromAssetFileName));
            cmd.Parameters.AddWithValue("@df_akind", Db.Val(r.DerivedFromAssetKind));
            cmd.Parameters.AddWithValue("@df_role", Db.Val(r.DerivedFromReferenceRole));
            cmd.ExecuteNonQuery();

            SqliteDebugLogger.LogWrite("session_assets", r.AssetId, $"upsert kind={r.Kind}");
        }

        public AssetRecord? GetAssetById(string assetId)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM session_assets WHERE asset_id = @id;";
            cmd.Parameters.AddWithValue("@id", assetId);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadAsset(r) : null;
        }

        public List<AssetRecord> GetSessionAssets(string sessionId, int limit = 20, int offset = 0)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT * FROM session_assets WHERE session_id = @sid
ORDER BY created_at DESC LIMIT @limit OFFSET @offset;";
            cmd.Parameters.AddWithValue("@sid", sessionId);
            cmd.Parameters.AddWithValue("@limit", limit);
            cmd.Parameters.AddWithValue("@offset", offset);

            var list = new List<AssetRecord>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(ReadAsset(r));
            SqliteDebugLogger.LogRead("session_assets", $"session={sessionId} limit={limit}", list.Count);
            return list;
        }

        public int GetSessionAssetCount(string sessionId)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM session_assets WHERE session_id = @sid;";
            cmd.Parameters.AddWithValue("@sid", sessionId);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        public void DeleteAsset(string assetId)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM session_assets WHERE asset_id = @id;";
            cmd.Parameters.AddWithValue("@id", assetId);
            cmd.ExecuteNonQuery();
            SqliteDebugLogger.LogWrite("session_assets", assetId, "delete");
        }

        // ═══ 参考图 ═══════════════════════════════════════════

        public void UpsertReferenceImage(ReferenceImageRecord r)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO reference_images (id, session_id, file_path, sort_order, width, height, created_at)
VALUES (@id, @sid, @fp, @sort, @w, @h, @cat)
ON CONFLICT(id) DO UPDATE SET
    file_path=excluded.file_path, sort_order=excluded.sort_order,
    width=excluded.width, height=excluded.height;";
            cmd.Parameters.AddWithValue("@id", r.Id);
            cmd.Parameters.AddWithValue("@sid", r.SessionId);
            cmd.Parameters.AddWithValue("@fp", r.FilePath);
            cmd.Parameters.AddWithValue("@sort", r.SortOrder);
            cmd.Parameters.AddWithValue("@w", Db.Val(r.Width));
            cmd.Parameters.AddWithValue("@h", Db.Val(r.Height));
            cmd.Parameters.AddWithValue("@cat", Db.Ts(r.CreatedAt));
            cmd.ExecuteNonQuery();
            SqliteDebugLogger.LogWrite("reference_images", r.Id, "upsert");
        }

        public List<ReferenceImageRecord> GetSessionReferenceImages(string sessionId)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM reference_images WHERE session_id = @sid ORDER BY sort_order;";
            cmd.Parameters.AddWithValue("@sid", sessionId);

            var list = new List<ReferenceImageRecord>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new ReferenceImageRecord
                {
                    Id = Db.Str(r["id"]),
                    SessionId = Db.Str(r["session_id"]),
                    FilePath = Db.Str(r["file_path"]),
                    SortOrder = Db.Int(r["sort_order"]),
                    Width = Db.NInt(r["width"]),
                    Height = Db.NInt(r["height"]),
                    CreatedAt = Db.Dt(r["created_at"]),
                });
            }
            return list;
        }

        public void DeleteReferenceImage(string id)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM reference_images WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }

        public void ClearSessionReferenceImages(string sessionId)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM reference_images WHERE session_id = @sid;";
            cmd.Parameters.AddWithValue("@sid", sessionId);
            cmd.ExecuteNonQuery();
            SqliteDebugLogger.LogWrite("reference_images", sessionId, "clear-all");
        }

        // ═══ 内部读取 ═══════════════════════════════════════

        private static TaskRecord ReadTask(SqliteDataReader r)
        {
            return new TaskRecord
            {
                Id = Db.Str(r["id"]),
                SessionId = Db.Str(r["session_id"]),
                TaskType = Db.Str(r["task_type"]),
                Status = Db.Str(r["status"]),
                Prompt = Db.Str(r["prompt"]),
                Progress = Db.Dbl(r["progress"]),
                ResultFilePath = Db.NStr(r["result_file_path"]),
                ErrorMessage = Db.NStr(r["error_message"]),
                HasReferenceInput = Db.Bool(r["has_reference_input"]),
                RemoteVideoId = Db.NStr(r["remote_video_id"]),
                RemoteVideoApiMode = Db.NStr(r["remote_video_api_mode"]),
                RemoteGenerationId = Db.NStr(r["remote_generation_id"]),
                RemoteDownloadUrl = Db.NStr(r["remote_download_url"]),
                GenerateSeconds = Db.NDbl(r["generate_seconds"]),
                DownloadSeconds = Db.NDbl(r["download_seconds"]),
                CreatedAt = Db.Dt(r["created_at"]),
                UpdatedAt = Db.Dt(r["updated_at"]),
            };
        }

        private static AssetRecord ReadAsset(SqliteDataReader r)
        {
            return new AssetRecord
            {
                AssetId = Db.Str(r["asset_id"]),
                SessionId = Db.Str(r["session_id"]),
                GroupId = Db.Str(r["group_id"]),
                Kind = Db.Str(r["kind"]),
                Workflow = Db.Str(r["workflow"]),
                FileName = Db.Str(r["file_name"]),
                FilePath = Db.Str(r["file_path"]),
                PreviewPath = Db.Str(r["preview_path"]),
                PromptText = Db.Str(r["prompt_text"]),
                FileSize = Db.NLong(r["file_size"]),
                MimeType = Db.NStr(r["mime_type"]),
                Width = Db.NInt(r["width"]),
                Height = Db.NInt(r["height"]),
                DurationMs = Db.NInt(r["duration_ms"]),
                CreatedAt = Db.Dt(r["created_at"]),
                ModifiedAt = Db.Dt(r["modified_at"]),
                StorageScope = Db.Str(r["storage_scope"]),
                DerivedFromSessionId = Db.NStr(r["derived_from_session_id"]),
                DerivedFromSessionName = Db.NStr(r["derived_from_session_name"]),
                DerivedFromAssetId = Db.NStr(r["derived_from_asset_id"]),
                DerivedFromAssetFileName = Db.NStr(r["derived_from_asset_file_name"]),
                DerivedFromAssetKind = Db.NStr(r["derived_from_asset_kind"]),
                DerivedFromReferenceRole = Db.NStr(r["derived_from_reference_role"]),
            };
        }
    }
}
