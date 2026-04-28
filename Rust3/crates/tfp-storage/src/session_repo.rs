use rusqlite::params;
use serde::{Deserialize, Serialize};
use sha2::{Sha256, Digest};

use crate::db::{Database, map_db_err};
use tfp_core::TranslationHistory;

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Session {
    pub id: String,
    pub title: String,
    pub session_type: String,
    pub message_count: i64,
    pub token_total: i64,
    pub created_at: String,
    pub updated_at: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Message {
    pub id: String,
    pub session_id: String,
    pub role: String,
    pub content: String,
    pub mode: String,
    pub reasoning_text: Option<String>,
    pub prompt_tokens: Option<i64>,
    pub completion_tokens: Option<i64>,
    pub image_base64: Option<String>,
    pub attachments: Option<String>,
    pub content_hash: Option<String>,
    pub created_at: String,
}

impl Database {
    pub async fn create_session(&self, session: &Session) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute(
            "INSERT INTO sessions (id, title, session_type, message_count, token_total, created_at, updated_at)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)",
            params![
                session.id, session.title, session.session_type,
                session.message_count, session.token_total,
                session.created_at, session.updated_at,
            ],
        ).map_err(map_db_err)?;
        Ok(())
    }

    pub async fn get_session(&self, id: &str) -> tfp_core::Result<Option<Session>> {
        let conn = self.conn().lock().await;
        let mut stmt = conn.prepare(
            "SELECT id, title, session_type, message_count, token_total, created_at, updated_at
             FROM sessions WHERE id = ?1"
        ).map_err(map_db_err)?;
        let mut rows = stmt.query(params![id]).map_err(map_db_err)?;
        match rows.next().map_err(map_db_err)? {
            Some(row) => Ok(Some(map_session_row(row)?)),
            None => Ok(None),
        }
    }

    pub async fn list_sessions(&self) -> tfp_core::Result<Vec<Session>> {
        let conn = self.conn().lock().await;
        let mut stmt = conn.prepare(
            "SELECT id, title, session_type, message_count, token_total, created_at, updated_at
             FROM sessions ORDER BY updated_at DESC"
        ).map_err(map_db_err)?;
        let rows = stmt
            .query_map([], map_session_query_row)
            .map_err(map_db_err)?;
        collect_rows(rows)
    }

    pub async fn delete_session(&self, id: &str) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute("DELETE FROM sessions WHERE id = ?1", params![id])
            .map_err(map_db_err)?;
        Ok(())
    }

    pub async fn rename_session(&self, id: &str, new_title: &str) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute(
            "UPDATE sessions SET title = ?2, updated_at = datetime('now') WHERE id = ?1",
            params![id, new_title],
        ).map_err(map_db_err)?;
        Ok(())
    }

    pub async fn list_sessions_by_type(&self, session_type: &str) -> tfp_core::Result<Vec<Session>> {
        let conn = self.conn().lock().await;
        let mut stmt = conn.prepare(
            "SELECT id, title, session_type, message_count, token_total, created_at, updated_at
             FROM sessions WHERE session_type = ?1 ORDER BY updated_at DESC"
        ).map_err(map_db_err)?;
        let rows = stmt
            .query_map(params![session_type], map_session_query_row)
            .map_err(map_db_err)?;
        collect_rows(rows)
    }

    pub async fn add_message(&self, msg: &Message) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;

        let hash_input = format!("{}:{}:{}", msg.session_id, msg.role, msg.content);
        let content_hash = format!("{:x}", Sha256::digest(hash_input.as_bytes()));

        let exists: bool = conn
            .query_row(
                "SELECT COUNT(*) > 0 FROM messages
                 WHERE session_id = ?1 AND content_hash = ?2
                 AND created_at > datetime('now', '-2 seconds')",
                params![msg.session_id, content_hash],
                |row| row.get(0),
            )
            .unwrap_or(false);
        if exists {
            return Ok(());
        }

        conn.execute(
            "INSERT INTO messages (id, session_id, role, content, mode, reasoning_text, prompt_tokens,
             completion_tokens, image_base64, attachments, created_at, content_hash)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12)",
            params![
                msg.id, msg.session_id, msg.role, msg.content, msg.mode,
                msg.reasoning_text, msg.prompt_tokens, msg.completion_tokens,
                msg.image_base64, msg.attachments, msg.created_at, content_hash,
            ],
        ).map_err(map_db_err)?;

        let token_delta = msg.prompt_tokens.unwrap_or(0) + msg.completion_tokens.unwrap_or(0);
        conn.execute(
            "UPDATE sessions SET message_count = message_count + 1, token_total = token_total + ?1,
             updated_at = datetime('now') WHERE id = ?2",
            params![token_delta, msg.session_id],
        ).map_err(map_db_err)?;

        Ok(())
    }

    pub async fn list_messages(&self, session_id: &str) -> tfp_core::Result<Vec<Message>> {
        let conn = self.conn().lock().await;
        let mut stmt = conn.prepare(
            "SELECT id, session_id, role, content, mode, reasoning_text, prompt_tokens,
             completion_tokens, image_base64, attachments, content_hash, created_at
             FROM messages WHERE session_id = ?1 ORDER BY created_at ASC"
        ).map_err(map_db_err)?;
        let rows = stmt
            .query_map(params![session_id], |row| {
                Ok(Message {
                    id: row.get(0)?,
                    session_id: row.get(1)?,
                    role: row.get(2)?,
                    content: row.get(3)?,
                    mode: row.get(4)?,
                    reasoning_text: row.get(5)?,
                    prompt_tokens: row.get(6)?,
                    completion_tokens: row.get(7)?,
                    image_base64: row.get(8)?,
                    attachments: row.get(9)?,
                    content_hash: row.get(10)?,
                    created_at: row.get(11)?,
                })
            })
            .map_err(map_db_err)?;
        let mut result = Vec::new();
        for r in rows {
            result.push(r.map_err(map_db_err)?);
        }
        Ok(result)
    }

    pub async fn delete_messages_by_session(&self, session_id: &str) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute(
            "DELETE FROM messages WHERE session_id = ?1",
            params![session_id],
        ).map_err(map_db_err)?;
        Ok(())
    }

    // ── Legacy translation history (deprecated) ──

    pub async fn insert_translation(&self, record: &TranslationHistory) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute(
            "INSERT INTO translation_history (id, source_text, translated_text, source_lang, target_lang, provider, created_at)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)",
            params![
                record.id,
                record.source_text,
                record.translated_text,
                record.source_lang,
                record.target_lang,
                record.provider,
                record.created_at,
            ],
        ).map_err(map_db_err)?;
        Ok(())
    }

    pub async fn list_translations(&self, limit: u32) -> tfp_core::Result<Vec<TranslationHistory>> {
        let conn = self.conn().lock().await;
        let mut stmt = conn.prepare(
            "SELECT id, source_text, translated_text, source_lang, target_lang, provider, created_at
             FROM translation_history ORDER BY created_at DESC LIMIT ?1",
        ).map_err(map_db_err)?;
        let rows = stmt.query_map(params![limit], |row| {
            Ok(TranslationHistory {
                id: row.get(0)?,
                source_text: row.get(1)?,
                translated_text: row.get(2)?,
                source_lang: row.get(3)?,
                target_lang: row.get(4)?,
                provider: row.get(5)?,
                created_at: row.get(6)?,
            })
        }).map_err(map_db_err)?;
        let mut result = Vec::new();
        for r in rows {
            result.push(r.map_err(map_db_err)?);
        }
        Ok(result)
    }
}

fn map_session_row(row: &rusqlite::Row) -> tfp_core::Result<Session> {
    Ok(Session {
        id: row.get(0).map_err(map_db_err)?,
        title: row.get(1).map_err(map_db_err)?,
        session_type: row.get(2).map_err(map_db_err)?,
        message_count: row.get(3).map_err(map_db_err)?,
        token_total: row.get(4).map_err(map_db_err)?,
        created_at: row.get(5).map_err(map_db_err)?,
        updated_at: row.get(6).map_err(map_db_err)?,
    })
}

fn map_session_query_row(row: &rusqlite::Row) -> rusqlite::Result<Session> {
    Ok(Session {
        id: row.get(0)?,
        title: row.get(1)?,
        session_type: row.get(2)?,
        message_count: row.get(3)?,
        token_total: row.get(4)?,
        created_at: row.get(5)?,
        updated_at: row.get(6)?,
    })
}

fn collect_rows(rows: impl Iterator<Item = rusqlite::Result<Session>>) -> tfp_core::Result<Vec<Session>> {
    let mut result = Vec::new();
    for r in rows {
        result.push(r.map_err(map_db_err)?);
    }
    Ok(result)
}

#[cfg(test)]
mod tests;
