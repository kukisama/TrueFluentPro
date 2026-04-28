use rusqlite::params;

use crate::db::{Database, map_db_err};
use crate::studio_repo::map_studio_task;
use tfp_core::{
    AudioFile, AudioTranscript, AudioSegment, AudioStageOutput,
    AudioResearchTopic, AudioAutoTag, AudioStagePreset, StudioTask,
};

// ── Internal row mapper ──

pub(crate) fn map_audio_file(row: &rusqlite::Row) -> rusqlite::Result<AudioFile> {
    Ok(AudioFile {
        id: row.get(0)?,
        display_name: row.get(1)?,
        source_path: row.get(2)?,
        mp3_path: row.get(3)?,
        sample_rate: row.get(4)?,
        channels: row.get(5)?,
        duration_ms: row.get(6)?,
        file_size_bytes: row.get(7)?,
        sha256: row.get(8)?,
        imported_at: row.get(9)?,
        last_opened_at: row.get(10)?,
        is_legacy_import: row.get::<_, i64>(11)? != 0,
        legacy_source_path: row.get(12)?,
        import_batch_id: row.get(13)?,
        session_id: row.get(14)?,
    })
}

// ── Database impl ──

impl Database {
    // ── File CRUD (5) ──

    pub async fn audiolab_insert_file(&self, f: &AudioFile) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute(
            "INSERT INTO audio_files (id, display_name, source_path, mp3_path, sample_rate, channels, duration_ms, file_size_bytes, sha256, imported_at, last_opened_at, is_legacy_import, legacy_source_path, import_batch_id)
             VALUES (?1,?2,?3,?4,?5,?6,?7,?8,?9,?10,?11,?12,?13,?14)",
            params![
                f.id, f.display_name, f.source_path, f.mp3_path,
                f.sample_rate, f.channels, f.duration_ms, f.file_size_bytes,
                f.sha256, f.imported_at, f.last_opened_at,
                f.is_legacy_import as i64, f.legacy_source_path, f.import_batch_id
            ],
        ).map_err(map_db_err)?;
        Ok(())
    }

    pub async fn audiolab_list_files(
        &self,
        limit: i64,
        offset: i64,
        search: Option<&str>,
        sort: Option<&str>,
    ) -> tfp_core::Result<Vec<AudioFile>> {
        let conn = self.conn().lock().await;
        let order = match sort {
            Some("duration") => "f.duration_ms DESC",
            Some("name") => "f.display_name ASC",
            _ => "f.imported_at DESC",
        };
        let cols = "f.id,f.display_name,f.source_path,f.mp3_path,f.sample_rate,f.channels,\
                    f.duration_ms,f.file_size_bytes,f.sha256,f.imported_at,f.last_opened_at,\
                    f.is_legacy_import,f.legacy_source_path,f.import_batch_id,s.id";
        let sql = if search.is_some() {
            format!(
                "SELECT {cols} FROM audio_files f \
                 LEFT JOIN studio_sessions s ON s.source_asset_id=f.id AND s.session_type='audio' \
                 WHERE f.display_name LIKE ?1 ORDER BY {order} LIMIT ?2 OFFSET ?3"
            )
        } else {
            format!(
                "SELECT {cols} FROM audio_files f \
                 LEFT JOIN studio_sessions s ON s.source_asset_id=f.id AND s.session_type='audio' \
                 ORDER BY {order} LIMIT ?1 OFFSET ?2"
            )
        };
        let mut stmt = conn.prepare(&sql).map_err(map_db_err)?;
        let rows = if let Some(q) = search {
            let pattern = format!("%{q}%");
            stmt.query_map(params![pattern, limit, offset], map_audio_file)
                .map_err(map_db_err)?
                .collect::<Result<Vec<_>, _>>()
                .map_err(map_db_err)?
        } else {
            stmt.query_map(params![limit, offset], map_audio_file)
                .map_err(map_db_err)?
                .collect::<Result<Vec<_>, _>>()
                .map_err(map_db_err)?
        };
        Ok(rows)
    }

    pub async fn audiolab_get_file(&self, file_id: &str) -> tfp_core::Result<Option<AudioFile>> {
        let conn = self.conn().lock().await;
        let mut stmt = conn.prepare(
            "SELECT f.id,f.display_name,f.source_path,f.mp3_path,f.sample_rate,f.channels,\
             f.duration_ms,f.file_size_bytes,f.sha256,f.imported_at,f.last_opened_at,\
             f.is_legacy_import,f.legacy_source_path,f.import_batch_id,s.id \
             FROM audio_files f \
             LEFT JOIN studio_sessions s ON s.source_asset_id=f.id AND s.session_type='audio' \
             WHERE f.id=?1"
        ).map_err(map_db_err)?;
        let mut rows = stmt.query(params![file_id]).map_err(map_db_err)?;
        if let Some(row) = rows.next().map_err(map_db_err)? {
            Ok(Some(map_audio_file(row).map_err(map_db_err)?))
        } else {
            Ok(None)
        }
    }

    pub async fn audiolab_remove_file(&self, file_id: &str) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute("DELETE FROM audio_files WHERE id=?1", params![file_id])
            .map_err(map_db_err)?;
        Ok(())
    }

    pub async fn audiolab_update_last_opened(&self, file_id: &str) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        let now = chrono::Utc::now().to_rfc3339();
        conn.execute(
            "UPDATE audio_files SET last_opened_at=?1 WHERE id=?2",
            params![now, file_id],
        ).map_err(map_db_err)?;
        Ok(())
    }

    // ── Transcript (4) ──

    pub async fn audiolab_insert_transcript(&self, t: &AudioTranscript) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute(
            "INSERT INTO audio_transcripts (id, session_id, audio_file_id, language, raw_json, parser_kind, created_at)
             VALUES (?1,?2,?3,?4,?5,?6,?7)",
            params![t.id, t.session_id, t.audio_file_id, t.language, t.raw_json, t.parser_kind, t.created_at],
        ).map_err(map_db_err)?;
        Ok(())
    }

    pub async fn audiolab_get_transcript(&self, session_id: &str) -> tfp_core::Result<Option<AudioTranscript>> {
        let conn = self.conn().lock().await;
        let mut stmt = conn.prepare(
            "SELECT id,session_id,audio_file_id,language,raw_json,parser_kind,created_at \
             FROM audio_transcripts WHERE session_id=?1 ORDER BY created_at DESC LIMIT 1"
        ).map_err(map_db_err)?;
        let mut rows = stmt.query(params![session_id]).map_err(map_db_err)?;
        if let Some(row) = rows.next().map_err(map_db_err)? {
            Ok(Some(AudioTranscript {
                id: row.get(0).map_err(map_db_err)?,
                session_id: row.get(1).map_err(map_db_err)?,
                audio_file_id: row.get(2).map_err(map_db_err)?,
                language: row.get(3).map_err(map_db_err)?,
                raw_json: row.get(4).map_err(map_db_err)?,
                parser_kind: row.get(5).map_err(map_db_err)?,
                created_at: row.get(6).map_err(map_db_err)?,
            }))
        } else {
            Ok(None)
        }
    }

    pub async fn audiolab_insert_segments(&self, segments: &[AudioSegment]) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        let mut stmt = conn.prepare(
            "INSERT INTO audio_segments (id, transcript_id, sequence, speaker, speaker_index, start_ms, end_ms, text, confidence)
             VALUES (?1,?2,?3,?4,?5,?6,?7,?8,?9)"
        ).map_err(map_db_err)?;
        for s in segments {
            stmt.execute(params![
                s.id, s.transcript_id, s.sequence, s.speaker,
                s.speaker_index, s.start_ms, s.end_ms, s.text, s.confidence
            ]).map_err(map_db_err)?;
        }
        Ok(())
    }

    pub async fn audiolab_get_segments(&self, transcript_id: &str) -> tfp_core::Result<Vec<AudioSegment>> {
        let conn = self.conn().lock().await;
        let mut stmt = conn.prepare(
            "SELECT id,transcript_id,sequence,speaker,speaker_index,start_ms,end_ms,text,confidence \
             FROM audio_segments WHERE transcript_id=?1 ORDER BY sequence ASC"
        ).map_err(map_db_err)?;
        let rows = stmt.query_map(params![transcript_id], |row| {
            Ok(AudioSegment {
                id: row.get(0)?,
                transcript_id: row.get(1)?,
                sequence: row.get(2)?,
                speaker: row.get(3)?,
                speaker_index: row.get(4)?,
                start_ms: row.get(5)?,
                end_ms: row.get(6)?,
                text: row.get(7)?,
                confidence: row.get(8)?,
            })
        }).map_err(map_db_err)?;
        rows.collect::<Result<Vec<_>, _>>().map_err(map_db_err)
    }

    // ── Stage outputs (2) ──

    pub async fn audiolab_upsert_stage_output(&self, o: &AudioStageOutput) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute(
            "INSERT INTO audio_stage_outputs (id, session_id, stage_key, content_markdown, status, error_message, model_ref, generated_at, custom_stage_key, custom_is_mindmap)
             VALUES (?1,?2,?3,?4,?5,?6,?7,?8,?9,?10)
             ON CONFLICT(session_id, stage_key) DO UPDATE SET content_markdown=excluded.content_markdown, status=excluded.status, error_message=excluded.error_message, model_ref=excluded.model_ref, generated_at=excluded.generated_at",
            params![
                o.id, o.session_id, o.stage_key, o.content_markdown, o.status,
                o.error_message, o.model_ref, o.generated_at, o.custom_stage_key,
                o.custom_is_mindmap.map(|b| b as i64)
            ],
        ).map_err(map_db_err)?;
        Ok(())
    }

    pub async fn audiolab_get_stage_outputs(&self, session_id: &str) -> tfp_core::Result<Vec<AudioStageOutput>> {
        let conn = self.conn().lock().await;
        let mut stmt = conn.prepare(
            "SELECT id,session_id,stage_key,content_markdown,status,error_message,model_ref,generated_at,custom_stage_key,custom_is_mindmap \
             FROM audio_stage_outputs WHERE session_id=?1"
        ).map_err(map_db_err)?;
        let rows = stmt.query_map(params![session_id], |row| {
            let mindmap_val: Option<i64> = row.get(9)?;
            Ok(AudioStageOutput {
                id: row.get(0)?,
                session_id: row.get(1)?,
                stage_key: row.get(2)?,
                content_markdown: row.get(3)?,
                status: row.get(4)?,
                error_message: row.get(5)?,
                model_ref: row.get(6)?,
                generated_at: row.get(7)?,
                custom_stage_key: row.get(8)?,
                custom_is_mindmap: mindmap_val.map(|v| v != 0),
            })
        }).map_err(map_db_err)?;
        rows.collect::<Result<Vec<_>, _>>().map_err(map_db_err)
    }

    // ── Research topics (3) ──

    pub async fn audiolab_insert_research_topic(&self, t: &AudioResearchTopic) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute(
            "INSERT INTO audio_research_topics (id, session_id, title, description, status, report_markdown, created_at)
             VALUES (?1,?2,?3,?4,?5,?6,?7)",
            params![t.id, t.session_id, t.title, t.description, t.status, t.report_markdown, t.created_at],
        ).map_err(map_db_err)?;
        Ok(())
    }

    pub async fn audiolab_get_research_topics(&self, session_id: &str) -> tfp_core::Result<Vec<AudioResearchTopic>> {
        let conn = self.conn().lock().await;
        let mut stmt = conn.prepare(
            "SELECT id,session_id,title,description,status,report_markdown,created_at \
             FROM audio_research_topics WHERE session_id=?1 ORDER BY created_at ASC"
        ).map_err(map_db_err)?;
        let rows = stmt.query_map(params![session_id], |row| {
            Ok(AudioResearchTopic {
                id: row.get(0)?,
                session_id: row.get(1)?,
                title: row.get(2)?,
                description: row.get(3)?,
                status: row.get(4)?,
                report_markdown: row.get(5)?,
                created_at: row.get(6)?,
            })
        }).map_err(map_db_err)?;
        rows.collect::<Result<Vec<_>, _>>().map_err(map_db_err)
    }

    pub async fn audiolab_delete_research_topic(&self, topic_id: &str) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute("DELETE FROM audio_research_topics WHERE id=?1", params![topic_id])
            .map_err(map_db_err)?;
        Ok(())
    }

    // ── Auto tags (3) ──

    pub async fn audiolab_get_auto_tags(&self, session_id: &str) -> tfp_core::Result<Vec<AudioAutoTag>> {
        let conn = self.conn().lock().await;
        let mut stmt = conn.prepare(
            "SELECT id,session_id,tag,source,created_at \
             FROM audio_auto_tags WHERE session_id=?1 ORDER BY created_at ASC"
        ).map_err(map_db_err)?;
        let rows = stmt.query_map(params![session_id], |row| {
            Ok(AudioAutoTag {
                id: row.get(0)?,
                session_id: row.get(1)?,
                tag: row.get(2)?,
                source: row.get(3)?,
                created_at: row.get(4)?,
            })
        }).map_err(map_db_err)?;
        rows.collect::<Result<Vec<_>, _>>().map_err(map_db_err)
    }

    pub async fn audiolab_insert_auto_tag(&self, t: &AudioAutoTag) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute(
            "INSERT INTO audio_auto_tags (id, session_id, tag, source, created_at)
             VALUES (?1,?2,?3,?4,?5)",
            params![t.id, t.session_id, t.tag, t.source, t.created_at],
        ).map_err(map_db_err)?;
        Ok(())
    }

    pub async fn audiolab_remove_tag(&self, tag_id: &str) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute("DELETE FROM audio_auto_tags WHERE id=?1", params![tag_id])
            .map_err(map_db_err)?;
        Ok(())
    }

    // ── Stage presets (3) ──

    pub async fn audiolab_list_stage_presets(&self) -> tfp_core::Result<Vec<AudioStagePreset>> {
        let conn = self.conn().lock().await;
        let mut stmt = conn.prepare(
            "SELECT id,stage,display_name,system_prompt,show_in_tab,include_in_batch,is_enabled,display_mode,sort_order \
             FROM audio_stage_presets ORDER BY sort_order ASC"
        ).map_err(map_db_err)?;
        let rows = stmt.query_map([], |row| {
            Ok(AudioStagePreset {
                id: row.get(0)?,
                stage: row.get(1)?,
                display_name: row.get(2)?,
                system_prompt: row.get(3)?,
                show_in_tab: row.get::<_, i64>(4)? != 0,
                include_in_batch: row.get::<_, i64>(5)? != 0,
                is_enabled: row.get::<_, i64>(6)? != 0,
                display_mode: row.get(7)?,
                sort_order: row.get(8)?,
            })
        }).map_err(map_db_err)?;
        rows.collect::<Result<Vec<_>, _>>().map_err(map_db_err)
    }

    pub async fn audiolab_upsert_stage_preset(&self, p: &AudioStagePreset) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute(
            "INSERT INTO audio_stage_presets (id, stage, display_name, system_prompt, show_in_tab, include_in_batch, is_enabled, display_mode, sort_order)
             VALUES (?1,?2,?3,?4,?5,?6,?7,?8,?9)
             ON CONFLICT(stage) DO UPDATE SET display_name=excluded.display_name, system_prompt=excluded.system_prompt, show_in_tab=excluded.show_in_tab, include_in_batch=excluded.include_in_batch, is_enabled=excluded.is_enabled, display_mode=excluded.display_mode, sort_order=excluded.sort_order",
            params![
                p.id, p.stage, p.display_name, p.system_prompt,
                p.show_in_tab as i64, p.include_in_batch as i64, p.is_enabled as i64,
                p.display_mode, p.sort_order
            ],
        ).map_err(map_db_err)?;
        Ok(())
    }

    pub async fn audiolab_delete_stage_preset(&self, stage: &str) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute("DELETE FROM audio_stage_presets WHERE stage=?1", params![stage])
            .map_err(map_db_err)?;
        Ok(())
    }

    // ── Running tasks (1) ──

    pub async fn audiolab_list_running_tasks(&self, session_id: &str) -> tfp_core::Result<Vec<StudioTask>> {
        let conn = self.conn().lock().await;
        let mut stmt = conn.prepare(
            "SELECT * FROM studio_tasks WHERE session_id=?1 AND status IN ('pending','running') ORDER BY created_at"
        ).map_err(map_db_err)?;
        let rows = stmt.query_map(params![session_id], map_studio_task).map_err(map_db_err)?;
        let mut result = Vec::new();
        for r in rows {
            result.push(r.map_err(map_db_err)?);
        }
        Ok(result)
    }

    // ── Speaker/segment editing (2) ──

    pub async fn audiolab_rename_speaker(&self, transcript_id: &str, old_index: i64, new_label: &str) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute(
            "UPDATE audio_segments SET speaker=?1 WHERE transcript_id=?2 AND speaker_index=?3",
            params![new_label, transcript_id, old_index],
        ).map_err(map_db_err)?;
        Ok(())
    }

    pub async fn audiolab_update_segment(
        &self,
        segment_id: &str,
        text: Option<&str>,
        speaker: Option<&str>,
        start_ms: Option<i64>,
        end_ms: Option<i64>,
    ) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        if let Some(t) = text {
            conn.execute("UPDATE audio_segments SET text=?1 WHERE id=?2", params![t, segment_id])
                .map_err(map_db_err)?;
        }
        if let Some(s) = speaker {
            conn.execute("UPDATE audio_segments SET speaker=?1 WHERE id=?2", params![s, segment_id])
                .map_err(map_db_err)?;
        }
        if let Some(ms) = start_ms {
            conn.execute("UPDATE audio_segments SET start_ms=?1 WHERE id=?2", params![ms, segment_id])
                .map_err(map_db_err)?;
        }
        if let Some(ms) = end_ms {
            conn.execute("UPDATE audio_segments SET end_ms=?1 WHERE id=?2", params![ms, segment_id])
                .map_err(map_db_err)?;
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests;
