use rusqlite::params;
use chrono::Utc;

use crate::db::{Database, map_db_err};
use tfp_core::{AudioLibraryItem, AudioLifecycleRow, AudioTaskRow, TaskExecutionRow, TaskEngineStats, BillingRecord, BillingSummary, BillingByModel};

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

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  Batch-27: Missing audio/task methods
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    pub async fn get_audio_item(&self, item_id: &str) -> tfp_core::Result<Option<AudioLibraryItem>> {
        let conn = self.conn().lock().await;
        let mut stmt = conn.prepare(
            "SELECT id, file_name, file_path, duration_ms, sample_rate, channels, source_lang, created_at, updated_at
             FROM audio_library_items WHERE id = ?1",
        ).map_err(map_db_err)?;
        let mut rows = stmt.query_map(params![item_id], |row| {
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
        match rows.next() {
            Some(Ok(item)) => Ok(Some(item)),
            Some(Err(e)) => Err(map_db_err(e)),
            None => Ok(None),
        }
    }

    pub async fn get_next_queued_task(&self) -> tfp_core::Result<Option<AudioTaskRow>> {
        let conn = self.conn().lock().await;
        let mut stmt = conn.prepare(
            "SELECT id, audio_item_id, stage, task_type, status, priority, retry_count, max_retries, progress, prompt_text, result_text, error, submitted_at, started_at, completed_at
             FROM audio_task_queue WHERE status = 'Queued' ORDER BY priority DESC, submitted_at ASC LIMIT 1"
        ).map_err(map_db_err)?;
        let mut rows = stmt.query_map([], map_task_row).map_err(map_db_err)?;
        match rows.next() {
            Some(Ok(task)) => Ok(Some(task)),
            Some(Err(e)) => Err(map_db_err(e)),
            None => Ok(None),
        }
    }

    /// Crash recovery — requeue tasks that were left in Executing state
    pub async fn recover_interrupted_tasks(&self) -> tfp_core::Result<usize> {
        let conn = self.conn().lock().await;
        conn.execute(
            "UPDATE audio_task_queue SET status = 'Queued', started_at = NULL, error = 'Crash recovery: previous execution interrupted' WHERE status = 'Executing'",
            [],
        ).map_err(map_db_err)
    }

    /// Atomically increment retry_count and requeue the task
    pub async fn increment_retry_and_requeue(&self, id: &str, error: &str) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute(
            "UPDATE audio_task_queue SET status = 'Queued', retry_count = retry_count + 1, error = ?1 WHERE id = ?2",
            params![error, id],
        ).map_err(map_db_err)?;
        Ok(())
    }

    pub async fn add_task_execution(&self, exec: &TaskExecutionRow) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute(
            "INSERT INTO task_executions (id, task_id, attempt, status, error, prompt_tokens, completion_tokens, duration_ms, started_at, completed_at)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10)",
            params![
                exec.id, exec.task_id, exec.attempt, exec.status,
                exec.error, exec.prompt_tokens, exec.completion_tokens,
                exec.duration_ms, exec.started_at, exec.completed_at,
            ],
        ).map_err(map_db_err)?;
        Ok(())
    }

    /// DAG cascade invalidation — reset downstream stages when an upstream stage is re-executed
    pub async fn invalidate_downstream_stages(&self, audio_item_id: &str, stage: &str) -> tfp_core::Result<usize> {
        let downstream = match stage {
            "Transcribed" => vec![
                "Summarized", "MindMap", "Insight", "Research",
                "PodcastScript", "PodcastAudio", "Translated",
            ],
            "Summarized" => vec![
                "MindMap", "Insight", "Research", "PodcastScript", "PodcastAudio",
            ],
            "Insight" => vec!["Research"],
            "PodcastScript" => vec!["PodcastAudio"],
            _ => vec![],
        };

        if downstream.is_empty() {
            return Ok(0);
        }

        let conn = self.conn().lock().await;

        // Check if this is a "redo" scenario — downstream has non-Pending lifecycle records
        let mut is_redo = false;
        for ds in &downstream {
            let id = format!("{}-{}", audio_item_id, ds);
            let count: i64 = conn.query_row(
                "SELECT COUNT(*) FROM audio_lifecycle WHERE id = ?1 AND status != 'Pending'",
                params![id],
                |row| row.get(0),
            ).unwrap_or(0);
            if count > 0 {
                is_redo = true;
                break;
            }
        }

        let mut total = 0usize;

        // Reset downstream lifecycles to Pending
        for ds in &downstream {
            let id = format!("{}-{}", audio_item_id, ds);
            let affected = conn.execute(
                "UPDATE audio_lifecycle SET status = 'Pending', result_text = NULL, result_json = NULL,
                 error = NULL, started_at = NULL, completed_at = NULL
                 WHERE id = ?1 AND status != 'Pending'",
                params![id],
            ).map_err(map_db_err)?;
            total += affected;
        }

        // Only cancel downstream Queued tasks in redo scenarios
        if is_redo {
            let placeholders: Vec<String> = downstream.iter().enumerate()
                .map(|(i, _)| format!("?{}", i + 2))
                .collect();
            let sql = format!(
                "UPDATE audio_task_queue SET status = 'Cancelled'
                 WHERE audio_item_id = ?1 AND stage IN ({}) AND status = 'Queued'",
                placeholders.join(", ")
            );
            let mut stmt = conn.prepare(&sql).map_err(map_db_err)?;
            let params_vec: Vec<Box<dyn rusqlite::types::ToSql>> = std::iter::once(
                Box::new(audio_item_id.to_string()) as Box<dyn rusqlite::types::ToSql>,
            )
            .chain(downstream.iter().map(|s| Box::new(s.to_string()) as Box<dyn rusqlite::types::ToSql>))
            .collect();
            let param_refs: Vec<&dyn rusqlite::types::ToSql> = params_vec.iter().map(|p| p.as_ref()).collect();
            let cancelled = stmt.execute(param_refs.as_slice()).map_err(map_db_err)?;
            total += cancelled;
        }

        Ok(total)
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  Batch-27: Billing record methods
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    pub async fn add_billing_record(&self, rec: &BillingRecord) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute(
            "INSERT INTO billing_records (id, task_id, endpoint_id, model_id, prompt_tokens, completion_tokens, cost_usd, created_at, status)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9)",
            params![
                rec.id, rec.task_id, rec.endpoint_id, rec.model_id,
                rec.prompt_tokens, rec.completion_tokens, rec.cost_usd, rec.created_at,
                rec.status,
            ],
        ).map_err(map_db_err)?;
        Ok(())
    }

    /// Update billing record status (Staging → Running → Landed → Committed)
    pub async fn update_billing_status(&self, id: &str, status: &str) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute(
            "UPDATE billing_records SET status = ?1 WHERE id = ?2",
            params![status, id],
        ).map_err(map_db_err)?;
        Ok(())
    }

    /// Update billing record token counts (backfill after task completion)
    pub async fn update_billing_tokens(&self, id: &str, prompt_tokens: i64, completion_tokens: i64) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute(
            "UPDATE billing_records SET prompt_tokens = ?1, completion_tokens = ?2 WHERE id = ?3",
            params![prompt_tokens, completion_tokens, id],
        ).map_err(map_db_err)?;
        Ok(())
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

fn map_billing_row(row: &rusqlite::Row) -> rusqlite::Result<BillingRecord> {
    Ok(BillingRecord {
        id: row.get(0)?,
        task_id: row.get(1)?,
        endpoint_id: row.get(2)?,
        model_id: row.get(3)?,
        prompt_tokens: row.get(4)?,
        completion_tokens: row.get(5)?,
        cost_usd: row.get(6)?,
        created_at: row.get(7)?,
        status: row.get(8)?,
    })
}

// ── Billing query methods ──

impl Database {
    /// Retrieve billing records ordered by most recent, up to `limit`.
    pub async fn get_billing_records(&self, limit: u32) -> tfp_core::Result<Vec<BillingRecord>> {
        let conn = self.conn().lock().await;
        let mut stmt = conn.prepare(
            "SELECT id, task_id, endpoint_id, model_id, prompt_tokens, completion_tokens, \
             cost_usd, created_at, COALESCE(status, 'Committed') as status \
             FROM billing_records ORDER BY created_at DESC LIMIT ?1",
        ).map_err(map_db_err)?;
        let rows = stmt.query_map(params![limit], map_billing_row).map_err(map_db_err)?;
        let mut result = Vec::new();
        for r in rows {
            result.push(r.map_err(map_db_err)?);
        }
        Ok(result)
    }

    /// Compute an aggregated billing summary with per-model breakdown.
    pub async fn get_billing_summary(&self) -> tfp_core::Result<BillingSummary> {
        let conn = self.conn().lock().await;

        let (total_prompt, total_completion, total_cost, count): (i64, i64, f64, i64) =
            conn.query_row(
                "SELECT COALESCE(SUM(prompt_tokens),0), COALESCE(SUM(completion_tokens),0), \
                 COALESCE(SUM(cost_usd),0.0), COUNT(*) FROM billing_records",
                [],
                |row| Ok((row.get(0)?, row.get(1)?, row.get(2)?, row.get(3)?)),
            ).map_err(map_db_err)?;

        let mut stmt = conn.prepare(
            "SELECT model_id, SUM(prompt_tokens), SUM(completion_tokens), \
             COALESCE(SUM(cost_usd),0.0), COUNT(*) \
             FROM billing_records GROUP BY model_id ORDER BY SUM(prompt_tokens) DESC",
        ).map_err(map_db_err)?;
        let by_model_rows = stmt.query_map([], |row| {
            Ok(BillingByModel {
                model_id: row.get(0)?,
                prompt_tokens: row.get(1)?,
                completion_tokens: row.get(2)?,
                cost_usd: row.get(3)?,
                count: row.get(4)?,
            })
        }).map_err(map_db_err)?;
        let mut by_model = Vec::new();
        for r in by_model_rows {
            by_model.push(r.map_err(map_db_err)?);
        }

        Ok(BillingSummary {
            total_prompt_tokens: total_prompt,
            total_completion_tokens: total_completion,
            total_cost_usd: total_cost,
            record_count: count,
            by_model,
        })
    }
}

#[cfg(test)]
mod tests;