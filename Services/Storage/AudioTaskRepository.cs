using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services.Storage
{
    /// <summary>
    /// 音频任务队列仓储接口 — 管理 audio_task_queue 表的 CRUD 操作。
    /// </summary>
    public interface IAudioTaskRepository
    {
        /// <summary>插入一条新任务。</summary>
        void Insert(AudioTaskRecord record);

        /// <summary>根据 task_id 获取任务。</summary>
        AudioTaskRecord? GetById(string taskId);

        /// <summary>查找指定音频+阶段下处于 Pending 或 Running 状态的任务。</summary>
        AudioTaskRecord? FindActiveTask(string audioItemId, string stage);

        /// <summary>获取下一个可执行的任务（Pending 且依赖满足，按优先级+提交时间排序）。</summary>
        AudioTaskRecord? GetNextPendingTask();

        /// <summary>获取一批待执行的 Pending 任务（按优先级+提交时间排序）。</summary>
        List<AudioTaskRecord> GetPendingTasks(int limit);

        /// <summary>更新任务状态。</summary>
        void UpdateStatus(string taskId, AudioTaskStatus newStatus, string? errorMessage = null);

        /// <summary>更新任务的依赖信息。</summary>
        void UpdateDependsOn(string taskId, string? dependsOnJson);

        /// <summary>更新任务的进度描述消息。</summary>
        void UpdateProgressMessage(string taskId, string? progressMessage);

        /// <summary>将任务标记为 Running（设置 started_at）。</summary>
        void MarkRunning(string taskId);

        /// <summary>将任务标记为 Completed（设置 completed_at，清空 progress_message）。</summary>
        void MarkCompleted(string taskId);

        /// <summary>将任务标记为 Failed（设置 completed_at + error_message，清空 progress_message）。</summary>
        void MarkFailed(string taskId, string errorMessage);

        /// <summary>将任务标记为 Cancelled。</summary>
        void MarkCancelled(string taskId);

        /// <summary>重试任务（重置为 Pending，增加 retry_count）。</summary>
        void Retry(string taskId);

        /// <summary>查询任务列表（支持筛选）。</summary>
        List<AudioTaskRecord> Query(AudioTaskQueryFilter? filter = null);

        /// <summary>获取指定音频的所有活跃任务（Pending/Running）。</summary>
        List<AudioTaskRecord> GetActiveTasksForAudio(string audioItemId);

        /// <summary>获取统计数据。</summary>
        AudioTaskQueueStats GetStats();

        /// <summary>清理已完成/已取消的旧任务。</summary>
        int CleanupOldTasks(TimeSpan olderThan);

        /// <summary>启动时恢复：将超时的 Running 任务重置为 Pending。</summary>
        int RecoverStalledRunningTasks(TimeSpan stallThreshold);
    }

    /// <summary>任务查询过滤条件。</summary>
    public class AudioTaskQueryFilter
    {
        public AudioTaskStatus? Status { get; set; }
        public string? AudioItemId { get; set; }
        public int Limit { get; set; } = 100;
        public int Offset { get; set; }
    }

    /// <summary>任务队列统计数据。</summary>
    public class AudioTaskQueueStats
    {
        public int PendingCount { get; set; }
        public int RunningCount { get; set; }
        public int CompletedCount { get; set; }
        public int FailedCount { get; set; }
        public int CancelledCount { get; set; }
    }

    /// <summary>
    /// 音频任务队列仓储实现 — 基于 SQLite 的 audio_task_queue 表。
    /// </summary>
    public sealed class AudioTaskRepository : IAudioTaskRepository
    {
        private readonly ISqliteDbService _db;

        public AudioTaskRepository(ISqliteDbService db) => _db = db;

        public void Insert(AudioTaskRecord r)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO audio_task_queue (
    task_id, audio_item_id, stage, status, priority,
    depends_on, error_message, progress_message, retry_count,
    submitted_at, started_at, completed_at, submitted_by
) VALUES (
    @tid, @aid, @stage, @status, @pri,
    @deps, @err, @prog, @retry,
    @sub_at, @start_at, @comp_at, @sub_by
);";
            BindParams(cmd, r);
            cmd.ExecuteNonQuery();
        }

        public AudioTaskRecord? GetById(string taskId)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM audio_task_queue WHERE task_id = @tid;";
            cmd.Parameters.AddWithValue("@tid", taskId);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadRow(reader) : null;
        }

        public AudioTaskRecord? FindActiveTask(string audioItemId, string stage)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT * FROM audio_task_queue
WHERE audio_item_id = @aid AND stage = @stage AND status IN ('Pending', 'Running')
LIMIT 1;";
            cmd.Parameters.AddWithValue("@aid", audioItemId);
            cmd.Parameters.AddWithValue("@stage", stage);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadRow(reader) : null;
        }

        public AudioTaskRecord? GetNextPendingTask()
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT * FROM audio_task_queue
WHERE status = 'Pending'
ORDER BY priority DESC, submitted_at ASC
LIMIT 1;";
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadRow(reader) : null;
        }

        public List<AudioTaskRecord> GetPendingTasks(int limit)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT * FROM audio_task_queue
WHERE status = 'Pending'
ORDER BY priority DESC, submitted_at ASC
LIMIT @limit;";
            cmd.Parameters.AddWithValue("@limit", limit);
            var list = new List<AudioTaskRecord>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) list.Add(ReadRow(reader));
            return list;
        }

        public void UpdateStatus(string taskId, AudioTaskStatus newStatus, string? errorMessage = null)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
UPDATE audio_task_queue
SET status = @status, error_message = @err
WHERE task_id = @tid;";
            cmd.Parameters.AddWithValue("@tid", taskId);
            cmd.Parameters.AddWithValue("@status", newStatus.ToString());
            cmd.Parameters.AddWithValue("@err", Db.Val(errorMessage));
            cmd.ExecuteNonQuery();
        }

        public void UpdateDependsOn(string taskId, string? dependsOnJson)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
UPDATE audio_task_queue
SET depends_on = @deps
WHERE task_id = @tid;";
            cmd.Parameters.AddWithValue("@tid", taskId);
            cmd.Parameters.AddWithValue("@deps", Db.Val(dependsOnJson));
            cmd.ExecuteNonQuery();
        }

        public void UpdateProgressMessage(string taskId, string? progressMessage)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
UPDATE audio_task_queue
SET progress_message = @prog
WHERE task_id = @tid;";
            cmd.Parameters.AddWithValue("@tid", taskId);
            cmd.Parameters.AddWithValue("@prog", Db.Val(progressMessage));
            cmd.ExecuteNonQuery();
        }

        public void MarkRunning(string taskId)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
UPDATE audio_task_queue
SET status = 'Running', started_at = @t
WHERE task_id = @tid;";
            cmd.Parameters.AddWithValue("@tid", taskId);
            cmd.Parameters.AddWithValue("@t", Db.Ts(DateTime.Now));
            cmd.ExecuteNonQuery();
        }

        public void MarkCompleted(string taskId)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
UPDATE audio_task_queue
SET status = 'Completed', completed_at = @t, progress_message = NULL
WHERE task_id = @tid;";
            cmd.Parameters.AddWithValue("@tid", taskId);
            cmd.Parameters.AddWithValue("@t", Db.Ts(DateTime.Now));
            cmd.ExecuteNonQuery();
        }

        public void MarkFailed(string taskId, string errorMessage)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
UPDATE audio_task_queue
SET status = 'Failed', completed_at = @t, error_message = @err, progress_message = NULL
WHERE task_id = @tid;";
            cmd.Parameters.AddWithValue("@tid", taskId);
            cmd.Parameters.AddWithValue("@t", Db.Ts(DateTime.Now));
            cmd.Parameters.AddWithValue("@err", errorMessage);
            cmd.ExecuteNonQuery();
        }

        public void MarkCancelled(string taskId)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
UPDATE audio_task_queue
SET status = 'Cancelled', completed_at = @t, progress_message = NULL
WHERE task_id = @tid;";
            cmd.Parameters.AddWithValue("@tid", taskId);
            cmd.Parameters.AddWithValue("@t", Db.Ts(DateTime.Now));
            cmd.ExecuteNonQuery();
        }

        public void Retry(string taskId)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
UPDATE audio_task_queue
SET status = 'Pending', error_message = NULL, progress_message = NULL, started_at = NULL,
    completed_at = NULL, retry_count = retry_count + 1
WHERE task_id = @tid;";
            cmd.Parameters.AddWithValue("@tid", taskId);
            cmd.ExecuteNonQuery();
        }

        public List<AudioTaskRecord> Query(AudioTaskQueryFilter? filter = null)
        {
            filter ??= new AudioTaskQueryFilter();
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();

            var sql = "SELECT * FROM audio_task_queue WHERE 1=1";
            if (filter.Status.HasValue)
            {
                sql += " AND status = @status";
                cmd.Parameters.AddWithValue("@status", filter.Status.Value.ToString());
            }
            if (!string.IsNullOrEmpty(filter.AudioItemId))
            {
                sql += " AND audio_item_id = @aid";
                cmd.Parameters.AddWithValue("@aid", filter.AudioItemId);
            }
            sql += " ORDER BY priority DESC, submitted_at DESC LIMIT @limit OFFSET @offset";
            cmd.Parameters.AddWithValue("@limit", filter.Limit);
            cmd.Parameters.AddWithValue("@offset", filter.Offset);

            cmd.CommandText = sql;
            var list = new List<AudioTaskRecord>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) list.Add(ReadRow(reader));
            return list;
        }

        public List<AudioTaskRecord> GetActiveTasksForAudio(string audioItemId)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT * FROM audio_task_queue
WHERE audio_item_id = @aid AND status IN ('Pending', 'Running')
ORDER BY priority DESC, submitted_at ASC;";
            cmd.Parameters.AddWithValue("@aid", audioItemId);

            var list = new List<AudioTaskRecord>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) list.Add(ReadRow(reader));
            return list;
        }

        public AudioTaskQueueStats GetStats()
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT status, COUNT(*) as cnt FROM audio_task_queue GROUP BY status;";

            var stats = new AudioTaskQueueStats();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var status = Db.Str(reader["status"]);
                var count = Db.Int(reader["cnt"]);
                switch (status)
                {
                    case "Pending": stats.PendingCount = count; break;
                    case "Running": stats.RunningCount = count; break;
                    case "Completed": stats.CompletedCount = count; break;
                    case "Failed": stats.FailedCount = count; break;
                    case "Cancelled": stats.CancelledCount = count; break;
                }
            }
            return stats;
        }

        public int CleanupOldTasks(TimeSpan olderThan)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            var cutoff = DateTime.Now.Subtract(olderThan);
            cmd.CommandText = @"
DELETE FROM audio_task_queue
WHERE status IN ('Completed', 'Cancelled')
  AND completed_at IS NOT NULL
  AND completed_at < @cutoff;";
            cmd.Parameters.AddWithValue("@cutoff", Db.Ts(cutoff));
            return cmd.ExecuteNonQuery();
        }

        public int RecoverStalledRunningTasks(TimeSpan stallThreshold)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            var cutoff = DateTime.Now.Subtract(stallThreshold);
            cmd.CommandText = @"
UPDATE audio_task_queue
SET status = 'Pending', started_at = NULL
WHERE status = 'Running'
  AND started_at IS NOT NULL
  AND started_at < @cutoff;";
            cmd.Parameters.AddWithValue("@cutoff", Db.Ts(cutoff));
            return cmd.ExecuteNonQuery();
        }

        // ── 私有辅助 ──────────────────────────────────

        private static void BindParams(SqliteCommand cmd, AudioTaskRecord r)
        {
            cmd.Parameters.AddWithValue("@tid", r.TaskId);
            cmd.Parameters.AddWithValue("@aid", r.AudioItemId);
            cmd.Parameters.AddWithValue("@stage", r.Stage);
            cmd.Parameters.AddWithValue("@status", r.Status.ToString());
            cmd.Parameters.AddWithValue("@pri", r.Priority);
            cmd.Parameters.AddWithValue("@deps", Db.Val(r.DependsOn));
            cmd.Parameters.AddWithValue("@err", Db.Val(r.ErrorMessage));
            cmd.Parameters.AddWithValue("@prog", Db.Val(r.ProgressMessage));
            cmd.Parameters.AddWithValue("@retry", r.RetryCount);
            cmd.Parameters.AddWithValue("@sub_at", Db.Ts(r.SubmittedAt));
            cmd.Parameters.AddWithValue("@start_at", Db.Val(r.StartedAt));
            cmd.Parameters.AddWithValue("@comp_at", Db.Val(r.CompletedAt));
            cmd.Parameters.AddWithValue("@sub_by", Db.Val(r.SubmittedBy));
        }

        private static AudioTaskRecord ReadRow(SqliteDataReader r) => new()
        {
            TaskId = Db.Str(r["task_id"]),
            AudioItemId = Db.Str(r["audio_item_id"]),
            Stage = Db.Str(r["stage"]),
            Status = Enum.TryParse<AudioTaskStatus>(Db.Str(r["status"]), out var s) ? s : AudioTaskStatus.Pending,
            Priority = Db.Int(r["priority"]),
            DependsOn = Db.NStr(r["depends_on"]),
            ErrorMessage = Db.NStr(r["error_message"]),
            ProgressMessage = Db.NStr(r["progress_message"]),
            RetryCount = Db.Int(r["retry_count"]),
            SubmittedAt = Db.Dt(r["submitted_at"]),
            StartedAt = Db.NDt(r["started_at"]),
            CompletedAt = Db.NDt(r["completed_at"]),
            SubmittedBy = Db.NStr(r["submitted_by"]),
        };
    }
}
