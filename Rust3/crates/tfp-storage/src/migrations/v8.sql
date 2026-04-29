-- V8: Batch processing tables

CREATE TABLE IF NOT EXISTS batch_packages (
    id              TEXT PRIMARY KEY,
    session_id      TEXT NOT NULL DEFAULT '',
    audio_file_id   TEXT NOT NULL DEFAULT '',
    display_name    TEXT NOT NULL DEFAULT '',
    state           TEXT NOT NULL DEFAULT 'pending',
    is_paused       INTEGER NOT NULL DEFAULT 0,
    is_removed      INTEGER NOT NULL DEFAULT 0,
    total_count     INTEGER NOT NULL DEFAULT 0,
    completed_count INTEGER NOT NULL DEFAULT 0,
    failed_count    INTEGER NOT NULL DEFAULT 0,
    progress        REAL NOT NULL DEFAULT 0.0,
    created_at      TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at      TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS idx_bp_state ON batch_packages(state);
CREATE INDEX IF NOT EXISTS idx_bp_session ON batch_packages(session_id);

CREATE TABLE IF NOT EXISTS batch_queue_items (
    id              TEXT PRIMARY KEY,
    package_id      TEXT NOT NULL,
    queue_type      TEXT NOT NULL DEFAULT 'review_sheet',
    file_name       TEXT NOT NULL DEFAULT '',
    full_path       TEXT NOT NULL DEFAULT '',
    sheet_name      TEXT NOT NULL DEFAULT '',
    sheet_tag       TEXT NOT NULL DEFAULT '',
    prompt          TEXT NOT NULL DEFAULT '',
    status          TEXT NOT NULL DEFAULT 'pending',
    progress        REAL NOT NULL DEFAULT 0.0,
    status_message  TEXT NOT NULL DEFAULT '',
    error           TEXT,
    created_at      TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at      TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS idx_bqi_package ON batch_queue_items(package_id);
CREATE INDEX IF NOT EXISTS idx_bqi_status ON batch_queue_items(status);
CREATE INDEX IF NOT EXISTS idx_bqi_pkg_status ON batch_queue_items(package_id, status);
