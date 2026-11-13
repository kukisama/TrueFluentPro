//! 报表专用只读 SQLite 查询引擎（1:1 复刻 C# `VoiceImeUsageReportService` 全部口径）。
//!
//! 绝不创建缺失数据库文件，绝不执行写操作或表结构迁移；使用 `DEFERRED` 只读快照事务确保多表一致性。

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};

use chrono::{DateTime, Duration as ChronoDuration, FixedOffset, Local, NaiveDate};
use rusqlite::{params, Connection, OpenFlags, TransactionBehavior};
use serde::{Deserialize, Serialize};

use crate::schema::default_usage_db_path;

pub const SESSION_DETAIL_LIMIT: usize = 200;
pub const SESSION_TEXT_PAGE_LIMIT: usize = 100;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum ReportAvailability {
    Ready,
    DatabaseMissing,
    SchemaMissing,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum SessionTextAvailability {
    Ready,
    DatabaseMissing,
    SchemaMissing,
    SessionMissing,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ReportDay {
    pub date: String,
    pub short_date: String,
    pub characters: i64,
    pub utterances: i64,
    pub recording_milliseconds: i64,
    pub sessions: i64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ReportHour {
    pub hour: u32,
    pub characters: i64,
    pub utterances: i64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ReportBucket {
    pub key: String,
    pub label: String,
    pub count: i64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ActiveRun {
    pub start_date: String,
    pub end_date: String,
    pub days: i64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PeriodComparison {
    pub start_date: String,
    pub end_date: String,
    pub characters: i64,
    pub recording_milliseconds: i64,
    pub sessions: i64,
    pub characters_change_label: String,
    pub recording_change_label: String,
    pub sessions_change_label: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ReportSession {
    pub session_id: String,
    pub created_at_display: String,
    pub created_at_full: String,
    pub created_local_date: String,
    pub characters: i64,
    pub utterances: i64,
    pub recording_milliseconds: i64,
    pub duration_belongs_to_range: bool,
    pub state: String,
    pub stop_reason: Option<String>,
    pub state_label: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct UsageReport {
    pub start_date: String,
    pub end_date: String,
    pub captured_at_time: String,
    pub availability: ReportAvailability,
    pub characters: i64,
    pub utterances: i64,
    pub recording_milliseconds: i64,
    pub session_count: i64,
    pub matching_sessions: i64,
    pub active_days: usize,
    pub longest_active_streak: i64,
    pub daily_average_characters: f64,
    pub active_runs: Vec<ActiveRun>,
    pub days: Vec<ReportDay>,
    pub hours: Vec<ReportHour>,
    pub sentence_lengths: Vec<ReportBucket>,
    pub outcomes: Vec<ReportBucket>,
    pub previous_period: Option<PeriodComparison>,
    pub sessions: Vec<ReportSession>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SessionTextEntry {
    pub sequence: i64,
    pub finalized_at_full: String,
    pub local_date: String,
    pub characters: i64,
    pub original_text: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SessionTextPage {
    pub session_id: String,
    pub availability: SessionTextAvailability,
    pub entries: Vec<SessionTextEntry>,
    pub next_sequence: i64,
    pub has_more: bool,
}

#[derive(Debug, Clone)]
pub struct UsageReportService {
    db_path: PathBuf,
}

impl UsageReportService {
    pub fn new(db_path: impl Into<PathBuf>) -> Self {
        Self {
            db_path: db_path.into(),
        }
    }

    pub fn default_service() -> Self {
        Self::new(default_usage_db_path())
    }

    pub fn path(&self) -> &Path {
        &self.db_path
    }

    /// 读取 1 / 7 / 30 天用量报表快照。
    pub fn read_report(&self, days: u32) -> Result<UsageReport, String> {
        if !matches!(days, 1 | 7 | 30) {
            return Err("不支持的统计天数范围，仅支持 1 / 7 / 30 天。".into());
        }
        let now_local = Local::now();
        let end = now_local.date_naive();
        let start = end - ChronoDuration::days((days - 1) as i64);
        self.read_report_for_range(start, end, now_local.format("%H:%M:%S").to_string())
    }

    pub fn read_report_for_range(
        &self,
        start: NaiveDate,
        end: NaiveDate,
        captured_at_time: String,
    ) -> Result<UsageReport, String> {
        let start_str = start.format("%Y-%m-%d").to_string();
        let end_str = end.format("%Y-%m-%d").to_string();

        if !self.db_path.exists() {
            return Ok(empty_report(&start_str, &end_str, &captured_at_time, ReportAvailability::DatabaseMissing));
        }

        let mut conn = open_readonly(&self.db_path)?;
        let tx = conn
            .transaction_with_behavior(TransactionBehavior::Deferred)
            .map_err(|e| format!("开启只读报表事务失败：{e}"))?;

        if !has_usage_schema(&tx)? {
            return Ok(empty_report(&start_str, &end_str, &captured_at_time, ReportAvailability::SchemaMissing));
        }

        #[derive(Default)]
        struct DayTotals {
            chars: i64,
            utterances: i64,
            rec_ms: i64,
            sessions: i64,
        }

        let mut daily: BTreeMap<NaiveDate, DayTotals> = BTreeMap::new();
        let mut cursor = start;
        while cursor <= end {
            daily.insert(cursor, DayTotals::default());
            cursor += ChronoDuration::days(1);
        }

        {
            let mut stmt = tx
                .prepare(
                    "SELECT local_date, SUM(character_count), COUNT(*) \
                     FROM voice_ime_utterances \
                     WHERE local_date >= ?1 AND local_date <= ?2 \
                     GROUP BY local_date;",
                )
                .map_err(map_sql_err)?;
            let rows = stmt
                .query_map(params![&start_str, &end_str], |r| {
                    Ok((r.get::<_, String>(0)?, r.get::<_, i64>(1)?, r.get::<_, i64>(2)?))
                })
                .map_err(map_sql_err)?;
            for row in rows {
                let (d_str, chars, utts) = row.map_err(map_sql_err)?;
                if let Ok(d) = NaiveDate::parse_from_str(&d_str, "%Y-%m-%d") {
                    if let Some(slot) = daily.get_mut(&d) {
                        slot.chars = chars;
                        slot.utterances = utts;
                    }
                }
            }
        }

        {
            let mut stmt = tx
                .prepare(
                    "SELECT created_local_date, SUM(recording_ms), COUNT(*) \
                     FROM voice_ime_sessions \
                     WHERE created_local_date >= ?1 AND created_local_date <= ?2 \
                     GROUP BY created_local_date;",
                )
                .map_err(map_sql_err)?;
            let rows = stmt
                .query_map(params![&start_str, &end_str], |r| {
                    Ok((r.get::<_, String>(0)?, r.get::<_, i64>(1)?, r.get::<_, i64>(2)?))
                })
                .map_err(map_sql_err)?;
            for row in rows {
                let (d_str, ms, sess) = row.map_err(map_sql_err)?;
                if let Ok(d) = NaiveDate::parse_from_str(&d_str, "%Y-%m-%d") {
                    if let Some(slot) = daily.get_mut(&d) {
                        slot.rec_ms = ms;
                        slot.sessions = sess;
                    }
                }
            }
        }

        let mut hours: Vec<ReportHour> = (0..24)
            .map(|h| ReportHour {
                hour: h,
                characters: 0,
                utterances: 0,
            })
            .collect();
        {
            let mut stmt = tx
                .prepare(
                    "SELECT CAST(strftime('%H', substr(finalized_utc,1,19), printf('%+d minutes', utc_offset_minutes)) AS INTEGER), \
                            SUM(character_count), COUNT(*) \
                     FROM voice_ime_utterances \
                     WHERE local_date >= ?1 AND local_date <= ?2 \
                     GROUP BY 1;",
                )
                .map_err(map_sql_err)?;
            let rows = stmt
                .query_map(params![&start_str, &end_str], |r| {
                    Ok((r.get::<_, Option<i32>>(0)?, r.get::<_, i64>(1)?, r.get::<_, i64>(2)?))
                })
                .map_err(map_sql_err)?;
            for row in rows {
                let (h_opt, chars, utts) = row.map_err(map_sql_err)?;
                if let Some(h) = h_opt {
                    if (0..24).contains(&h) {
                        hours[h as usize] = ReportHour {
                            hour: h as u32,
                            characters: chars,
                            utterances: utts,
                        };
                    }
                }
            }
        }

        let mut sentence_lengths = vec![
            ReportBucket { key: "zero".into(), label: "0 字".into(), count: 0 },
            ReportBucket { key: "short".into(), label: "1–10 字".into(), count: 0 },
            ReportBucket { key: "medium".into(), label: "11–30 字".into(), count: 0 },
            ReportBucket { key: "long".into(), label: "31–60 字".into(), count: 0 },
            ReportBucket { key: "extended".into(), label: "61+ 字".into(), count: 0 },
        ];
        {
            let mut stmt = tx
                .prepare(
                    "SELECT CASE WHEN character_count=0 THEN 0 \
                                 WHEN character_count<=10 THEN 1 \
                                 WHEN character_count<=30 THEN 2 \
                                 WHEN character_count<=60 THEN 3 \
                                 ELSE 4 END, COUNT(*) \
                     FROM voice_ime_utterances \
                     WHERE local_date >= ?1 AND local_date <= ?2 \
                     GROUP BY 1;",
                )
                .map_err(map_sql_err)?;
            let rows = stmt
                .query_map(params![&start_str, &end_str], |r| {
                    Ok((r.get::<_, usize>(0)?, r.get::<_, i64>(1)?))
                })
                .map_err(map_sql_err)?;
            for row in rows {
                let (idx, cnt) = row.map_err(map_sql_err)?;
                if idx < sentence_lengths.len() {
                    sentence_lengths[idx].count = cnt;
                }
            }
        }

        let mut outcomes = vec![
            ReportBucket { key: "manual_stop".into(), label: "手动停止".into(), count: 0 },
            ReportBucket { key: "inactivity_timeout".into(), label: "无字超时".into(), count: 0 },
            ReportBucket { key: "other_ime".into(), label: "切换输入法".into(), count: 0 },
            ReportBucket { key: "failed".into(), label: "语音失败".into(), count: 0 },
            ReportBucket { key: "owner_exit".into(), label: "后台退出".into(), count: 0 },
            ReportBucket { key: "unfinished".into(), label: "未完成".into(), count: 0 },
            ReportBucket { key: "interrupted".into(), label: "异常中断".into(), count: 0 },
            ReportBucket { key: "other".into(), label: "其他 / 未知".into(), count: 0 },
        ];
        {
            let mut stmt = tx
                .prepare(
                    "SELECT state, stop_reason, COUNT(*) \
                     FROM voice_ime_sessions \
                     WHERE created_local_date >= ?1 AND created_local_date <= ?2 \
                     GROUP BY state, stop_reason;",
                )
                .map_err(map_sql_err)?;
            let rows = stmt
                .query_map(params![&start_str, &end_str], |r| {
                    Ok((
                        r.get::<_, String>(0)?,
                        r.get::<_, Option<String>>(1)?,
                        r.get::<_, i64>(2)?,
                    ))
                })
                .map_err(map_sql_err)?;
            for row in rows {
                let (state, reason, cnt) = row.map_err(map_sql_err)?;
                let bucket_key = match (state.as_str(), reason.as_deref()) {
                    ("connecting" | "recording", _) => "unfinished",
                    ("interrupted", _) => "interrupted",
                    (
                        "ended",
                        Some(
                            r @ ("manual_stop"
                            | "inactivity_timeout"
                            | "other_ime"
                            | "failed"
                            | "owner_exit"),
                        ),
                    ) => r,
                    _ => "other",
                };
                if let Some(b) = outcomes.iter_mut().find(|b| b.key == bucket_key) {
                    b.count += cnt;
                }
            }
        }

        let span_days = (end - start).num_days() + 1;
        let prev_start = start - ChronoDuration::days(span_days);
        let prev_end = start - ChronoDuration::days(1);
        let prev_start_str = prev_start.format("%Y-%m-%d").to_string();
        let prev_end_str = prev_end.format("%Y-%m-%d").to_string();

        let prev_chars: i64 = tx
            .query_row(
                "SELECT COALESCE(SUM(character_count), 0) FROM voice_ime_utterances \
                 WHERE local_date >= ?1 AND local_date <= ?2;",
                params![&prev_start_str, &prev_end_str],
                |r| r.get(0),
            )
            .map_err(map_sql_err)?;
        let (prev_ms, prev_sessions): (i64, i64) = tx
            .query_row(
                "SELECT COALESCE(SUM(recording_ms), 0), COUNT(*) FROM voice_ime_sessions \
                 WHERE created_local_date >= ?1 AND created_local_date <= ?2;",
                params![&prev_start_str, &prev_end_str],
                |r| Ok((r.get(0)?, r.get(1)?)),
            )
            .map_err(map_sql_err)?;

        let mut sessions = Vec::new();
        let mut matching_sessions: i64 = 0;
        {
            let mut stmt = tx
                .prepare(
                    "WITH finalized AS ( \
                        SELECT session_id, SUM(character_count) AS characters, COUNT(*) AS utterances \
                        FROM voice_ime_utterances \
                        WHERE local_date >= ?1 AND local_date <= ?2 \
                        GROUP BY session_id \
                     ) \
                     SELECT s.session_id, s.created_utc, s.created_local_date, s.created_offset_minutes, \
                            COALESCE(f.characters, 0), COALESCE(f.utterances, 0), s.recording_ms, \
                            s.state, s.stop_reason, COUNT(*) OVER() \
                     FROM voice_ime_sessions s \
                     LEFT JOIN finalized f ON f.session_id = s.session_id \
                     WHERE (s.created_local_date >= ?1 AND s.created_local_date <= ?2) OR f.session_id IS NOT NULL \
                     ORDER BY s.created_utc DESC, s.session_id DESC \
                     LIMIT ?3;",
                )
                .map_err(map_sql_err)?;
            let rows = stmt
                .query_map(params![&start_str, &end_str, SESSION_DETAIL_LIMIT as i64], |r| {
                    Ok((
                        r.get::<_, String>(0)?,
                        r.get::<_, String>(1)?,
                        r.get::<_, String>(2)?,
                        r.get::<_, i32>(3)?,
                        r.get::<_, i64>(4)?,
                        r.get::<_, i64>(5)?,
                        r.get::<_, i64>(6)?,
                        r.get::<_, String>(7)?,
                        r.get::<_, Option<String>>(8)?,
                        r.get::<_, i64>(9)?,
                    ))
                })
                .map_err(map_sql_err)?;

            for row in rows {
                let (sid, c_utc, c_date, offset_min, chars, utts, rec_ms, state, reason, total_cnt) =
                    row.map_err(map_sql_err)?;
                matching_sessions = total_cnt;
                let (short_ts, full_ts) = format_offset_timestamp(&c_utc, offset_min);
                let in_range = c_date.as_str() >= start_str.as_str() && c_date.as_str() <= end_str.as_str();
                let state_label = format_state_label(&state, reason.as_deref());
                sessions.push(ReportSession {
                    session_id: sid,
                    created_at_display: short_ts,
                    created_at_full: full_ts,
                    created_local_date: c_date,
                    characters: chars,
                    utterances: utts,
                    recording_milliseconds: rec_ms,
                    duration_belongs_to_range: in_range,
                    state,
                    stop_reason: reason,
                    state_label,
                });
            }
        }
        tx.commit().map_err(map_sql_err)?;

        let mut days_vec = Vec::with_capacity(daily.len());
        let mut active_runs: Vec<ActiveRun> = Vec::new();
        let mut prev_active_date: Option<NaiveDate> = None;

        for (d, totals) in &daily {
            let d_str = d.format("%Y-%m-%d").to_string();
            let short_d = d.format("%m/%d").to_string();
            if totals.chars > 0 {
                if let Some(prev) = prev_active_date {
                    if *d == prev + ChronoDuration::days(1) {
                        if let Some(last_run) = active_runs.last_mut() {
                            last_run.end_date = short_d.clone();
                            last_run.days += 1;
                        }
                    } else {
                        active_runs.push(ActiveRun {
                            start_date: short_d.clone(),
                            end_date: short_d.clone(),
                            days: 1,
                        });
                    }
                } else {
                    active_runs.push(ActiveRun {
                        start_date: short_d.clone(),
                        end_date: short_d.clone(),
                        days: 1,
                    });
                }
                prev_active_date = Some(*d);
            }
            days_vec.push(ReportDay {
                date: d_str,
                short_date: short_d,
                characters: totals.chars,
                utterances: totals.utterances,
                recording_milliseconds: totals.rec_ms,
                sessions: totals.sessions,
            });
        }

        let total_chars: i64 = days_vec.iter().map(|d| d.characters).sum();
        let total_utts: i64 = days_vec.iter().map(|d| d.utterances).sum();
        let total_ms: i64 = days_vec.iter().map(|d| d.recording_milliseconds).sum();
        let total_sessions: i64 = days_vec.iter().map(|d| d.sessions).sum();
        let active_days = days_vec.iter().filter(|d| d.characters > 0).count();
        let longest_active_streak = active_runs.iter().map(|r| r.days).max().unwrap_or(0);
        let daily_average_characters = if days_vec.is_empty() {
            0.0
        } else {
            total_chars as f64 / days_vec.len() as f64
        };

        Ok(UsageReport {
            start_date: start_str,
            end_date: end_str,
            captured_at_time,
            availability: ReportAvailability::Ready,
            characters: total_chars,
            utterances: total_utts,
            recording_milliseconds: total_ms,
            session_count: total_sessions,
            matching_sessions,
            active_days,
            longest_active_streak,
            daily_average_characters,
            active_runs,
            days: days_vec,
            hours,
            sentence_lengths,
            outcomes,
            previous_period: Some(PeriodComparison {
                start_date: prev_start_str,
                end_date: prev_end_str,
                characters: prev_chars,
                recording_milliseconds: prev_ms,
                sessions: prev_sessions,
                characters_change_label: format_change_label(total_chars, prev_chars),
                recording_change_label: format_change_label(total_ms, prev_ms),
                sessions_change_label: format_change_label(total_sessions, prev_sessions),
            }),
            sessions,
        })
    }

    /// 游标分页读取单个会话已保存的识别原文（最多 100 句/批）。
    pub fn read_session_text(
        &self,
        session_id: &str,
        after_sequence: i64,
        page_size: usize,
    ) -> Result<SessionTextPage, String> {
        let trimmed = session_id.trim();
        if trimmed.is_empty() || trimmed.len() > 128 {
            return Err("会话标识无效。".into());
        }
        let limit = page_size.clamp(1, SESSION_TEXT_PAGE_LIMIT);
        let after = after_sequence.max(0);

        if !self.db_path.exists() {
            return Ok(empty_text_page(trimmed, after, SessionTextAvailability::DatabaseMissing));
        }

        let mut conn = open_readonly(&self.db_path)?;
        let tx = conn
            .transaction_with_behavior(TransactionBehavior::Deferred)
            .map_err(map_sql_err)?;

        if !has_usage_schema(&tx)? {
            return Ok(empty_text_page(trimmed, after, SessionTextAvailability::SchemaMissing));
        }

        let session_exists: Option<i32> = tx
            .query_row(
                "SELECT 1 FROM voice_ime_sessions WHERE session_id=?1;",
                params![trimmed],
                |r| r.get(0),
            )
            .ok();
        if session_exists.is_none() {
            return Ok(empty_text_page(trimmed, after, SessionTextAvailability::SessionMissing));
        }

        let mut entries = Vec::with_capacity(limit);
        {
            let mut stmt = tx
                .prepare(
                    "SELECT sequence_no, finalized_utc, local_date, utc_offset_minutes, character_count, original_text \
                     FROM voice_ime_utterances \
                     WHERE session_id=?1 AND sequence_no>?2 \
                     ORDER BY sequence_no \
                     LIMIT ?3;",
                )
                .map_err(map_sql_err)?;
            let rows = stmt
                .query_map(params![trimmed, after, limit as i64], |r| {
                    Ok((
                        r.get::<_, i64>(0)?,
                        r.get::<_, String>(1)?,
                        r.get::<_, String>(2)?,
                        r.get::<_, i32>(3)?,
                        r.get::<_, i64>(4)?,
                        r.get::<_, Option<String>>(5)?,
                    ))
                })
                .map_err(map_sql_err)?;
            for row in rows {
                let (seq, f_utc, l_date, offset_min, chars, orig) = row.map_err(map_sql_err)?;
                let (_, full_ts) = format_offset_timestamp(&f_utc, offset_min);
                entries.push(SessionTextEntry {
                    sequence: seq,
                    finalized_at_full: full_ts,
                    local_date: l_date,
                    characters: chars,
                    original_text: orig,
                });
            }
        }

        let next_sequence = entries.last().map(|e| e.sequence).unwrap_or(after);
        let has_more: bool = tx
            .query_row(
                "SELECT 1 FROM voice_ime_utterances WHERE session_id=?1 AND sequence_no>?2 LIMIT 1;",
                params![trimmed, next_sequence],
                |_| Ok(true),
            )
            .unwrap_or(false);

        tx.commit().map_err(map_sql_err)?;
        Ok(SessionTextPage {
            session_id: trimmed.to_string(),
            availability: SessionTextAvailability::Ready,
            entries,
            next_sequence,
            has_more,
        })
    }
}

fn open_readonly(path: &Path) -> Result<Connection, String> {
    let conn = Connection::open_with_flags(
        path,
        OpenFlags::SQLITE_OPEN_READ_ONLY | OpenFlags::SQLITE_OPEN_NO_MUTEX,
    )
    .map_err(map_sql_err)?;
    let _ = conn.busy_timeout(std::time::Duration::from_secs(2));
    Ok(conn)
}

fn has_usage_schema(conn: &Connection) -> Result<bool, String> {
    let cnt: i64 = conn
        .query_row(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('voice_ime_sessions','voice_ime_utterances');",
            [],
            |r| r.get(0),
        )
        .map_err(map_sql_err)?;
    Ok(cnt == 2)
}

fn map_sql_err(err: rusqlite::Error) -> String {
    match err {
        rusqlite::Error::SqliteFailure(e, _) if matches!(e.extended_code & 0xFF, 5 | 6) => {
            "统计数据库暂时繁忙，请稍后点击刷新重试。语音输入不受影响。".into()
        }
        _ => "暂时无法读取本地统计数据库，请稍后刷新重试。".into(),
    }
}

fn format_offset_timestamp(utc_rfc3339: &str, offset_minutes: i32) -> (String, String) {
    if let Ok(dt) = DateTime::parse_from_rfc3339(utc_rfc3339) {
        if let Some(offset) = FixedOffset::east_opt(offset_minutes * 60) {
            let local_dt = dt.with_timezone(&offset);
            return (
                local_dt.format("%m-%d %H:%M:%S").to_string(),
                local_dt.format("%Y-%m-%d %H:%M:%S %:z").to_string(),
            );
        }
    }
    (utc_rfc3339.to_string(), utc_rfc3339.to_string())
}

fn format_state_label(state: &str, stop_reason: Option<&str>) -> String {
    match state {
        "connecting" => "连接中 · 检查点".into(),
        "recording" => "录音中 · 检查点".into(),
        "interrupted" => "异常中断 · 检查点".into(),
        "ended" => match stop_reason {
            Some("manual_stop") => "手动停止".into(),
            Some("inactivity_timeout") => "无字超时".into(),
            Some("other_ime") => "切换输入法".into(),
            Some("failed") => "语音失败".into(),
            Some("owner_exit") => "后台退出".into(),
            _ => "已结束".into(),
        },
        _ => "其他状态".into(),
    }
}

pub fn format_change_label(current: i64, previous: i64) -> String {
    if previous == 0 {
        if current == 0 {
            "持平 · 均为 0".into()
        } else {
            "从 0 开始".into()
        }
    } else if current == previous {
        "较上期持平".into()
    } else {
        let pct = ((current - previous) as f64) * 100.0 / (previous as f64);
        if pct > 0.0 {
            format!("较上期 +{pct:.1}%")
        } else {
            format!("较上期 {pct:.1}%")
        }
    }
}

fn empty_report(
    start: &str,
    end: &str,
    captured_at: &str,
    availability: ReportAvailability,
) -> UsageReport {
    UsageReport {
        start_date: start.into(),
        end_date: end.into(),
        captured_at_time: captured_at.into(),
        availability,
        characters: 0,
        utterances: 0,
        recording_milliseconds: 0,
        session_count: 0,
        matching_sessions: 0,
        active_days: 0,
        longest_active_streak: 0,
        daily_average_characters: 0.0,
        active_runs: Vec::new(),
        days: Vec::new(),
        hours: Vec::new(),
        sentence_lengths: Vec::new(),
        outcomes: Vec::new(),
        previous_period: None,
        sessions: Vec::new(),
    }
}

fn empty_text_page(
    session_id: &str,
    after: i64,
    availability: SessionTextAvailability,
) -> SessionTextPage {
    SessionTextPage {
        session_id: session_id.into(),
        availability,
        entries: Vec::new(),
        next_sequence: after,
        has_more: false,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::writer::{StopReason, UsageWriterService};

    #[tokio::test]
    async fn writer_and_readonly_report_roundtrip_with_privacy_gate() {
        let dir = std::env::temp_dir().join(format!("voice_ime_db_{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&dir).unwrap();
        let db_path = dir.join("usage.db");

        // 缺库时报表不会主动建文件
        let report_svc = UsageReportService::new(&db_path);
        let missing = report_svc.read_report(7).unwrap();
        assert_eq!(missing.availability, ReportAvailability::DatabaseMissing);
        assert!(!db_path.exists());

        // 启动 writer：默认不保存原文，但统计字数/句数/时长
        let writer = UsageWriterService::spawn(db_path.clone(), "session-1");
        assert!(writer.flush().await);

        let sid1 = writer.begin_session();
        writer.recording_started(sid1);
        writer.recognized(sid1, "第一句不存原文！", true);
        writer.end_session(sid1, StopReason::ManualStop, true);

        // 开启原文保存后记录第二轮
        assert!(writer.set_history_enabled(true).await);
        let sid2 = writer.begin_session();
        writer.recording_started(sid2);
        writer.recognized(sid2, "第二句已授权保存。", true);
        writer.recognized(sid2, "未完末句", false);
        writer.end_session(sid2, StopReason::ManualStop, true);
        assert!(writer.flush().await);

        let report = report_svc.read_report(7).unwrap();
        assert_eq!(report.availability, ReportAvailability::Ready);
        assert_eq!(report.session_count, 2);
        assert_eq!(report.utterances, 3);
        assert_eq!(report.characters, 7 + 8 + 4);

        let p1 = report_svc
            .read_session_text(&sid1.hyphenated().to_string(), 0, 100)
            .unwrap();
        assert_eq!(p1.entries.len(), 1);
        assert_eq!(p1.entries[0].original_text, None);

        let p2 = report_svc
            .read_session_text(&sid2.hyphenated().to_string(), 0, 100)
            .unwrap();
        assert_eq!(p2.entries.len(), 2);
        assert_eq!(p2.entries[0].original_text.as_deref(), Some("第二句已授权保存。"));
        assert_eq!(p2.entries[1].original_text.as_deref(), Some("未完末句"));

        writer.shutdown();
        let _ = std::fs::remove_dir_all(&dir);
    }
}
