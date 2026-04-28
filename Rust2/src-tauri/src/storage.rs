use rusqlite::{params, Connection, Result as SqlResult};
use sha2::{Sha256, Digest};
use std::collections::HashMap;
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
            -- [DEPRECATED] PR-4 决议: translation_history 为老表，已被 translation_sessions + translation_segments 替代。
            -- 保留仅为兼容旧数据读取。新代码不应写入此表。未来版本将删除。
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

        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        //  创作工坊 8 张表（对齐 C# StorageModels.cs + CreativeSessionRepository.cs）
        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        conn.execute_batch(
            "
            CREATE TABLE IF NOT EXISTS studio_sessions (
                id TEXT PRIMARY KEY,
                session_type TEXT NOT NULL DEFAULT '',
                name TEXT NOT NULL DEFAULT '',
                directory_path TEXT NOT NULL DEFAULT '',
                canvas_mode TEXT NOT NULL DEFAULT '',
                media_kind TEXT NOT NULL DEFAULT '',
                is_deleted INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at TEXT NOT NULL DEFAULT (datetime('now')),
                last_accessed_at TEXT,
                source_session_id TEXT,
                source_session_name TEXT,
                source_session_directory_name TEXT,
                source_asset_id TEXT,
                source_asset_kind TEXT,
                source_asset_file_name TEXT,
                source_asset_path TEXT,
                source_preview_path TEXT,
                source_reference_role TEXT,
                message_count INTEGER NOT NULL DEFAULT 0,
                task_count INTEGER NOT NULL DEFAULT 0,
                asset_count INTEGER NOT NULL DEFAULT 0,
                latest_message_preview TEXT,
                legacy_source_path TEXT,
                import_batch_id TEXT,
                imported_at TEXT,
                is_legacy_import INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_studio_sessions_type ON studio_sessions(session_type);
            CREATE INDEX IF NOT EXISTS idx_studio_sessions_updated ON studio_sessions(updated_at);

            CREATE TABLE IF NOT EXISTS studio_messages (
                id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                sequence_no INTEGER NOT NULL,
                role TEXT NOT NULL DEFAULT '',
                content_type TEXT NOT NULL DEFAULT 'text',
                text TEXT NOT NULL DEFAULT '',
                reasoning_text TEXT NOT NULL DEFAULT '',
                prompt_tokens INTEGER,
                completion_tokens INTEGER,
                generate_seconds REAL,
                download_seconds REAL,
                search_summary TEXT,
                timestamp TEXT NOT NULL DEFAULT (datetime('now')),
                is_deleted INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_studio_msg_session ON studio_messages(session_id);
            CREATE INDEX IF NOT EXISTS idx_studio_msg_seq ON studio_messages(session_id, sequence_no);

            CREATE TABLE IF NOT EXISTS studio_media_refs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                message_id TEXT NOT NULL,
                media_path TEXT NOT NULL DEFAULT '',
                media_kind TEXT NOT NULL DEFAULT '',
                sort_order INTEGER NOT NULL DEFAULT 0,
                preview_path TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_studio_mr_msg ON studio_media_refs(message_id);

            CREATE TABLE IF NOT EXISTS studio_citations (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                message_id TEXT NOT NULL,
                citation_number INTEGER NOT NULL DEFAULT 0,
                title TEXT NOT NULL DEFAULT '',
                url TEXT NOT NULL DEFAULT '',
                snippet TEXT NOT NULL DEFAULT '',
                hostname TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS idx_studio_cit_msg ON studio_citations(message_id);

            CREATE TABLE IF NOT EXISTS studio_attachments (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                message_id TEXT NOT NULL,
                attachment_type TEXT NOT NULL DEFAULT '',
                file_name TEXT NOT NULL DEFAULT '',
                file_path TEXT NOT NULL DEFAULT '',
                file_size INTEGER NOT NULL DEFAULT 0,
                sort_order INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_studio_att_msg ON studio_attachments(message_id);

            CREATE TABLE IF NOT EXISTS studio_tasks (
                id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                task_type TEXT NOT NULL DEFAULT '',
                status TEXT NOT NULL DEFAULT 'pending',
                prompt TEXT NOT NULL DEFAULT '',
                progress REAL NOT NULL DEFAULT 0.0,
                result_file_path TEXT,
                error_message TEXT,
                has_reference_input INTEGER NOT NULL DEFAULT 0,
                remote_video_id TEXT,
                remote_video_api_mode TEXT,
                remote_generation_id TEXT,
                remote_download_url TEXT,
                generate_seconds REAL,
                download_seconds REAL,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE INDEX IF NOT EXISTS idx_studio_task_session ON studio_tasks(session_id);
            CREATE INDEX IF NOT EXISTS idx_studio_task_status ON studio_tasks(status);

            CREATE TABLE IF NOT EXISTS studio_assets (
                asset_id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                group_id TEXT NOT NULL DEFAULT '',
                kind TEXT NOT NULL DEFAULT '',
                workflow TEXT NOT NULL DEFAULT '',
                file_name TEXT NOT NULL DEFAULT '',
                file_path TEXT NOT NULL DEFAULT '',
                preview_path TEXT NOT NULL DEFAULT '',
                prompt_text TEXT NOT NULL DEFAULT '',
                file_size INTEGER,
                mime_type TEXT,
                width INTEGER,
                height INTEGER,
                duration_ms INTEGER,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                modified_at TEXT NOT NULL DEFAULT (datetime('now')),
                storage_scope TEXT NOT NULL DEFAULT 'workspace-relative',
                derived_from_session_id TEXT,
                derived_from_session_name TEXT,
                derived_from_asset_id TEXT,
                derived_from_asset_file_name TEXT,
                derived_from_asset_kind TEXT,
                derived_from_reference_role TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_studio_asset_session ON studio_assets(session_id);

            CREATE TABLE IF NOT EXISTS studio_reference_images (
                id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                file_path TEXT NOT NULL DEFAULT '',
                sort_order INTEGER NOT NULL DEFAULT 0,
                width INTEGER,
                height INTEGER,
                created_at TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE INDEX IF NOT EXISTS idx_studio_refimg_session ON studio_reference_images(session_id);
            "
        )?;

        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        //  实时翻译 2 张表（对齐 C# TranslationHistoryRepository.cs）
        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        conn.execute_batch(
            "
            CREATE TABLE IF NOT EXISTS translation_sessions (
                id              TEXT PRIMARY KEY,
                started_at      TEXT NOT NULL DEFAULT (datetime('now')),
                stopped_at      TEXT,
                source_lang     TEXT NOT NULL DEFAULT '',
                target_langs    TEXT NOT NULL DEFAULT '[]',
                provider        TEXT NOT NULL DEFAULT '',
                status          TEXT NOT NULL DEFAULT 'active'
            );
            CREATE INDEX IF NOT EXISTS idx_tsess_status ON translation_sessions(status);
            CREATE INDEX IF NOT EXISTS idx_tsess_started ON translation_sessions(started_at);

            CREATE TABLE IF NOT EXISTS translation_segments (
                id              TEXT PRIMARY KEY,
                session_id      TEXT NOT NULL,
                sequence        INTEGER NOT NULL,
                original_text   TEXT NOT NULL DEFAULT '',
                translated_text TEXT NOT NULL DEFAULT '',
                target_lang     TEXT NOT NULL DEFAULT '',
                started_at      TEXT,
                ended_at        TEXT,
                is_bookmarked   INTEGER NOT NULL DEFAULT 0,
                bookmark_note   TEXT,
                audio_path      TEXT,
                raw_event_json  TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_tseg_session ON translation_segments(session_id);
            CREATE INDEX IF NOT EXISTS idx_tseg_seq ON translation_segments(session_id, sequence);
            CREATE INDEX IF NOT EXISTS idx_tseg_bookmark ON translation_segments(is_bookmarked);
            "
        )?;

        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        //  媒体中心 2 张增量表 + studio_sessions 加列
        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        conn.execute_batch(
            "
            CREATE TABLE IF NOT EXISTS canvas_rounds (
                id          TEXT PRIMARY KEY,
                session_id  TEXT NOT NULL,
                round_index INTEGER NOT NULL,
                prompt      TEXT NOT NULL DEFAULT '',
                params_json TEXT NOT NULL DEFAULT '{}',
                model_ref   TEXT NOT NULL DEFAULT '',
                created_at  TEXT NOT NULL DEFAULT (datetime('now')),
                status      TEXT NOT NULL DEFAULT 'pending'
            );
            CREATE INDEX IF NOT EXISTS idx_cr_session ON canvas_rounds(session_id);
            CREATE INDEX IF NOT EXISTS idx_cr_session_idx ON canvas_rounds(session_id, round_index);

            CREATE TABLE IF NOT EXISTS canvas_round_assets (
                id          TEXT PRIMARY KEY,
                round_id    TEXT NOT NULL,
                asset_id    TEXT NOT NULL,
                sequence    INTEGER NOT NULL DEFAULT 0,
                is_selected INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_cra_round ON canvas_round_assets(round_id);
            CREATE INDEX IF NOT EXISTS idx_cra_asset ON canvas_round_assets(asset_id);
            "
        )?;

        // 增量加列: studio_sessions 加 current_round_id（NULL 兼容，不破坏创作工坊数据）
        let _ = conn.execute_batch(
            "ALTER TABLE studio_sessions ADD COLUMN current_round_id TEXT;"
        );

        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        //  听析中心（AudioLab）6 张新表
        //  对齐 C# StorageModels.cs 字段，复用 studio_sessions(session_type='audio')
        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        conn.execute_batch(
            "
            CREATE TABLE IF NOT EXISTS audio_files (
                id                  TEXT PRIMARY KEY,
                display_name        TEXT NOT NULL,
                source_path         TEXT NOT NULL,
                mp3_path            TEXT,
                sample_rate         INTEGER NOT NULL DEFAULT 16000,
                channels            INTEGER NOT NULL DEFAULT 1,
                duration_ms         INTEGER NOT NULL DEFAULT 0,
                file_size_bytes     INTEGER NOT NULL DEFAULT 0,
                sha256              TEXT NOT NULL DEFAULT '',
                imported_at         TEXT NOT NULL DEFAULT (datetime('now')),
                last_opened_at      TEXT,
                is_legacy_import    INTEGER NOT NULL DEFAULT 0,
                legacy_source_path  TEXT,
                import_batch_id     TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_af_imported ON audio_files(imported_at);
            CREATE INDEX IF NOT EXISTS idx_af_sha256 ON audio_files(sha256);

            CREATE TABLE IF NOT EXISTS audio_transcripts (
                id              TEXT PRIMARY KEY,
                session_id      TEXT NOT NULL,
                audio_file_id   TEXT NOT NULL,
                language        TEXT NOT NULL DEFAULT '',
                raw_json        TEXT,
                parser_kind     TEXT NOT NULL DEFAULT 'fast',
                created_at      TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE INDEX IF NOT EXISTS idx_at_session ON audio_transcripts(session_id);
            CREATE INDEX IF NOT EXISTS idx_at_file ON audio_transcripts(audio_file_id);

            CREATE TABLE IF NOT EXISTS audio_segments (
                id              TEXT PRIMARY KEY,
                transcript_id   TEXT NOT NULL,
                sequence        INTEGER NOT NULL,
                speaker         TEXT NOT NULL DEFAULT '',
                speaker_index   INTEGER NOT NULL DEFAULT 0,
                start_ms        INTEGER NOT NULL DEFAULT 0,
                end_ms          INTEGER NOT NULL DEFAULT 0,
                text            TEXT NOT NULL DEFAULT '',
                confidence      REAL
            );
            CREATE INDEX IF NOT EXISTS idx_aseg_transcript ON audio_segments(transcript_id);
            CREATE INDEX IF NOT EXISTS idx_aseg_seq ON audio_segments(transcript_id, sequence);

            CREATE TABLE IF NOT EXISTS audio_stage_outputs (
                id                  TEXT PRIMARY KEY,
                session_id          TEXT NOT NULL,
                stage_key           TEXT NOT NULL,
                content_markdown    TEXT NOT NULL DEFAULT '',
                status              TEXT NOT NULL DEFAULT 'Empty',
                error_message       TEXT,
                model_ref           TEXT,
                generated_at        TEXT,
                custom_stage_key    TEXT,
                custom_is_mindmap   INTEGER
            );
            CREATE INDEX IF NOT EXISTS idx_aso_session ON audio_stage_outputs(session_id);
            CREATE UNIQUE INDEX IF NOT EXISTS idx_aso_session_stage ON audio_stage_outputs(session_id, stage_key);

            CREATE TABLE IF NOT EXISTS audio_research_topics (
                id              TEXT PRIMARY KEY,
                session_id      TEXT NOT NULL,
                title           TEXT NOT NULL DEFAULT '',
                description     TEXT NOT NULL DEFAULT '',
                status          TEXT NOT NULL DEFAULT 'idle',
                report_markdown TEXT,
                created_at      TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE INDEX IF NOT EXISTS idx_art_session ON audio_research_topics(session_id);

            CREATE TABLE IF NOT EXISTS audio_auto_tags (
                id              TEXT PRIMARY KEY,
                session_id      TEXT NOT NULL,
                tag             TEXT NOT NULL DEFAULT '',
                source          TEXT NOT NULL DEFAULT 'auto',
                created_at      TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE INDEX IF NOT EXISTS idx_aat_session ON audio_auto_tags(session_id);

            CREATE TABLE IF NOT EXISTS audio_stage_presets (
                id              TEXT PRIMARY KEY,
                stage           TEXT NOT NULL,
                display_name    TEXT NOT NULL DEFAULT '',
                system_prompt   TEXT NOT NULL DEFAULT '',
                show_in_tab     INTEGER NOT NULL DEFAULT 1,
                include_in_batch INTEGER NOT NULL DEFAULT 1,
                is_enabled      INTEGER NOT NULL DEFAULT 1,
                display_mode    TEXT NOT NULL DEFAULT 'Markdown',
                sort_order      INTEGER NOT NULL DEFAULT 0
            );
            CREATE UNIQUE INDEX IF NOT EXISTS idx_asp_stage ON audio_stage_presets(stage);
            "
        )?;

        // 增量加列: studio_sessions 加 audio_path / audio_duration_ms / audio_imported_at
        let _ = conn.execute_batch(
            "ALTER TABLE studio_sessions ADD COLUMN audio_path TEXT;"
        );
        let _ = conn.execute_batch(
            "ALTER TABLE studio_sessions ADD COLUMN audio_duration_ms INTEGER;"
        );
        let _ = conn.execute_batch(
            "ALTER TABLE studio_sessions ADD COLUMN audio_imported_at TEXT;"
        );

        // ── 老 audio_sessions 表决议 ──
        // 废弃标记：不删除旧表（防止已有数据丢失），但新代码不再读写。
        // 新代码全部使用 studio_sessions(session_type='audio') + audio_files 体系。
        // 如果未来需要迁移旧数据，可读 audio_sessions 表手动导入。

        // ── PR-1 任务监控: 增强 task_executions 表 ──
        // 对齐 C# TaskExecutionRepository schema:
        // 新增 billable, audio_item_id, stage, model_name, cancel_reason, debug_prompt, debug_response, tokens_in, tokens_out
        let _ = conn.execute_batch("ALTER TABLE task_executions ADD COLUMN billable INTEGER NOT NULL DEFAULT 0;");
        let _ = conn.execute_batch("ALTER TABLE task_executions ADD COLUMN audio_item_id TEXT;");
        let _ = conn.execute_batch("ALTER TABLE task_executions ADD COLUMN stage TEXT;");
        let _ = conn.execute_batch("ALTER TABLE task_executions ADD COLUMN model_name TEXT;");
        let _ = conn.execute_batch("ALTER TABLE task_executions ADD COLUMN cancel_reason TEXT;");
        let _ = conn.execute_batch("ALTER TABLE task_executions ADD COLUMN debug_prompt TEXT;");
        let _ = conn.execute_batch("ALTER TABLE task_executions ADD COLUMN debug_response TEXT;");
        let _ = conn.execute_batch("ALTER TABLE task_executions ADD COLUMN tokens_in INTEGER;");
        let _ = conn.execute_batch("ALTER TABLE task_executions ADD COLUMN tokens_out INTEGER;");
        let _ = conn.execute_batch("ALTER TABLE task_executions ADD COLUMN error_message TEXT;");
        let _ = conn.execute_batch("ALTER TABLE task_executions ADD COLUMN finished_at TEXT;");

        // PR-1: audio_task_queue 增加 progress_message 列
        let _ = conn.execute_batch("ALTER TABLE audio_task_queue ADD COLUMN progress_message TEXT;");
        // PR-1: audio_task_queue 增加 parent_task_id 列 (重试追踪)
        let _ = conn.execute_batch("ALTER TABLE audio_task_queue ADD COLUMN parent_task_id TEXT;");
        // PR-1: audio_task_queue 增加 last_heartbeat_at 列 (跨进程恢复)
        let _ = conn.execute_batch("ALTER TABLE audio_task_queue ADD COLUMN last_heartbeat_at TEXT;");

        // ── PR-1: batch_tasks 决议 ──
        // [DEPRECATED] batch_tasks 表标记废弃。字段与 C# 不匹配，且 audio_task_queue 已是统一任务表。
        // 不删除旧表（防止数据丢失），但新代码不再写入此表。

        // PR-1: 初始化监控默认设置
        let _ = conn.execute_batch(
            "INSERT OR IGNORE INTO kv_store (key, value) VALUES ('monitor.max_transcription_concurrency', '2');
             INSERT OR IGNORE INTO kv_store (key, value) VALUES ('monitor.max_ai_concurrency', '4');
             INSERT OR IGNORE INTO kv_store (key, value) VALUES ('monitor.transcription_timeout_minutes', '10');"
        );

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

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  创作工坊 CRUD（对齐 C# CreativeSessionRepository + SessionMessageRepository）
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    pub async fn studio_create_session(&self, s: &StudioSession) -> SqlResult<()> {
        let conn = self.conn.lock().await;
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
        )?;
        Ok(())
    }

    pub async fn studio_get_session(&self, id: &str) -> SqlResult<Option<StudioSession>> {
        let conn = self.conn.lock().await;
        let mut stmt = conn.prepare("SELECT * FROM studio_sessions WHERE id = ?1")?;
        let mut rows = stmt.query_map(params![id], Self::map_studio_session)?;
        match rows.next() {
            Some(Ok(s)) => Ok(Some(s)),
            Some(Err(e)) => Err(e),
            None => Ok(None),
        }
    }

    pub async fn studio_list_sessions(&self, limit: i64, offset: i64) -> SqlResult<Vec<StudioSession>> {
        let conn = self.conn.lock().await;
        let mut stmt = conn.prepare(
            "SELECT * FROM studio_sessions WHERE is_deleted = 0 ORDER BY updated_at DESC, id DESC LIMIT ?1 OFFSET ?2"
        )?;
        let rows = stmt.query_map(params![limit, offset], Self::map_studio_session)?;
        rows.collect()
    }

    pub async fn studio_rename_session(&self, id: &str, new_name: &str) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        let now = chrono::Utc::now().to_rfc3339();
        conn.execute(
            "UPDATE studio_sessions SET name = ?1, updated_at = ?2 WHERE id = ?3",
            params![new_name, now, id],
        )?;
        Ok(())
    }

    pub async fn studio_soft_delete_session(&self, id: &str) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        let now = chrono::Utc::now().to_rfc3339();
        conn.execute(
            "UPDATE studio_sessions SET is_deleted = 1, updated_at = ?1 WHERE id = ?2",
            params![now, id],
        )?;
        Ok(())
    }

    pub async fn studio_update_counts(&self, id: &str, mc: i64, tc: i64, ac: i64) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        let now = chrono::Utc::now().to_rfc3339();
        conn.execute(
            "UPDATE studio_sessions SET message_count = ?1, task_count = ?2, asset_count = ?3, updated_at = ?4 WHERE id = ?5",
            params![mc, tc, ac, now, id],
        )?;
        Ok(())
    }

    pub async fn studio_update_last_accessed(&self, id: &str) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        let now = chrono::Utc::now().to_rfc3339();
        conn.execute(
            "UPDATE studio_sessions SET last_accessed_at = ?1 WHERE id = ?2",
            params![now, id],
        )?;
        Ok(())
    }

    pub async fn studio_update_latest_preview(&self, id: &str, preview: &str) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        let now = chrono::Utc::now().to_rfc3339();
        conn.execute(
            "UPDATE studio_sessions SET latest_message_preview = ?1, updated_at = ?2 WHERE id = ?3",
            params![preview, now, id],
        )?;
        Ok(())
    }

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

    // ── 消息 CRUD ──

    pub async fn studio_append_message(&self, msg: &StudioMessage) -> SqlResult<()> {
        let conn = self.conn.lock().await;
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
        )?;
        // 更新 session counts
        conn.execute(
            "UPDATE studio_sessions SET message_count = message_count + 1, updated_at = ?1 WHERE id = ?2",
            params![msg.timestamp, msg.session_id],
        )?;
        Ok(())
    }

    pub async fn studio_update_message(&self, msg: &StudioMessage) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        conn.execute(
            "UPDATE studio_messages SET text = ?1, reasoning_text = ?2, prompt_tokens = ?3,
             completion_tokens = ?4, generate_seconds = ?5, download_seconds = ?6,
             search_summary = ?7, timestamp = ?8 WHERE id = ?9",
            params![
                msg.text, msg.reasoning_text, msg.prompt_tokens, msg.completion_tokens,
                msg.generate_seconds, msg.download_seconds, msg.search_summary, msg.timestamp, msg.id,
            ],
        )?;
        Ok(())
    }

    pub async fn studio_get_max_sequence(&self, session_id: &str) -> SqlResult<i64> {
        let conn = self.conn.lock().await;
        let seq: i64 = conn.query_row(
            "SELECT COALESCE(MAX(sequence_no), 0) FROM studio_messages WHERE session_id = ?1",
            params![session_id],
            |row| row.get(0),
        )?;
        Ok(seq)
    }

    pub async fn studio_get_messages_before(&self, session_id: &str, before_seq: i64, limit: i64) -> SqlResult<Vec<StudioMessage>> {
        let conn = self.conn.lock().await;
        let mut stmt = conn.prepare(
            "SELECT * FROM studio_messages WHERE session_id = ?1 AND is_deleted = 0 AND sequence_no < ?2
             ORDER BY sequence_no DESC LIMIT ?3"
        )?;
        let rows = stmt.query_map(params![session_id, before_seq, limit], Self::map_studio_message)?;
        let mut result: Vec<StudioMessage> = rows.collect::<Result<Vec<_>, _>>()?;
        result.reverse();
        Ok(result)
    }

    pub async fn studio_get_session_bundle(&self, session_id: &str) -> SqlResult<StudioSessionBundle> {
        let conn = self.conn.lock().await;

        // 1. 全量消息
        let mut msg_stmt = conn.prepare(
            "SELECT * FROM studio_messages WHERE session_id = ?1 AND is_deleted = 0 ORDER BY sequence_no ASC"
        )?;
        let messages: Vec<StudioMessage> = msg_stmt.query_map(params![session_id], Self::map_studio_message)?.collect::<Result<Vec<_>, _>>()?;
        if messages.is_empty() {
            return Ok(StudioSessionBundle {
                messages, media_refs: HashMap::new(), citations: HashMap::new(), attachments: HashMap::new(),
            });
        }

        let ids: Vec<String> = messages.iter().map(|m| m.id.clone()).collect();
        let placeholders: String = ids.iter().enumerate().map(|(i, _)| format!("?{}", i + 1)).collect::<Vec<_>>().join(",");

        // 2. 媒体引用
        let mut media_refs: HashMap<String, Vec<StudioMediaRef>> = HashMap::new();
        {
            let sql = format!("SELECT * FROM studio_media_refs WHERE message_id IN ({}) ORDER BY sort_order", placeholders);
            let mut stmt = conn.prepare(&sql)?;
            let params_vec: Vec<Box<dyn rusqlite::types::ToSql>> = ids.iter().map(|id| Box::new(id.clone()) as Box<dyn rusqlite::types::ToSql>).collect();
            let param_refs: Vec<&dyn rusqlite::types::ToSql> = params_vec.iter().map(|p| p.as_ref()).collect();
            let rows = stmt.query_map(param_refs.as_slice(), |row| {
                Ok(StudioMediaRef {
                    id: row.get("id")?,
                    message_id: row.get("message_id")?,
                    media_path: row.get("media_path")?,
                    media_kind: row.get("media_kind")?,
                    sort_order: row.get("sort_order")?,
                    preview_path: row.get("preview_path")?,
                })
            })?;
            for r in rows {
                let rec = r?;
                media_refs.entry(rec.message_id.clone()).or_default().push(rec);
            }
        }

        // 3. 引文
        let mut citations: HashMap<String, Vec<StudioCitation>> = HashMap::new();
        {
            let sql = format!("SELECT * FROM studio_citations WHERE message_id IN ({}) ORDER BY citation_number", placeholders);
            let mut stmt = conn.prepare(&sql)?;
            let params_vec: Vec<Box<dyn rusqlite::types::ToSql>> = ids.iter().map(|id| Box::new(id.clone()) as Box<dyn rusqlite::types::ToSql>).collect();
            let param_refs: Vec<&dyn rusqlite::types::ToSql> = params_vec.iter().map(|p| p.as_ref()).collect();
            let rows = stmt.query_map(param_refs.as_slice(), |row| {
                Ok(StudioCitation {
                    id: row.get("id")?,
                    message_id: row.get("message_id")?,
                    citation_number: row.get("citation_number")?,
                    title: row.get("title")?,
                    url: row.get("url")?,
                    snippet: row.get("snippet")?,
                    hostname: row.get("hostname")?,
                })
            })?;
            for r in rows {
                let rec = r?;
                citations.entry(rec.message_id.clone()).or_default().push(rec);
            }
        }

        // 4. 附件
        let mut attachments: HashMap<String, Vec<StudioAttachment>> = HashMap::new();
        {
            let sql = format!("SELECT * FROM studio_attachments WHERE message_id IN ({}) ORDER BY sort_order", placeholders);
            let mut stmt = conn.prepare(&sql)?;
            let params_vec: Vec<Box<dyn rusqlite::types::ToSql>> = ids.iter().map(|id| Box::new(id.clone()) as Box<dyn rusqlite::types::ToSql>).collect();
            let param_refs: Vec<&dyn rusqlite::types::ToSql> = params_vec.iter().map(|p| p.as_ref()).collect();
            let rows = stmt.query_map(param_refs.as_slice(), |row| {
                Ok(StudioAttachment {
                    id: row.get("id")?,
                    message_id: row.get("message_id")?,
                    attachment_type: row.get("attachment_type")?,
                    file_name: row.get("file_name")?,
                    file_path: row.get("file_path")?,
                    file_size: row.get("file_size")?,
                    sort_order: row.get("sort_order")?,
                })
            })?;
            for r in rows {
                let rec = r?;
                attachments.entry(rec.message_id.clone()).or_default().push(rec);
            }
        }

        Ok(StudioSessionBundle { messages, media_refs, citations, attachments })
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

    // ── 任务 CRUD ──

    pub async fn studio_upsert_task(&self, t: &StudioTask) -> SqlResult<()> {
        let conn = self.conn.lock().await;
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
        )?;
        Ok(())
    }

    pub async fn studio_list_running_tasks(&self, session_id: &str) -> SqlResult<Vec<StudioTask>> {
        let conn = self.conn.lock().await;
        let mut stmt = conn.prepare(
            "SELECT * FROM studio_tasks WHERE session_id = ?1 AND status IN ('pending','running') ORDER BY created_at"
        )?;
        let rows = stmt.query_map(params![session_id], Self::map_studio_task)?;
        rows.collect()
    }

    pub async fn studio_list_all_running_tasks(&self) -> SqlResult<Vec<StudioTask>> {
        let conn = self.conn.lock().await;
        let mut stmt = conn.prepare(
            "SELECT * FROM studio_tasks WHERE status IN ('pending','running') ORDER BY created_at"
        )?;
        let rows = stmt.query_map([], Self::map_studio_task)?;
        rows.collect()
    }

    pub async fn studio_get_interrupted_video_tasks(&self) -> SqlResult<Vec<StudioTask>> {
        let conn = self.conn.lock().await;
        let mut stmt = conn.prepare(
            "SELECT * FROM studio_tasks WHERE status = 'running' AND remote_video_id IS NOT NULL ORDER BY created_at"
        )?;
        let rows = stmt.query_map([], Self::map_studio_task)?;
        rows.collect()
    }

    pub async fn studio_update_task_status(&self, task_id: &str, status: &str, error: Option<&str>) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        let now = chrono::Utc::now().to_rfc3339();
        conn.execute(
            "UPDATE studio_tasks SET status = ?1, error_message = ?2, updated_at = ?3 WHERE id = ?4",
            params![status, error, now, task_id],
        )?;
        Ok(())
    }

    pub async fn studio_update_task_progress(&self, task_id: &str, progress: f64) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        let now = chrono::Utc::now().to_rfc3339();
        conn.execute(
            "UPDATE studio_tasks SET progress = ?1, updated_at = ?2 WHERE id = ?3",
            params![progress, now, task_id],
        )?;
        Ok(())
    }

    pub async fn studio_update_task_result(&self, task_id: &str, result_path: &str, gen_secs: Option<f64>, dl_secs: Option<f64>) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        let now = chrono::Utc::now().to_rfc3339();
        conn.execute(
            "UPDATE studio_tasks SET status = 'completed', result_file_path = ?1, generate_seconds = ?2, download_seconds = ?3, progress = 1.0, updated_at = ?4 WHERE id = ?5",
            params![result_path, gen_secs, dl_secs, now, task_id],
        )?;
        Ok(())
    }

    fn map_studio_task(row: &rusqlite::Row) -> rusqlite::Result<StudioTask> {
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

    // ── 参考图 CRUD ──

    pub async fn studio_add_reference_image(&self, img: &StudioReferenceImage) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        conn.execute(
            "INSERT INTO studio_reference_images (id, session_id, file_path, sort_order, width, height, created_at)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)",
            params![img.id, img.session_id, img.file_path, img.sort_order, img.width, img.height, img.created_at],
        )?;
        Ok(())
    }

    pub async fn studio_list_reference_images(&self, session_id: &str) -> SqlResult<Vec<StudioReferenceImage>> {
        let conn = self.conn.lock().await;
        let mut stmt = conn.prepare(
            "SELECT id, session_id, file_path, sort_order, width, height, created_at
             FROM studio_reference_images WHERE session_id = ?1 ORDER BY sort_order"
        )?;
        let rows = stmt.query_map(params![session_id], |row| {
            Ok(StudioReferenceImage {
                id: row.get(0)?, session_id: row.get(1)?, file_path: row.get(2)?,
                sort_order: row.get(3)?, width: row.get(4)?, height: row.get(5)?, created_at: row.get(6)?,
            })
        })?;
        rows.collect()
    }

    pub async fn studio_delete_reference_image(&self, id: &str) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        conn.execute("DELETE FROM studio_reference_images WHERE id = ?1", params![id])?;
        Ok(())
    }

    // ── 媒体引用批量操作 ──

    pub async fn studio_insert_asset(&self, a: &StudioAsset) -> SqlResult<()> {
        let conn = self.conn.lock().await;
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
        )?;
        // 更新 session asset_count
        conn.execute(
            "UPDATE studio_sessions SET asset_count = (SELECT COUNT(*) FROM studio_assets WHERE session_id = ?1) WHERE id = ?1",
            params![a.session_id],
        )?;
        Ok(())
    }

    pub async fn studio_insert_media_refs(&self, message_id: &str, refs: &[StudioMediaRef]) -> SqlResult<()> {
        if refs.is_empty() { return Ok(()); }
        let conn = self.conn.lock().await;
        for mr in refs {
            conn.execute(
                "INSERT INTO studio_media_refs (message_id, media_path, media_kind, sort_order, preview_path)
                 VALUES (?1, ?2, ?3, ?4, ?5)",
                params![message_id, mr.media_path, mr.media_kind, mr.sort_order, mr.preview_path],
            )?;
        }
        Ok(())
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  实时翻译 — translation_sessions / translation_segments
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    pub async fn live_create_session(&self, session: &TranslationSession) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        conn.execute(
            "INSERT INTO translation_sessions (id, started_at, stopped_at, source_lang, target_langs, provider, status)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)",
            params![
                session.id, session.started_at, session.stopped_at,
                session.source_lang, session.target_langs, session.provider, session.status,
            ],
        )?;
        Ok(())
    }

    pub async fn live_stop_session(&self, session_id: &str, stopped_at: &str) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        conn.execute(
            "UPDATE translation_sessions SET status = 'stopped', stopped_at = ?1 WHERE id = ?2",
            params![stopped_at, session_id],
        )?;
        Ok(())
    }

    pub async fn live_get_active_session(&self) -> SqlResult<Option<TranslationSession>> {
        let conn = self.conn.lock().await;
        let mut stmt = conn.prepare(
            "SELECT id, started_at, stopped_at, source_lang, target_langs, provider, status
             FROM translation_sessions WHERE status = 'active' ORDER BY started_at DESC LIMIT 1"
        )?;
        let mut rows = stmt.query([])?;
        if let Some(row) = rows.next()? {
            Ok(Some(TranslationSession {
                id: row.get(0)?,
                started_at: row.get(1)?,
                stopped_at: row.get(2)?,
                source_lang: row.get(3)?,
                target_langs: row.get(4)?,
                provider: row.get(5)?,
                status: row.get(6)?,
            }))
        } else {
            Ok(None)
        }
    }

    pub async fn live_list_sessions(&self, limit: u32, offset: u32) -> SqlResult<Vec<TranslationSession>> {
        let conn = self.conn.lock().await;
        let mut stmt = conn.prepare(
            "SELECT id, started_at, stopped_at, source_lang, target_langs, provider, status
             FROM translation_sessions ORDER BY started_at DESC LIMIT ?1 OFFSET ?2"
        )?;
        let rows = stmt.query_map(params![limit, offset], |row| {
            Ok(TranslationSession {
                id: row.get(0)?,
                started_at: row.get(1)?,
                stopped_at: row.get(2)?,
                source_lang: row.get(3)?,
                target_langs: row.get(4)?,
                provider: row.get(5)?,
                status: row.get(6)?,
            })
        })?;
        rows.collect()
    }

    pub async fn live_insert_segment(&self, seg: &TranslationSegment) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        conn.execute(
            "INSERT INTO translation_segments
             (id, session_id, sequence, original_text, translated_text, target_lang, started_at, ended_at, is_bookmarked, bookmark_note, audio_path, raw_event_json)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12)",
            params![
                seg.id, seg.session_id, seg.sequence,
                seg.original_text, seg.translated_text, seg.target_lang,
                seg.started_at, seg.ended_at,
                seg.is_bookmarked, seg.bookmark_note,
                seg.audio_path, seg.raw_event_json,
            ],
        )?;
        Ok(())
    }

    pub async fn live_get_recent_segments(&self, session_id: &str, limit: u32) -> SqlResult<Vec<TranslationSegment>> {
        let conn = self.conn.lock().await;
        let mut stmt = conn.prepare(
            "SELECT id, session_id, sequence, original_text, translated_text, target_lang,
                    started_at, ended_at, is_bookmarked, bookmark_note, audio_path, raw_event_json
             FROM translation_segments WHERE session_id = ?1
             ORDER BY sequence DESC LIMIT ?2"
        )?;
        let rows = stmt.query_map(params![session_id, limit], |row| {
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
        })?;
        let mut segs: Vec<TranslationSegment> = rows.collect::<SqlResult<Vec<_>>>()?;
        segs.reverse();
        Ok(segs)
    }

    pub async fn live_get_max_sequence(&self, session_id: &str) -> SqlResult<i64> {
        let conn = self.conn.lock().await;
        let mut stmt = conn.prepare(
            "SELECT COALESCE(MAX(sequence), 0) FROM translation_segments WHERE session_id = ?1"
        )?;
        stmt.query_row(params![session_id], |row| row.get(0))
    }

    pub async fn live_bookmark_segment(&self, segment_id: &str, note: Option<&str>) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        conn.execute(
            "UPDATE translation_segments SET is_bookmarked = 1, bookmark_note = ?1 WHERE id = ?2",
            params![note, segment_id],
        )?;
        Ok(())
    }

    pub async fn live_unbookmark_segment(&self, segment_id: &str) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        conn.execute(
            "UPDATE translation_segments SET is_bookmarked = 0, bookmark_note = NULL WHERE id = ?1",
            params![segment_id],
        )?;
        Ok(())
    }

    /// PR-4: 清空指定会话的所有片段
    pub async fn live_clear_session_segments(&self, session_id: &str) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        conn.execute(
            "DELETE FROM translation_segments WHERE session_id = ?1",
            params![session_id],
        )?;
        Ok(())
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  媒体中心 CRUD（对齐 C# SessionContentRepository + ICreativeSessionRepository）
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    pub async fn center_list_workspaces(&self, limit: i64, offset: i64) -> SqlResult<Vec<CenterWorkspace>> {
        let conn = self.conn.lock().await;
        let mut stmt = conn.prepare(
            "SELECT s.id, s.session_type, s.name, s.is_deleted, s.created_at, s.updated_at,
                    s.last_accessed_at, s.current_round_id,
                    (SELECT COUNT(*) FROM canvas_rounds WHERE session_id = s.id) as round_count,
                    s.asset_count,
                    (SELECT COUNT(*) FROM studio_tasks WHERE session_id = s.id AND status IN ('pending','running','polling')) as has_running
             FROM studio_sessions s
             WHERE s.is_deleted = 0 AND s.session_type IN ('canvas_image','canvas_video')
             ORDER BY s.updated_at DESC LIMIT ?1 OFFSET ?2"
        )?;
        let rows = stmt.query_map(params![limit, offset], |row| {
            Ok(CenterWorkspace {
                id: row.get(0)?,
                session_type: row.get(1)?,
                name: row.get(2)?,
                is_deleted: row.get::<_, i64>(3)? != 0,
                created_at: row.get(4)?,
                updated_at: row.get(5)?,
                last_accessed_at: row.get(6)?,
                current_round_id: row.get(7)?,
                round_count: row.get(8)?,
                asset_count: row.get(9)?,
                has_running_task: row.get::<_, i64>(10)? > 0,
            })
        })?;
        rows.collect()
    }

    pub async fn center_create_workspace(&self, kind: &str, name: &str) -> SqlResult<CenterWorkspace> {
        let conn = self.conn.lock().await;
        let id = uuid::Uuid::new_v4().to_string();
        let now = chrono::Utc::now().to_rfc3339();
        conn.execute(
            "INSERT INTO studio_sessions (id, session_type, name, directory_path, canvas_mode, media_kind, is_deleted, created_at, updated_at, last_accessed_at, message_count, task_count, asset_count, is_legacy_import)
             VALUES (?1, ?2, ?3, '', '', '', 0, ?4, ?4, ?4, 0, 0, 0, 0)",
            params![id, kind, name, now],
        )?;
        Ok(CenterWorkspace {
            id,
            session_type: kind.to_string(),
            name: name.to_string(),
            is_deleted: false,
            created_at: now.clone(),
            updated_at: now.clone(),
            last_accessed_at: Some(now),
            current_round_id: None,
            round_count: 0,
            asset_count: 0,
            has_running_task: false,
        })
    }

    pub async fn center_rename_workspace(&self, id: &str, new_name: &str) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        let now = chrono::Utc::now().to_rfc3339();
        conn.execute(
            "UPDATE studio_sessions SET name = ?1, updated_at = ?2 WHERE id = ?3",
            params![new_name, now, id],
        )?;
        Ok(())
    }

    pub async fn center_soft_delete_workspace(&self, id: &str) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        let now = chrono::Utc::now().to_rfc3339();
        conn.execute(
            "UPDATE studio_sessions SET is_deleted = 1, updated_at = ?1 WHERE id = ?2",
            params![now, id],
        )?;
        Ok(())
    }

    pub async fn center_get_workspace_bundle(&self, workspace_id: &str) -> SqlResult<CenterWorkspaceBundle> {
        let conn = self.conn.lock().await;

        // 1. 工作区元数据
        let ws = conn.query_row(
            "SELECT s.id, s.session_type, s.name, s.is_deleted, s.created_at, s.updated_at,
                    s.last_accessed_at, s.current_round_id,
                    (SELECT COUNT(*) FROM canvas_rounds WHERE session_id = s.id) as round_count,
                    s.asset_count,
                    (SELECT COUNT(*) FROM studio_tasks WHERE session_id = s.id AND status IN ('pending','running','polling')) as has_running
             FROM studio_sessions s WHERE s.id = ?1",
            params![workspace_id],
            |row| Ok(CenterWorkspace {
                id: row.get(0)?,
                session_type: row.get(1)?,
                name: row.get(2)?,
                is_deleted: row.get::<_, i64>(3)? != 0,
                created_at: row.get(4)?,
                updated_at: row.get(5)?,
                last_accessed_at: row.get(6)?,
                current_round_id: row.get(7)?,
                round_count: row.get(8)?,
                asset_count: row.get(9)?,
                has_running_task: row.get::<_, i64>(10)? > 0,
            }),
        )?;

        // 2. 所有 rounds
        let mut round_stmt = conn.prepare(
            "SELECT id, session_id, round_index, prompt, params_json, model_ref, created_at, status
             FROM canvas_rounds WHERE session_id = ?1 ORDER BY round_index ASC"
        )?;
        let rounds: Vec<CanvasRound> = round_stmt.query_map(params![workspace_id], |row| {
            Ok(CanvasRound {
                id: row.get(0)?,
                session_id: row.get(1)?,
                round_index: row.get(2)?,
                prompt: row.get(3)?,
                params_json: row.get(4)?,
                model_ref: row.get(5)?,
                created_at: row.get(6)?,
                status: row.get(7)?,
            })
        })?.collect::<SqlResult<Vec<_>>>()?;

        // 3. 当前 round 的资产
        let current_round_id = ws.current_round_id.clone()
            .or_else(|| rounds.last().map(|r| r.id.clone()));
        let current_round_assets = if let Some(ref rid) = current_round_id {
            let mut stmt = conn.prepare(
                "SELECT cra.id, cra.round_id, cra.asset_id, cra.sequence, cra.is_selected,
                        sa.file_path, sa.preview_path, sa.kind, sa.width, sa.height, sa.duration_ms, sa.created_at
                 FROM canvas_round_assets cra
                 JOIN studio_assets sa ON sa.asset_id = cra.asset_id
                 WHERE cra.round_id = ?1 ORDER BY cra.sequence"
            )?;
            let rows = stmt.query_map(params![rid], |row| {
                Ok(CenterAssetDetail {
                    id: row.get(0)?,
                    round_id: row.get(1)?,
                    asset_id: row.get(2)?,
                    sequence: row.get(3)?,
                    is_selected: row.get::<_, i64>(4)? != 0,
                    file_path: row.get(5)?,
                    preview_path: row.get(6)?,
                    kind: row.get(7)?,
                    width: row.get(8)?,
                    height: row.get(9)?,
                    duration_ms: row.get(10)?,
                    created_at: row.get(11)?,
                })
            })?;
            rows.collect::<SqlResult<Vec<_>>>()?
        } else {
            vec![]
        };

        // 4. 参考图
        let mut ref_stmt = conn.prepare(
            "SELECT id, session_id, file_path, sort_order, width, height, created_at
             FROM studio_reference_images WHERE session_id = ?1 ORDER BY sort_order"
        )?;
        let reference_images: Vec<StudioReferenceImage> = ref_stmt.query_map(params![workspace_id], |row| {
            Ok(StudioReferenceImage {
                id: row.get(0)?, session_id: row.get(1)?, file_path: row.get(2)?,
                sort_order: row.get(3)?, width: row.get(4)?, height: row.get(5)?, created_at: row.get(6)?,
            })
        })?.collect::<SqlResult<Vec<_>>>()?;

        // 5. 正在运行的任务
        let mut task_stmt = conn.prepare(
            "SELECT * FROM studio_tasks WHERE session_id = ?1 AND status IN ('pending','running','polling')"
        )?;
        let running_tasks: Vec<StudioTask> = task_stmt.query_map(params![workspace_id], Self::map_studio_task)?.collect::<SqlResult<Vec<_>>>()?;

        Ok(CenterWorkspaceBundle {
            workspace: ws,
            rounds,
            current_round_assets,
            reference_images,
            running_tasks,
        })
    }

    pub async fn center_list_rounds(&self, workspace_id: &str) -> SqlResult<Vec<CanvasRound>> {
        let conn = self.conn.lock().await;
        let mut stmt = conn.prepare(
            "SELECT id, session_id, round_index, prompt, params_json, model_ref, created_at, status
             FROM canvas_rounds WHERE session_id = ?1 ORDER BY round_index ASC"
        )?;
        let rows = stmt.query_map(params![workspace_id], |row| {
            Ok(CanvasRound {
                id: row.get(0)?, session_id: row.get(1)?, round_index: row.get(2)?,
                prompt: row.get(3)?, params_json: row.get(4)?, model_ref: row.get(5)?,
                created_at: row.get(6)?, status: row.get(7)?,
            })
        })?;
        rows.collect()
    }

    pub async fn center_get_round(&self, round_id: &str) -> SqlResult<Option<CanvasRound>> {
        let conn = self.conn.lock().await;
        let mut stmt = conn.prepare(
            "SELECT id, session_id, round_index, prompt, params_json, model_ref, created_at, status
             FROM canvas_rounds WHERE id = ?1"
        )?;
        let mut rows = stmt.query_map(params![round_id], |row| {
            Ok(CanvasRound {
                id: row.get(0)?, session_id: row.get(1)?, round_index: row.get(2)?,
                prompt: row.get(3)?, params_json: row.get(4)?, model_ref: row.get(5)?,
                created_at: row.get(6)?, status: row.get(7)?,
            })
        })?;
        match rows.next() {
            Some(Ok(r)) => Ok(Some(r)),
            Some(Err(e)) => Err(e),
            None => Ok(None),
        }
    }

    pub async fn center_create_round(&self, session_id: &str, prompt: &str, params_json: &str, model_ref: &str) -> SqlResult<CanvasRound> {
        let conn = self.conn.lock().await;
        let id = uuid::Uuid::new_v4().to_string();
        let now = chrono::Utc::now().to_rfc3339();
        let round_index: i64 = conn.query_row(
            "SELECT COALESCE(MAX(round_index), 0) + 1 FROM canvas_rounds WHERE session_id = ?1",
            params![session_id],
            |row| row.get(0),
        )?;
        conn.execute(
            "INSERT INTO canvas_rounds (id, session_id, round_index, prompt, params_json, model_ref, created_at, status)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, 'pending')",
            params![id, session_id, round_index, prompt, params_json, model_ref, now],
        )?;
        // 设置当前 round
        conn.execute(
            "UPDATE studio_sessions SET current_round_id = ?1, updated_at = ?2 WHERE id = ?3",
            params![id, now, session_id],
        )?;
        Ok(CanvasRound {
            id,
            session_id: session_id.to_string(),
            round_index,
            prompt: prompt.to_string(),
            params_json: params_json.to_string(),
            model_ref: model_ref.to_string(),
            created_at: now,
            status: "pending".to_string(),
        })
    }

    pub async fn center_set_active_round(&self, workspace_id: &str, round_id: &str) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        let now = chrono::Utc::now().to_rfc3339();
        conn.execute(
            "UPDATE studio_sessions SET current_round_id = ?1, updated_at = ?2 WHERE id = ?3",
            params![round_id, now, workspace_id],
        )?;
        Ok(())
    }

    pub async fn center_update_round_status(&self, round_id: &str, status: &str) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        conn.execute(
            "UPDATE canvas_rounds SET status = ?1 WHERE id = ?2",
            params![status, round_id],
        )?;
        Ok(())
    }

    pub async fn center_add_round_asset(&self, round_id: &str, asset_id: &str, sequence: i64) -> SqlResult<String> {
        let conn = self.conn.lock().await;
        let id = uuid::Uuid::new_v4().to_string();
        conn.execute(
            "INSERT INTO canvas_round_assets (id, round_id, asset_id, sequence, is_selected)
             VALUES (?1, ?2, ?3, ?4, 0)",
            params![id, round_id, asset_id, sequence],
        )?;
        Ok(id)
    }

    pub async fn center_select_assets(&self, round_id: &str, asset_ids: &[String], selected: bool) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        let val = if selected { 1i64 } else { 0 };
        for aid in asset_ids {
            conn.execute(
                "UPDATE canvas_round_assets SET is_selected = ?1 WHERE round_id = ?2 AND asset_id = ?3",
                params![val, round_id, aid],
            )?;
        }
        Ok(())
    }

    pub async fn center_delete_assets(&self, asset_ids: &[String]) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        for aid in asset_ids {
            conn.execute("DELETE FROM canvas_round_assets WHERE asset_id = ?1", params![aid])?;
            conn.execute("DELETE FROM studio_assets WHERE asset_id = ?1", params![aid])?;
        }
        Ok(())
    }

    pub async fn center_get_round_assets(&self, round_id: &str) -> SqlResult<Vec<CenterAssetDetail>> {
        let conn = self.conn.lock().await;
        let mut stmt = conn.prepare(
            "SELECT cra.id, cra.round_id, cra.asset_id, cra.sequence, cra.is_selected,
                    sa.file_path, sa.preview_path, sa.kind, sa.width, sa.height, sa.duration_ms, sa.created_at
             FROM canvas_round_assets cra
             JOIN studio_assets sa ON sa.asset_id = cra.asset_id
             WHERE cra.round_id = ?1 ORDER BY cra.sequence"
        )?;
        let rows = stmt.query_map(params![round_id], |row| {
            Ok(CenterAssetDetail {
                id: row.get(0)?, round_id: row.get(1)?, asset_id: row.get(2)?,
                sequence: row.get(3)?, is_selected: row.get::<_, i64>(4)? != 0,
                file_path: row.get(5)?, preview_path: row.get(6)?, kind: row.get(7)?,
                width: row.get(8)?, height: row.get(9)?, duration_ms: row.get(10)?,
                created_at: row.get(11)?,
            })
        })?;
        rows.collect()
    }

    pub async fn center_get_next_round_index(&self, session_id: &str) -> SqlResult<i64> {
        let conn = self.conn.lock().await;
        let idx: i64 = conn.query_row(
            "SELECT COALESCE(MAX(round_index), 0) + 1 FROM canvas_rounds WHERE session_id = ?1",
            params![session_id],
            |row| row.get(0),
        )?;
        Ok(idx)
    }

    pub async fn center_update_last_accessed(&self, id: &str) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        let now = chrono::Utc::now().to_rfc3339();
        conn.execute(
            "UPDATE studio_sessions SET last_accessed_at = ?1 WHERE id = ?2",
            params![now, id],
        )?;
        Ok(())
    }

    pub async fn center_get_asset_path(&self, asset_id: &str) -> SqlResult<Option<String>> {
        let conn = self.conn.lock().await;
        let mut stmt = conn.prepare("SELECT file_path FROM studio_assets WHERE asset_id = ?1")?;
        let mut rows = stmt.query(params![asset_id])?;
        if let Some(row) = rows.next()? {
            Ok(Some(row.get(0)?))
        } else {
            Ok(None)
        }
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  听析中心（AudioLab）CRUD
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    pub async fn audiolab_insert_file(&self, f: &AudioFile) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        conn.execute(
            "INSERT INTO audio_files (id, display_name, source_path, mp3_path, sample_rate, channels, duration_ms, file_size_bytes, sha256, imported_at, last_opened_at, is_legacy_import, legacy_source_path, import_batch_id)
             VALUES (?1,?2,?3,?4,?5,?6,?7,?8,?9,?10,?11,?12,?13,?14)",
            params![f.id, f.display_name, f.source_path, f.mp3_path, f.sample_rate, f.channels, f.duration_ms, f.file_size_bytes, f.sha256, f.imported_at, f.last_opened_at, f.is_legacy_import as i64, f.legacy_source_path, f.import_batch_id],
        )?;
        Ok(())
    }

    pub async fn audiolab_list_files(&self, limit: i64, offset: i64, search: Option<&str>, sort: Option<&str>) -> SqlResult<Vec<AudioFile>> {
        let conn = self.conn.lock().await;
        let order = match sort {
            Some("duration") => "f.duration_ms DESC",
            Some("name") => "f.display_name ASC",
            _ => "f.imported_at DESC",
        };
        // LEFT JOIN studio_sessions 通过 source_asset_id 获取对应 session_id
        let cols = "f.id,f.display_name,f.source_path,f.mp3_path,f.sample_rate,f.channels,f.duration_ms,f.file_size_bytes,f.sha256,f.imported_at,f.last_opened_at,f.is_legacy_import,f.legacy_source_path,f.import_batch_id,s.id";
        let sql = if search.is_some() {
            format!("SELECT {} FROM audio_files f LEFT JOIN studio_sessions s ON s.source_asset_id=f.id AND s.session_type='audio' WHERE f.display_name LIKE ?1 ORDER BY {} LIMIT ?2 OFFSET ?3", cols, order)
        } else {
            format!("SELECT {} FROM audio_files f LEFT JOIN studio_sessions s ON s.source_asset_id=f.id AND s.session_type='audio' ORDER BY {} LIMIT ?1 OFFSET ?2", cols, order)
        };
        let mut stmt = conn.prepare(&sql)?;
        let rows = if let Some(q) = search {
            let pattern = format!("%{}%", q);
            let rows = stmt.query_map(params![pattern, limit, offset], |row| Self::map_audio_file(row))?;
            rows.collect::<SqlResult<Vec<_>>>()?
        } else {
            let rows = stmt.query_map(params![limit, offset], |row| Self::map_audio_file(row))?;
            rows.collect::<SqlResult<Vec<_>>>()?
        };
        Ok(rows)
    }

    fn map_audio_file(row: &rusqlite::Row) -> rusqlite::Result<AudioFile> {
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

    pub async fn audiolab_get_file(&self, file_id: &str) -> SqlResult<Option<AudioFile>> {
        let conn = self.conn.lock().await;
        let mut stmt = conn.prepare("SELECT f.id,f.display_name,f.source_path,f.mp3_path,f.sample_rate,f.channels,f.duration_ms,f.file_size_bytes,f.sha256,f.imported_at,f.last_opened_at,f.is_legacy_import,f.legacy_source_path,f.import_batch_id,s.id FROM audio_files f LEFT JOIN studio_sessions s ON s.source_asset_id=f.id AND s.session_type='audio' WHERE f.id=?1")?;
        let mut rows = stmt.query(params![file_id])?;
        if let Some(row) = rows.next()? {
            Ok(Some(Self::map_audio_file(row)?))
        } else {
            Ok(None)
        }
    }

    pub async fn audiolab_remove_file(&self, file_id: &str) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        conn.execute("DELETE FROM audio_files WHERE id=?1", params![file_id])?;
        Ok(())
    }

    pub async fn audiolab_update_last_opened(&self, file_id: &str) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        let now = chrono::Utc::now().to_rfc3339();
        conn.execute("UPDATE audio_files SET last_opened_at=?1 WHERE id=?2", params![now, file_id])?;
        Ok(())
    }

    // ── 转录 ──

    pub async fn audiolab_insert_transcript(&self, t: &AudioTranscript) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        conn.execute(
            "INSERT INTO audio_transcripts (id, session_id, audio_file_id, language, raw_json, parser_kind, created_at)
             VALUES (?1,?2,?3,?4,?5,?6,?7)",
            params![t.id, t.session_id, t.audio_file_id, t.language, t.raw_json, t.parser_kind, t.created_at],
        )?;
        Ok(())
    }

    pub async fn audiolab_get_transcript(&self, session_id: &str) -> SqlResult<Option<AudioTranscript>> {
        let conn = self.conn.lock().await;
        let mut stmt = conn.prepare("SELECT id,session_id,audio_file_id,language,raw_json,parser_kind,created_at FROM audio_transcripts WHERE session_id=?1 ORDER BY created_at DESC LIMIT 1")?;
        let mut rows = stmt.query(params![session_id])?;
        if let Some(row) = rows.next()? {
            Ok(Some(AudioTranscript {
                id: row.get(0)?,
                session_id: row.get(1)?,
                audio_file_id: row.get(2)?,
                language: row.get(3)?,
                raw_json: row.get(4)?,
                parser_kind: row.get(5)?,
                created_at: row.get(6)?,
            }))
        } else {
            Ok(None)
        }
    }

    // ── 段落 ──

    pub async fn audiolab_insert_segments(&self, segments: &[AudioSegment]) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        let mut stmt = conn.prepare(
            "INSERT INTO audio_segments (id, transcript_id, sequence, speaker, speaker_index, start_ms, end_ms, text, confidence)
             VALUES (?1,?2,?3,?4,?5,?6,?7,?8,?9)"
        )?;
        for s in segments {
            stmt.execute(params![s.id, s.transcript_id, s.sequence, s.speaker, s.speaker_index, s.start_ms, s.end_ms, s.text, s.confidence])?;
        }
        Ok(())
    }

    pub async fn audiolab_get_segments(&self, transcript_id: &str) -> SqlResult<Vec<AudioSegment>> {
        let conn = self.conn.lock().await;
        let mut stmt = conn.prepare("SELECT id,transcript_id,sequence,speaker,speaker_index,start_ms,end_ms,text,confidence FROM audio_segments WHERE transcript_id=?1 ORDER BY sequence ASC")?;
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
        })?;
        rows.collect()
    }

    // ── 阶段产出 ──

    pub async fn audiolab_upsert_stage_output(&self, o: &AudioStageOutput) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        conn.execute(
            "INSERT INTO audio_stage_outputs (id, session_id, stage_key, content_markdown, status, error_message, model_ref, generated_at, custom_stage_key, custom_is_mindmap)
             VALUES (?1,?2,?3,?4,?5,?6,?7,?8,?9,?10)
             ON CONFLICT(session_id, stage_key) DO UPDATE SET content_markdown=excluded.content_markdown, status=excluded.status, error_message=excluded.error_message, model_ref=excluded.model_ref, generated_at=excluded.generated_at",
            params![o.id, o.session_id, o.stage_key, o.content_markdown, o.status, o.error_message, o.model_ref, o.generated_at, o.custom_stage_key, o.custom_is_mindmap.map(|b| b as i64)],
        )?;
        Ok(())
    }

    pub async fn audiolab_get_stage_outputs(&self, session_id: &str) -> SqlResult<Vec<AudioStageOutput>> {
        let conn = self.conn.lock().await;
        let mut stmt = conn.prepare("SELECT id,session_id,stage_key,content_markdown,status,error_message,model_ref,generated_at,custom_stage_key,custom_is_mindmap FROM audio_stage_outputs WHERE session_id=?1")?;
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
        })?;
        rows.collect()
    }

    // ── 研究 topic ──

    pub async fn audiolab_insert_research_topic(&self, t: &AudioResearchTopic) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        conn.execute(
            "INSERT INTO audio_research_topics (id, session_id, title, description, status, report_markdown, created_at) VALUES (?1,?2,?3,?4,?5,?6,?7)",
            params![t.id, t.session_id, t.title, t.description, t.status, t.report_markdown, t.created_at],
        )?;
        Ok(())
    }

    pub async fn audiolab_get_research_topics(&self, session_id: &str) -> SqlResult<Vec<AudioResearchTopic>> {
        let conn = self.conn.lock().await;
        let mut stmt = conn.prepare("SELECT id,session_id,title,description,status,report_markdown,created_at FROM audio_research_topics WHERE session_id=?1 ORDER BY created_at ASC")?;
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
        })?;
        rows.collect()
    }

    pub async fn audiolab_delete_research_topic(&self, topic_id: &str) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        conn.execute("DELETE FROM audio_research_topics WHERE id=?1", params![topic_id])?;
        Ok(())
    }

    // ── 自动标签 ──

    pub async fn audiolab_get_auto_tags(&self, session_id: &str) -> SqlResult<Vec<AudioAutoTag>> {
        let conn = self.conn.lock().await;
        let mut stmt = conn.prepare("SELECT id,session_id,tag,source,created_at FROM audio_auto_tags WHERE session_id=?1 ORDER BY created_at ASC")?;
        let rows = stmt.query_map(params![session_id], |row| {
            Ok(AudioAutoTag {
                id: row.get(0)?,
                session_id: row.get(1)?,
                tag: row.get(2)?,
                source: row.get(3)?,
                created_at: row.get(4)?,
            })
        })?;
        rows.collect()
    }

    pub async fn audiolab_insert_auto_tag(&self, t: &AudioAutoTag) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        conn.execute(
            "INSERT INTO audio_auto_tags (id, session_id, tag, source, created_at) VALUES (?1,?2,?3,?4,?5)",
            params![t.id, t.session_id, t.tag, t.source, t.created_at],
        )?;
        Ok(())
    }

    pub async fn audiolab_remove_tag(&self, tag_id: &str) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        conn.execute("DELETE FROM audio_auto_tags WHERE id=?1", params![tag_id])?;
        Ok(())
    }

    // ── 阶段预设 ──

    pub async fn audiolab_list_stage_presets(&self) -> SqlResult<Vec<AudioStagePreset>> {
        let conn = self.conn.lock().await;
        let mut stmt = conn.prepare("SELECT id,stage,display_name,system_prompt,show_in_tab,include_in_batch,is_enabled,display_mode,sort_order FROM audio_stage_presets ORDER BY sort_order ASC")?;
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
        })?;
        rows.collect()
    }

    pub async fn audiolab_upsert_stage_preset(&self, p: &AudioStagePreset) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        conn.execute(
            "INSERT INTO audio_stage_presets (id, stage, display_name, system_prompt, show_in_tab, include_in_batch, is_enabled, display_mode, sort_order)
             VALUES (?1,?2,?3,?4,?5,?6,?7,?8,?9)
             ON CONFLICT(stage) DO UPDATE SET display_name=excluded.display_name, system_prompt=excluded.system_prompt, show_in_tab=excluded.show_in_tab, include_in_batch=excluded.include_in_batch, is_enabled=excluded.is_enabled, display_mode=excluded.display_mode, sort_order=excluded.sort_order",
            params![p.id, p.stage, p.display_name, p.system_prompt, p.show_in_tab as i64, p.include_in_batch as i64, p.is_enabled as i64, p.display_mode, p.sort_order],
        )?;
        Ok(())
    }

    pub async fn audiolab_delete_stage_preset(&self, stage: &str) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        conn.execute("DELETE FROM audio_stage_presets WHERE stage=?1", params![stage])?;
        Ok(())
    }

    // ── 运行中任务查询 ──

    pub async fn audiolab_list_running_tasks(&self, session_id: &str) -> SqlResult<Vec<StudioTask>> {
        let conn = self.conn.lock().await;
        let mut stmt = conn.prepare(
            "SELECT id, session_id, task_type, status, prompt, progress, result_file_path, error_message, has_reference_input, remote_video_id, remote_video_api_mode, remote_generation_id, remote_download_url, generate_seconds, download_seconds, created_at, updated_at
             FROM studio_tasks WHERE session_id=?1 AND status IN ('pending','running')"
        )?;
        let rows = stmt.query_map(params![session_id], |row| {
            Ok(StudioTask {
                id: row.get(0)?,
                session_id: row.get(1)?,
                task_type: row.get(2)?,
                status: row.get(3)?,
                prompt: row.get(4)?,
                progress: row.get(5)?,
                result_file_path: row.get(6)?,
                error_message: row.get(7)?,
                has_reference_input: row.get::<_, i64>(8)? != 0,
                remote_video_id: row.get(9)?,
                remote_video_api_mode: row.get(10)?,
                remote_generation_id: row.get(11)?,
                remote_download_url: row.get(12)?,
                generate_seconds: row.get(13)?,
                download_seconds: row.get(14)?,
                created_at: row.get(15)?,
                updated_at: row.get(16)?,
            })
        })?;
        rows.collect()
    }

    // ── PR-4: 说话人重命名 + 段落编辑 ──

    pub async fn audiolab_rename_speaker(&self, transcript_id: &str, old_index: i64, new_label: &str) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        conn.execute(
            "UPDATE audio_segments SET speaker=?1 WHERE transcript_id=?2 AND speaker_index=?3",
            params![new_label, transcript_id, old_index],
        )?;
        Ok(())
    }

    pub async fn audiolab_update_segment(&self, segment_id: &str, text: Option<&str>, speaker: Option<&str>, start_ms: Option<i64>, end_ms: Option<i64>) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        if let Some(t) = text {
            conn.execute("UPDATE audio_segments SET text=?1 WHERE id=?2", params![t, segment_id])?;
        }
        if let Some(s) = speaker {
            conn.execute("UPDATE audio_segments SET speaker=?1 WHERE id=?2", params![s, segment_id])?;
        }
        if let Some(ms) = start_ms {
            conn.execute("UPDATE audio_segments SET start_ms=?1 WHERE id=?2", params![ms, segment_id])?;
        }
        if let Some(ms) = end_ms {
            conn.execute("UPDATE audio_segments SET end_ms=?1 WHERE id=?2", params![ms, segment_id])?;
        }
        Ok(())
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  PR-1: 任务监控 Storage Methods
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    pub async fn monitor_get_status_counts(&self) -> SqlResult<std::collections::HashMap<String, i64>> {
        let conn = self.conn.lock().await;
        let mut stmt = conn.prepare(
            "SELECT status, COUNT(*) FROM audio_task_queue GROUP BY status"
        )?;
        let mut map = std::collections::HashMap::new();
        let rows = stmt.query_map([], |row| {
            Ok((row.get::<_, String>(0)?, row.get::<_, i64>(1)?))
        })?;
        for r in rows {
            let (status, count) = r?;
            map.insert(status, count);
        }
        Ok(map)
    }

    pub async fn monitor_get_tasks_by_status(
        &self,
        status: &str,
        sort_column: &str,
        sort_ascending: bool,
    ) -> SqlResult<Vec<MonitorTaskRow>> {
        let conn = self.conn.lock().await;
        let order_col = match sort_column {
            "TaskId" => "t.id",
            "AudioFileName" => "COALESCE(a.file_name, ali.file_name, t.audio_item_id)",
            "Stage" => "t.stage",
            "SubmittedAt" => "t.submitted_at",
            "Status" => "t.status",
            _ => "t.submitted_at",
        };
        let order_dir = if sort_ascending { "ASC" } else { "DESC" };
        // PR-4.7: 支持逗号分隔的多状态过滤（如 "Failed,Timeout,Interrupted"）
        let statuses: Vec<&str> = status.split(',').collect();
        let placeholders = statuses.iter().enumerate()
            .map(|(i, _)| format!("?{}", i + 1))
            .collect::<Vec<_>>().join(",");
        let sql = format!(
            "SELECT t.id, t.audio_item_id, t.stage, t.task_type, t.status, t.priority,
                    t.retry_count, t.progress, t.prompt_text, t.error, t.submitted_at,
                    t.started_at, t.completed_at, t.progress_message,
                    COALESCE(a.file_name, ali.file_name) as audio_file_name
             FROM audio_task_queue t
             LEFT JOIN audio_files a ON a.id = t.audio_item_id
             LEFT JOIN audio_library_items ali ON ali.id = t.audio_item_id
             WHERE t.status IN ({placeholders})
             ORDER BY {order_col} {order_dir}
             LIMIT 500"
        );
        let mut stmt = conn.prepare(&sql)?;
        let params: Vec<Box<dyn rusqlite::types::ToSql>> = statuses.iter()
            .map(|s| Box::new(s.to_string()) as Box<dyn rusqlite::types::ToSql>)
            .collect();
        let param_refs: Vec<&dyn rusqlite::types::ToSql> = params.iter().map(|p| p.as_ref()).collect();
        let rows = stmt.query_map(param_refs.as_slice(), |row| {
            Ok(MonitorTaskRow {
                id: row.get(0)?,
                audio_item_id: row.get(1)?,
                stage: row.get(2)?,
                task_type: row.get(3)?,
                status: row.get(4)?,
                priority: row.get(5)?,
                retry_count: row.get(6)?,
                progress: row.get(7)?,
                prompt_text: row.get(8)?,
                error: row.get(9)?,
                submitted_at: row.get(10)?,
                started_at: row.get(11)?,
                completed_at: row.get(12)?,
                progress_message: row.get(13)?,
                audio_file_name: row.get(14)?,
            })
        })?;
        rows.collect()
    }

    pub async fn monitor_get_all_tasks(&self) -> SqlResult<Vec<MonitorTaskRow>> {
        let conn = self.conn.lock().await;
        let mut stmt = conn.prepare(
            "SELECT t.id, t.audio_item_id, t.stage, t.task_type, t.status, t.priority,
                    t.retry_count, t.progress, t.prompt_text, t.error, t.submitted_at,
                    t.started_at, t.completed_at, t.progress_message,
                    COALESCE(a.file_name, ali.file_name) as audio_file_name
             FROM audio_task_queue t
             LEFT JOIN audio_files a ON a.id = t.audio_item_id
             LEFT JOIN audio_library_items ali ON ali.id = t.audio_item_id
             ORDER BY t.submitted_at DESC
             LIMIT 500"
        )?;
        let rows = stmt.query_map([], |row| {
            Ok(MonitorTaskRow {
                id: row.get(0)?,
                audio_item_id: row.get(1)?,
                stage: row.get(2)?,
                task_type: row.get(3)?,
                status: row.get(4)?,
                priority: row.get(5)?,
                retry_count: row.get(6)?,
                progress: row.get(7)?,
                prompt_text: row.get(8)?,
                error: row.get(9)?,
                submitted_at: row.get(10)?,
                started_at: row.get(11)?,
                completed_at: row.get(12)?,
                progress_message: row.get(13)?,
                audio_file_name: row.get(14)?,
            })
        })?;
        rows.collect()
    }

    pub async fn monitor_get_tasks_by_date_range(
        &self,
        date_from: &str,
        date_to: &str,
    ) -> SqlResult<Vec<MonitorTaskRow>> {
        let conn = self.conn.lock().await;
        let mut stmt = conn.prepare(
            "SELECT t.id, t.audio_item_id, t.stage, t.task_type, t.status, t.priority,
                    t.retry_count, t.progress, t.prompt_text, t.error, t.submitted_at,
                    t.started_at, t.completed_at, t.progress_message,
                    COALESCE(a.file_name, ali.file_name) as audio_file_name
             FROM audio_task_queue t
             LEFT JOIN audio_files a ON a.id = t.audio_item_id
             LEFT JOIN audio_library_items ali ON ali.id = t.audio_item_id
             WHERE t.submitted_at >= ?1 AND t.submitted_at <= ?2
             ORDER BY t.submitted_at DESC
             LIMIT 500"
        )?;
        let rows = stmt.query_map(params![date_from, date_to], |row| {
            Ok(MonitorTaskRow {
                id: row.get(0)?,
                audio_item_id: row.get(1)?,
                stage: row.get(2)?,
                task_type: row.get(3)?,
                status: row.get(4)?,
                priority: row.get(5)?,
                retry_count: row.get(6)?,
                progress: row.get(7)?,
                prompt_text: row.get(8)?,
                error: row.get(9)?,
                submitted_at: row.get(10)?,
                started_at: row.get(11)?,
                completed_at: row.get(12)?,
                progress_message: row.get(13)?,
                audio_file_name: row.get(14)?,
            })
        })?;
        rows.collect()
    }

    pub async fn monitor_get_global_stats(&self) -> SqlResult<crate::commands::monitor::MonitorGlobalStats> {
        let conn = self.conn.lock().await;
        let row = conn.query_row(
            "SELECT
                COUNT(*) as total,
                COALESCE(SUM(CASE WHEN billable = 1 THEN 1 ELSE 0 END), 0) as billable_cnt,
                COALESCE(SUM(CASE WHEN billable = 1 THEN COALESCE(tokens_in, COALESCE(prompt_tokens, 0)) ELSE 0 END), 0) as bill_tok_in,
                COALESCE(SUM(CASE WHEN billable = 1 THEN COALESCE(tokens_out, COALESCE(completion_tokens, 0)) ELSE 0 END), 0) as bill_tok_out
             FROM task_executions",
            [],
            |row| {
                Ok(crate::commands::monitor::MonitorGlobalStats {
                    total_executions: row.get(0)?,
                    billable_executions: row.get(1)?,
                    billable_tokens_in: row.get(2)?,
                    billable_tokens_out: row.get(3)?,
                })
            },
        )?;
        Ok(row)
    }

    pub async fn monitor_get_executions(&self, task_id: &str) -> SqlResult<Vec<MonitorExecutionRow>> {
        let conn = self.conn.lock().await;
        let mut stmt = conn.prepare(
            "SELECT id, task_id, status,
                    COALESCE(billable, 0) as billable,
                    model_name,
                    COALESCE(tokens_in, prompt_tokens) as tok_in,
                    COALESCE(tokens_out, completion_tokens) as tok_out,
                    duration_ms,
                    COALESCE(error_message, error) as err_msg,
                    cancel_reason,
                    started_at,
                    COALESCE(finished_at, completed_at) as fin,
                    debug_prompt,
                    debug_response
             FROM task_executions WHERE task_id = ?1 ORDER BY started_at ASC"
        )?;
        let rows = stmt.query_map(params![task_id], |row| {
            Ok(MonitorExecutionRow {
                id: row.get(0)?,
                task_id: row.get(1)?,
                status: row.get(2)?,
                billable: row.get::<_, i64>(3)? != 0,
                model_name: row.get(4)?,
                tokens_in: row.get(5)?,
                tokens_out: row.get(6)?,
                duration_ms: row.get(7)?,
                error_message: row.get(8)?,
                cancel_reason: row.get(9)?,
                started_at: row.get(10)?,
                finished_at: row.get(11)?,
                debug_prompt: row.get(12)?,
                debug_response: row.get(13)?,
            })
        })?;
        rows.collect()
    }

    pub async fn monitor_get_execution_by_id(&self, execution_id: &str) -> SqlResult<MonitorExecutionRow> {
        let conn = self.conn.lock().await;
        conn.query_row(
            "SELECT id, task_id, status,
                    COALESCE(billable, 0) as billable,
                    model_name,
                    COALESCE(tokens_in, prompt_tokens) as tok_in,
                    COALESCE(tokens_out, completion_tokens) as tok_out,
                    duration_ms,
                    COALESCE(error_message, error) as err_msg,
                    cancel_reason,
                    started_at,
                    COALESCE(finished_at, completed_at) as fin,
                    debug_prompt,
                    debug_response
             FROM task_executions WHERE id = ?1",
            params![execution_id],
            |row| {
                Ok(MonitorExecutionRow {
                    id: row.get(0)?,
                    task_id: row.get(1)?,
                    status: row.get(2)?,
                    billable: row.get::<_, i64>(3)? != 0,
                    model_name: row.get(4)?,
                    tokens_in: row.get(5)?,
                    tokens_out: row.get(6)?,
                    duration_ms: row.get(7)?,
                    error_message: row.get(8)?,
                    cancel_reason: row.get(9)?,
                    started_at: row.get(10)?,
                    finished_at: row.get(11)?,
                    debug_prompt: row.get(12)?,
                    debug_response: row.get(13)?,
                })
            },
        )
    }

    /// S-6: 获取某个任务最近一条执行记录的 debug_prompt 和 debug_response
    pub async fn monitor_get_latest_execution_debug(&self, task_id: &str) -> SqlResult<Option<(String, String)>> {
        let conn = self.conn.lock().await;
        let result = conn.query_row(
            "SELECT COALESCE(debug_prompt, '') as dp, COALESCE(debug_response, '') as dr
             FROM task_executions WHERE task_id = ?1
             ORDER BY started_at DESC LIMIT 1",
            params![task_id],
            |row| Ok((row.get::<_, String>(0)?, row.get::<_, String>(1)?)),
        );
        match result {
            Ok(pair) => Ok(Some(pair)),
            Err(rusqlite::Error::QueryReturnedNoRows) => Ok(None),
            Err(e) => Err(e),
        }
    }

    pub async fn monitor_cancel_task(&self, task_id: &str, reason: &str) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        let now = chrono::Utc::now().to_rfc3339();
        conn.execute(
            "UPDATE audio_task_queue SET status = 'Cancelled', error = ?1, completed_at = ?2, updated_at = ?2
             WHERE id = ?3 AND status IN ('Queued', 'Executing')",
            params![reason, now, task_id],
        )?;
        Ok(())
    }

    pub async fn monitor_insert_execution(
        &self,
        id: &str,
        task_id: &str,
        status: &str,
        billable: bool,
        model_name: Option<&str>,
        tokens_in: Option<i64>,
        tokens_out: Option<i64>,
        duration_ms: Option<i64>,
        cancel_reason: Option<&str>,
        debug_prompt: Option<&str>,
        debug_response: Option<&str>,
        started_at: &str,
        finished_at: Option<&str>,
    ) -> SqlResult<()> {
        let conn = self.conn.lock().await;
        conn.execute(
            "INSERT INTO task_executions (id, task_id, attempt, status, billable, model_name,
             tokens_in, tokens_out, duration_ms, cancel_reason, debug_prompt, debug_response,
             started_at, finished_at, error_message)
             VALUES (?1, ?2, 1, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, NULL)",
            params![
                id, task_id, status, billable as i64, model_name,
                tokens_in, tokens_out, duration_ms, cancel_reason,
                debug_prompt, debug_response, started_at, finished_at,
            ],
        )?;
        Ok(())
    }

    pub async fn monitor_cleanup_completed(&self, days: u32) -> SqlResult<u32> {
        let conn = self.conn.lock().await;
        let cutoff = format!("-{} days", days);
        conn.execute(
            "DELETE FROM task_executions WHERE task_id IN (
                SELECT id FROM audio_task_queue
                WHERE status IN ('Completed','Cancelled','Failed','Timeout')
                AND COALESCE(updated_at, submitted_at) < datetime('now', ?1)
            )",
            [&cutoff],
        )?;
        let deleted = conn.execute(
            "DELETE FROM audio_task_queue
             WHERE status IN ('Completed','Cancelled','Failed','Timeout')
             AND COALESCE(updated_at, submitted_at) < datetime('now', ?1)",
            [&cutoff],
        )?;
        Ok(deleted as u32)
    }

    pub async fn monitor_retry_task(&self, task_id: &str) -> SqlResult<String> {
        let conn = self.conn.lock().await;
        let (audio_item_id, stage, task_type, priority, prompt_text): (String, String, String, i64, Option<String>) =
            conn.query_row(
                "SELECT audio_item_id, stage, task_type, priority, prompt_text FROM audio_task_queue WHERE id = ?1",
                params![task_id],
                |row| Ok((row.get(0)?, row.get(1)?, row.get(2)?, row.get(3)?, row.get(4)?)),
            )?;
        let new_id = uuid::Uuid::new_v4().to_string();
        let now = chrono::Utc::now().to_rfc3339();
        conn.execute(
            "INSERT INTO audio_task_queue (id, audio_item_id, stage, task_type, status, priority, retry_count, max_retries, progress, prompt_text, submitted_at, parent_task_id, updated_at)
             VALUES (?1, ?2, ?3, ?4, 'Queued', ?5, 0, 3, 0.0, ?6, ?7, ?8, ?7)",
            params![new_id, audio_item_id, stage, task_type, priority, prompt_text, now, task_id],
        )?;
        Ok(new_id)
    }

    pub async fn monitor_batch_delete(&self, task_ids: &[String]) -> SqlResult<u32> {
        let conn = self.conn.lock().await;
        let mut count = 0u32;
        for tid in task_ids {
            let deleted = conn.execute(
                "DELETE FROM audio_task_queue WHERE id = ?1 AND status IN ('Completed','Cancelled','Failed','Timeout')",
                params![tid],
            )?;
            if deleted > 0 {
                conn.execute("DELETE FROM task_executions WHERE task_id = ?1", params![tid])?;
                count += 1;
            }
        }
        Ok(count)
    }

    /// PR-4.7: 跨进程恢复 — 标记 Executing 任务为 Interrupted + 写执行记录
    pub async fn monitor_recover_interrupted(&self) -> SqlResult<u32> {
        let conn = self.conn.lock().await;
        let now = chrono::Utc::now().to_rfc3339();
        // 找出所有 Executing 状态的任务（跨进程重启残留）
        let mut stmt = conn.prepare(
            "SELECT id FROM audio_task_queue WHERE status = 'Executing'"
        )?;
        let task_ids: Vec<String> = stmt.query_map([], |row| row.get(0))?
            .filter_map(|r| r.ok())
            .collect();
        drop(stmt);
        if task_ids.is_empty() {
            return Ok(0);
        }
        let count = task_ids.len() as u32;
        for tid in &task_ids {
            // 标记为 Interrupted
            conn.execute(
                "UPDATE audio_task_queue SET status = 'Interrupted', error = 'interrupted: process restart',
                 completed_at = ?1, updated_at = ?1
                 WHERE id = ?2 AND status = 'Executing'",
                params![now, tid],
            )?;
            // 写一条 Interrupted 执行记录
            let exec_id = uuid::Uuid::new_v4().to_string();
            conn.execute(
                "INSERT OR IGNORE INTO task_executions (id, task_id, status, billable, cancel_reason, started_at, finished_at)
                 VALUES (?1, ?2, 'Interrupted', 0, 'process_restart', ?3, ?3)",
                params![exec_id, tid, now],
            )?;
        }
        Ok(count)
    }
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  PR-1: Monitor 内部行模型
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

#[derive(Debug, Clone)]
pub struct MonitorTaskRow {
    pub id: String,
    pub audio_item_id: String,
    pub stage: String,
    pub task_type: String,
    pub status: String,
    pub priority: i64,
    pub retry_count: i64,
    pub progress: f64,
    pub prompt_text: Option<String>,
    pub error: Option<String>,
    pub submitted_at: String,
    pub started_at: Option<String>,
    pub completed_at: Option<String>,
    pub progress_message: Option<String>,
    pub audio_file_name: Option<String>,
}

#[derive(Debug, Clone)]
pub struct MonitorExecutionRow {
    pub id: String,
    pub task_id: String,
    pub status: String,
    pub billable: bool,
    pub model_name: Option<String>,
    pub tokens_in: Option<i64>,
    pub tokens_out: Option<i64>,
    pub duration_ms: Option<i64>,
    pub error_message: Option<String>,
    pub cancel_reason: Option<String>,
    pub started_at: String,
    pub finished_at: Option<String>,
    pub debug_prompt: Option<String>,
    pub debug_response: Option<String>,
}
