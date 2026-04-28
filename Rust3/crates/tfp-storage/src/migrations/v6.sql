-- V6: Media center canvas rounds + studio_sessions column

CREATE TABLE IF NOT EXISTS canvas_rounds (
    id          TEXT PRIMARY KEY,
    session_id  TEXT NOT NULL,
    round_index INTEGER NOT NULL,
    prompt      TEXT NOT NULL DEFAULT '',
    params_json TEXT NOT NULL DEFAULT '{}',
    model_ref   TEXT NOT NULL DEFAULT '',
    created_at  TEXT NOT NULL DEFAULT (datetime('now')),
    status      TEXT NOT NULL DEFAULT 'pending'
);
CREATE INDEX IF NOT EXISTS idx_cr_session ON canvas_rounds(session_id);
CREATE INDEX IF NOT EXISTS idx_cr_session_idx ON canvas_rounds(session_id, round_index);

CREATE TABLE IF NOT EXISTS canvas_round_assets (
    id          TEXT PRIMARY KEY,
    round_id    TEXT NOT NULL,
    asset_id    TEXT NOT NULL,
    sequence    INTEGER NOT NULL DEFAULT 0,
    is_selected INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_cra_round ON canvas_round_assets(round_id);
CREATE INDEX IF NOT EXISTS idx_cra_asset ON canvas_round_assets(asset_id);

-- Incremental column: studio_sessions add current_round_id
ALTER TABLE studio_sessions ADD COLUMN current_round_id TEXT;
