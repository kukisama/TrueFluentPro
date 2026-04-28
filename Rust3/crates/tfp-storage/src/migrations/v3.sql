-- V3: Message attachments, association tables, indexes

CREATE TABLE IF NOT EXISTS message_attachments (
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
CREATE INDEX IF NOT EXISTS idx_attach_msg ON message_attachments(message_id);

CREATE TABLE IF NOT EXISTS session_tasks (
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
CREATE INDEX IF NOT EXISTS idx_mc_msg ON message_citations(message_id);
