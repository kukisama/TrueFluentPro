use rusqlite::params;
use chrono::Utc;

use crate::db::{Database, map_db_err};
use tfp_core::{AudioLibraryItem, AudioLifecycleRow, AudioTaskRow, TaskExecutionRow, TaskEngineStats};

impl Database {
    pub async fn add_audio_item(&self, item: &AudioLibraryItem) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute(
            "INSERT INTO audio_library_items (id, file_name, file_path, duration_ms, sample_rate, channels, source_lang, created_at, updated_at)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9)",
            params![
                item.id, item.file_name, item.file_path, item.duration_ms,
                item.sample_rate, item.channels, item.source_lang,
                item.created_at, item.updated_at,
            ],
        ).map_err(map_db_err)?;
        Ok(())
    }

    pub async fn list_audio_items(&self) -> tfp_core::Result<Vec<AudioLibraryItem>> {
        let conn = self.conn().lock().await;
        let mut stmt = conn.prepare(
            "SELECT id, file_name, file_path, duration_ms, sample_rate, channels, source_lang, created_at, updated_at
             FROM audio_library_items ORDER BY created_at DESC",
        ).map_err(map_db_err)?;
        let rows = stmt.query_map([], |row| {
            Ok(AudioLibraryItem {
                id: row.get(0)?,
                file_name: row.get(1)?,
                file_path: row.get(2)?,
                duration_ms: row.get(3)?,
                sample_rate: row.get(4)?,
                channels: row.get(5)?,
                source_lang: row.get(6)?,
                created_at: row.get(7)?,
                updated_at: row.get(8)?,
            })
        }).map_err(map_db_err)?;
        let mut result = Vec::new();
        for r in rows {
            result.push(r.map_err(map_db_err)?);
        }
        Ok(result)
    }

    pub async fn delete_audio_item(&self, item_id: &str) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute("DELETE FROM audio_library_items WHERE id = ?1", params![item_id])
            .map_err(map_db_err)?;
        Ok(())
    }

    pub async fn get_audio_lifecycle(&self, audio_item_id: &str) -> tfp_core::Result<Vec<AudioLifecycleRow>> {
        let conn = self.conn().lock().await;
        let mut stmt = conn.prepare(
            "SELECT id, audio_item_id, stage, status, result_text, result_json, model_id, token_used, error, started_at, completed_at
             FROM audio_lifecycle WHERE audio_item_id = ?1 ORDER BY rowid ASC",
        ).map_err(map_db_err)?;
        let rows = stmt.query_map(params![audio_item_id], |row| {
            Ok(AudioLifecycleRow {
                id: row.get(0)?,
                audio_item_id: row.get(1)?,
                stage: row.get(2)?,
                status: row.get(3)?,
                result_text: row.get(4)?,
                result_json: row.get(5)?,
                model_id: row.get(6)?,
                token_used: row.get(7)?,
                error: row.get(8)?,
                started_at: row.get(9)?,
                completed_at: row.get(10)?,
            })
        }).map_err(map_db_err)?;
        let mut result = Vec::new();
        for r in rows {
            result.push(r.map_err(map_db_err)?);
        }
        Ok(result)
    }

    pub async fn upsert_lifecycle(&self, lc: &AudioLifecycleRow) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute(
            "INSERT INTO audio_lifecycle (id, audio_item_id, stage, status, result_text, result_json, model_id, token_used, error, started_at, completed_at)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11)
             ON CONFLICT(id) DO UPDATE SET status=excluded.status, result_text=excluded.result_text, result_json=excluded.result_json,
             model_id=excluded.model_id, token_used=excluded.token_used, error=excluded.error,
             started_at=excluded.started_at, completed_at=excluded.completed_at",
            params![
                lc.id, lc.audio_item_id, lc.stage, lc.status,
                lc.result_text, lc.result_json, lc.model_id, lc.token_used,
                lc.error, lc.started_at, lc.completed_at,
            ],
        ).map_err(map_db_err)?;
        Ok(())
    }

    pub async fn init_lifecycle_stages(&self, audio_item_id: &str) -> tfp_core::Result<()> {
        let stages = [
            "Transcribed", "Summarized", "MindMap", "Insight",
            "Research", "PodcastScript", "PodcastAudio", "Translated",
        ];
        let conn = self.conn().lock().await;
        for stage in &stages {
            let id = format!("{}-{}", audio_item_id, stage);
            conn.execute(
                "INSERT OR IGNORE INTO audio_lifecycle (id, audio_item_id, stage, status)
                 VALUES (?1, ?2, ?3, 'Pending')",
                params![id, audio_item_id, stage],
            ).map_err(map_db_err)?;
        }
        Ok(())
    }

    pub async fn submit_task(&self, task: &AudioTaskRow) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute(
            "INSERT INTO audio_task_queue (id, audio_item_id, stage, task_type, status, priority, retry_count, max_retries, progress, prompt_text, result_text, error, submitted_at)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13)",
            params![
                task.id, task.audio_item_id, task.stage, task.task_type,
                task.status, task.priority, task.retry_count, task.max_retries,
                task.progress, task.prompt_text, task.result_text, task.error, task.submitted_at,
            ],
        ).map_err(map_db_err)?;
        Ok(())
    }

    pub async fn list_tasks(&self, status: Option<&str>, limit: u32) -> tfp_core::Result<Vec<AudioTaskRow>> {
        let conn = self.conn().lock().await;
        let sql = match status {
            Some(_) => "SELECT id, audio_item_id, stage, task_type, status, priority, retry_count, max_retries, progress, prompt_text, result_text, error, submitted_at, started_at, completed_at
                         FROM audio_task_queue WHERE status = ?1 ORDER BY priority DESC, submitted_at ASC LIMIT ?2",
            None => "SELECT id, audio_item_id, stage, task_type, status, priority, retry_count, max_retries, progress, prompt_text, result_text, error, submitted_at, started_at, completed_at
                     FROM audio_task_queue ORDER BY submitted_at DESC LIMIT ?1",
        };
        let mut stmt = conn.prepare(sql).map_err(map_db_err)?;
        let rows = if let Some(s) = status {
            stmt.query_map(params![s, limit], map_task_row).map_err(map_db_err)?
        } else {
            stmt.query_map(params![limit], map_task_row).map_err(map_db_err)?
        };
        let mut result = Vec::new();
        for r in rows {
            result.push(r.map_err(map_db_err)?);
        }
        Ok(result)
    }

    pub async fn update_task_status_new(&self, id: &str, status: &str, error: Option<&str>) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        let now = Utc::now().to_rfc3339();
        match status {
            "Executing" => {
                conn.execute(
                    "UPDATE audio_task_queue SET status = ?1, started_at = ?2 WHERE id = ?3",
                    params![status, now, id],
                ).map_err(map_db_err)?;
            }
            "Completed" | "Failed" | "Cancelled" => {
                conn.execute(
                    "UPDATE audio_task_queue SET status = ?1, error = ?2, completed_at = ?3 WHERE id = ?4",
                    params![status, error, now, id],
                ).map_err(map_db_err)?;
            }
            _ => {
                conn.execute(
                    "UPDATE audio_task_queue SET status = ?1 WHERE id = ?2",
                    params![status, id],
                ).map_err(map_db_err)?;
            }
        }
        Ok(())
    }

    pub async fn get_task_stats(&self) -> tfp_core::Result<TaskEngineStats> {
        let conn = self.conn().lock().await;
        let mut stmt = conn.prepare(
            "SELECT status, COUNT(*) FROM audio_task_queue GROUP BY status"
        ).map_err(map_db_err)?;
        let mut stats = TaskEngineStats::default();
        let rows = stmt.query_map([], |row| {
            Ok((row.get::<_, String>(0)?, row.get::<_, i64>(1)?))
        }).map_err(map_db_err)?;
        for row in rows {
            let (status, count) = row.map_err(map_db_err)?;
            match status.as_str() {
                "Queued" => stats.queued = count,
                "Executing" => stats.executing = count,
                "Completed" => stats.completed = count,
                "Failed" => stats.failed = count,
                "Cancelled" => stats.cancelled = count,
                _ => {}
            }
        }
        let total: i64 = conn.query_row(
            "SELECT COALESCE(SUM(COALESCE(prompt_tokens, 0) + COALESCE(completion_tokens, 0)), 0) FROM task_executions",
            [], |row| row.get(0),
        ).map_err(map_db_err)?;
        stats.total_tokens = total;
        Ok(stats)
    }

    pub async fn cleanup_expired_tasks(&self, days: u32) -> tfp_core::Result<u32> {
        let conn = self.conn().lock().await;
        let cutoff = format!("-{} days", days);
        conn.execute(
            "DELETE FROM task_executions WHERE task_id IN (
                SELECT id FROM audio_task_queue
                WHERE status IN ('Completed','Cancelled') AND updated_at < datetime('now', ?1)
            )", [&cutoff],
        ).map_err(map_db_err)?;
        let deleted = conn.execute(
            "DELETE FROM audio_task_queue WHERE status IN ('Completed','Cancelled') AND updated_at < datetime('now', ?1)",
            [&cutoff],
        ).map_err(map_db_err)?;
        Ok(deleted as u32)
    }

    pub async fn get_task_executions(&self, task_id: &str) -> tfp_core::Result<Vec<TaskExecutionRow>> {
        let conn = self.conn().lock().await;
        let mut stmt = conn.prepare(
            "SELECT id, task_id, attempt, status, error, prompt_tokens, completion_tokens, duration_ms, started_at, completed_at
             FROM task_executions WHERE task_id = ?1 ORDER BY attempt ASC",
        ).map_err(map_db_err)?;
        let rows = stmt.query_map(params![task_id], |row| {
            Ok(TaskExecutionRow {
                id: row.get(0)?,
                task_id: row.get(1)?,
                attempt: row.get(2)?,
                status: row.get(3)?,
                error: row.get(4)?,
                prompt_tokens: row.get(5)?,
                completion_tokens: row.get(6)?,
                duration_ms: row.get(7)?,
                started_at: row.get(8)?,
                completed_at: row.get(9)?,
            })
        }).map_err(map_db_err)?;
        let mut result = Vec::new();
        for r in rows {
            result.push(r.map_err(map_db_err)?);
        }
        Ok(result)
    }
}

fn map_task_row(row: &rusqlite::Row) -> rusqlite::Result<AudioTaskRow> {
    Ok(AudioTaskRow {
        id: row.get(0)?,
        audio_item_id: row.get(1)?,
        stage: row.get(2)?,
        task_type: row.get(3)?,
        status: row.get(4)?,
        priority: row.get(5)?,
        retry_count: row.get(6)?,
        max_retries: row.get(7)?,
        progress: row.get(8)?,
        prompt_text: row.get(9)?,
        result_text: row.get(10)?,
        error: row.get(11)?,
        submitted_at: row.get(12)?,
        started_at: row.get(13)?,
        completed_at: row.get(14)?,
    })
}
