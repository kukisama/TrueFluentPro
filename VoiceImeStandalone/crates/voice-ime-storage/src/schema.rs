//! `usage.db` 表结构初始化与进程重启后的未结会话恢复。

use std::fs;
use std::path::{Path, PathBuf};

use rusqlite::{params, Connection};

pub const HISTORY_META_KEY: &str = "voice_ime.history_enabled";

pub fn default_usage_db_path() -> PathBuf {
    dirs::data_local_dir()
        .or_else(dirs::config_dir)
        .unwrap_or_else(|| PathBuf::from("."))
        .join("TrueFluentVoiceIme")
        .join("usage.db")
}

/// 打开可写连接并初始化独立 `usage.db` 表结构与索引。
pub fn open_and_initialize_writer_db(path: &Path, owner_scope: &str) -> rusqlite::Result<(Connection, bool)> {
    if let Some(parent) = path.parent() {
        let _ = fs::create_dir_all(parent);
    }
    let mut conn = Connection::open(path)?;
    conn.pragma_update(None, "journal_mode", "WAL")?;
    conn.pragma_update(None, "busy_timeout", 3000)?;

    conn.execute_batch(
        r#"
        CREATE TABLE IF NOT EXISTS _meta (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS voice_ime_sessions (
            session_id               TEXT PRIMARY KEY,
            owner_scope              TEXT NOT NULL,
            created_utc              TEXT NOT NULL,
            created_local_date       TEXT NOT NULL,
            created_offset_minutes   INTEGER NOT NULL,
            recording_started_utc    TEXT,
            checkpoint_utc           TEXT NOT NULL,
            checkpoint_local_date    TEXT NOT NULL,
            checkpoint_offset_minutes INTEGER NOT NULL,
            ended_utc                TEXT,
            recording_ms             INTEGER NOT NULL DEFAULT 0,
            state                    TEXT NOT NULL,
            stop_reason              TEXT,
            revision                 INTEGER NOT NULL DEFAULT 1
        );

        CREATE INDEX IF NOT EXISTS idx_voice_ime_sessions_created_date
            ON voice_ime_sessions(created_local_date);

        CREATE TABLE IF NOT EXISTS voice_ime_utterances (
            session_id         TEXT NOT NULL,
            sequence_no        INTEGER NOT NULL,
            finalized_utc      TEXT NOT NULL,
            local_date         TEXT NOT NULL,
            utc_offset_minutes INTEGER NOT NULL,
            character_count    INTEGER NOT NULL,
            original_text      TEXT,
            PRIMARY KEY (session_id, sequence_no)
        );

        CREATE INDEX IF NOT EXISTS idx_voice_ime_utterances_local_date
            ON voice_ime_utterances(local_date);
        "#,
    )?;

    let tx = conn.transaction()?;
    tx.execute(
        "UPDATE voice_ime_sessions SET state='interrupted', stop_reason='interrupted' \
         WHERE owner_scope=?1 AND state IN ('connecting','recording');",
        params![owner_scope],
    )?;

    let raw_meta: Option<String> = tx
        .query_row(
            "SELECT value FROM _meta WHERE key=?1;",
            params![HISTORY_META_KEY],
            |row| row.get(0),
        )
        .ok();

    let enabled = match raw_meta.as_deref() {
        None | Some("0") => false,
        Some("1") => true,
        _ => return Err(rusqlite::Error::InvalidQuery),
    };
    tx.commit()?;
    Ok((conn, enabled))
}
