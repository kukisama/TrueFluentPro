-- V2: Media tables (deprecated audio_sessions kept for data compat)

CREATE TABLE IF NOT EXISTS media_sessions (
    id           TEXT PRIMARY KEY,
    name         TEXT NOT NULL,
    session_type TEXT NOT NULL,
    created_at   TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS media_items (
    id          TEXT PRIMARY KEY,
    session_id  TEXT NOT NULL REFERENCES media_sessions(id),
    prompt      TEXT NOT NULL,
    result_url  TEXT,
    status      TEXT NOT NULL DEFAULT 'pending',
    created_at  TEXT NOT NULL DEFAULT (datetime('now'))
);

-- [DEPRECATED] Kept for legacy data. New code uses studio_sessions(session_type='audio') + audio_files.
CREATE TABLE IF NOT EXISTS audio_sessions (
    id          TEXT PRIMARY KEY,
    name        TEXT NOT NULL,
    stage       TEXT NOT NULL DEFAULT 'recording',
    file_path   TEXT,
    duration_ms INTEGER NOT NULL DEFAULT 0,
    created_at  TEXT NOT NULL DEFAULT (datetime('now'))
);
