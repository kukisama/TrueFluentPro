use std::collections::HashMap;

use rusqlite::params;
use chrono::Utc;

use crate::db::{Database, map_db_err};
use tfp_core::{
    StudioSession, StudioMessage, StudioMediaRef, StudioCitation,
    StudioAttachment, StudioTask, StudioAsset, StudioReferenceImage,
    StudioSessionBundle,
};

// ── Internal row mappers ──

fn map_studio_session(row: &rusqlite::Row) -> rusqlite::Result<StudioSession> {
    Ok(StudioSession {
        id: row.get("id")?,
        session_type: row.get("session_type")?,
        name: row.get("name")?,
        directory_path: row.get("directory_path")?,
        canvas_mode: row.get("canvas_mode")?,
        media_kind: row.get("media_kind")?,
        is_deleted: row.get::<_, i64>("is_deleted")? != 0,
        created_at: row.get("created_at")?,
        updated_at: row.get("updated_at")?,
        last_accessed_at: row.get("last_accessed_at")?,
        source_session_id: row.get("source_session_id")?,
        source_session_name: row.get("source_session_name")?,
        source_session_directory_name: row.get("source_session_directory_name")?,
        source_asset_id: row.get("source_asset_id")?,
        source_asset_kind: row.get("source_asset_kind")?,
        source_asset_file_name: row.get("source_asset_file_name")?,
        source_asset_path: row.get("source_asset_path")?,
        source_preview_path: row.get("source_preview_path")?,
        source_reference_role: row.get("source_reference_role")?,
        message_count: row.get("message_count")?,
        task_count: row.get("task_count")?,
        asset_count: row.get("asset_count")?,
        latest_message_preview: row.get("latest_message_preview")?,
        legacy_source_path: row.get("legacy_source_path")?,
        import_batch_id: row.get("import_batch_id")?,
        imported_at: row.get("imported_at")?,
        is_legacy_import: row.get::<_, i64>("is_legacy_import")? != 0,
    })
}

fn map_studio_message(row: &rusqlite::Row) -> rusqlite::Result<StudioMessage> {
    Ok(StudioMessage {
        id: row.get("id")?,
        session_id: row.get("session_id")?,
        sequence_no: row.get("sequence_no")?,
        role: row.get("role")?,
        content_type: row.get("content_type")?,
        text: row.get("text")?,
        reasoning_text: row.get("reasoning_text")?,
        prompt_tokens: row.get("prompt_tokens")?,
        completion_tokens: row.get("completion_tokens")?,
        generate_seconds: row.get("generate_seconds")?,
        download_seconds: row.get("download_seconds")?,
        search_summary: row.get("search_summary")?,
        timestamp: row.get("timestamp")?,
        is_deleted: row.get::<_, i64>("is_deleted")? != 0,
    })
}

pub(crate) fn map_studio_task(row: &rusqlite::Row) -> rusqlite::Result<StudioTask> {
    Ok(StudioTask {
        id: row.get("id")?,
        session_id: row.get("session_id")?,
        task_type: row.get("task_type")?,
        status: row.get("status")?,
        prompt: row.get("prompt")?,
        progress: row.get("progress")?,
        result_file_path: row.get("result_file_path")?,
        error_message: row.get("error_message")?,
        has_reference_input: row.get::<_, i64>("has_reference_input")? != 0,
        remote_video_id: row.get("remote_video_id")?,
        remote_video_api_mode: row.get("remote_video_api_mode")?,
        remote_generation_id: row.get("remote_generation_id")?,
        remote_download_url: row.get("remote_download_url")?,
        generate_seconds: row.get("generate_seconds")?,
        download_seconds: row.get("download_seconds")?,
        created_at: row.get("created_at")?,
        updated_at: row.get("updated_at")?,
    })
}

// ── Database impl ──

impl Database {
    // ── Session CRUD (8) ──

    pub async fn studio_create_session(&self, s: &StudioSession) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute(
            "INSERT INTO studio_sessions (
                id, session_type, name, directory_path, canvas_mode, media_kind,
                is_deleted, created_at, updated_at, last_accessed_at,
                source_session_id, source_session_name, source_session_directory_name,
                source_asset_id, source_asset_kind, source_asset_file_name,
                source_asset_path, source_preview_path, source_reference_role,
                message_count, task_count, asset_count, latest_message_preview,
                legacy_source_path, import_batch_id, imported_at, is_legacy_import
            ) VALUES (
                ?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10,
                ?11, ?12, ?13, ?14, ?15, ?16, ?17, ?18, ?19,
                ?20, ?21, ?22, ?23, ?24, ?25, ?26, ?27
            )",
            params![
                s.id, s.session_type, s.name, s.directory_path, s.canvas_mode, s.media_kind,
                s.is_deleted as i64, s.created_at, s.updated_at, s.last_accessed_at,
                s.source_session_id, s.source_session_name, s.source_session_directory_name,
                s.source_asset_id, s.source_asset_kind, s.source_asset_file_name,
                s.source_asset_path, s.source_preview_path, s.source_reference_role,
                s.message_count, s.task_count, s.asset_count, s.latest_message_preview,
                s.legacy_source_path, s.import_batch_id, s.imported_at, s.is_legacy_import as i64,
            ],
        ).map_err(map_db_err)?;
        Ok(())
    }

    pub async fn studio_get_session(&self, id: &str) -> tfp_core::Result<Option<StudioSession>> {
        let conn = self.conn().lock().await;
        let mut stmt = conn.prepare("SELECT * FROM studio_sessions WHERE id = ?1")
            .map_err(map_db_err)?;
        let mut rows = stmt.query_map(params![id], map_studio_session).map_err(map_db_err)?;
        match rows.next() {
            Some(Ok(s)) => Ok(Some(s)),
            Some(Err(e)) => Err(map_db_err(e)),
            None => Ok(None),
        }
    }

    pub async fn studio_list_sessions(&self, limit: i64, offset: i64) -> tfp_core::Result<Vec<StudioSession>> {
        let conn = self.conn().lock().await;
        let mut stmt = conn.prepare(
            "SELECT * FROM studio_sessions WHERE is_deleted = 0 ORDER BY updated_at DESC, id DESC LIMIT ?1 OFFSET ?2"
        ).map_err(map_db_err)?;
        let rows = stmt.query_map(params![limit, offset], map_studio_session).map_err(map_db_err)?;
        let mut result = Vec::new();
        for r in rows {
            result.push(r.map_err(map_db_err)?);
        }
        Ok(result)
    }

    pub async fn studio_rename_session(&self, id: &str, new_name: &str) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        let now = Utc::now().to_rfc3339();
        conn.execute(
            "UPDATE studio_sessions SET name = ?1, updated_at = ?2 WHERE id = ?3",
            params![new_name, now, id],
        ).map_err(map_db_err)?;
        Ok(())
    }

    pub async fn studio_soft_delete_session(&self, id: &str) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        let now = Utc::now().to_rfc3339();
        conn.execute(
            "UPDATE studio_sessions SET is_deleted = 1, updated_at = ?1 WHERE id = ?2",
            params![now, id],
        ).map_err(map_db_err)?;
        Ok(())
    }

    pub async fn studio_update_counts(&self, id: &str, mc: i64, tc: i64, ac: i64) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        let now = Utc::now().to_rfc3339();
        conn.execute(
            "UPDATE studio_sessions SET message_count = ?1, task_count = ?2, asset_count = ?3, updated_at = ?4 WHERE id = ?5",
            params![mc, tc, ac, now, id],
        ).map_err(map_db_err)?;
        Ok(())
    }

    pub async fn studio_update_last_accessed(&self, id: &str) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        let now = Utc::now().to_rfc3339();
        conn.execute(
            "UPDATE studio_sessions SET last_accessed_at = ?1 WHERE id = ?2",
            params![now, id],
        ).map_err(map_db_err)?;
        Ok(())
    }

    pub async fn studio_update_latest_preview(&self, id: &str, preview: &str) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        let now = Utc::now().to_rfc3339();
        conn.execute(
            "UPDATE studio_sessions SET latest_message_preview = ?1, updated_at = ?2 WHERE id = ?3",
            params![preview, now, id],
        ).map_err(map_db_err)?;
        Ok(())
    }

    // ── Message CRUD (5) ──

    pub async fn studio_append_message(&self, msg: &StudioMessage) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute(
            "INSERT INTO studio_messages (
                id, session_id, sequence_no, role, content_type, text, reasoning_text,
                prompt_tokens, completion_tokens, generate_seconds, download_seconds,
                search_summary, timestamp, is_deleted
            ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, 0)",
            params![
                msg.id, msg.session_id, msg.sequence_no, msg.role, msg.content_type,
                msg.text, msg.reasoning_text, msg.prompt_tokens, msg.completion_tokens,
                msg.generate_seconds, msg.download_seconds, msg.search_summary, msg.timestamp,
            ],
        ).map_err(map_db_err)?;
        conn.execute(
            "UPDATE studio_sessions SET message_count = message_count + 1, updated_at = ?1 WHERE id = ?2",
            params![msg.timestamp, msg.session_id],
        ).map_err(map_db_err)?;
        Ok(())
    }

    pub async fn studio_update_message(&self, msg: &StudioMessage) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute(
            "UPDATE studio_messages SET text = ?1, reasoning_text = ?2, prompt_tokens = ?3,
             completion_tokens = ?4, generate_seconds = ?5, download_seconds = ?6,
             search_summary = ?7, timestamp = ?8 WHERE id = ?9",
            params![
                msg.text, msg.reasoning_text, msg.prompt_tokens, msg.completion_tokens,
                msg.generate_seconds, msg.download_seconds, msg.search_summary, msg.timestamp, msg.id,
            ],
        ).map_err(map_db_err)?;
        Ok(())
    }

    pub async fn studio_get_max_sequence(&self, session_id: &str) -> tfp_core::Result<i64> {
        let conn = self.conn().lock().await;
        let seq: i64 = conn.query_row(
            "SELECT COALESCE(MAX(sequence_no), 0) FROM studio_messages WHERE session_id = ?1",
            params![session_id],
            |row| row.get(0),
        ).map_err(map_db_err)?;
        Ok(seq)
    }

    pub async fn studio_get_messages_before(
        &self,
        session_id: &str,
        before_seq: i64,
        limit: i64,
    ) -> tfp_core::Result<Vec<StudioMessage>> {
        let conn = self.conn().lock().await;
        let mut stmt = conn.prepare(
            "SELECT * FROM studio_messages WHERE session_id = ?1 AND is_deleted = 0 AND sequence_no < ?2
             ORDER BY sequence_no DESC LIMIT ?3"
        ).map_err(map_db_err)?;
        let rows = stmt.query_map(params![session_id, before_seq, limit], map_studio_message)
            .map_err(map_db_err)?;
        let mut result: Vec<StudioMessage> = Vec::new();
        for r in rows {
            result.push(r.map_err(map_db_err)?);
        }
        result.reverse();
        Ok(result)
    }

    pub async fn studio_get_session_bundle(&self, session_id: &str) -> tfp_core::Result<StudioSessionBundle> {
        let conn = self.conn().lock().await;

        // 1. All messages
        let mut msg_stmt = conn.prepare(
            "SELECT * FROM studio_messages WHERE session_id = ?1 AND is_deleted = 0 ORDER BY sequence_no ASC"
        ).map_err(map_db_err)?;
        let messages: Vec<StudioMessage> = {
            let rows = msg_stmt.query_map(params![session_id], map_studio_message).map_err(map_db_err)?;
            let mut v = Vec::new();
            for r in rows {
                v.push(r.map_err(map_db_err)?);
            }
            v
        };
        if messages.is_empty() {
            return Ok(StudioSessionBundle {
                messages,
                media_refs: HashMap::new(),
                citations: HashMap::new(),
                attachments: HashMap::new(),
            });
        }

        let ids: Vec<String> = messages.iter().map(|m| m.id.clone()).collect();
        let placeholders: String = ids.iter().enumerate()
            .map(|(i, _)| format!("?{}", i + 1))
            .collect::<Vec<_>>()
            .join(",");

        // Dynamic params helper
        let params_vec: Vec<Box<dyn rusqlite::types::ToSql>> = ids.iter()
            .map(|id| Box::new(id.clone()) as Box<dyn rusqlite::types::ToSql>)
            .collect();
        let param_refs: Vec<&dyn rusqlite::types::ToSql> = params_vec.iter()
            .map(|p| p.as_ref())
            .collect();

        // 2. Media refs
        let mut media_refs: HashMap<String, Vec<StudioMediaRef>> = HashMap::new();
        {
            let sql = format!(
                "SELECT * FROM studio_media_refs WHERE message_id IN ({}) ORDER BY sort_order",
                placeholders
            );
            let mut stmt = conn.prepare(&sql).map_err(map_db_err)?;
            let rows = stmt.query_map(param_refs.as_slice(), |row| {
                Ok(StudioMediaRef {
                    id: row.get("id")?,
                    message_id: row.get("message_id")?,
                    media_path: row.get("media_path")?,
                    media_kind: row.get("media_kind")?,
                    sort_order: row.get("sort_order")?,
                    preview_path: row.get("preview_path")?,
                })
            }).map_err(map_db_err)?;
            for r in rows {
                let rec = r.map_err(map_db_err)?;
                media_refs.entry(rec.message_id.clone()).or_default().push(rec);
            }
        }

        // 3. Citations
        let mut citations: HashMap<String, Vec<StudioCitation>> = HashMap::new();
        {
            let sql = format!(
                "SELECT * FROM studio_citations WHERE message_id IN ({}) ORDER BY citation_number",
                placeholders
            );
            let mut stmt = conn.prepare(&sql).map_err(map_db_err)?;
            // Rebuild param refs (previous borrow ended)
            let params_vec2: Vec<Box<dyn rusqlite::types::ToSql>> = ids.iter()
                .map(|id| Box::new(id.clone()) as Box<dyn rusqlite::types::ToSql>)
                .collect();
            let param_refs2: Vec<&dyn rusqlite::types::ToSql> = params_vec2.iter()
                .map(|p| p.as_ref())
                .collect();
            let rows = stmt.query_map(param_refs2.as_slice(), |row| {
                Ok(StudioCitation {
                    id: row.get("id")?,
                    message_id: row.get("message_id")?,
                    citation_number: row.get("citation_number")?,
                    title: row.get("title")?,
                    url: row.get("url")?,
                    snippet: row.get("snippet")?,
                    hostname: row.get("hostname")?,
                })
            }).map_err(map_db_err)?;
            for r in rows {
                let rec = r.map_err(map_db_err)?;
                citations.entry(rec.message_id.clone()).or_default().push(rec);
            }
        }

        // 4. Attachments
        let mut attachments: HashMap<String, Vec<StudioAttachment>> = HashMap::new();
        {
            let sql = format!(
                "SELECT * FROM studio_attachments WHERE message_id IN ({}) ORDER BY sort_order",
                placeholders
            );
            let mut stmt = conn.prepare(&sql).map_err(map_db_err)?;
            let params_vec3: Vec<Box<dyn rusqlite::types::ToSql>> = ids.iter()
                .map(|id| Box::new(id.clone()) as Box<dyn rusqlite::types::ToSql>)
                .collect();
            let param_refs3: Vec<&dyn rusqlite::types::ToSql> = params_vec3.iter()
                .map(|p| p.as_ref())
                .collect();
            let rows = stmt.query_map(param_refs3.as_slice(), |row| {
                Ok(StudioAttachment {
                    id: row.get("id")?,
                    message_id: row.get("message_id")?,
                    attachment_type: row.get("attachment_type")?,
                    file_name: row.get("file_name")?,
                    file_path: row.get("file_path")?,
                    file_size: row.get("file_size")?,
                    sort_order: row.get("sort_order")?,
                })
            }).map_err(map_db_err)?;
            for r in rows {
                let rec = r.map_err(map_db_err)?;
                attachments.entry(rec.message_id.clone()).or_default().push(rec);
            }
        }

        Ok(StudioSessionBundle { messages, media_refs, citations, attachments })
    }

    // ── Task CRUD (7) ──

    pub async fn studio_upsert_task(&self, t: &StudioTask) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute(
            "INSERT INTO studio_tasks (
                id, session_id, task_type, status, prompt, progress,
                result_file_path, error_message, has_reference_input,
                remote_video_id, remote_video_api_mode, remote_generation_id, remote_download_url,
                generate_seconds, download_seconds, created_at, updated_at
            ) VALUES (?1,?2,?3,?4,?5,?6,?7,?8,?9,?10,?11,?12,?13,?14,?15,?16,?17)
            ON CONFLICT(id) DO UPDATE SET
                status=excluded.status, progress=excluded.progress,
                result_file_path=excluded.result_file_path, error_message=excluded.error_message,
                remote_video_id=excluded.remote_video_id, remote_video_api_mode=excluded.remote_video_api_mode,
                remote_generation_id=excluded.remote_generation_id, remote_download_url=excluded.remote_download_url,
                generate_seconds=excluded.generate_seconds, download_seconds=excluded.download_seconds,
                updated_at=excluded.updated_at",
            params![
                t.id, t.session_id, t.task_type, t.status, t.prompt, t.progress,
                t.result_file_path, t.error_message, t.has_reference_input as i64,
                t.remote_video_id, t.remote_video_api_mode, t.remote_generation_id, t.remote_download_url,
                t.generate_seconds, t.download_seconds, t.created_at, t.updated_at,
            ],
        ).map_err(map_db_err)?;
        Ok(())
    }

    pub async fn studio_list_running_tasks(&self, session_id: &str) -> tfp_core::Result<Vec<StudioTask>> {
        let conn = self.conn().lock().await;
        let mut stmt = conn.prepare(
            "SELECT * FROM studio_tasks WHERE session_id = ?1 AND status IN ('pending','running') ORDER BY created_at"
        ).map_err(map_db_err)?;
        let rows = stmt.query_map(params![session_id], map_studio_task).map_err(map_db_err)?;
        let mut result = Vec::new();
        for r in rows {
            result.push(r.map_err(map_db_err)?);
        }
        Ok(result)
    }

    pub async fn studio_list_all_running_tasks(&self) -> tfp_core::Result<Vec<StudioTask>> {
        let conn = self.conn().lock().await;
        let mut stmt = conn.prepare(
            "SELECT * FROM studio_tasks WHERE status IN ('pending','running') ORDER BY created_at"
        ).map_err(map_db_err)?;
        let rows = stmt.query_map([], map_studio_task).map_err(map_db_err)?;
        let mut result = Vec::new();
        for r in rows {
            result.push(r.map_err(map_db_err)?);
        }
        Ok(result)
    }

    pub async fn studio_get_interrupted_video_tasks(&self) -> tfp_core::Result<Vec<StudioTask>> {
        let conn = self.conn().lock().await;
        let mut stmt = conn.prepare(
            "SELECT * FROM studio_tasks WHERE status = 'running' AND remote_video_id IS NOT NULL ORDER BY created_at"
        ).map_err(map_db_err)?;
        let rows = stmt.query_map([], map_studio_task).map_err(map_db_err)?;
        let mut result = Vec::new();
        for r in rows {
            result.push(r.map_err(map_db_err)?);
        }
        Ok(result)
    }

    pub async fn studio_update_task_status(&self, task_id: &str, status: &str, error: Option<&str>) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        let now = Utc::now().to_rfc3339();
        conn.execute(
            "UPDATE studio_tasks SET status = ?1, error_message = ?2, updated_at = ?3 WHERE id = ?4",
            params![status, error, now, task_id],
        ).map_err(map_db_err)?;
        Ok(())
    }

    pub async fn studio_update_task_progress(&self, task_id: &str, progress: f64) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        let now = Utc::now().to_rfc3339();
        conn.execute(
            "UPDATE studio_tasks SET progress = ?1, updated_at = ?2 WHERE id = ?3",
            params![progress, now, task_id],
        ).map_err(map_db_err)?;
        Ok(())
    }

    pub async fn studio_update_task_result(&self, task_id: &str, result_path: &str, gen_secs: Option<f64>, dl_secs: Option<f64>) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        let now = Utc::now().to_rfc3339();
        conn.execute(
            "UPDATE studio_tasks SET status = 'completed', result_file_path = ?1, generate_seconds = ?2, download_seconds = ?3, progress = 1.0, updated_at = ?4 WHERE id = ?5",
            params![result_path, gen_secs, dl_secs, now, task_id],
        ).map_err(map_db_err)?;
        Ok(())
    }

    // ── Reference images (3) ──

    pub async fn studio_add_reference_image(&self, img: &StudioReferenceImage) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute(
            "INSERT INTO studio_reference_images (id, session_id, file_path, sort_order, width, height, created_at)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)",
            params![img.id, img.session_id, img.file_path, img.sort_order, img.width, img.height, img.created_at],
        ).map_err(map_db_err)?;
        Ok(())
    }

    pub async fn studio_list_reference_images(&self, session_id: &str) -> tfp_core::Result<Vec<StudioReferenceImage>> {
        let conn = self.conn().lock().await;
        let mut stmt = conn.prepare(
            "SELECT id, session_id, file_path, sort_order, width, height, created_at
             FROM studio_reference_images WHERE session_id = ?1 ORDER BY sort_order"
        ).map_err(map_db_err)?;
        let rows = stmt.query_map(params![session_id], |row| {
            Ok(StudioReferenceImage {
                id: row.get(0)?,
                session_id: row.get(1)?,
                file_path: row.get(2)?,
                sort_order: row.get(3)?,
                width: row.get(4)?,
                height: row.get(5)?,
                created_at: row.get(6)?,
            })
        }).map_err(map_db_err)?;
        let mut result = Vec::new();
        for r in rows {
            result.push(r.map_err(map_db_err)?);
        }
        Ok(result)
    }

    pub async fn studio_delete_reference_image(&self, id: &str) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute("DELETE FROM studio_reference_images WHERE id = ?1", params![id])
            .map_err(map_db_err)?;
        Ok(())
    }

    // ── Asset + Media refs (2) ──

    pub async fn studio_insert_asset(&self, a: &StudioAsset) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute(
            "INSERT OR REPLACE INTO studio_assets (
                asset_id, session_id, group_id, kind, workflow, file_name, file_path, preview_path, prompt_text,
                file_size, mime_type, width, height, duration_ms, created_at, modified_at, storage_scope,
                derived_from_session_id, derived_from_session_name, derived_from_asset_id,
                derived_from_asset_file_name, derived_from_asset_kind, derived_from_reference_role
            ) VALUES (?1,?2,?3,?4,?5,?6,?7,?8,?9,?10,?11,?12,?13,?14,?15,?16,?17,?18,?19,?20,?21,?22,?23)",
            params![
                a.asset_id, a.session_id, a.group_id, a.kind, a.workflow, a.file_name, a.file_path,
                a.preview_path, a.prompt_text, a.file_size, a.mime_type, a.width, a.height, a.duration_ms,
                a.created_at, a.modified_at, a.storage_scope,
                a.derived_from_session_id, a.derived_from_session_name, a.derived_from_asset_id,
                a.derived_from_asset_file_name, a.derived_from_asset_kind, a.derived_from_reference_role,
            ],
        ).map_err(map_db_err)?;
        conn.execute(
            "UPDATE studio_sessions SET asset_count = (SELECT COUNT(*) FROM studio_assets WHERE session_id = ?1) WHERE id = ?1",
            params![a.session_id],
        ).map_err(map_db_err)?;
        Ok(())
    }

    pub async fn studio_insert_media_refs(&self, message_id: &str, refs: &[StudioMediaRef]) -> tfp_core::Result<()> {
        if refs.is_empty() { return Ok(()); }
        let conn = self.conn().lock().await;
        for mr in refs {
            conn.execute(
                "INSERT INTO studio_media_refs (message_id, media_path, media_kind, sort_order, preview_path)
                 VALUES (?1, ?2, ?3, ?4, ?5)",
                params![message_id, mr.media_path, mr.media_kind, mr.sort_order, mr.preview_path],
            ).map_err(map_db_err)?;
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests;
