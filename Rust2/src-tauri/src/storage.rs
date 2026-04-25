use rusqlite::{params, Connection, Result as SqlResult};
use std::path::Path;
use std::sync::Mutex;

use crate::models::*;

/// SQLite 数据库访问层
pub struct Database {
    conn: Mutex<Connection>,
}

impl Database {
    pub fn open(path: &Path) -> SqlResult<Self> {
        let conn = Connection::open(path)?;
        conn.execute_batch("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;")?;
        let db = Self {
            conn: Mutex::new(conn),
        };
        db.migrate()?;
        Ok(db)
    }

    fn migrate(&self) -> SqlResult<()> {
        let conn = self.conn.lock().unwrap();
        conn.execute_batch(
            "
            CREATE TABLE IF NOT EXISTS translation_history (
                id          TEXT PRIMARY KEY,
                source_text TEXT NOT NULL,
                translated_text TEXT NOT NULL,
                source_lang TEXT NOT NULL,
                target_lang TEXT NOT NULL,
                provider    TEXT NOT NULL,
                created_at  TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS batch_tasks (
                id          TEXT PRIMARY KEY,
                name        TEXT NOT NULL,
                status      TEXT NOT NULL DEFAULT 'pending',
                task_type   TEXT NOT NULL,
                progress    REAL NOT NULL DEFAULT 0.0,
                error       TEXT,
                created_at  TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at  TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS media_sessions (
                id          TEXT PRIMARY KEY,
                name        TEXT NOT NULL,
                session_type TEXT NOT NULL,
                created_at  TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS media_items (
                id          TEXT PRIMARY KEY,
                session_id  TEXT NOT NULL REFERENCES media_sessions(id),
                prompt      TEXT NOT NULL,
                result_url  TEXT,
                status      TEXT NOT NULL DEFAULT 'pending',
                created_at  TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS audio_sessions (
                id          TEXT PRIMARY KEY,
                name        TEXT NOT NULL,
                stage       TEXT NOT NULL DEFAULT 'recording',
                file_path   TEXT,
                duration_ms INTEGER NOT NULL DEFAULT 0,
                created_at  TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS kv_store (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            ",
        )?;
        Ok(())
    }

    // ── KV 存储（用于配置持久化） ──

    pub fn kv_get(&self, key: &str) -> SqlResult<Option<String>> {
        let conn = self.conn.lock().unwrap();
        let mut stmt = conn.prepare("SELECT value FROM kv_store WHERE key = ?1")?;
        let mut rows = stmt.query(params![key])?;
        if let Some(row) = rows.next()? {
            Ok(Some(row.get(0)?))
        } else {
            Ok(None)
        }
    }

    pub fn kv_set(&self, key: &str, value: &str) -> SqlResult<()> {
        let conn = self.conn.lock().unwrap();
        conn.execute(
            "INSERT OR REPLACE INTO kv_store (key, value) VALUES (?1, ?2)",
            params![key, value],
        )?;
        Ok(())
    }

    // ── 翻译历史 ──

    pub fn insert_translation(&self, record: &TranslationHistory) -> SqlResult<()> {
        let conn = self.conn.lock().unwrap();
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
        )?;
        Ok(())
    }

    pub fn list_translations(&self, limit: u32) -> SqlResult<Vec<TranslationHistory>> {
        let conn = self.conn.lock().unwrap();
        let mut stmt = conn.prepare(
            "SELECT id, source_text, translated_text, source_lang, target_lang, provider, created_at
             FROM translation_history ORDER BY created_at DESC LIMIT ?1",
        )?;
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
        })?;
        rows.collect()
    }

    // ── 批量任务 ──

    pub fn insert_batch_task(&self, task: &BatchTask) -> SqlResult<()> {
        let conn = self.conn.lock().unwrap();
        conn.execute(
            "INSERT INTO batch_tasks (id, name, status, task_type, progress, created_at, updated_at)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)",
            params![
                task.id,
                task.name,
                serde_json::to_string(&task.status).unwrap_or_default(),
                serde_json::to_string(&task.task_type).unwrap_or_default(),
                task.progress,
                task.created_at,
                task.updated_at,
            ],
        )?;
        Ok(())
    }

    pub fn update_task_progress(&self, id: &str, progress: f64, status: &TaskStatus) -> SqlResult<()> {
        let conn = self.conn.lock().unwrap();
        conn.execute(
            "UPDATE batch_tasks SET progress = ?1, status = ?2, updated_at = datetime('now') WHERE id = ?3",
            params![
                progress,
                serde_json::to_string(status).unwrap_or_default(),
                id,
            ],
        )?;
        Ok(())
    }

    pub fn list_batch_tasks(&self, limit: u32) -> SqlResult<Vec<BatchTask>> {
        let conn = self.conn.lock().unwrap();
        let mut stmt = conn.prepare(
            "SELECT id, name, status, task_type, progress, created_at, updated_at, error
             FROM batch_tasks ORDER BY created_at DESC LIMIT ?1",
        )?;
        let rows = stmt.query_map(params![limit], |row| {
            let status_str: String = row.get(2)?;
            let type_str: String = row.get(3)?;
            Ok(BatchTask {
                id: row.get(0)?,
                name: row.get(1)?,
                status: serde_json::from_str(&status_str).unwrap_or(TaskStatus::Pending),
                task_type: serde_json::from_str(&type_str).unwrap_or(BatchTaskType::TextTranslation),
                progress: row.get(4)?,
                created_at: row.get(5)?,
                updated_at: row.get(6)?,
                error: row.get(7)?,
            })
        })?;
        rows.collect()
    }
}
