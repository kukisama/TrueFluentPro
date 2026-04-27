use rusqlite::{params, Connection, Result as SqlResult};
use sha2::{Sha256, Digest};
use std::path::Path;
use tokio::sync::Mutex;

use crate::models::*;

/// SQLite 数据库访问层
///
/// # 已知限制（RV-N2）
/// 当前使用 `tokio::sync::Mutex<rusqlite::Connection>`，锁等待已异步化，
/// 但 `conn.execute()` / `conn.query_row()` 等 SQLite 操作本身仍是**同步阻塞 I/O**，
/// 会在持锁期间占用 tokio worker 线程。低并发下无感知，高并发时（如 task_engine
/// 同时执行多个任务）可能短暂阻塞 worker。
///
/// **后续迭代方案**（二选一）：
/// 1. `tokio::task::spawn_blocking()` 包装每次 SQLite 操作
/// 2. 迁移到 `sqlx` 的 async SQLite driver
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
        let conn = self.conn.blocking_lock();
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

            -- 图片保存记录（对齐 C# ImageSaveResult + 文件存储）
            CREATE TABLE IF NOT EXISTS saved_images (
                id              TEXT PRIMARY KEY,
                prompt          TEXT NOT NULL,
                revised_prompt  TEXT,
                file_path       TEXT NOT NULL,
                file_size       INTEGER NOT NULL DEFAULT 0,
                width           INTEGER,
                height          INTEGER,
                model_id        TEXT,
                endpoint_id     TEXT,
                generate_seconds REAL,
                source          TEXT NOT NULL DEFAULT 'media_center',
                created_at      TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE INDEX IF NOT EXISTS idx_saved_images_time ON saved_images(created_at);
            ",
        )?;

        // ── 孤儿项 Schema 增量迁移 ──

        // O-12: billing_records 增加 status 列
        let _ = conn.execute_batch(
            "ALTER TABLE billing_records ADD COLUMN status TEXT NOT NULL DEFAULT 'Committed';"
        );
        // O-14: 消息附件归一化表
        conn.execute_batch(
            "CREATE TABLE IF NOT EXISTS message_attachments (
                id          TEXT PRIMARY KEY,
                message_id  TEXT NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
                file_type   TEXT NOT NULL DEFAULT 'image',
                file_path   TEXT,
                file_url    TEXT,
                file_name   TEXT,
                file_size   INTEGER,
                mime_type   TEXT,
                created_at  TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE INDEX IF NOT EXISTS idx_attach_msg ON message_attachments(message_id);",
        )?;
        // O-44: audio_lifecycle 增加 is_stale 标记列
        let _ = conn.execute_batch(
            "ALTER TABLE audio_lifecycle ADD COLUMN is_stale INTEGER NOT NULL DEFAULT 0;"
        );
        // O-44: audio_task_queue 增加 updated_at 列（用于 O-48 清理逻辑）
        let _ = conn.execute_batch(
            "ALTER TABLE audio_task_queue ADD COLUMN updated_at TEXT NOT NULL DEFAULT (datetime('now'));"
        );

        // O-18 + RV-O5: messages 增加 content_hash 列（时间窗口去重，非永久唯一）
        let _ = conn.execute_batch(
            "ALTER TABLE messages ADD COLUMN content_hash TEXT;"
        );
        // RV-O5: 改为普通索引（非唯一），允许同一内容在不同时间段重复
        let _ = conn.execute_batch(
            "DROP INDEX IF EXISTS idx_messages_content_hash;"
        );
        let _ = conn.execute_batch(
            "CREATE INDEX IF NOT EXISTS idx_messages_content_hash ON messages(session_id, content_hash);"
        );

        // O-24: 4 张关联表
        conn.execute_batch(
            "CREATE TABLE IF NOT EXISTS session_tasks (
                id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                task_id TEXT NOT NULL,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                UNIQUE(session_id, task_id)
            );
            CREATE INDEX IF NOT EXISTS idx_st_session ON session_tasks(session_id);
            CREATE INDEX IF NOT EXISTS idx_st_task ON session_tasks(task_id);

            CREATE TABLE IF NOT EXISTS session_assets (
                id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                asset_type TEXT NOT NULL,
                asset_path TEXT NOT NULL,
                file_size INTEGER,
                created_at TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE INDEX IF NOT EXISTS idx_sa_session ON session_assets(session_id);

            CREATE TABLE IF NOT EXISTS message_media_refs (
                id TEXT PRIMARY KEY,
                message_id TEXT NOT NULL,
                media_type TEXT NOT NULL,
                media_url TEXT NOT NULL,
                thumbnail_url TEXT,
                width INTEGER,
                height INTEGER,
                created_at TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE INDEX IF NOT EXISTS idx_mmr_msg ON message_media_refs(message_id);

            CREATE TABLE IF NOT EXISTS message_citations (
                id TEXT PRIMARY KEY,
                message_id TEXT NOT NULL,
                citation_index INTEGER NOT NULL,
                title TEXT,
                url TEXT,
                snippet TEXT,
                created_at TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE INDEX IF NOT EXISTS idx_mc_msg ON message_citations(message_id);"
        )?;

        Ok(())
    }

    // ── KV 存储（用于配置持久化） ──

    /// 同步版本 — 仅用于 AppState::new() 启动阶段（此时无并发竞争）
    pub fn kv_get_blocking(&self, key: &str) -> SqlResult<Option<String>> {
        let conn = self.conn.blocking_lock();
        let mut stmt = conn.prepare("SELECT value FROM kv_store WHERE key = ?1")?;
        let mut rows = stmt.query(params![key])?;
        if let Some(row) = rows.next()? {
            Ok(Some(row.get(0)?))
        } else {
            Ok(None)
        }
    }

    pub async fn kv_get(&self, key: &str) -> SqlResult<Option<String>> {
        let conn = self.conn.lock().await;
        let mut stmt = conn.prepare("SELECT value FROM kv_store WHERE key = ?1")?;
        let mut rows = stmt.query(params![key])?;
        if let Some(row) = rows.next()? {
            Ok(Some(row.get(0)?))
        } else {
            Ok(None)
        }
    }

    pub async fn kv_set(&self, key: &str, value: &str) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        conn.execute(
            "INSERT OR REPLACE INTO kv_store (key, value) VALUES (?1, ?2)",
            params![key, value],
        )?;
        Ok(())
    }

    // ── 翻译历史 ──

    pub async fn insert_translation(&self, record: &TranslationHistory) -> SqlResult<()> {
        let conn = self.conn.lock().await;
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

    pub async fn list_translations(&self, limit: u32) -> SqlResult<Vec<TranslationHistory>> {
        let conn = self.conn.lock().await;
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

    pub async fn create_session(&self, session: &Session) -> SqlResult<()> {
        let conn = self.conn.lock().await;
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

    pub async fn list_sessions(&self, session_type: Option<&str>) -> SqlResult<Vec<Session>> {
        let conn = self.conn.lock().await;
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

    pub async fn delete_session(&self, session_id: &str) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        conn.execute("DELETE FROM sessions WHERE id = ?1", params![session_id])?;
        Ok(())
    }

    /// O-05: 会话重命名持久化
    pub async fn rename_session(&self, session_id: &str, new_title: &str) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        conn.execute(
            "UPDATE sessions SET title = ?1, updated_at = datetime('now') WHERE id = ?2",
            params![new_title, session_id],
        )?;
        Ok(())
    }

    pub async fn update_session_counts(&self, session_id: &str, msg_count: i64, token_total: i64) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        conn.execute(
            "UPDATE sessions SET message_count = ?1, token_total = ?2, updated_at = datetime('now') WHERE id = ?3",
            params![msg_count, token_total, session_id],
        )?;
        Ok(())
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  P1.2: 消息 CRUD
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    pub async fn add_message(&self, msg: &Message) -> SqlResult<()> {
        let conn = self.conn.lock().await;

        // O-18 + RV-O5: SHA256 指纹去重 — 同一会话内 2 秒窗口内相同内容跳过
        // 避免网络抖动等导致的重复消息，但允许用户有意地重新发送同一问题
        let hash_input = format!("{}:{}:{}", msg.session_id, msg.role, msg.content);
        let content_hash = format!("{:x}", Sha256::digest(hash_input.as_bytes()));
        let exists: bool = conn.query_row(
            "SELECT COUNT(*) > 0 FROM messages WHERE session_id = ?1 AND content_hash = ?2 AND created_at > datetime('now', '-2 seconds')",
            params![msg.session_id, content_hash],
            |row| row.get(0),
        ).unwrap_or(false);
        if exists { return Ok(()); }

        conn.execute(
            "INSERT INTO messages (id, session_id, role, content, mode, reasoning_text, prompt_tokens, completion_tokens, image_base64, attachments, created_at, content_hash)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12)",
            params![
                msg.id, msg.session_id, msg.role, msg.content, msg.mode,
                msg.reasoning_text, msg.prompt_tokens, msg.completion_tokens,
                msg.image_base64, msg.attachments, msg.created_at, content_hash,
            ],
        )?;
        // B-07 修复: 发送消息后自动更新 session 的 message_count 和 token_total
        let token_delta = msg.prompt_tokens.unwrap_or(0) + msg.completion_tokens.unwrap_or(0);
        conn.execute(
            "UPDATE sessions SET message_count = message_count + 1, token_total = token_total + ?1, updated_at = datetime('now') WHERE id = ?2",
            params![token_delta, msg.session_id],
        )?;
        Ok(())
    }

    pub async fn get_session_messages(&self, session_id: &str) -> SqlResult<Vec<Message>> {
        let conn = self.conn.lock().await;
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

    pub async fn add_audio_item(&self, item: &AudioLibraryItem) -> SqlResult<()> {
        let conn = self.conn.lock().await;
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

    pub async fn list_audio_items(&self) -> SqlResult<Vec<AudioLibraryItem>> {
        let conn = self.conn.lock().await;
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

    pub async fn delete_audio_item(&self, item_id: &str) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        conn.execute("DELETE FROM audio_library_items WHERE id = ?1", params![item_id])?;
        Ok(())
    }

    pub async fn get_audio_item(&self, item_id: &str) -> SqlResult<Option<AudioLibraryItem>> {
        let conn = self.conn.lock().await;
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

    pub async fn get_audio_lifecycle(&self, audio_item_id: &str) -> SqlResult<Vec<AudioLifecycleRow>> {
        let conn = self.conn.lock().await;
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

    pub async fn upsert_lifecycle(&self, lc: &AudioLifecycleRow) -> SqlResult<()> {
        let conn = self.conn.lock().await;
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

    pub async fn init_lifecycle_stages(&self, audio_item_id: &str) -> SqlResult<()> {
        let stages = ["Transcribed", "Summarized", "MindMap", "Insight", "Research", "PodcastScript", "PodcastAudio", "Translated"];
        let conn = self.conn.lock().await;
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

    /// P3-10: DAG 级联失效 — 当上游阶段重新执行时，将所有下游阶段重置为 Pending
    ///
    /// DAG 依赖关系:
    /// Transcribed → Summarized → MindMap
    /// Transcribed → Summarized → Insight → Research
    /// Transcribed → Summarized → PodcastScript → PodcastAudio
    /// Transcribed → Translated
    ///
    /// RV-6 修复: 仅在"重做"场景触发取消 — 先检查下游是否已有 Completed 结果，
    /// 如果下游 lifecycle 全部是 Pending（首次 SubmitAll），则跳过任务取消。
    pub async fn invalidate_downstream_stages(&self, audio_item_id: &str, stage: &str) -> SqlResult<usize> {
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

        let conn = self.conn.lock().await;

        // RV-6: 检查是否是"重做"场景 — 下游是否有非 Pending 的 lifecycle 记录
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

        // 重置下游 lifecycle 为 Pending（仅影响非 Pending 行，首次 SubmitAll 时无影响）
        for ds in &downstream {
            let id = format!("{}-{}", audio_item_id, ds);
            let affected = conn.execute(
                "UPDATE audio_lifecycle SET status = 'Pending', result_text = NULL, result_json = NULL,
                 error = NULL, started_at = NULL, completed_at = NULL
                 WHERE id = ?1 AND status != 'Pending'",
                params![id],
            )?;
            total += affected;
        }

        // RV-6: 仅在重做场景下取消下游 Queued 任务
        // 首次 SubmitAll 时下游全部是 Pending + Queued，不应被取消
        if is_redo {
            let placeholders: Vec<String> = downstream.iter().enumerate()
                .map(|(i, _)| format!("?{}", i + 2))
                .collect();
            let sql = format!(
                "UPDATE audio_task_queue SET status = 'Cancelled'
                 WHERE audio_item_id = ?1 AND stage IN ({}) AND status = 'Queued'",
                placeholders.join(", ")
            );
            let mut stmt = conn.prepare(&sql)?;
            let params_vec: Vec<Box<dyn rusqlite::types::ToSql>> = std::iter::once(
                Box::new(audio_item_id.to_string()) as Box<dyn rusqlite::types::ToSql>,
            )
            .chain(downstream.iter().map(|s| Box::new(s.to_string()) as Box<dyn rusqlite::types::ToSql>))
            .collect();
            let param_refs: Vec<&dyn rusqlite::types::ToSql> = params_vec.iter().map(|p| p.as_ref()).collect();
            let cancelled = stmt.execute(param_refs.as_slice())?;
            total += cancelled;
        }

        Ok(total)
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  P2.1: 任务队列 CRUD
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    pub async fn submit_task(&self, task: &AudioTaskRow) -> SqlResult<()> {
        let conn = self.conn.lock().await;
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

    pub async fn get_next_queued_task(&self) -> SqlResult<Option<AudioTaskRow>> {
        let conn = self.conn.lock().await;
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

    /// O-25: 崩溃恢复 — 将上次崩溃时停留在 Executing 状态的任务重新排队
    pub async fn recover_interrupted_tasks(&self) -> SqlResult<usize> {
        let conn = self.conn.lock().await;
        conn.execute(
            "UPDATE audio_task_queue SET status = 'Queued', started_at = NULL, error = '崩溃恢复: 上次执行中断' WHERE status = 'Executing'",
            [],
        )
    }

    pub async fn list_tasks(&self, status: Option<&str>, limit: u32) -> SqlResult<Vec<AudioTaskRow>> {
        let conn = self.conn.lock().await;
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

    pub async fn update_task_status_new(&self, id: &str, status: &str, error: Option<&str>) -> SqlResult<()> {
        let conn = self.conn.lock().await;
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

    /// B-02 修复: 重试时原子递增 retry_count 并重置状态为 Queued
    pub async fn increment_retry_and_requeue(&self, id: &str, error: &str) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        conn.execute(
            "UPDATE audio_task_queue SET status = 'Queued', retry_count = retry_count + 1, error = ?1 WHERE id = ?2",
            params![error, id],
        )?;
        Ok(())
    }

    pub async fn get_task_stats(&self) -> SqlResult<TaskEngineStats> {
        let conn = self.conn.lock().await;
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

    /// O-48: 清理已完成/已取消且超过 N 天的任务及其执行记录
    pub async fn cleanup_expired_tasks(&self, days: u32) -> SqlResult<u32> {
        let conn = self.conn.lock().await;
        let cutoff = format!("-{} days", days);
        // 先删执行记录
        conn.execute(
            "DELETE FROM task_executions WHERE task_id IN (
                SELECT id FROM audio_task_queue
                WHERE status IN ('Completed','Cancelled') AND updated_at < datetime('now', ?1)
            )", [&cutoff],
        )?;
        let deleted = conn.execute(
            "DELETE FROM audio_task_queue WHERE status IN ('Completed','Cancelled') AND updated_at < datetime('now', ?1)",
            [&cutoff],
        )?;
        Ok(deleted as u32)
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  P2.1: 任务执行历史
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    pub async fn add_task_execution(&self, exec: &TaskExecutionRow) -> SqlResult<()> {
        let conn = self.conn.lock().await;
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

    pub async fn get_task_executions(&self, task_id: &str) -> SqlResult<Vec<TaskExecutionRow>> {
        let conn = self.conn.lock().await;
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

    pub async fn add_billing_record(&self, rec: &BillingRecord) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        conn.execute(
            "INSERT INTO billing_records (id, task_id, endpoint_id, model_id, prompt_tokens, completion_tokens, cost_usd, created_at, status)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9)",
            params![
                rec.id, rec.task_id, rec.endpoint_id, rec.model_id,
                rec.prompt_tokens, rec.completion_tokens, rec.cost_usd, rec.created_at,
                rec.status,
            ],
        )?;
        Ok(())
    }

    /// RV-O3: 更新计费记录状态（Staging → Running → Landed → Committed）
    pub async fn update_billing_status(&self, id: &str, status: &str) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        conn.execute(
            "UPDATE billing_records SET status = ?1 WHERE id = ?2",
            params![status, id],
        )?;
        Ok(())
    }

    /// RV-O3: 更新计费记录的 token 数据（任务完成后回填）
    pub async fn update_billing_tokens(&self, id: &str, prompt_tokens: i64, completion_tokens: i64) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        conn.execute(
            "UPDATE billing_records SET prompt_tokens = ?1, completion_tokens = ?2 WHERE id = ?3",
            params![prompt_tokens, completion_tokens, id],
        )?;
        Ok(())
    }

    pub async fn get_billing_records(&self, limit: u32) -> SqlResult<Vec<BillingRecord>> {
        let conn = self.conn.lock().await;
        let mut stmt = conn.prepare(
            "SELECT id, task_id, endpoint_id, model_id, prompt_tokens, completion_tokens, cost_usd, created_at, COALESCE(status, 'Committed') as status
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
                status: row.get(8)?,
            })
        })?;
        rows.collect()
    }

    pub async fn get_billing_summary(&self) -> SqlResult<BillingSummary> {
        let conn = self.conn.lock().await;

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

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  图片保存记录
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    pub async fn add_saved_image(&self, img: &SavedImage) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        conn.execute(
            "INSERT INTO saved_images (id, prompt, revised_prompt, file_path, file_size, width, height, model_id, endpoint_id, generate_seconds, source, created_at)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12)",
            params![
                img.id, img.prompt, img.revised_prompt, img.file_path,
                img.file_size, img.width, img.height,
                img.model_id, img.endpoint_id, img.generate_seconds,
                img.source, img.created_at,
            ],
        )?;
        Ok(())
    }

    pub async fn list_saved_images(&self, limit: u32) -> SqlResult<Vec<SavedImage>> {
        let conn = self.conn.lock().await;
        let mut stmt = conn.prepare(
            "SELECT id, prompt, revised_prompt, file_path, file_size, width, height, model_id, endpoint_id, generate_seconds, source, created_at
             FROM saved_images ORDER BY created_at DESC LIMIT ?1",
        )?;
        let rows = stmt.query_map(params![limit], |row| {
            Ok(SavedImage {
                id: row.get(0)?,
                prompt: row.get(1)?,
                revised_prompt: row.get(2)?,
                file_path: row.get(3)?,
                file_size: row.get(4)?,
                width: row.get(5)?,
                height: row.get(6)?,
                model_id: row.get(7)?,
                endpoint_id: row.get(8)?,
                generate_seconds: row.get(9)?,
                source: row.get(10)?,
                created_at: row.get(11)?,
            })
        })?;
        rows.collect()
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  RV-O4: 关联表 CRUD
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    // ── message_attachments ──

    pub async fn add_attachment(&self, att: &MessageAttachment) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        conn.execute(
            "INSERT INTO message_attachments (id, message_id, file_type, file_path, file_url, file_name, file_size, mime_type, created_at)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9)",
            params![att.id, att.message_id, att.file_type, att.file_path, att.file_url, att.file_name, att.file_size, att.mime_type, att.created_at],
        )?;
        Ok(())
    }

    pub async fn list_attachments_by_message(&self, message_id: &str) -> SqlResult<Vec<MessageAttachment>> {
        let conn = self.conn.lock().await;
        let mut stmt = conn.prepare(
            "SELECT id, message_id, file_type, file_path, file_url, file_name, file_size, mime_type, created_at
             FROM message_attachments WHERE message_id = ?1 ORDER BY created_at"
        )?;
        let rows = stmt.query_map(params![message_id], |row| {
            Ok(MessageAttachment {
                id: row.get(0)?, message_id: row.get(1)?, file_type: row.get(2)?,
                file_path: row.get(3)?, file_url: row.get(4)?, file_name: row.get(5)?,
                file_size: row.get(6)?, mime_type: row.get(7)?, created_at: row.get(8)?,
            })
        })?;
        rows.collect()
    }

    // ── session_tasks ──

    pub async fn add_session_task(&self, st: &SessionTask) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        conn.execute(
            "INSERT OR IGNORE INTO session_tasks (id, session_id, task_id, created_at) VALUES (?1, ?2, ?3, ?4)",
            params![st.id, st.session_id, st.task_id, st.created_at],
        )?;
        Ok(())
    }

    pub async fn list_tasks_by_session(&self, session_id: &str) -> SqlResult<Vec<SessionTask>> {
        let conn = self.conn.lock().await;
        let mut stmt = conn.prepare(
            "SELECT id, session_id, task_id, created_at FROM session_tasks WHERE session_id = ?1 ORDER BY created_at"
        )?;
        let rows = stmt.query_map(params![session_id], |row| {
            Ok(SessionTask {
                id: row.get(0)?, session_id: row.get(1)?, task_id: row.get(2)?, created_at: row.get(3)?,
            })
        })?;
        rows.collect()
    }

    // ── session_assets ──

    pub async fn add_session_asset(&self, sa: &SessionAsset) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        conn.execute(
            "INSERT INTO session_assets (id, session_id, asset_type, asset_path, file_size, created_at) VALUES (?1, ?2, ?3, ?4, ?5, ?6)",
            params![sa.id, sa.session_id, sa.asset_type, sa.asset_path, sa.file_size, sa.created_at],
        )?;
        Ok(())
    }

    pub async fn list_assets_by_session(&self, session_id: &str) -> SqlResult<Vec<SessionAsset>> {
        let conn = self.conn.lock().await;
        let mut stmt = conn.prepare(
            "SELECT id, session_id, asset_type, asset_path, file_size, created_at FROM session_assets WHERE session_id = ?1 ORDER BY created_at"
        )?;
        let rows = stmt.query_map(params![session_id], |row| {
            Ok(SessionAsset {
                id: row.get(0)?, session_id: row.get(1)?, asset_type: row.get(2)?,
                asset_path: row.get(3)?, file_size: row.get(4)?, created_at: row.get(5)?,
            })
        })?;
        rows.collect()
    }

    // ── message_media_refs ──

    pub async fn add_media_ref(&self, mr: &MessageMediaRef) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        conn.execute(
            "INSERT INTO message_media_refs (id, message_id, media_type, media_url, thumbnail_url, width, height, created_at)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8)",
            params![mr.id, mr.message_id, mr.media_type, mr.media_url, mr.thumbnail_url, mr.width, mr.height, mr.created_at],
        )?;
        Ok(())
    }

    pub async fn list_media_refs_by_message(&self, message_id: &str) -> SqlResult<Vec<MessageMediaRef>> {
        let conn = self.conn.lock().await;
        let mut stmt = conn.prepare(
            "SELECT id, message_id, media_type, media_url, thumbnail_url, width, height, created_at
             FROM message_media_refs WHERE message_id = ?1 ORDER BY created_at"
        )?;
        let rows = stmt.query_map(params![message_id], |row| {
            Ok(MessageMediaRef {
                id: row.get(0)?, message_id: row.get(1)?, media_type: row.get(2)?,
                media_url: row.get(3)?, thumbnail_url: row.get(4)?,
                width: row.get(5)?, height: row.get(6)?, created_at: row.get(7)?,
            })
        })?;
        rows.collect()
    }

    // ── message_citations ──

    pub async fn add_citation(&self, c: &MessageCitation) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        conn.execute(
            "INSERT INTO message_citations (id, message_id, citation_index, title, url, snippet, created_at)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)",
            params![c.id, c.message_id, c.citation_index, c.title, c.url, c.snippet, c.created_at],
        )?;
        Ok(())
    }

    pub async fn list_citations_by_message(&self, message_id: &str) -> SqlResult<Vec<MessageCitation>> {
        let conn = self.conn.lock().await;
        let mut stmt = conn.prepare(
            "SELECT id, message_id, citation_index, title, url, snippet, created_at
             FROM message_citations WHERE message_id = ?1 ORDER BY citation_index"
        )?;
        let rows = stmt.query_map(params![message_id], |row| {
            Ok(MessageCitation {
                id: row.get(0)?, message_id: row.get(1)?, citation_index: row.get(2)?,
                title: row.get(3)?, url: row.get(4)?, snippet: row.get(5)?, created_at: row.get(6)?,
            })
        })?;
        rows.collect()
    }
}
