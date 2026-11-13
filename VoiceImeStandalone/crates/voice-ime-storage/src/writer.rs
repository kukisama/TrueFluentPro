//! 异步有界队列写入器与原文保存隐私门控（1:1 对齐 C# `VoiceImeUsageService`）。

use std::path::PathBuf;
use std::sync::{Arc, Mutex};
use std::time::Instant;

use chrono::{DateTime, Local, SecondsFormat, Utc};
use rusqlite::{params, Connection};
use serde::{Deserialize, Serialize};
use tokio::sync::{mpsc, oneshot, watch};
use uuid::Uuid;

use crate::char_count::count_visible_characters;
use crate::schema::{open_and_initialize_writer_db, HISTORY_META_KEY};

const MAX_TEXT_CHARS_BOUND: usize = 65536;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct UsageStatus {
    pub history_enabled: bool,
    pub available: bool,
    pub updating: bool,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum StopReason {
    ManualStop,
    InactivityTimeout,
    OtherIme,
    Failed,
    OwnerExit,
}

impl StopReason {
    pub fn as_str(self) -> &'static str {
        match self {
            StopReason::ManualStop => "manual_stop",
            StopReason::InactivityTimeout => "inactivity_timeout",
            StopReason::OtherIme => "other_ime",
            StopReason::Failed => "failed",
            StopReason::OwnerExit => "owner_exit",
        }
    }
}

#[derive(Debug, Clone)]
struct UsageTime {
    utc: String,
    local_date: String,
    offset_minutes: i32,
}

impl UsageTime {
    fn now() -> Self {
        let utc_now: DateTime<Utc> = Utc::now();
        let local_now: DateTime<Local> = utc_now.with_timezone(&Local);
        Self {
            utc: utc_now.to_rfc3339_opts(SecondsFormat::Nanos, true),
            local_date: local_now.format("%Y-%m-%d").to_string(),
            offset_minutes: local_now.offset().local_minus_utc() / 60,
        }
    }
}

#[derive(Debug, Clone)]
struct SessionSnapshot {
    id: Uuid,
    scope: String,
    created: UsageTime,
    started: Option<UsageTime>,
    observed: UsageTime,
    recording_ms: i64,
    state: &'static str,
    reason: Option<&'static str>,
    revision: i64,
}

#[derive(Debug, Clone)]
struct UtteranceSnapshot {
    session_id: Uuid,
    sequence: i64,
    time: UsageTime,
    characters: i64,
    original: Option<String>,
}

enum DbWork {
    WriteSession(SessionSnapshot, Option<oneshot::Sender<bool>>),
    WriteUtterance(UtteranceSnapshot, Option<oneshot::Sender<bool>>),
    SetHistory {
        enabled: bool,
        request_id: u64,
        done: oneshot::Sender<bool>,
    },
    Barrier(oneshot::Sender<bool>),
}

struct ActiveRound {
    id: Uuid,
    created: UsageTime,
    started: Option<UsageTime>,
    started_instant: Option<Instant>,
    sequence: i64,
    revision: i64,
    sentence_privacy_version: Option<u64>,
    pending_characters: i64,
    has_pending: bool,
    pending_original: Option<String>,
}

impl ActiveRound {
    fn clear_pending(&mut self) {
        self.pending_characters = 0;
        self.has_pending = false;
        self.pending_original = None;
        self.sentence_privacy_version = None;
    }
}

struct SharedState {
    enabled: bool,
    available: bool,
    updating: bool,
    faulted: bool,
    disposed: bool,
    privacy_version: u64,
    setting_request: u64,
    round: Option<ActiveRound>,
}

pub struct UsageWriterService {
    owner_scope: String,
    state: Arc<Mutex<SharedState>>,
    tx: mpsc::Sender<DbWork>,
    status_tx: watch::Sender<UsageStatus>,
    status_rx: watch::Receiver<UsageStatus>,
}

impl UsageWriterService {
    pub fn spawn(db_path: PathBuf, owner_scope: impl Into<String>) -> Self {
        let owner_scope = owner_scope.into();
        let state = Arc::new(Mutex::new(SharedState {
            enabled: false,
            available: false,
            updating: false,
            faulted: false,
            disposed: false,
            privacy_version: 0,
            setting_request: 0,
            round: None,
        }));
        let initial_status = UsageStatus {
            history_enabled: false,
            available: false,
            updating: false,
        };
        let (status_tx, status_rx) = watch::channel(initial_status);
        let (tx, mut rx) = mpsc::channel::<DbWork>(256);

        let worker_state = Arc::clone(&state);
        let worker_status_tx = status_tx.clone();
        let worker_scope = owner_scope.clone();

        std::thread::Builder::new()
            .name("voice-ime-usage-db".into())
            .spawn(move || {
                let mut conn_opt: Option<Connection> = None;
                match open_and_initialize_writer_db(&db_path, &worker_scope) {
                    Ok((conn, history_enabled)) => {
                        conn_opt = Some(conn);
                        let mut guard = worker_state.lock().unwrap();
                        if !guard.faulted && !guard.disposed && guard.setting_request == 0 {
                            guard.enabled = history_enabled;
                            guard.available = true;
                            guard.privacy_version += 1;
                            if let Some(r) = &mut guard.round {
                                r.pending_original = None;
                            }
                            let _ = worker_status_tx.send(UsageStatus {
                                history_enabled: guard.enabled,
                                available: guard.available,
                                updating: guard.updating,
                            });
                        }
                    }
                    Err(_) => {
                        fault_state(&worker_state, &worker_status_tx);
                    }
                }

                while let Some(work) = rx.blocking_recv() {
                    let healthy = !worker_state.lock().unwrap().faulted && conn_opt.is_some();
                    match work {
                        DbWork::WriteSession(session, done) => {
                            let ok = healthy
                                && write_session(conn_opt.as_ref().unwrap(), &session).is_ok();
                            if !ok && healthy {
                                fault_state(&worker_state, &worker_status_tx);
                            }
                            if let Some(d) = done {
                                let _ = d.send(ok);
                            }
                        }
                        DbWork::WriteUtterance(utterance, done) => {
                            let ok = healthy
                                && write_utterance(conn_opt.as_ref().unwrap(), &utterance).is_ok();
                            if !ok && healthy {
                                fault_state(&worker_state, &worker_status_tx);
                            }
                            if let Some(d) = done {
                                let _ = d.send(ok);
                            }
                        }
                        DbWork::SetHistory {
                            enabled,
                            request_id,
                            done,
                        } => {
                            let ok = healthy
                                && set_history_meta(conn_opt.as_ref().unwrap(), enabled).is_ok();
                            if ok {
                                let mut guard = worker_state.lock().unwrap();
                                if !guard.faulted
                                    && !guard.disposed
                                    && guard.setting_request == request_id
                                {
                                    guard.enabled = enabled;
                                    guard.available = true;
                                    guard.updating = false;
                                    guard.privacy_version += 1;
                                    if let Some(r) = &mut guard.round {
                                        r.pending_original = None;
                                    }
                                    let _ = worker_status_tx.send(UsageStatus {
                                        history_enabled: guard.enabled,
                                        available: guard.available,
                                        updating: guard.updating,
                                    });
                                }
                            } else {
                                fault_state(&worker_state, &worker_status_tx);
                            }
                            let _ = done.send(ok);
                        }
                        DbWork::Barrier(done) => {
                            let ok = !worker_state.lock().unwrap().faulted && conn_opt.is_some();
                            let _ = done.send(ok);
                        }
                    }
                }
            })
            .expect("启动用量写入线程失败");

        Self {
            owner_scope,
            state,
            tx,
            status_tx,
            status_rx,
        }
    }

    pub fn status(&self) -> UsageStatus {
        *self.status_rx.borrow()
    }

    pub fn subscribe_status(&self) -> watch::Receiver<UsageStatus> {
        self.status_rx.clone()
    }

    pub fn begin_session(&self) -> Uuid {
        let mut guard = self.state.lock().unwrap();
        if let Some(old) = &mut guard.round {
            old.clear_pending();
        }
        let id = Uuid::new_v4();
        let now = UsageTime::now();
        let round = ActiveRound {
            id,
            created: now.clone(),
            started: None,
            started_instant: None,
            sequence: 0,
            revision: 1,
            sentence_privacy_version: None,
            pending_characters: 0,
            has_pending: false,
            pending_original: None,
        };
        let snapshot = SessionSnapshot {
            id,
            scope: self.owner_scope.clone(),
            created: now.clone(),
            started: None,
            observed: now,
            recording_ms: 0,
            state: "connecting",
            reason: None,
            revision: 1,
        };
        guard.round = Some(round);
        drop(guard);
        self.try_enqueue(DbWork::WriteSession(snapshot, None));
        id
    }

    pub fn recording_started(&self, session_id: Uuid) {
        let mut guard = self.state.lock().unwrap();
        let Some(r) = &mut guard.round else { return };
        if r.id != session_id || r.started.is_some() {
            return;
        }
        let now = UsageTime::now();
        r.started = Some(now.clone());
        r.started_instant = Some(Instant::now());
        r.revision += 1;
        let snapshot = SessionSnapshot {
            id: r.id,
            scope: self.owner_scope.clone(),
            created: r.created.clone(),
            started: r.started.clone(),
            observed: now,
            recording_ms: 0,
            state: "recording",
            reason: None,
            revision: r.revision,
        };
        drop(guard);
        self.try_enqueue(DbWork::WriteSession(snapshot, None));
    }

    pub fn recognized(&self, session_id: Uuid, text: &str, is_final: bool) {
        if text.chars().count() > MAX_TEXT_CHARS_BOUND {
            fault_state(&self.state, &self.status_tx);
            return;
        }
        let mut guard = self.state.lock().unwrap();
        if guard.faulted || guard.disposed {
            return;
        }
        let pv = guard.privacy_version;
        let allow_capture = guard.enabled && guard.available;
        let Some(r) = &mut guard.round else { return };
        if r.id != session_id {
            return;
        }
        let sentence_pv = *r.sentence_privacy_version.get_or_insert(pv);
        let original = if allow_capture && sentence_pv == pv {
            Some(text.to_string())
        } else {
            None
        };
        let characters = count_visible_characters(text) as i64;
        if !is_final {
            r.pending_characters = characters;
            r.has_pending = !text.trim().is_empty();
            r.pending_original = original;
            return;
        }
        r.sequence += 1;
        let utterance = UtteranceSnapshot {
            session_id: r.id,
            sequence: r.sequence,
            time: UsageTime::now(),
            characters,
            original,
        };
        r.clear_pending();
        drop(guard);
        self.try_enqueue(DbWork::WriteUtterance(utterance, None));
    }

    pub fn checkpoint(&self, session_id: Uuid) {
        let mut guard = self.state.lock().unwrap();
        let Some(r) = &mut guard.round else { return };
        if r.id != session_id || r.started.is_none() {
            return;
        }
        r.revision += 1;
        let elapsed_ms = r
            .started_instant
            .map(|t| t.elapsed().as_millis() as i64)
            .unwrap_or(0);
        let snapshot = SessionSnapshot {
            id: r.id,
            scope: self.owner_scope.clone(),
            created: r.created.clone(),
            started: r.started.clone(),
            observed: UsageTime::now(),
            recording_ms: elapsed_ms.max(0),
            state: "recording",
            reason: None,
            revision: r.revision,
        };
        drop(guard);
        self.try_enqueue(DbWork::WriteSession(snapshot, None));
    }

    pub fn end_session(&self, session_id: Uuid, reason: StopReason, finalize_pending: bool) {
        let mut pending_utterance: Option<UtteranceSnapshot> = None;
        let session_snapshot: SessionSnapshot;
        {
            let mut guard = self.state.lock().unwrap();
            let pv = guard.privacy_version;
            let allow_capture = guard.enabled && guard.available;
            let Some(mut r) = guard.round.take() else { return };
            if r.id != session_id {
                guard.round = Some(r);
                return;
            }
            if finalize_pending && r.has_pending {
                let orig = if allow_capture && r.sentence_privacy_version == Some(pv) {
                    r.pending_original.take()
                } else {
                    None
                };
                r.sequence += 1;
                pending_utterance = Some(UtteranceSnapshot {
                    session_id: r.id,
                    sequence: r.sequence,
                    time: UsageTime::now(),
                    characters: r.pending_characters,
                    original: orig,
                });
            }
            r.clear_pending();
            r.revision += 1;
            let elapsed_ms = r
                .started_instant
                .map(|t| t.elapsed().as_millis() as i64)
                .unwrap_or(0);
            session_snapshot = SessionSnapshot {
                id: r.id,
                scope: self.owner_scope.clone(),
                created: r.created,
                started: r.started,
                observed: UsageTime::now(),
                recording_ms: elapsed_ms.max(0),
                state: "ended",
                reason: Some(reason.as_str()),
                revision: r.revision,
            };
        }
        if let Some(u) = pending_utterance {
            self.try_enqueue(DbWork::WriteUtterance(u, None));
        }
        self.try_enqueue(DbWork::WriteSession(session_snapshot, None));
    }

    pub async fn set_history_enabled(&self, enabled: bool) -> bool {
        let request_id = {
            let mut guard = self.state.lock().unwrap();
            if guard.faulted || guard.disposed {
                guard.enabled = false;
                guard.available = false;
                guard.updating = false;
                let _ = self.status_tx.send(UsageStatus {
                    history_enabled: false,
                    available: false,
                    updating: false,
                });
                return false;
            }
            guard.enabled = false;
            guard.available = false;
            guard.updating = true;
            guard.privacy_version += 1;
            if let Some(r) = &mut guard.round {
                r.pending_original = None;
            }
            guard.setting_request += 1;
            let _ = self.status_tx.send(UsageStatus {
                history_enabled: false,
                available: false,
                updating: true,
            });
            guard.setting_request
        };

        let (done_tx, done_rx) = oneshot::channel();
        if self
            .tx
            .try_send(DbWork::SetHistory {
                enabled,
                request_id,
                done: done_tx,
            })
            .is_err()
        {
            fault_state(&self.state, &self.status_tx);
            return false;
        }
        done_rx.await.unwrap_or(false)
    }

    pub async fn flush(&self) -> bool {
        let (done_tx, done_rx) = oneshot::channel();
        if self.tx.try_send(DbWork::Barrier(done_tx)).is_err() {
            fault_state(&self.state, &self.status_tx);
            return false;
        }
        done_rx.await.unwrap_or(false)
    }

    pub fn shutdown(&self) {
        let active_id = {
            let guard = self.state.lock().unwrap();
            guard.round.as_ref().map(|r| r.id)
        };
        if let Some(id) = active_id {
            self.end_session(id, StopReason::OwnerExit, false);
        }
        let mut guard = self.state.lock().unwrap();
        guard.disposed = true;
        guard.enabled = false;
        guard.available = false;
        guard.updating = false;
        guard.privacy_version += 1;
        let _ = self.status_tx.send(UsageStatus {
            history_enabled: false,
            available: false,
            updating: false,
        });
    }

    fn try_enqueue(&self, work: DbWork) {
        if self.tx.try_send(work).is_err() {
            fault_state(&self.state, &self.status_tx);
        }
    }
}

fn fault_state(state: &Arc<Mutex<SharedState>>, status_tx: &watch::Sender<UsageStatus>) {
    let mut guard = state.lock().unwrap();
    if guard.faulted {
        return;
    }
    guard.faulted = true;
    guard.enabled = false;
    guard.available = false;
    guard.updating = false;
    guard.privacy_version += 1;
    if let Some(r) = &mut guard.round {
        r.pending_original = None;
    }
    let _ = status_tx.send(UsageStatus {
        history_enabled: false,
        available: false,
        updating: false,
    });
}

fn set_history_meta(conn: &Connection, enabled: bool) -> rusqlite::Result<()> {
    conn.execute(
        "INSERT INTO _meta(key, value) VALUES(?1, ?2) \
         ON CONFLICT(key) DO UPDATE SET value=excluded.value;",
        params![HISTORY_META_KEY, if enabled { "1" } else { "0" }],
    )?;
    Ok(())
}

fn write_session(conn: &Connection, s: &SessionSnapshot) -> rusqlite::Result<()> {
    let ended_utc = if s.state == "ended" {
        Some(s.observed.utc.as_str())
    } else {
        None
    };
    conn.execute(
        r#"
        INSERT INTO voice_ime_sessions(
            session_id, owner_scope, created_utc, created_local_date, created_offset_minutes,
            recording_started_utc, checkpoint_utc, checkpoint_local_date, checkpoint_offset_minutes,
            ended_utc, recording_ms, state, stop_reason, revision
        )
        VALUES(?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14)
        ON CONFLICT(session_id) DO UPDATE SET
            recording_started_utc = excluded.recording_started_utc,
            checkpoint_utc = excluded.checkpoint_utc,
            checkpoint_local_date = excluded.checkpoint_local_date,
            checkpoint_offset_minutes = excluded.checkpoint_offset_minutes,
            ended_utc = excluded.ended_utc,
            recording_ms = excluded.recording_ms,
            state = excluded.state,
            stop_reason = excluded.stop_reason,
            revision = excluded.revision
        WHERE excluded.revision > voice_ime_sessions.revision
          AND voice_ime_sessions.state NOT IN ('ended', 'interrupted');
        "#,
        params![
            s.id.hyphenated().to_string(),
            s.scope,
            s.created.utc,
            s.created.local_date,
            s.created.offset_minutes,
            s.started.as_ref().map(|t| t.utc.as_str()),
            s.observed.utc,
            s.observed.local_date,
            s.observed.offset_minutes,
            ended_utc,
            s.recording_ms,
            s.state,
            s.reason,
            s.revision,
        ],
    )?;
    Ok(())
}

fn write_utterance(conn: &Connection, u: &UtteranceSnapshot) -> rusqlite::Result<()> {
    conn.execute(
        r#"
        INSERT INTO voice_ime_utterances(
            session_id, sequence_no, finalized_utc, local_date, utc_offset_minutes,
            character_count, original_text
        )
        VALUES(?1, ?2, ?3, ?4, ?5, ?6, ?7)
        ON CONFLICT(session_id, sequence_no) DO NOTHING;
        "#,
        params![
            u.session_id.hyphenated().to_string(),
            u.sequence,
            u.time.utc,
            u.time.local_date,
            u.time.offset_minutes,
            u.characters,
            u.original,
        ],
    )?;
    Ok(())
}
