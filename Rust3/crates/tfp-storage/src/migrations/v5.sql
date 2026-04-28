-- V5: Real-time translation 2 tables

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
