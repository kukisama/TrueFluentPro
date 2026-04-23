-- TrueFluentPro Gateway — Initial Schema (SQLite)

-- ═══ System Config (KV store) ═══
CREATE TABLE IF NOT EXISTS system_config (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);

-- ═══ Users ═══
CREATE TABLE IF NOT EXISTS users (
    id              TEXT PRIMARY KEY,
    username        TEXT,
    display_name    TEXT NOT NULL,
    email           TEXT,
    role            TEXT NOT NULL DEFAULT 'user',
    plan_id         TEXT NOT NULL DEFAULT 'free',
    is_active       INTEGER NOT NULL DEFAULT 1,
    auth_provider   TEXT NOT NULL DEFAULT 'local',
    tenant_id       TEXT NOT NULL DEFAULT 'default',
    first_seen_at   TEXT NOT NULL DEFAULT (datetime('now')),
    last_seen_at    TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_users_username ON users(username) WHERE username IS NOT NULL;

-- ═══ User Passwords (local auth) ═══
CREATE TABLE IF NOT EXISTS user_passwords (
    user_id       TEXT PRIMARY KEY REFERENCES users(id),
    password_hash TEXT NOT NULL
);

-- ═══ Capabilities ═══
CREATE TABLE IF NOT EXISTS capabilities (
    id              TEXT PRIMARY KEY,
    display_name    TEXT NOT NULL,
    category        TEXT NOT NULL,
    is_enabled      INTEGER NOT NULL DEFAULT 0,
    description     TEXT,
    created_at      TEXT NOT NULL DEFAULT (datetime('now'))
);

-- ═══ Providers ═══
CREATE TABLE IF NOT EXISTS providers (
    id              TEXT PRIMARY KEY,
    vendor          TEXT NOT NULL,
    display_name    TEXT NOT NULL,
    is_enabled      INTEGER NOT NULL DEFAULT 0,
    config_json     TEXT,
    created_at      TEXT NOT NULL DEFAULT (datetime('now'))
);

-- ═══ Capability ↔ Provider Bindings ═══
CREATE TABLE IF NOT EXISTS capability_provider_bindings (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    capability_id   TEXT NOT NULL REFERENCES capabilities(id),
    provider_id     TEXT NOT NULL REFERENCES providers(id),
    is_enabled      INTEGER NOT NULL DEFAULT 1,
    priority        INTEGER NOT NULL DEFAULT 100,
    weight          INTEGER NOT NULL DEFAULT 100,
    match_condition TEXT,
    config_json     TEXT,
    created_at      TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE (capability_id, provider_id, match_condition)
);

-- ═══ Provider Credentials (encrypted) ═══
CREATE TABLE IF NOT EXISTS provider_credentials (
    provider_id     TEXT NOT NULL REFERENCES providers(id),
    credential_key  TEXT NOT NULL,
    encrypted_value BLOB NOT NULL,
    nonce           BLOB NOT NULL,
    version         INTEGER NOT NULL DEFAULT 1,
    is_active       INTEGER NOT NULL DEFAULT 1,
    created_at      TEXT NOT NULL DEFAULT (datetime('now')),
    PRIMARY KEY (provider_id, credential_key, version)
);

-- ═══ Subscription Plans ═══
CREATE TABLE IF NOT EXISTS subscription_plans (
    id              TEXT PRIMARY KEY,
    display_name    TEXT NOT NULL,
    price_monthly   REAL,
    limits_json     TEXT NOT NULL DEFAULT '{}',
    is_active       INTEGER NOT NULL DEFAULT 1,
    created_at      TEXT NOT NULL DEFAULT (datetime('now'))
);

-- ═══ Usage Events (billing) ═══
CREATE TABLE IF NOT EXISTS usage_events (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id         TEXT NOT NULL REFERENCES users(id),
    capability_id   TEXT NOT NULL,
    resource_type   TEXT NOT NULL,
    amount          INTEGER NOT NULL,
    provider_id     TEXT,
    model           TEXT,
    endpoint_path   TEXT,
    upstream_status INTEGER,
    latency_ms      INTEGER,
    request_id      TEXT,
    created_at      TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX IF NOT EXISTS idx_usage_user_date ON usage_events(user_id, created_at);

-- ═══ Monthly Usage Summary ═══
CREATE TABLE IF NOT EXISTS monthly_usage (
    user_id         TEXT NOT NULL REFERENCES users(id),
    resource_type   TEXT NOT NULL,
    year_month      TEXT NOT NULL,
    total_amount    INTEGER NOT NULL DEFAULT 0,
    updated_at      TEXT NOT NULL DEFAULT (datetime('now')),
    PRIMARY KEY (user_id, resource_type, year_month)
);

-- ═══ Audit Log ═══
CREATE TABLE IF NOT EXISTS audit_log (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id         TEXT,
    action          TEXT NOT NULL,
    detail          TEXT,
    ip_address      TEXT,
    created_at      TEXT NOT NULL DEFAULT (datetime('now'))
);

-- ═══ Default Data ═══

-- Default subscription plans
INSERT OR IGNORE INTO subscription_plans (id, display_name, price_monthly, limits_json, is_active) VALUES
    ('free', 'Free', 0, '{"chat_token":50000,"speech_minute":10,"image":5,"video_second":0,"translate_minute":10,"tts_character":10000}', 1),
    ('basic', 'Basic', 49, '{"chat_token":500000,"speech_minute":60,"image":50,"video_second":60,"translate_minute":60,"tts_character":100000}', 1),
    ('pro', 'Pro', 149, '{"chat_token":2000000,"speech_minute":300,"image":200,"video_second":300,"translate_minute":300,"tts_character":500000}', 1),
    ('unlimited', 'Unlimited', NULL, '{"chat_token":-1,"speech_minute":-1,"image":-1,"video_second":-1,"translate_minute":-1,"tts_character":-1}', 1);

-- Default capabilities (all disabled until admin configures providers)
INSERT OR IGNORE INTO capabilities (id, display_name, category, is_enabled, description) VALUES
    ('chat', 'AI Chat', 'ai', 0, 'AI conversation (OpenAI compatible)'),
    ('text.translate', 'Text Translation', 'translate', 0, 'Text translation between languages'),
    ('speech.live.translate', 'Live Speech Translation', 'translate', 0, 'Real-time speech translation via WebSocket'),
    ('speech.stt', 'Speech to Text', 'speech', 0, 'Audio transcription'),
    ('speech.tts', 'Text to Speech', 'speech', 0, 'Speech synthesis'),
    ('image.generate', 'Image Generation', 'image', 0, 'AI image generation'),
    ('video.generate', 'Video Generation', 'video', 0, 'AI video generation');
