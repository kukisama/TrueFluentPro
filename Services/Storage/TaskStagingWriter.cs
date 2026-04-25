using System;
using Microsoft.Data.Sqlite;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services.Storage
{
    /// <summary>
    /// task_staging 写入器：追踪每个生成任务的完整生命周期。
    /// 所有写操作使用事务确保原子性。
    /// </summary>
    public sealed class TaskStagingWriter
    {
        private readonly ISqliteDbService _db;

        public TaskStagingWriter(ISqliteDbService db) => _db = db;

        /// <summary>
        /// 插入新的 staging 记录（Pending 状态）。
        /// </summary>
        public void Insert(TaskStagingEntry entry)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO task_staging (
    task_id, session_id, task_type, status, prompt,
    error_code, error_message, error_detail,
    input_tokens, output_tokens, estimated_cost_usd,
    created_at, started_at, finished_at,
    result_file_path, file_size,
    remote_video_id, remote_generation_id
) VALUES (
    @taskId, @sessionId, @taskType, @status, @prompt,
    @errorCode, @errorMessage, @errorDetail,
    @inputTokens, @outputTokens, @estimatedCostUsd,
    @createdAt, @startedAt, @finishedAt,
    @resultFilePath, @fileSize,
    @remoteVideoId, @remoteGenerationId
);";
            BindParams(cmd, entry);
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// 更新 staging 状态（Running/Landed/Failed/Cancelled/Committed）。
        /// </summary>
        public void UpdateStatus(string taskId, TaskStagingStatus newStatus,
            string? errorCode = null, string? errorMessage = null, string? errorDetail = null,
            int? inputTokens = null, int? outputTokens = null, double? estimatedCostUsd = null,
            string? resultFilePath = null, long? fileSize = null)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
UPDATE task_staging SET
    status = @status,
    error_code = COALESCE(@errorCode, error_code),
    error_message = COALESCE(@errorMessage, error_message),
    error_detail = COALESCE(@errorDetail, error_detail),
    input_tokens = COALESCE(@inputTokens, input_tokens),
    output_tokens = COALESCE(@outputTokens, output_tokens),
    estimated_cost_usd = COALESCE(@estimatedCostUsd, estimated_cost_usd),
    result_file_path = COALESCE(@resultFilePath, result_file_path),
    file_size = COALESCE(@fileSize, file_size),
    started_at = CASE WHEN @status = 1 AND started_at IS NULL THEN @now ELSE started_at END,
    finished_at = CASE WHEN @status IN (2,3,4,5) AND finished_at IS NULL THEN @now ELSE finished_at END
WHERE task_id = @taskId;";
            cmd.Parameters.AddWithValue("@taskId", taskId);
            cmd.Parameters.AddWithValue("@status", (int)newStatus);
            cmd.Parameters.AddWithValue("@errorCode", (object?)errorCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@errorMessage", (object?)errorMessage ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@errorDetail", (object?)errorDetail ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@inputTokens", (object?)inputTokens ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@outputTokens", (object?)outputTokens ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@estimatedCostUsd", (object?)estimatedCostUsd ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@resultFilePath", (object?)resultFilePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@fileSize", (object?)fileSize ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }

        private static void BindParams(SqliteCommand cmd, TaskStagingEntry e)
        {
            cmd.Parameters.AddWithValue("@taskId", e.TaskId);
            cmd.Parameters.AddWithValue("@sessionId", e.SessionId);
            cmd.Parameters.AddWithValue("@taskType", e.TaskType);
            cmd.Parameters.AddWithValue("@status", (int)e.Status);
            cmd.Parameters.AddWithValue("@prompt", e.Prompt);
            cmd.Parameters.AddWithValue("@errorCode", (object?)e.ErrorCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@errorMessage", (object?)e.ErrorMessage ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@errorDetail", (object?)e.ErrorDetail ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@inputTokens", (object?)e.InputTokens ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@outputTokens", (object?)e.OutputTokens ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@estimatedCostUsd", (object?)e.EstimatedCostUsd ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@createdAt", e.CreatedAt);
            cmd.Parameters.AddWithValue("@startedAt", (object?)e.StartedAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@finishedAt", (object?)e.FinishedAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@resultFilePath", (object?)e.ResultFilePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@fileSize", (object?)e.FileSize ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@remoteVideoId", (object?)e.RemoteVideoId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@remoteGenerationId", (object?)e.RemoteGenerationId ?? DBNull.Value);
        }
    }
}
