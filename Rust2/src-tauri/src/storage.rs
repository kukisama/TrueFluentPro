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

            -- P1.2: 会话与消息
            CREATE TABLE IF NOT EXISTS sessions (
                id            TEXT PRIMARY KEY,
                title         TEXT NOT NULL,
                session_type  TEXT NOT NULL DEFAULT 'chat',
                message_count INTEGER NOT NULL DEFAULT 0,
                token_total   INTEGER NOT NULL DEFAULT 0,
                created_at    TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at    TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS messages (
                id                TEXT PRIMARY KEY,
                session_id        TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                role              TEXT NOT NULL,
                content           TEXT NOT NULL,
                mode              TEXT NOT NULL DEFAULT 'text',
                reasoning_text    TEXT,
                prompt_tokens     INTEGER,
                completion_tokens INTEGER,
                image_base64      TEXT,
                attachments       TEXT,
                created_at        TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE INDEX IF NOT EXISTS idx_messages_session ON messages(session_id);

            -- P1.2: 音频库
            CREATE TABLE IF NOT EXISTS audio_library_items (
                id          TEXT PRIMARY KEY,
                file_name   TEXT NOT NULL,
                file_path   TEXT NOT NULL,
                duration_ms INTEGER NOT NULL DEFAULT 0,
                sample_rate INTEGER NOT NULL DEFAULT 16000,
                channels    INTEGER NOT NULL DEFAULT 1,
                source_lang TEXT NOT NULL DEFAULT 'zh-CN',
                created_at  TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at  TEXT NOT NULL DEFAULT (datetime('now'))
            );

            -- P1.2: 音频生命周期
            CREATE TABLE IF NOT EXISTS audio_lifecycle (
                id            TEXT PRIMARY KEY,
                audio_item_id TEXT NOT NULL REFERENCES audio_library_items(id) ON DELETE CASCADE,
                stage         TEXT NOT NULL,
                status        TEXT NOT NULL DEFAULT 'Pending',
                result_text   TEXT,
                result_json   TEXT,
                model_id      TEXT,
                token_used    INTEGER,
                error         TEXT,
                started_at    TEXT,
                completed_at  TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_lifecycle_audio ON audio_lifecycle(audio_item_id);

            -- P2.1: 任务队列
            CREATE TABLE IF NOT EXISTS audio_task_queue (
                id            TEXT PRIMARY KEY,
                audio_item_id TEXT NOT NULL,
                stage         TEXT NOT NULL,
                task_type     TEXT NOT NULL,
                status        TEXT NOT NULL DEFAULT 'Queued',
                priority      INTEGER NOT NULL DEFAULT 0,
                retry_count   INTEGER NOT NULL DEFAULT 0,
                max_retries   INTEGER NOT NULL DEFAULT 3,
                progress      REAL NOT NULL DEFAULT 0.0,
                prompt_text   TEXT,
                result_text   TEXT,
                error         TEXT,
                submitted_at  TEXT NOT NULL DEFAULT (datetime('now')),
                started_at    TEXT,
                completed_at  TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_task_status ON audio_task_queue(status);
            CREATE INDEX IF NOT EXISTS idx_task_audio ON audio_task_queue(audio_item_id);

            -- P2.1: 任务执行历史
            CREATE TABLE IF NOT EXISTS task_executions (
                id                TEXT PRIMARY KEY,
                task_id           TEXT NOT NULL REFERENCES audio_task_queue(id) ON DELETE CASCADE,
                attempt           INTEGER NOT NULL,
                status            TEXT NOT NULL,
                error             TEXT,
                prompt_tokens     INTEGER,
                completion_tokens INTEGER,
                duration_ms       INTEGER,
                started_at        TEXT NOT NULL DEFAULT (datetime('now')),
                completed_at      TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_exec_task ON task_executions(task_id);

            -- P3.5: 计费记录
            CREATE TABLE IF NOT EXISTS billing_records (
                id                TEXT PRIMARY KEY,
                task_id           TEXT,
                endpoint_id       TEXT NOT NULL,
                model_id          TEXT NOT NULL,
                prompt_tokens     INTEGER NOT NULL DEFAULT 0,
                completion_tokens INTEGER NOT NULL DEFAULT 0,
                cost_usd          REAL,
                created_at        TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE INDEX IF NOT EXISTS idx_billing_time ON billing_records(created_at);
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

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  P1.2: 会话 CRUD
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    pub fn create_session(&self, session: &Session) -> SqlResult<()> {
        let conn = self.conn.lock().unwrap();
        conn.execute(
            "INSERT INTO sessions (id, title, session_type, message_count, token_total, created_at, updated_at)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)",
            params![
                session.id, session.title, session.session_type,
                session.message_count, session.token_total,
                session.created_at, session.updated_at,
            ],
        )?;
        Ok(())
    }

    pub fn list_sessions(&self, session_type: Option<&str>) -> SqlResult<Vec<Session>> {
        let conn = self.conn.lock().unwrap();
        let sql = match session_type {
            Some(_) => "SELECT id, title, session_type, message_count, token_total, created_at, updated_at
                         FROM sessions WHERE session_type = ?1 ORDER BY updated_at DESC",
            None => "SELECT id, title, session_type, message_count, token_total, created_at, updated_at
                     FROM sessions ORDER BY updated_at DESC",
        };
        let mut stmt = conn.prepare(sql)?;
        let rows = if let Some(st) = session_type {
            stmt.query_map(params![st], Self::map_session)?
        } else {
            stmt.query_map([], Self::map_session)?
        };
        rows.collect()
    }

    fn map_session(row: &rusqlite::Row) -> rusqlite::Result<Session> {
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

    pub fn delete_session(&self, session_id: &str) -> SqlResult<()> {
        let conn = self.conn.lock().unwrap();
        conn.execute("DELETE FROM sessions WHERE id = ?1", params![session_id])?;
        Ok(())
    }

    pub fn update_session_counts(&self, session_id: &str, msg_count: i64, token_total: i64) -> SqlResult<()> {
        let conn = self.conn.lock().unwrap();
        conn.execute(
            "UPDATE sessions SET message_count = ?1, token_total = ?2, updated_at = datetime('now') WHERE id = ?3",
            params![msg_count, token_total, session_id],
        )?;
        Ok(())
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  P1.2: 消息 CRUD
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    pub fn add_message(&self, msg: &Message) -> SqlResult<()> {
        let conn = self.conn.lock().unwrap();
        conn.execute(
            "INSERT INTO messages (id, session_id, role, content, mode, reasoning_text, prompt_tokens, completion_tokens, image_base64, attachments, created_at)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11)",
            params![
                msg.id, msg.session_id, msg.role, msg.content, msg.mode,
                msg.reasoning_text, msg.prompt_tokens, msg.completion_tokens,
                msg.image_base64, msg.attachments, msg.created_at,
            ],
        )?;
        Ok(())
    }

    pub fn get_session_messages(&self, session_id: &str) -> SqlResult<Vec<Message>> {
        let conn = self.conn.lock().unwrap();
        let mut stmt = conn.prepare(
            "SELECT id, session_id, role, content, mode, reasoning_text, prompt_tokens, completion_tokens, image_base64, attachments, created_at
             FROM messages WHERE session_id = ?1 ORDER BY created_at ASC",
        )?;
        let rows = stmt.query_map(params![session_id], |row| {
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
                created_at: row.get(10)?,
            })
        })?;
        rows.collect()
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  P1.2: 音频库 CRUD
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    pub fn add_audio_item(&self, item: &AudioLibraryItem) -> SqlResult<()> {
        let conn = self.conn.lock().unwrap();
        conn.execute(
            "INSERT INTO audio_library_items (id, file_name, file_path, duration_ms, sample_rate, channels, source_lang, created_at, updated_at)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9)",
            params![
                item.id, item.file_name, item.file_path, item.duration_ms,
                item.sample_rate, item.channels, item.source_lang,
                item.created_at, item.updated_at,
            ],
        )?;
        Ok(())
    }

    pub fn list_audio_items(&self) -> SqlResult<Vec<AudioLibraryItem>> {
        let conn = self.conn.lock().unwrap();
        let mut stmt = conn.prepare(
            "SELECT id, file_name, file_path, duration_ms, sample_rate, channels, source_lang, created_at, updated_at
             FROM audio_library_items ORDER BY created_at DESC",
        )?;
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
        })?;
        rows.collect()
    }

    pub fn delete_audio_item(&self, item_id: &str) -> SqlResult<()> {
        let conn = self.conn.lock().unwrap();
        conn.execute("DELETE FROM audio_library_items WHERE id = ?1", params![item_id])?;
        Ok(())
    }

    pub fn get_audio_item(&self, item_id: &str) -> SqlResult<Option<AudioLibraryItem>> {
        let conn = self.conn.lock().unwrap();
        let mut stmt = conn.prepare(
            "SELECT id, file_name, file_path, duration_ms, sample_rate, channels, source_lang, created_at, updated_at
             FROM audio_library_items WHERE id = ?1",
        )?;
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
        })?;
        match rows.next() {
            Some(Ok(item)) => Ok(Some(item)),
            Some(Err(e)) => Err(e),
            None => Ok(None),
        }
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  P1.2: 音频生命周期 CRUD
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    pub fn get_audio_lifecycle(&self, audio_item_id: &str) -> SqlResult<Vec<AudioLifecycleRow>> {
        let conn = self.conn.lock().unwrap();
        let mut stmt = conn.prepare(
            "SELECT id, audio_item_id, stage, status, result_text, result_json, model_id, token_used, error, started_at, completed_at
             FROM audio_lifecycle WHERE audio_item_id = ?1 ORDER BY rowid ASC",
        )?;
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
        })?;
        rows.collect()
    }

    pub fn upsert_lifecycle(&self, lc: &AudioLifecycleRow) -> SqlResult<()> {
        let conn = self.conn.lock().unwrap();
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
        )?;
        Ok(())
    }

    pub fn init_lifecycle_stages(&self, audio_item_id: &str) -> SqlResult<()> {
        let stages = ["Transcribed", "Summarized", "MindMap", "Insight", "Research", "PodcastScript", "PodcastAudio", "Translated"];
        let conn = self.conn.lock().unwrap();
        for stage in &stages {
            let id = format!("{}-{}", audio_item_id, stage);
            conn.execute(
                "INSERT OR IGNORE INTO audio_lifecycle (id, audio_item_id, stage, status)
                 VALUES (?1, ?2, ?3, 'Pending')",
                params![id, audio_item_id, stage],
            )?;
        }
        Ok(())
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  P2.1: 任务队列 CRUD
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    pub fn submit_task(&self, task: &AudioTaskRow) -> SqlResult<()> {
        let conn = self.conn.lock().unwrap();
        conn.execute(
            "INSERT INTO audio_task_queue (id, audio_item_id, stage, task_type, status, priority, retry_count, max_retries, progress, prompt_text, result_text, error, submitted_at)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13)",
            params![
                task.id, task.audio_item_id, task.stage, task.task_type,
                task.status, task.priority, task.retry_count, task.max_retries,
                task.progress, task.prompt_text, task.result_text, task.error, task.submitted_at,
            ],
        )?;
        Ok(())
    }

    pub fn get_next_queued_task(&self) -> SqlResult<Option<AudioTaskRow>> {
        let conn = self.conn.lock().unwrap();
        let mut stmt = conn.prepare(
            "SELECT id, audio_item_id, stage, task_type, status, priority, retry_count, max_retries, progress, prompt_text, result_text, error, submitted_at, started_at, completed_at
             FROM audio_task_queue WHERE status = 'Queued' ORDER BY priority DESC, submitted_at ASC LIMIT 1"
        )?;
        let mut rows = stmt.query_map([], Self::map_task)?;
        match rows.next() {
            Some(Ok(task)) => Ok(Some(task)),
            Some(Err(e)) => Err(e),
            None => Ok(None),
        }
    }

    pub fn list_tasks(&self, status: Option<&str>, limit: u32) -> SqlResult<Vec<AudioTaskRow>> {
        let conn = self.conn.lock().unwrap();
        let sql = match status {
            Some(_) => "SELECT id, audio_item_id, stage, task_type, status, priority, retry_count, max_retries, progress, prompt_text, result_text, error, submitted_at, started_at, completed_at
                         FROM audio_task_queue WHERE status = ?1 ORDER BY priority DESC, submitted_at ASC LIMIT ?2",
            None => "SELECT id, audio_item_id, stage, task_type, status, priority, retry_count, max_retries, progress, prompt_text, result_text, error, submitted_at, started_at, completed_at
                     FROM audio_task_queue ORDER BY submitted_at DESC LIMIT ?1",
        };
        let mut stmt = conn.prepare(sql)?;
        let rows = if let Some(s) = status {
            stmt.query_map(params![s, limit], Self::map_task)?
        } else {
            stmt.query_map(params![limit], Self::map_task)?
        };
        rows.collect()
    }

    fn map_task(row: &rusqlite::Row) -> rusqlite::Result<AudioTaskRow> {
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

    pub fn update_task_status_new(&self, id: &str, status: &str, error: Option<&str>) -> SqlResult<()> {
        let conn = self.conn.lock().unwrap();
        let now = chrono::Utc::now().to_rfc3339();
        match status {
            "Executing" => {
                conn.execute(
                    "UPDATE audio_task_queue SET status = ?1, started_at = ?2 WHERE id = ?3",
                    params![status, now, id],
                )?;
            }
            "Completed" | "Failed" | "Cancelled" => {
                conn.execute(
                    "UPDATE audio_task_queue SET status = ?1, error = ?2, completed_at = ?3 WHERE id = ?4",
                    params![status, error, now, id],
                )?;
            }
            _ => {
                conn.execute(
                    "UPDATE audio_task_queue SET status = ?1 WHERE id = ?2",
                    params![status, id],
                )?;
            }
        }
        Ok(())
    }

    pub fn get_task_stats(&self) -> SqlResult<TaskEngineStats> {
        let conn = self.conn.lock().unwrap();
        let mut stmt = conn.prepare(
            "SELECT status, COUNT(*) FROM audio_task_queue GROUP BY status"
        )?;
        let mut stats = TaskEngineStats::default();
        let rows = stmt.query_map([], |row| {
            Ok((row.get::<_, String>(0)?, row.get::<_, i64>(1)?))
        })?;
        for row in rows {
            let (status, count) = row?;
            match status.as_str() {
                "Queued" => stats.queued = count,
                "Executing" => stats.executing = count,
                "Completed" => stats.completed = count,
                "Failed" => stats.failed = count,
                "Cancelled" => stats.cancelled = count,
                _ => {}
            }
        }
        // Total tokens from executions
        let total: i64 = conn.query_row(
            "SELECT COALESCE(SUM(COALESCE(prompt_tokens, 0) + COALESCE(completion_tokens, 0)), 0) FROM task_executions",
            [], |row| row.get(0),
        )?;
        stats.total_tokens = total;
        Ok(stats)
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  P2.1: 任务执行历史
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    pub fn add_task_execution(&self, exec: &TaskExecutionRow) -> SqlResult<()> {
        let conn = self.conn.lock().unwrap();
        conn.execute(
            "INSERT INTO task_executions (id, task_id, attempt, status, error, prompt_tokens, completion_tokens, duration_ms, started_at, completed_at)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10)",
            params![
                exec.id, exec.task_id, exec.attempt, exec.status,
                exec.error, exec.prompt_tokens, exec.completion_tokens,
                exec.duration_ms, exec.started_at, exec.completed_at,
            ],
        )?;
        Ok(())
    }

    pub fn get_task_executions(&self, task_id: &str) -> SqlResult<Vec<TaskExecutionRow>> {
        let conn = self.conn.lock().unwrap();
        let mut stmt = conn.prepare(
            "SELECT id, task_id, attempt, status, error, prompt_tokens, completion_tokens, duration_ms, started_at, completed_at
             FROM task_executions WHERE task_id = ?1 ORDER BY attempt ASC",
        )?;
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
        })?;
        rows.collect()
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  P3.5: 计费记录
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    pub fn add_billing_record(&self, rec: &BillingRecord) -> SqlResult<()> {
        let conn = self.conn.lock().unwrap();
        conn.execute(
            "INSERT INTO billing_records (id, task_id, endpoint_id, model_id, prompt_tokens, completion_tokens, cost_usd, created_at)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8)",
            params![
                rec.id, rec.task_id, rec.endpoint_id, rec.model_id,
                rec.prompt_tokens, rec.completion_tokens, rec.cost_usd, rec.created_at,
            ],
        )?;
        Ok(())
    }

    pub fn get_billing_records(&self, limit: u32) -> SqlResult<Vec<BillingRecord>> {
        let conn = self.conn.lock().unwrap();
        let mut stmt = conn.prepare(
            "SELECT id, task_id, endpoint_id, model_id, prompt_tokens, completion_tokens, cost_usd, created_at
             FROM billing_records ORDER BY created_at DESC LIMIT ?1",
        )?;
        let rows = stmt.query_map(params![limit], |row| {
            Ok(BillingRecord {
                id: row.get(0)?,
                task_id: row.get(1)?,
                endpoint_id: row.get(2)?,
                model_id: row.get(3)?,
                prompt_tokens: row.get(4)?,
                completion_tokens: row.get(5)?,
                cost_usd: row.get(6)?,
                created_at: row.get(7)?,
            })
        })?;
        rows.collect()
    }

    pub fn get_billing_summary(&self) -> SqlResult<BillingSummary> {
        let conn = self.conn.lock().unwrap();

        let (total_prompt, total_completion, total_cost, count): (i64, i64, f64, i64) =
            conn.query_row(
                "SELECT COALESCE(SUM(prompt_tokens),0), COALESCE(SUM(completion_tokens),0),
                        COALESCE(SUM(cost_usd),0.0), COUNT(*)
                 FROM billing_records",
                [],
                |row| Ok((row.get(0)?, row.get(1)?, row.get(2)?, row.get(3)?)),
            )?;

        let mut stmt = conn.prepare(
            "SELECT model_id, SUM(prompt_tokens), SUM(completion_tokens), COALESCE(SUM(cost_usd),0.0), COUNT(*)
             FROM billing_records GROUP BY model_id ORDER BY SUM(prompt_tokens) DESC",
        )?;
        let by_model = stmt.query_map([], |row| {
            Ok(BillingByModel {
                model_id: row.get(0)?,
                prompt_tokens: row.get(1)?,
                completion_tokens: row.get(2)?,
                cost_usd: row.get(3)?,
                count: row.get(4)?,
            })
        })?.collect::<Result<Vec<_>, _>>()?;

        Ok(BillingSummary {
            total_prompt_tokens: total_prompt,
            total_completion_tokens: total_completion,
            total_cost_usd: total_cost,
            record_count: count,
            by_model,
        })
    }
}
