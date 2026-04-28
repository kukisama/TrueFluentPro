use rusqlite::params;
use tfp_core::{TranslationSegment, TranslationSession};

use crate::db::{map_db_err, Database};

impl Database {
    pub async fn live_create_session(&self, session: &TranslationSession) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute(
            "INSERT INTO translation_sessions (id, started_at, stopped_at, source_lang, target_langs, provider, status)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)",
            params![
                session.id,
                session.started_at,
                session.stopped_at,
                session.source_lang,
                session.target_langs,
                session.provider,
                session.status,
            ],
        )
        .map_err(map_db_err)?;
        Ok(())
    }

    pub async fn live_stop_session(
        &self,
        session_id: &str,
        stopped_at: &str,
    ) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute(
            "UPDATE translation_sessions SET status = 'stopped', stopped_at = ?1 WHERE id = ?2",
            params![stopped_at, session_id],
        )
        .map_err(map_db_err)?;
        Ok(())
    }

    pub async fn live_get_active_session(
        &self,
    ) -> tfp_core::Result<Option<TranslationSession>> {
        let conn = self.conn().lock().await;
        let mut stmt = conn
            .prepare(
                "SELECT id, started_at, stopped_at, source_lang, target_langs, provider, status
                 FROM translation_sessions
                 WHERE status = 'active'
                 ORDER BY started_at DESC LIMIT 1",
            )
            .map_err(map_db_err)?;
        let mut rows = stmt.query([]).map_err(map_db_err)?;
        match rows.next().map_err(map_db_err)? {
            Some(row) => Ok(Some(map_session_row(row)?)),
            None => Ok(None),
        }
    }

    pub async fn live_list_sessions(
        &self,
        limit: u32,
        offset: u32,
    ) -> tfp_core::Result<Vec<TranslationSession>> {
        let conn = self.conn().lock().await;
        let mut stmt = conn
            .prepare(
                "SELECT id, started_at, stopped_at, source_lang, target_langs, provider, status
                 FROM translation_sessions
                 ORDER BY started_at DESC LIMIT ?1 OFFSET ?2",
            )
            .map_err(map_db_err)?;
        let rows = stmt
            .query_map(params![limit, offset], |row| {
                Ok(TranslationSession {
                    id: row.get(0)?,
                    started_at: row.get(1)?,
                    stopped_at: row.get(2)?,
                    source_lang: row.get(3)?,
                    target_langs: row.get(4)?,
                    provider: row.get(5)?,
                    status: row.get(6)?,
                })
            })
            .map_err(map_db_err)?;
        let mut result = Vec::new();
        for r in rows {
            result.push(r.map_err(map_db_err)?);
        }
        Ok(result)
    }

    pub async fn live_insert_segment(
        &self,
        seg: &TranslationSegment,
    ) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute(
            "INSERT INTO translation_segments
             (id, session_id, sequence, original_text, translated_text, target_lang,
              started_at, ended_at, is_bookmarked, bookmark_note, audio_path, raw_event_json)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12)",
            params![
                seg.id,
                seg.session_id,
                seg.sequence,
                seg.original_text,
                seg.translated_text,
                seg.target_lang,
                seg.started_at,
                seg.ended_at,
                seg.is_bookmarked,
                seg.bookmark_note,
                seg.audio_path,
                seg.raw_event_json,
            ],
        )
        .map_err(map_db_err)?;
        Ok(())
    }

    pub async fn live_get_recent_segments(
        &self,
        session_id: &str,
        limit: u32,
    ) -> tfp_core::Result<Vec<TranslationSegment>> {
        let conn = self.conn().lock().await;
        let mut stmt = conn
            .prepare(
                "SELECT id, session_id, sequence, original_text, translated_text, target_lang,
                        started_at, ended_at, is_bookmarked, bookmark_note, audio_path, raw_event_json
                 FROM translation_segments
                 WHERE session_id = ?1
                 ORDER BY sequence DESC LIMIT ?2",
            )
            .map_err(map_db_err)?;
        let rows = stmt
            .query_map(params![session_id, limit], map_segment_query_row)
            .map_err(map_db_err)?;
        let mut result = Vec::new();
        for r in rows {
            result.push(r.map_err(map_db_err)?);
        }
        result.reverse();
        Ok(result)
    }

    pub async fn live_get_max_sequence(&self, session_id: &str) -> tfp_core::Result<i64> {
        let conn = self.conn().lock().await;
        let val: i64 = conn
            .query_row(
                "SELECT COALESCE(MAX(sequence), 0) FROM translation_segments WHERE session_id = ?1",
                params![session_id],
                |row| row.get(0),
            )
            .map_err(map_db_err)?;
        Ok(val)
    }

    pub async fn live_bookmark_segment(
        &self,
        segment_id: &str,
        note: Option<&str>,
    ) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute(
            "UPDATE translation_segments SET is_bookmarked = 1, bookmark_note = ?1 WHERE id = ?2",
            params![note, segment_id],
        )
        .map_err(map_db_err)?;
        Ok(())
    }

    pub async fn live_unbookmark_segment(&self, segment_id: &str) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute(
            "UPDATE translation_segments SET is_bookmarked = 0, bookmark_note = NULL WHERE id = ?1",
            params![segment_id],
        )
        .map_err(map_db_err)?;
        Ok(())
    }

    pub async fn live_clear_session_segments(&self, session_id: &str) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute(
            "DELETE FROM translation_segments WHERE session_id = ?1",
            params![session_id],
        )
        .map_err(map_db_err)?;
        Ok(())
    }
}

fn map_session_row(row: &rusqlite::Row) -> tfp_core::Result<TranslationSession> {
    Ok(TranslationSession {
        id: row.get(0).map_err(map_db_err)?,
        started_at: row.get(1).map_err(map_db_err)?,
        stopped_at: row.get(2).map_err(map_db_err)?,
        source_lang: row.get(3).map_err(map_db_err)?,
        target_langs: row.get(4).map_err(map_db_err)?,
        provider: row.get(5).map_err(map_db_err)?,
        status: row.get(6).map_err(map_db_err)?,
    })
}

fn map_segment_query_row(row: &rusqlite::Row) -> rusqlite::Result<TranslationSegment> {
    Ok(TranslationSegment {
        id: row.get(0)?,
        session_id: row.get(1)?,
        sequence: row.get(2)?,
        original_text: row.get(3)?,
        translated_text: row.get(4)?,
        target_lang: row.get(5)?,
        started_at: row.get(6)?,
        ended_at: row.get(7)?,
        is_bookmarked: row.get(8)?,
        bookmark_note: row.get(9)?,
        audio_path: row.get(10)?,
        raw_event_json: row.get(11)?,
    })
}

#[cfg(test)]
mod tests;
