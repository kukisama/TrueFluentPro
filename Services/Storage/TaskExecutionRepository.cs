using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services.Storage
{
    /// <summary>
    /// 任务执行记录仓储接口 — 管理 task_executions 表。
    /// </summary>
    public interface ITaskExecutionRepository
    {
        /// <summary>插入一条新执行记录（任务开始时调用）。</summary>
        void Insert(TaskExecutionRecord record);

        /// <summary>标记执行完成（billable=true）。</summary>
        void MarkCompleted(string executionId, int? tokensIn, int? tokensOut, int durationMs, string? modelName);

        /// <summary>标记执行失败（billable=false）。</summary>
        void MarkFailed(string executionId, int? tokensIn, int? tokensOut, int durationMs, string? errorMessage);

        /// <summary>标记执行被取消（billable=false）。</summary>
        void MarkCancelled(string executionId, string cancelReason, int? tokensIn, int? tokensOut, int durationMs);

        /// <summary>标记执行被中断（app 崩溃/重启，billable=false，token=null）。</summary>
        void MarkInterrupted(string executionId, string cancelReason);

        /// <summary>App 启动时：将所有遗留 Running 的执行标记为 Interrupted。</summary>
        int RecoverInterruptedExecutions();

        /// <summary>按 task_id 获取所有执行记录，按 started_at 排序。</summary>
        List<TaskExecutionRecord> GetByTaskId(string taskId);

        /// <summary>按 task_id 获取执行汇总（billable token 合计、重试次数）。</summary>
        TaskExecutionSummary GetSummary(string taskId);

        /// <summary>按 audio_item_id 获取所有执行记录。</summary>
        List<TaskExecutionRecord> GetByAudioItemId(string audioItemId);

        /// <summary>获取全局运营统计。</summary>
        TaskExecutionGlobalStats GetGlobalStats();

        /// <summary>调试模式：保存提示词和响应到执行记录。</summary>
        void SaveDebugData(string executionId, string? debugPrompt, string? debugResponse);
    }

    /// <summary>全局执行统计。</summary>
    public class TaskExecutionGlobalStats
    {
        public int TotalExecutions { get; set; }
        public int BillableExecutions { get; set; }
        public int NonBillableExecutions { get; set; }
        public long BillableTokensIn { get; set; }
        public long BillableTokensOut { get; set; }
        public long NonBillableTokensIn { get; set; }
        public long NonBillableTokensOut { get; set; }
    }

    /// <summary>
    /// 任务执行记录仓储实现。
    /// </summary>
    public sealed class TaskExecutionRepository : ITaskExecutionRepository
    {
        private readonly ISqliteDbService _db;

        public TaskExecutionRepository(ISqliteDbService db)
        {
            _db = db;
        }

        public void Insert(TaskExecutionRecord record)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO task_executions (execution_id, task_id, audio_item_id, stage, status, billable,
    cancel_reason, model_name, tokens_in, tokens_out, duration_ms, error_message, started_at, finished_at,
    debug_prompt, debug_response)
VALUES (@eid, @tid, @aid, @stage, @status, @billable,
    @cancelReason, @model, @tokIn, @tokOut, @dur, @err, @start, @finish,
    @dbgPrompt, @dbgResponse);";
            cmd.Parameters.AddWithValue("@eid", record.ExecutionId);
            cmd.Parameters.AddWithValue("@tid", record.TaskId);
            cmd.Parameters.AddWithValue("@aid", record.AudioItemId);
            cmd.Parameters.AddWithValue("@stage", record.Stage);
            cmd.Parameters.AddWithValue("@status", record.Status);
            cmd.Parameters.AddWithValue("@billable", record.Billable ? 1 : 0);
            cmd.Parameters.AddWithValue("@cancelReason", Db.Val(record.CancelReason));
            cmd.Parameters.AddWithValue("@model", Db.Val(record.ModelName));
            cmd.Parameters.AddWithValue("@tokIn", Db.Val(record.TokensIn));
            cmd.Parameters.AddWithValue("@tokOut", Db.Val(record.TokensOut));
            cmd.Parameters.AddWithValue("@dur", Db.Val(record.DurationMs));
            cmd.Parameters.AddWithValue("@err", Db.Val(record.ErrorMessage));
            cmd.Parameters.AddWithValue("@start", Db.Ts(record.StartedAt));
            cmd.Parameters.AddWithValue("@finish", Db.Val(record.FinishedAt));
            cmd.Parameters.AddWithValue("@dbgPrompt", Db.Val(record.DebugPrompt));
            cmd.Parameters.AddWithValue("@dbgResponse", Db.Val(record.DebugResponse));
            cmd.ExecuteNonQuery();
        }

        public void MarkCompleted(string executionId, int? tokensIn, int? tokensOut, int durationMs, string? modelName)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
UPDATE task_executions SET status = 'Completed', billable = 1,
    tokens_in = @tokIn, tokens_out = @tokOut, duration_ms = @dur, model_name = @model,
    finished_at = @finish
WHERE execution_id = @eid;";
            cmd.Parameters.AddWithValue("@eid", executionId);
            cmd.Parameters.AddWithValue("@tokIn", Db.Val(tokensIn));
            cmd.Parameters.AddWithValue("@tokOut", Db.Val(tokensOut));
            cmd.Parameters.AddWithValue("@dur", durationMs);
            cmd.Parameters.AddWithValue("@model", Db.Val(modelName));
            cmd.Parameters.AddWithValue("@finish", Db.Ts(DateTime.Now));
            cmd.ExecuteNonQuery();
        }

        public void MarkFailed(string executionId, int? tokensIn, int? tokensOut, int durationMs, string? errorMessage)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
UPDATE task_executions SET status = 'Failed', billable = 0,
    tokens_in = @tokIn, tokens_out = @tokOut, duration_ms = @dur,
    error_message = @err, finished_at = @finish
WHERE execution_id = @eid;";
            cmd.Parameters.AddWithValue("@eid", executionId);
            cmd.Parameters.AddWithValue("@tokIn", Db.Val(tokensIn));
            cmd.Parameters.AddWithValue("@tokOut", Db.Val(tokensOut));
            cmd.Parameters.AddWithValue("@dur", durationMs);
            cmd.Parameters.AddWithValue("@err", Db.Val(errorMessage));
            cmd.Parameters.AddWithValue("@finish", Db.Ts(DateTime.Now));
            cmd.ExecuteNonQuery();
        }

        public void MarkCancelled(string executionId, string cancelReason, int? tokensIn, int? tokensOut, int durationMs)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
UPDATE task_executions SET status = 'Cancelled', billable = 0,
    cancel_reason = @reason, tokens_in = @tokIn, tokens_out = @tokOut, duration_ms = @dur,
    finished_at = @finish
WHERE execution_id = @eid;";
            cmd.Parameters.AddWithValue("@eid", executionId);
            cmd.Parameters.AddWithValue("@reason", cancelReason);
            cmd.Parameters.AddWithValue("@tokIn", Db.Val(tokensIn));
            cmd.Parameters.AddWithValue("@tokOut", Db.Val(tokensOut));
            cmd.Parameters.AddWithValue("@dur", durationMs);
            cmd.Parameters.AddWithValue("@finish", Db.Ts(DateTime.Now));
            cmd.ExecuteNonQuery();
        }

        public void MarkInterrupted(string executionId, string cancelReason)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
UPDATE task_executions SET status = 'Interrupted', billable = 0,
    cancel_reason = @reason, finished_at = @finish
WHERE execution_id = @eid;";
            cmd.Parameters.AddWithValue("@eid", executionId);
            cmd.Parameters.AddWithValue("@reason", cancelReason);
            cmd.Parameters.AddWithValue("@finish", Db.Ts(DateTime.Now));
            cmd.ExecuteNonQuery();
        }

        public int RecoverInterruptedExecutions()
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
UPDATE task_executions SET status = 'Interrupted', billable = 0,
    cancel_reason = 'app_restart', finished_at = @finish
WHERE status = 'Running';";
            cmd.Parameters.AddWithValue("@finish", Db.Ts(DateTime.Now));
            return cmd.ExecuteNonQuery();
        }

        public List<TaskExecutionRecord> GetByTaskId(string taskId)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT * FROM task_executions WHERE task_id = @tid ORDER BY started_at ASC;";
            cmd.Parameters.AddWithValue("@tid", taskId);
            return ReadList(cmd);
        }

        public TaskExecutionSummary GetSummary(string taskId)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT
    COUNT(*) AS total,
    SUM(CASE WHEN billable = 0 THEN 1 ELSE 0 END) AS non_billable,
    SUM(CASE WHEN billable = 1 THEN COALESCE(tokens_in, 0) ELSE 0 END) AS bill_tok_in,
    SUM(CASE WHEN billable = 1 THEN COALESCE(tokens_out, 0) ELSE 0 END) AS bill_tok_out,
    SUM(CASE WHEN billable = 1 THEN COALESCE(duration_ms, 0) ELSE 0 END) AS bill_dur
FROM task_executions WHERE task_id = @tid;";
            cmd.Parameters.AddWithValue("@tid", taskId);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return new TaskExecutionSummary
                {
                    TotalExecutions = Db.Int(reader["total"]),
                    NonBillableExecutions = Db.Int(reader["non_billable"]),
                    BillableTokensIn = Db.NInt(reader["bill_tok_in"]),
                    BillableTokensOut = Db.NInt(reader["bill_tok_out"]),
                    BillableDurationMs = Db.NInt(reader["bill_dur"]),
                };
            }
            return new TaskExecutionSummary();
        }

        public List<TaskExecutionRecord> GetByAudioItemId(string audioItemId)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT * FROM task_executions WHERE audio_item_id = @aid ORDER BY started_at DESC;";
            cmd.Parameters.AddWithValue("@aid", audioItemId);
            return ReadList(cmd);
        }

        public TaskExecutionGlobalStats GetGlobalStats()
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT
    COUNT(*) AS total,
    SUM(CASE WHEN billable = 1 THEN 1 ELSE 0 END) AS billable_cnt,
    SUM(CASE WHEN billable = 0 THEN 1 ELSE 0 END) AS non_billable_cnt,
    SUM(CASE WHEN billable = 1 THEN COALESCE(tokens_in, 0) ELSE 0 END) AS bill_tok_in,
    SUM(CASE WHEN billable = 1 THEN COALESCE(tokens_out, 0) ELSE 0 END) AS bill_tok_out,
    SUM(CASE WHEN billable = 0 AND tokens_in IS NOT NULL THEN tokens_in ELSE 0 END) AS nb_tok_in,
    SUM(CASE WHEN billable = 0 AND tokens_out IS NOT NULL THEN tokens_out ELSE 0 END) AS nb_tok_out
FROM task_executions;";
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return new TaskExecutionGlobalStats
                {
                    TotalExecutions = Db.Int(reader["total"]),
                    BillableExecutions = Db.Int(reader["billable_cnt"]),
                    NonBillableExecutions = Db.Int(reader["non_billable_cnt"]),
                    BillableTokensIn = Db.Long(reader["bill_tok_in"]),
                    BillableTokensOut = Db.Long(reader["bill_tok_out"]),
                    NonBillableTokensIn = Db.Long(reader["nb_tok_in"]),
                    NonBillableTokensOut = Db.Long(reader["nb_tok_out"]),
                };
            }
            return new TaskExecutionGlobalStats();
        }

        private static List<TaskExecutionRecord> ReadList(SqliteCommand cmd)
        {
            var list = new List<TaskExecutionRecord>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new TaskExecutionRecord
                {
                    ExecutionId = Db.Str(reader["execution_id"]),
                    TaskId = Db.Str(reader["task_id"]),
                    AudioItemId = Db.Str(reader["audio_item_id"]),
                    Stage = Db.Str(reader["stage"]),
                    Status = Db.Str(reader["status"]),
                    Billable = Db.Bool(reader["billable"]),
                    CancelReason = Db.NStr(reader["cancel_reason"]),
                    ModelName = Db.NStr(reader["model_name"]),
                    TokensIn = Db.NInt(reader["tokens_in"]),
                    TokensOut = Db.NInt(reader["tokens_out"]),
                    DurationMs = Db.NInt(reader["duration_ms"]),
                    ErrorMessage = Db.NStr(reader["error_message"]),
                    StartedAt = Db.Dt(reader["started_at"]),
                    FinishedAt = Db.NDt(reader["finished_at"]),
                    DebugPrompt = Db.NStr(reader["debug_prompt"]),
                    DebugResponse = Db.NStr(reader["debug_response"]),
                });
            }
            return list;
        }

        public void SaveDebugData(string executionId, string? debugPrompt, string? debugResponse)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE task_executions SET debug_prompt = @p, debug_response = @r WHERE execution_id = @eid;";
            cmd.Parameters.AddWithValue("@eid", executionId);
            cmd.Parameters.AddWithValue("@p", Db.Val(debugPrompt));
            cmd.Parameters.AddWithValue("@r", Db.Val(debugResponse));
            cmd.ExecuteNonQuery();
        }
    }
}
