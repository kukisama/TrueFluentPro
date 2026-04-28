use rusqlite::params;
use sha2::{Sha256, Digest};

use crate::db::{Database, map_db_err};

#[derive(Debug, Clone)]
pub struct Session {
    pub id: String,
    pub title: String,
    pub session_type: String,
    pub message_count: i64,
    pub token_total: i64,
    pub created_at: String,
    pub updated_at: String,
}

#[derive(Debug, Clone)]
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
            .query_map([], |row| {
                Ok(Session {
                    id: row.get(0)?,
                    title: row.get(1)?,
                    session_type: row.get(2)?,
                    message_count: row.get(3)?,
                    token_total: row.get(4)?,
                    created_at: row.get(5)?,
                    updated_at: row.get(6)?,
                })
            })
            .map_err(map_db_err)?;
        let mut result = Vec::new();
        for r in rows {
            result.push(r.map_err(map_db_err)?);
        }
        Ok(result)
    }

    pub async fn delete_session(&self, id: &str) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute("DELETE FROM sessions WHERE id = ?1", params![id])
            .map_err(map_db_err)?;
        Ok(())
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

#[cfg(test)]
mod tests {
    use crate::Database;
    use super::*;

    fn make_session(id: &str) -> Session {
        Session {
            id: id.to_string(),
            title: "Test".into(),
            session_type: "chat".into(),
            message_count: 0,
            token_total: 0,
            created_at: "2026-01-01T00:00:00".into(),
            updated_at: "2026-01-01T00:00:00".into(),
        }
    }

    fn make_message(id: &str, session_id: &str) -> Message {
        Message {
            id: id.to_string(),
            session_id: session_id.to_string(),
            role: "user".into(),
            content: "hello".into(),
            mode: "text".into(),
            reasoning_text: None,
            prompt_tokens: Some(10),
            completion_tokens: Some(20),
            image_base64: None,
            attachments: None,
            content_hash: None,
            created_at: "2026-01-01T00:00:00".into(),
        }
    }

    #[tokio::test]
    async fn test_session_crud() {
        let db = Database::open_in_memory().unwrap();
        let s = make_session("s1");

        db.create_session(&s).await.unwrap();
        let got = db.get_session("s1").await.unwrap();
        assert!(got.is_some());
        assert_eq!(got.unwrap().title, "Test");

        let list = db.list_sessions().await.unwrap();
        assert_eq!(list.len(), 1);

        db.delete_session("s1").await.unwrap();
        let got2 = db.get_session("s1").await.unwrap();
        assert!(got2.is_none());
    }

    #[tokio::test]
    async fn test_message_crud() {
        let db = Database::open_in_memory().unwrap();
        let s = make_session("s1");
        db.create_session(&s).await.unwrap();

        let m = make_message("m1", "s1");
        db.add_message(&m).await.unwrap();

        let msgs = db.list_messages("s1").await.unwrap();
        assert_eq!(msgs.len(), 1);
        assert_eq!(msgs[0].content, "hello");

        db.delete_messages_by_session("s1").await.unwrap();
        let msgs2 = db.list_messages("s1").await.unwrap();
        assert_eq!(msgs2.len(), 0);
    }
}
