-- V1: Core tables

CREATE TABLE IF NOT EXISTS kv_store (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);

-- [DEPRECATED] Kept for legacy data compatibility. New code uses translation_sessions + translation_segments.
CREATE TABLE IF NOT EXISTS translation_history (
    id              TEXT PRIMARY KEY,
    source_text     TEXT NOT NULL,
    translated_text TEXT NOT NULL,
    source_lang     TEXT NOT NULL,
    target_lang     TEXT NOT NULL,
    provider        TEXT NOT NULL,
    created_at      TEXT NOT NULL DEFAULT (datetime('now'))
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
    content_hash      TEXT,
    created_at        TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS idx_messages_session ON messages(session_id);
CREATE INDEX IF NOT EXISTS idx_messages_content_hash ON messages(session_id, content_hash);

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
    completed_at  TEXT,
    is_stale      INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_lifecycle_audio ON audio_lifecycle(audio_item_id);

CREATE TABLE IF NOT EXISTS audio_task_queue (
    id               TEXT PRIMARY KEY,
    audio_item_id    TEXT NOT NULL,
    stage            TEXT NOT NULL,
    task_type        TEXT NOT NULL,
    status           TEXT NOT NULL DEFAULT 'Queued',
    priority         INTEGER NOT NULL DEFAULT 0,
    retry_count      INTEGER NOT NULL DEFAULT 0,
    max_retries      INTEGER NOT NULL DEFAULT 3,
    progress         REAL NOT NULL DEFAULT 0.0,
    prompt_text      TEXT,
    result_text      TEXT,
    error            TEXT,
    submitted_at     TEXT NOT NULL DEFAULT (datetime('now')),
    started_at       TEXT,
    completed_at     TEXT,
    updated_at       TEXT NOT NULL DEFAULT (datetime('now')),
    progress_message TEXT,
    parent_task_id   TEXT,
    last_heartbeat_at TEXT
);
CREATE INDEX IF NOT EXISTS idx_task_status ON audio_task_queue(status);
CREATE INDEX IF NOT EXISTS idx_task_audio ON audio_task_queue(audio_item_id);

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
    completed_at      TEXT,
    billable          INTEGER NOT NULL DEFAULT 0,
    audio_item_id     TEXT,
    stage             TEXT,
    model_name        TEXT,
    cancel_reason     TEXT,
    debug_prompt      TEXT,
    debug_response    TEXT,
    tokens_in         INTEGER,
    tokens_out        INTEGER,
    error_message     TEXT,
    finished_at       TEXT
);
CREATE INDEX IF NOT EXISTS idx_exec_task ON task_executions(task_id);

CREATE TABLE IF NOT EXISTS billing_records (
    id                TEXT PRIMARY KEY,
    task_id           TEXT,
    endpoint_id       TEXT NOT NULL,
    model_id          TEXT NOT NULL,
    prompt_tokens     INTEGER NOT NULL DEFAULT 0,
    completion_tokens INTEGER NOT NULL DEFAULT 0,
    cost_usd          REAL,
    status            TEXT NOT NULL DEFAULT 'Committed',
    created_at        TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS idx_billing_time ON billing_records(created_at);

CREATE TABLE IF NOT EXISTS saved_images (
    id               TEXT PRIMARY KEY,
    prompt           TEXT NOT NULL,
    revised_prompt   TEXT,
    file_path        TEXT NOT NULL,
    file_size        INTEGER NOT NULL DEFAULT 0,
    width            INTEGER,
    height           INTEGER,
    model_id         TEXT,
    endpoint_id      TEXT,
    generate_seconds REAL,
    source           TEXT NOT NULL DEFAULT 'media_center',
    created_at       TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS idx_saved_images_time ON saved_images(created_at);
