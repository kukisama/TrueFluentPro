use rusqlite::params;
use chrono::Utc;

use crate::db::{Database, map_db_err};
use tfp_core::MonitorGlobalStats;

// ── Internal row models ──

#[derive(Debug, Clone)]
pub struct MonitorTaskRow {
    pub id: String,
    pub audio_item_id: String,
    pub stage: String,
    pub task_type: String,
    pub status: String,
    pub priority: i64,
    pub retry_count: i64,
    pub progress: f64,
    pub prompt_text: Option<String>,
    pub error: Option<String>,
    pub submitted_at: String,
    pub started_at: Option<String>,
    pub completed_at: Option<String>,
    pub progress_message: Option<String>,
    pub audio_file_name: Option<String>,
}

#[derive(Debug, Clone)]
pub struct MonitorExecutionRow {
    pub id: String,
    pub task_id: String,
    pub status: String,
    pub billable: bool,
    pub model_name: Option<String>,
    pub tokens_in: Option<i64>,
    pub tokens_out: Option<i64>,
    pub duration_ms: Option<i64>,
    pub error_message: Option<String>,
    pub cancel_reason: Option<String>,
    pub started_at: String,
    pub finished_at: Option<String>,
    pub debug_prompt: Option<String>,
    pub debug_response: Option<String>,
}

fn map_monitor_task_row(row: &rusqlite::Row) -> rusqlite::Result<MonitorTaskRow> {
    Ok(MonitorTaskRow {
        id: row.get(0)?,
        audio_item_id: row.get(1)?,
        stage: row.get(2)?,
        task_type: row.get(3)?,
        status: row.get(4)?,
        priority: row.get(5)?,
        retry_count: row.get(6)?,
        progress: row.get(7)?,
        prompt_text: row.get(8)?,
        error: row.get(9)?,
        submitted_at: row.get(10)?,
        started_at: row.get(11)?,
        completed_at: row.get(12)?,
        progress_message: row.get(13)?,
        audio_file_name: row.get(14)?,
    })
}

fn map_monitor_exec_row(row: &rusqlite::Row) -> rusqlite::Result<MonitorExecutionRow> {
    Ok(MonitorExecutionRow {
        id: row.get(0)?,
        task_id: row.get(1)?,
        status: row.get(2)?,
        billable: row.get::<_, i64>(3)? != 0,
        model_name: row.get(4)?,
        tokens_in: row.get(5)?,
        tokens_out: row.get(6)?,
        duration_ms: row.get(7)?,
        error_message: row.get(8)?,
        cancel_reason: row.get(9)?,
        started_at: row.get(10)?,
        finished_at: row.get(11)?,
        debug_prompt: row.get(12)?,
        debug_response: row.get(13)?,
    })
}

impl Database {
    pub async fn monitor_get_status_counts(&self) -> tfp_core::Result<std::collections::HashMap<String, i64>> {
        let conn = self.conn().lock().await;
        let mut stmt = conn.prepare(
            "SELECT status, COUNT(*) FROM audio_task_queue GROUP BY status"
        ).map_err(map_db_err)?;
        let mut map = std::collections::HashMap::new();
        let rows = stmt.query_map([], |row| {
            Ok((row.get::<_, String>(0)?, row.get::<_, i64>(1)?))
        }).map_err(map_db_err)?;
        for r in rows {
            let (status, count) = r.map_err(map_db_err)?;
            map.insert(status, count);
        }
        Ok(map)
    }

    pub async fn monitor_get_tasks_by_status(
        &self,
        status: &str,
        sort_column: &str,
        sort_ascending: bool,
    ) -> tfp_core::Result<Vec<MonitorTaskRow>> {
        let conn = self.conn().lock().await;
        let order_col = match sort_column {
            "TaskId" => "t.id",
            "AudioFileName" => "COALESCE(ali.file_name, t.audio_item_id)",
            "Stage" => "t.stage",
            "SubmittedAt" => "t.submitted_at",
            "Status" => "t.status",
            _ => "t.submitted_at",
        };
        let order_dir = if sort_ascending { "ASC" } else { "DESC" };
        let statuses: Vec<&str> = status.split(',').collect();
        let placeholders = statuses.iter().enumerate()
            .map(|(i, _)| format!("?{}", i + 1))
            .collect::<Vec<_>>().join(",");
        let sql = format!(
            "SELECT t.id, t.audio_item_id, t.stage, t.task_type, t.status, t.priority,
                    t.retry_count, t.progress, t.prompt_text, t.error, t.submitted_at,
                    t.started_at, t.completed_at, t.progress_message,
                    ali.file_name as audio_file_name
             FROM audio_task_queue t
             LEFT JOIN audio_library_items ali ON ali.id = t.audio_item_id
             WHERE t.status IN ({placeholders})
             ORDER BY {order_col} {order_dir}
             LIMIT 500"
        );
        let mut stmt = conn.prepare(&sql).map_err(map_db_err)?;
        let params: Vec<Box<dyn rusqlite::types::ToSql>> = statuses.iter()
            .map(|s| Box::new(s.to_string()) as Box<dyn rusqlite::types::ToSql>)
            .collect();
        let param_refs: Vec<&dyn rusqlite::types::ToSql> = params.iter().map(|p| p.as_ref()).collect();
        let rows = stmt.query_map(param_refs.as_slice(), map_monitor_task_row)
            .map_err(map_db_err)?;
        let mut result = Vec::new();
        for r in rows {
            result.push(r.map_err(map_db_err)?);
        }
        Ok(result)
    }

    pub async fn monitor_get_all_tasks(&self) -> tfp_core::Result<Vec<MonitorTaskRow>> {
        let conn = self.conn().lock().await;
        let mut stmt = conn.prepare(
            "SELECT t.id, t.audio_item_id, t.stage, t.task_type, t.status, t.priority,
                    t.retry_count, t.progress, t.prompt_text, t.error, t.submitted_at,
                    t.started_at, t.completed_at, t.progress_message,
                    ali.file_name as audio_file_name
             FROM audio_task_queue t
             LEFT JOIN audio_library_items ali ON ali.id = t.audio_item_id
             ORDER BY t.submitted_at DESC
             LIMIT 500"
        ).map_err(map_db_err)?;
        let rows = stmt.query_map([], map_monitor_task_row).map_err(map_db_err)?;
        let mut result = Vec::new();
        for r in rows {
            result.push(r.map_err(map_db_err)?);
        }
        Ok(result)
    }

    pub async fn monitor_get_tasks_by_date_range(
        &self,
        date_from: &str,
        date_to: &str,
    ) -> tfp_core::Result<Vec<MonitorTaskRow>> {
        let conn = self.conn().lock().await;
        let mut stmt = conn.prepare(
            "SELECT t.id, t.audio_item_id, t.stage, t.task_type, t.status, t.priority,
                    t.retry_count, t.progress, t.prompt_text, t.error, t.submitted_at,
                    t.started_at, t.completed_at, t.progress_message,
                    ali.file_name as audio_file_name
             FROM audio_task_queue t
             LEFT JOIN audio_library_items ali ON ali.id = t.audio_item_id
             WHERE t.submitted_at >= ?1 AND t.submitted_at <= ?2
             ORDER BY t.submitted_at DESC
             LIMIT 500"
        ).map_err(map_db_err)?;
        let rows = stmt.query_map(params![date_from, date_to], map_monitor_task_row)
            .map_err(map_db_err)?;
        let mut result = Vec::new();
        for r in rows {
            result.push(r.map_err(map_db_err)?);
        }
        Ok(result)
    }

    pub async fn monitor_get_global_stats(&self) -> tfp_core::Result<MonitorGlobalStats> {
        let conn = self.conn().lock().await;
        let row = conn.query_row(
            "SELECT
                COUNT(*) as total,
                COALESCE(SUM(CASE WHEN billable = 1 THEN 1 ELSE 0 END), 0) as billable_cnt,
                COALESCE(SUM(CASE WHEN billable = 1 THEN COALESCE(tokens_in, COALESCE(prompt_tokens, 0)) ELSE 0 END), 0) as bill_tok_in,
                COALESCE(SUM(CASE WHEN billable = 1 THEN COALESCE(tokens_out, COALESCE(completion_tokens, 0)) ELSE 0 END), 0) as bill_tok_out
             FROM task_executions",
            [],
            |row| {
                Ok(MonitorGlobalStats {
                    total_executions: row.get(0)?,
                    billable_executions: row.get(1)?,
                    billable_tokens_in: row.get(2)?,
                    billable_tokens_out: row.get(3)?,
                })
            },
        ).map_err(map_db_err)?;
        Ok(row)
    }

    pub async fn monitor_get_executions(&self, task_id: &str) -> tfp_core::Result<Vec<MonitorExecutionRow>> {
        let conn = self.conn().lock().await;
        let mut stmt = conn.prepare(
            "SELECT id, task_id, status,
                    COALESCE(billable, 0) as billable,
                    model_name,
                    COALESCE(tokens_in, prompt_tokens) as tok_in,
                    COALESCE(tokens_out, completion_tokens) as tok_out,
                    duration_ms,
                    COALESCE(error_message, error) as err_msg,
                    cancel_reason,
                    started_at,
                    COALESCE(finished_at, completed_at) as fin,
                    debug_prompt,
                    debug_response
             FROM task_executions WHERE task_id = ?1 ORDER BY started_at ASC"
        ).map_err(map_db_err)?;
        let rows = stmt.query_map(params![task_id], map_monitor_exec_row)
            .map_err(map_db_err)?;
        let mut result = Vec::new();
        for r in rows {
            result.push(r.map_err(map_db_err)?);
        }
        Ok(result)
    }

    pub async fn monitor_get_execution_by_id(&self, execution_id: &str) -> tfp_core::Result<MonitorExecutionRow> {
        let conn = self.conn().lock().await;
        conn.query_row(
            "SELECT id, task_id, status,
                    COALESCE(billable, 0) as billable,
                    model_name,
                    COALESCE(tokens_in, prompt_tokens) as tok_in,
                    COALESCE(tokens_out, completion_tokens) as tok_out,
                    duration_ms,
                    COALESCE(error_message, error) as err_msg,
                    cancel_reason,
                    started_at,
                    COALESCE(finished_at, completed_at) as fin,
                    debug_prompt,
                    debug_response
             FROM task_executions WHERE id = ?1",
            params![execution_id],
            map_monitor_exec_row,
        ).map_err(map_db_err)
    }

    pub async fn monitor_get_latest_execution_debug(&self, task_id: &str) -> tfp_core::Result<Option<(String, String)>> {
        let conn = self.conn().lock().await;
        let result = conn.query_row(
            "SELECT COALESCE(debug_prompt, '') as dp, COALESCE(debug_response, '') as dr
             FROM task_executions WHERE task_id = ?1
             ORDER BY started_at DESC LIMIT 1",
            params![task_id],
            |row| Ok((row.get::<_, String>(0)?, row.get::<_, String>(1)?)),
        );
        match result {
            Ok(pair) => Ok(Some(pair)),
            Err(rusqlite::Error::QueryReturnedNoRows) => Ok(None),
            Err(e) => Err(map_db_err(e)),
        }
    }

    pub async fn monitor_cancel_task(&self, task_id: &str, reason: &str) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        let now = Utc::now().to_rfc3339();
        conn.execute(
            "UPDATE audio_task_queue SET status = 'Cancelled', error = ?1, completed_at = ?2, updated_at = ?2
             WHERE id = ?3 AND status IN ('Queued', 'Executing')",
            params![reason, now, task_id],
        ).map_err(map_db_err)?;
        Ok(())
    }

    pub async fn monitor_insert_execution(
        &self,
        id: &str,
        task_id: &str,
        status: &str,
        billable: bool,
        model_name: Option<&str>,
        tokens_in: Option<i64>,
        tokens_out: Option<i64>,
        duration_ms: Option<i64>,
        cancel_reason: Option<&str>,
        debug_prompt: Option<&str>,
        debug_response: Option<&str>,
        started_at: &str,
        finished_at: Option<&str>,
    ) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute(
            "INSERT INTO task_executions (id, task_id, attempt, status, billable, model_name,
             tokens_in, tokens_out, duration_ms, cancel_reason, debug_prompt, debug_response,
             started_at, finished_at, error_message)
             VALUES (?1, ?2, 1, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, NULL)",
            params![
                id, task_id, status, billable as i64, model_name,
                tokens_in, tokens_out, duration_ms, cancel_reason,
                debug_prompt, debug_response, started_at, finished_at,
            ],
        ).map_err(map_db_err)?;
        Ok(())
    }

    pub async fn monitor_cleanup_completed(&self, days: u32) -> tfp_core::Result<u32> {
        let conn = self.conn().lock().await;
        let cutoff = format!("-{} days", days);
        conn.execute(
            "DELETE FROM task_executions WHERE task_id IN (
                SELECT id FROM audio_task_queue
                WHERE status IN ('Completed','Cancelled','Failed','Timeout')
                AND COALESCE(updated_at, submitted_at) < datetime('now', ?1)
            )",
            [&cutoff],
        ).map_err(map_db_err)?;
        let deleted = conn.execute(
            "DELETE FROM audio_task_queue
             WHERE status IN ('Completed','Cancelled','Failed','Timeout')
             AND COALESCE(updated_at, submitted_at) < datetime('now', ?1)",
            [&cutoff],
        ).map_err(map_db_err)?;
        Ok(deleted as u32)
    }

    pub async fn monitor_retry_task(&self, task_id: &str) -> tfp_core::Result<String> {
        let conn = self.conn().lock().await;
        let (audio_item_id, stage, task_type, priority, prompt_text): (String, String, String, i64, Option<String>) =
            conn.query_row(
                "SELECT audio_item_id, stage, task_type, priority, prompt_text FROM audio_task_queue WHERE id = ?1",
                params![task_id],
                |row| Ok((row.get(0)?, row.get(1)?, row.get(2)?, row.get(3)?, row.get(4)?)),
            ).map_err(map_db_err)?;
        let new_id = uuid::Uuid::new_v4().to_string();
        let now = Utc::now().to_rfc3339();
        conn.execute(
            "INSERT INTO audio_task_queue (id, audio_item_id, stage, task_type, status, priority, retry_count, max_retries, progress, prompt_text, submitted_at, parent_task_id, updated_at)
             VALUES (?1, ?2, ?3, ?4, 'Queued', ?5, 0, 3, 0.0, ?6, ?7, ?8, ?7)",
            params![new_id, audio_item_id, stage, task_type, priority, prompt_text, now, task_id],
        ).map_err(map_db_err)?;
        Ok(new_id)
    }

    pub async fn monitor_batch_delete(&self, task_ids: &[String]) -> tfp_core::Result<u32> {
        let conn = self.conn().lock().await;
        let mut count = 0u32;
        for tid in task_ids {
            let deleted = conn.execute(
                "DELETE FROM audio_task_queue WHERE id = ?1 AND status IN ('Completed','Cancelled','Failed','Timeout')",
                params![tid],
            ).map_err(map_db_err)?;
            if deleted > 0 {
                conn.execute("DELETE FROM task_executions WHERE task_id = ?1", params![tid])
                    .map_err(map_db_err)?;
                count += 1;
            }
        }
        Ok(count)
    }

    pub async fn monitor_recover_interrupted(&self) -> tfp_core::Result<u32> {
        let conn = self.conn().lock().await;
        let now = Utc::now().to_rfc3339();
        let mut stmt = conn.prepare(
            "SELECT id FROM audio_task_queue WHERE status = 'Executing'"
        ).map_err(map_db_err)?;
        let task_ids: Vec<String> = stmt.query_map([], |row| row.get(0))
            .map_err(map_db_err)?
            .filter_map(|r| r.ok())
            .collect();
        drop(stmt);
        if task_ids.is_empty() {
            return Ok(0);
        }
        let count = task_ids.len() as u32;
        for tid in &task_ids {
            conn.execute(
                "UPDATE audio_task_queue SET status = 'Interrupted', error = 'interrupted: process restart',
                 completed_at = ?1, updated_at = ?1
                 WHERE id = ?2 AND status = 'Executing'",
                params![now, tid],
            ).map_err(map_db_err)?;
            let exec_id = uuid::Uuid::new_v4().to_string();
            conn.execute(
                "INSERT OR IGNORE INTO task_executions (id, task_id, status, billable, cancel_reason, started_at, finished_at)
                 VALUES (?1, ?2, 'Interrupted', 0, 'process_restart', ?3, ?3)",
                params![exec_id, tid, now],
            ).map_err(map_db_err)?;
        }
        Ok(count)
    }
}
