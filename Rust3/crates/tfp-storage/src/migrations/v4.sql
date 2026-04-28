-- V4: Creative Studio 8 tables

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
