-- V7: AudioLab 7 tables + studio_sessions audio columns

CREATE TABLE IF NOT EXISTS audio_files (
    id                  TEXT PRIMARY KEY,
    display_name        TEXT NOT NULL,
    source_path         TEXT NOT NULL,
    mp3_path            TEXT,
    sample_rate         INTEGER NOT NULL DEFAULT 16000,
    channels            INTEGER NOT NULL DEFAULT 1,
    duration_ms         INTEGER NOT NULL DEFAULT 0,
    file_size_bytes     INTEGER NOT NULL DEFAULT 0,
    sha256              TEXT NOT NULL DEFAULT '',
    imported_at         TEXT NOT NULL DEFAULT (datetime('now')),
    last_opened_at      TEXT,
    is_legacy_import    INTEGER NOT NULL DEFAULT 0,
    legacy_source_path  TEXT,
    import_batch_id     TEXT
);
CREATE INDEX IF NOT EXISTS idx_af_imported ON audio_files(imported_at);
CREATE INDEX IF NOT EXISTS idx_af_sha256 ON audio_files(sha256);

CREATE TABLE IF NOT EXISTS audio_transcripts (
    id              TEXT PRIMARY KEY,
    session_id      TEXT NOT NULL,
    audio_file_id   TEXT NOT NULL,
    language        TEXT NOT NULL DEFAULT '',
    raw_json        TEXT,
    parser_kind     TEXT NOT NULL DEFAULT 'fast',
    created_at      TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS idx_at_session ON audio_transcripts(session_id);
CREATE INDEX IF NOT EXISTS idx_at_file ON audio_transcripts(audio_file_id);

CREATE TABLE IF NOT EXISTS audio_segments (
    id              TEXT PRIMARY KEY,
    transcript_id   TEXT NOT NULL,
    sequence        INTEGER NOT NULL,
    speaker         TEXT NOT NULL DEFAULT '',
    speaker_index   INTEGER NOT NULL DEFAULT 0,
    start_ms        INTEGER NOT NULL DEFAULT 0,
    end_ms          INTEGER NOT NULL DEFAULT 0,
    text            TEXT NOT NULL DEFAULT '',
    confidence      REAL
);
CREATE INDEX IF NOT EXISTS idx_aseg_transcript ON audio_segments(transcript_id);
CREATE INDEX IF NOT EXISTS idx_aseg_seq ON audio_segments(transcript_id, sequence);

CREATE TABLE IF NOT EXISTS audio_stage_outputs (
    id                  TEXT PRIMARY KEY,
    session_id          TEXT NOT NULL,
    stage_key           TEXT NOT NULL,
    content_markdown    TEXT NOT NULL DEFAULT '',
    status              TEXT NOT NULL DEFAULT 'Empty',
    error_message       TEXT,
    model_ref           TEXT,
    generated_at        TEXT,
    custom_stage_key    TEXT,
    custom_is_mindmap   INTEGER
);
CREATE INDEX IF NOT EXISTS idx_aso_session ON audio_stage_outputs(session_id);
CREATE UNIQUE INDEX IF NOT EXISTS idx_aso_session_stage ON audio_stage_outputs(session_id, stage_key);

CREATE TABLE IF NOT EXISTS audio_research_topics (
    id              TEXT PRIMARY KEY,
    session_id      TEXT NOT NULL,
    title           TEXT NOT NULL DEFAULT '',
    description     TEXT NOT NULL DEFAULT '',
    status          TEXT NOT NULL DEFAULT 'idle',
    report_markdown TEXT,
    created_at      TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS idx_art_session ON audio_research_topics(session_id);

CREATE TABLE IF NOT EXISTS audio_auto_tags (
    id              TEXT PRIMARY KEY,
    session_id      TEXT NOT NULL,
    tag             TEXT NOT NULL DEFAULT '',
    source          TEXT NOT NULL DEFAULT 'auto',
    created_at      TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS idx_aat_session ON audio_auto_tags(session_id);

CREATE TABLE IF NOT EXISTS audio_stage_presets (
    id               TEXT PRIMARY KEY,
    stage            TEXT NOT NULL,
    display_name     TEXT NOT NULL DEFAULT '',
    system_prompt    TEXT NOT NULL DEFAULT '',
    show_in_tab      INTEGER NOT NULL DEFAULT 1,
    include_in_batch INTEGER NOT NULL DEFAULT 1,
    is_enabled       INTEGER NOT NULL DEFAULT 1,
    display_mode     TEXT NOT NULL DEFAULT 'Markdown',
    sort_order       INTEGER NOT NULL DEFAULT 0
);
CREATE UNIQUE INDEX IF NOT EXISTS idx_asp_stage ON audio_stage_presets(stage);

-- Incremental columns: studio_sessions audio fields
ALTER TABLE studio_sessions ADD COLUMN audio_path TEXT;
ALTER TABLE studio_sessions ADD COLUMN audio_duration_ms INTEGER;
ALTER TABLE studio_sessions ADD COLUMN audio_imported_at TEXT;
