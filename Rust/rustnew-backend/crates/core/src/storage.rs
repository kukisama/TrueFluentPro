//! SQLite 持久化（与 C# 版同库同表）。
//!
//! 数据库路径：`%APPDATA%/TrueFluentPro/truefluentpro.db`（WAL 模式）。
//! 当前实时翻译切片只用到 `translation_history` 表，表结构与 C#
//! `Services/Storage/SqliteDbService.cs` 完全一致，时间戳用 RFC3339（兼容
//! C# 的 `DateTime.ToString("o")`），按 `created_at` 文本字典序即可正确排序。

use std::cell::Cell;
use std::path::PathBuf;
use std::time::{SystemTime, UNIX_EPOCH};

use rusqlite::{params, Connection};
use serde::{Deserialize, Serialize};

use crate::config::AppConfig;
use crate::error::Result;

/// 一条翻译历史记录。
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TranslationRecord {
    pub id: String,
    pub source_text: String,
    pub translated_text: String,
    #[serde(default)]
    pub source_language: Option<String>,
    #[serde(default)]
    pub target_language: Option<String>,
    /// RFC3339 时间字符串。
    pub created_at: String,
}

impl TranslationRecord {
    /// 用当前时间创建一条新记录（自动生成 ID 与时间戳）。
    pub fn now(
        source_text: impl Into<String>,
        translated_text: impl Into<String>,
        source_language: Option<String>,
        target_language: Option<String>,
    ) -> Self {
        Self {
            id: new_ulid(),
            source_text: source_text.into(),
            translated_text: translated_text.into(),
            source_language,
            target_language,
            created_at: chrono::Utc::now().to_rfc3339(),
        }
    }
}

/// 数据库文件路径。
pub fn db_path() -> Result<PathBuf> {
    Ok(AppConfig::config_dir()?.join("truefluentpro.db"))
}

/// 打开数据库连接（建目录 + WAL + 建表）。
pub fn open_connection() -> Result<Connection> {
    let path = db_path()?;
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)?;
    }
    let conn = Connection::open(&path)?;
    conn.pragma_update(None, "journal_mode", "WAL")?;
    conn.pragma_update(None, "foreign_keys", "ON")?;
    init_schema(&conn)?;
    Ok(conn)
}

/// 建表（仅本切片需要的表）。
fn init_schema(conn: &Connection) -> Result<()> {
    conn.execute_batch(
        r#"
CREATE TABLE IF NOT EXISTS translation_history (
    id              TEXT PRIMARY KEY,
    source_text     TEXT    NOT NULL DEFAULT '',
    translated_text TEXT    NOT NULL DEFAULT '',
    source_language TEXT,
    target_language TEXT,
    created_at      TEXT    NOT NULL,
    is_deleted      INTEGER NOT NULL DEFAULT 0
);
"#,
    )?;
    Ok(())
}

/// 插入一条翻译记录。
pub fn insert_translation(conn: &Connection, r: &TranslationRecord) -> Result<()> {
    conn.execute(
        "INSERT OR REPLACE INTO translation_history \
         (id, source_text, translated_text, source_language, target_language, created_at, is_deleted) \
         VALUES (?1, ?2, ?3, ?4, ?5, ?6, 0)",
        params![
            r.id,
            r.source_text,
            r.translated_text,
            r.source_language,
            r.target_language,
            r.created_at,
        ],
    )?;
    Ok(())
}

/// 列出最近的翻译记录（按时间倒序，跳过软删除）。
pub fn list_translations(conn: &Connection, limit: i64, offset: i64) -> Result<Vec<TranslationRecord>> {
    let mut stmt = conn.prepare(
        "SELECT id, source_text, translated_text, source_language, target_language, created_at \
         FROM translation_history WHERE is_deleted = 0 \
         ORDER BY created_at DESC LIMIT ?1 OFFSET ?2",
    )?;
    let rows = stmt.query_map(params![limit, offset], |row| {
        Ok(TranslationRecord {
            id: row.get(0)?,
            source_text: row.get(1)?,
            translated_text: row.get(2)?,
            source_language: row.get(3)?,
            target_language: row.get(4)?,
            created_at: row.get(5)?,
        })
    })?;
    let mut out = Vec::new();
    for r in rows {
        out.push(r?);
    }
    Ok(out)
}

/// 统计未删除记录数。
pub fn count_translations(conn: &Connection) -> Result<i64> {
    let n: i64 = conn.query_row(
        "SELECT COUNT(*) FROM translation_history WHERE is_deleted = 0",
        [],
        |row| row.get(0),
    )?;
    Ok(n)
}

/// 软删除单条记录。
pub fn soft_delete_translation(conn: &Connection, id: &str) -> Result<()> {
    conn.execute(
        "UPDATE translation_history SET is_deleted = 1 WHERE id = ?1",
        params![id],
    )?;
    Ok(())
}

/// 软删除全部记录（清空历史）。
pub fn clear_translations(conn: &Connection) -> Result<usize> {
    let n = conn.execute(
        "UPDATE translation_history SET is_deleted = 1 WHERE is_deleted = 0",
        [],
    )?;
    Ok(n)
}

// ───────────────────────── ULID 生成 ─────────────────────────

const CROCKFORD: &[u8; 32] = b"0123456789ABCDEFGHJKMNPQRSTVWXYZ";

thread_local! {
    static RNG_STATE: Cell<u64> = Cell::new(seed());
}

fn seed() -> u64 {
    let nanos = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_nanos() as u64)
        .unwrap_or(0);
    // 混入线程地址增加熵
    let addr = &nanos as *const _ as u64;
    nanos ^ addr.rotate_left(17) ^ 0x9E3779B97F4A7C15
}

fn next_rand() -> u64 {
    RNG_STATE.with(|s| {
        // splitmix64
        let mut z = s.get().wrapping_add(0x9E3779B97F4A7C15);
        s.set(z);
        z = (z ^ (z >> 30)).wrapping_mul(0xBF58476D1CE4E5B9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94D049BB133111EB);
        z ^ (z >> 31)
    })
}

/// 生成一个 26 字符的 ULID（48-bit 毫秒时间 + 80-bit 随机，Crockford Base32）。
///
/// 时间在前，因此字典序即时间序，便于做主键。
pub fn new_ulid() -> String {
    let ms = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_millis() as u64)
        .unwrap_or(0)
        & 0xFFFF_FFFF_FFFF; // 48 bit

    let r1 = next_rand();
    let r2 = next_rand();
    // 128-bit 值：高 48 位时间，后 80 位随机
    let high = (ms << 16) | (r1 >> 48); // 取 r1 高位补足到 64 位段
    let low = (r1 << 16) | (r2 & 0xFFFF); // 拼出剩余 64 位

    let mut bytes = [0u8; 16];
    bytes[..8].copy_from_slice(&high.to_be_bytes());
    bytes[8..].copy_from_slice(&low.to_be_bytes());

    encode_base32(&bytes)
}

/// 将 16 字节编码为 26 字符 Crockford Base32。
fn encode_base32(bytes: &[u8; 16]) -> String {
    // 128 bit → 26 个 5-bit 组（前 2 bit 补 0）
    let mut acc: u128 = 0;
    for b in bytes {
        acc = (acc << 8) | (*b as u128);
    }
    let mut out = [0u8; 26];
    for i in (0..26).rev() {
        let idx = (acc & 0x1F) as usize;
        out[i] = CROCKFORD[idx];
        acc >>= 5;
    }
    String::from_utf8_lossy(&out).into_owned()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ulid_is_26_chars_and_sortable() {
        let a = new_ulid();
        std::thread::sleep(std::time::Duration::from_millis(2));
        let b = new_ulid();
        assert_eq!(a.len(), 26);
        assert_eq!(b.len(), 26);
        assert!(a < b, "{a} 应小于 {b}");
    }

    #[test]
    fn translation_roundtrip() {
        let conn = Connection::open_in_memory().unwrap();
        init_schema(&conn).unwrap();
        let rec = TranslationRecord::now("hello", "你好", Some("en-US".into()), Some("zh-Hans".into()));
        insert_translation(&conn, &rec).unwrap();
        assert_eq!(count_translations(&conn).unwrap(), 1);
        let list = list_translations(&conn, 10, 0).unwrap();
        assert_eq!(list.len(), 1);
        assert_eq!(list[0].translated_text, "你好");
        soft_delete_translation(&conn, &rec.id).unwrap();
        assert_eq!(count_translations(&conn).unwrap(), 0);
    }
}
