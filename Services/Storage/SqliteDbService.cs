using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace TrueFluentPro.Services.Storage
{
    public sealed class SqliteDbService : ISqliteDbService
    {
        private const int CurrentSchemaVersion = 4;
        private readonly string _connectionString;
        private readonly object _initLock = new();
        private bool _initialized;

        public string DatabasePath { get; }
        public int SchemaVersion { get; private set; }

        public SqliteDbService()
        {
            DatabasePath = Path.Combine(PathManager.Instance.AppDataPath, "truefluentpro.db");
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ConnectionString;
        }

        public SqliteConnection CreateConnection()
        {
            EnsureCreated();
            var conn = new SqliteConnection(_connectionString);
            conn.Open();
            return conn;
        }

        public void EnsureCreated()
        {
            if (_initialized) return;
            lock (_initLock)
            {
                if (_initialized) return;
                InitializeDatabase();
                _initialized = true;
            }
        }

        public void Dispose() { }

        // ── 内部初始化 ──────────────────────────────────────

        private void InitializeDatabase()
        {
            var dir = Path.GetDirectoryName(DatabasePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            SqliteDebugLogger.LogLifecycle($"初始化数据库: {DatabasePath}");

            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            ExecutePragmas(conn);
            CreateAllTables(conn);
            CreateAllIndexes(conn);
            EnsureSchemaVersion(conn);

            SqliteDebugLogger.LogLifecycle($"数据库初始化完成, schema v{SchemaVersion}");
        }

        private static void ExecutePragmas(SqliteConnection conn)
        {
            Exec(conn, "PRAGMA journal_mode=WAL;");
            Exec(conn, "PRAGMA foreign_keys=ON;");
            SqliteDebugLogger.LogLifecycle("已启用 WAL 模式和外键约束");
        }

        private static void CreateAllTables(SqliteConnection conn)
        {
            Exec(conn, @"
CREATE TABLE IF NOT EXISTS _schema_version (
    version     INTEGER NOT NULL,
    applied_at  TEXT    NOT NULL
);");

            Exec(conn, @"
CREATE TABLE IF NOT EXISTS _meta (
    key    TEXT PRIMARY KEY,
    value  TEXT NOT NULL
);");

            Exec(conn, @"
CREATE TABLE IF NOT EXISTS sessions (
    id                              TEXT PRIMARY KEY,
    session_type                    TEXT NOT NULL,
    name                            TEXT NOT NULL,
    directory_path                  TEXT NOT NULL DEFAULT '',
    canvas_mode                     TEXT NOT NULL DEFAULT '',
    media_kind                      TEXT NOT NULL DEFAULT '',
    is_deleted                      INTEGER NOT NULL DEFAULT 0,
    created_at                      TEXT NOT NULL,
    updated_at                      TEXT NOT NULL,
    last_accessed_at                TEXT,
    source_session_id               TEXT,
    source_session_name             TEXT,
    source_session_directory_name   TEXT,
    source_asset_id                 TEXT,
    source_asset_kind               TEXT,
    source_asset_file_name          TEXT,
    source_asset_path               TEXT,
    source_preview_path             TEXT,
    source_reference_role           TEXT,
    message_count                   INTEGER NOT NULL DEFAULT 0,
    task_count                      INTEGER NOT NULL DEFAULT 0,
    asset_count                     INTEGER NOT NULL DEFAULT 0,
    latest_message_preview          TEXT,
    legacy_source_path              TEXT,
    import_batch_id                 TEXT,
    imported_at                     TEXT,
    is_legacy_import                INTEGER NOT NULL DEFAULT 0
);");

            Exec(conn, @"
CREATE TABLE IF NOT EXISTS session_messages (
    id                  TEXT PRIMARY KEY,
    session_id          TEXT    NOT NULL,
    sequence_no         INTEGER NOT NULL,
    role                TEXT    NOT NULL,
    content_type        TEXT    NOT NULL DEFAULT '',
    text                TEXT    NOT NULL DEFAULT '',
    reasoning_text      TEXT    NOT NULL DEFAULT '',
    prompt_tokens       INTEGER,
    completion_tokens   INTEGER,
    generate_seconds    REAL,
    download_seconds    REAL,
    search_summary      TEXT,
    timestamp           TEXT    NOT NULL,
    is_deleted          INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY (session_id) REFERENCES sessions(id)
);");

            Exec(conn, @"
CREATE TABLE IF NOT EXISTS message_media_refs (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    message_id  TEXT    NOT NULL,
    media_path  TEXT    NOT NULL,
    media_kind  TEXT    NOT NULL DEFAULT '',
    sort_order  INTEGER NOT NULL DEFAULT 0,
    preview_path TEXT,
    FOREIGN KEY (message_id) REFERENCES session_messages(id)
);");

            Exec(conn, @"
CREATE TABLE IF NOT EXISTS message_citations (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    message_id      TEXT    NOT NULL,
    citation_number INTEGER NOT NULL,
    title           TEXT    NOT NULL DEFAULT '',
    url             TEXT    NOT NULL DEFAULT '',
    snippet         TEXT    NOT NULL DEFAULT '',
    hostname        TEXT    NOT NULL DEFAULT '',
    FOREIGN KEY (message_id) REFERENCES session_messages(id)
);");

            Exec(conn, @"
CREATE TABLE IF NOT EXISTS message_attachments (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    message_id      TEXT    NOT NULL,
    attachment_type TEXT    NOT NULL DEFAULT '',
    file_name       TEXT    NOT NULL DEFAULT '',
    file_path       TEXT    NOT NULL DEFAULT '',
    file_size       INTEGER NOT NULL DEFAULT 0,
    sort_order      INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY (message_id) REFERENCES session_messages(id)
);");;

            Exec(conn, @"
CREATE TABLE IF NOT EXISTS session_tasks (
    id                      TEXT PRIMARY KEY,
    session_id              TEXT    NOT NULL,
    task_type               TEXT    NOT NULL,
    status                  TEXT    NOT NULL,
    prompt                  TEXT    NOT NULL DEFAULT '',
    progress                REAL    NOT NULL DEFAULT 0,
    result_file_path        TEXT,
    error_message           TEXT,
    has_reference_input     INTEGER NOT NULL DEFAULT 0,
    remote_video_id         TEXT,
    remote_video_api_mode   TEXT,
    remote_generation_id    TEXT,
    remote_download_url     TEXT,
    generate_seconds        REAL,
    download_seconds        REAL,
    created_at              TEXT    NOT NULL,
    updated_at              TEXT    NOT NULL,
    FOREIGN KEY (session_id) REFERENCES sessions(id)
);");

            Exec(conn, @"
CREATE TABLE IF NOT EXISTS session_assets (
    asset_id                        TEXT PRIMARY KEY,
    session_id                      TEXT    NOT NULL,
    group_id                        TEXT    NOT NULL DEFAULT '',
    kind                            TEXT    NOT NULL DEFAULT '',
    workflow                        TEXT    NOT NULL DEFAULT '',
    file_name                       TEXT    NOT NULL DEFAULT '',
    file_path                       TEXT    NOT NULL DEFAULT '',
    preview_path                    TEXT    NOT NULL DEFAULT '',
    prompt_text                     TEXT    NOT NULL DEFAULT '',
    file_size                       INTEGER,
    mime_type                       TEXT,
    width                           INTEGER,
    height                          INTEGER,
    duration_ms                     INTEGER,
    created_at                      TEXT    NOT NULL,
    modified_at                     TEXT    NOT NULL,
    storage_scope                   TEXT    NOT NULL DEFAULT 'workspace-relative',
    derived_from_session_id         TEXT,
    derived_from_session_name       TEXT,
    derived_from_asset_id           TEXT,
    derived_from_asset_file_name    TEXT,
    derived_from_asset_kind         TEXT,
    derived_from_reference_role     TEXT,
    FOREIGN KEY (session_id) REFERENCES sessions(id)
);");

            Exec(conn, @"
CREATE TABLE IF NOT EXISTS reference_images (
    id          TEXT PRIMARY KEY,
    session_id  TEXT    NOT NULL,
    file_path   TEXT    NOT NULL,
    sort_order  INTEGER NOT NULL DEFAULT 0,
    width       INTEGER,
    height      INTEGER,
    created_at  TEXT    NOT NULL,
    FOREIGN KEY (session_id) REFERENCES sessions(id)
);");

            Exec(conn, @"
CREATE TABLE IF NOT EXISTS audio_library_items (
    id                      TEXT PRIMARY KEY,
    file_path               TEXT NOT NULL UNIQUE,
    file_name               TEXT NOT NULL,
    directory_path          TEXT NOT NULL DEFAULT '',
    file_size               INTEGER NOT NULL DEFAULT 0,
    duration_ms             INTEGER,
    processing_state        TEXT NOT NULL DEFAULT 'None',
    processing_badge_text   TEXT,
    processing_detail_text  TEXT,
    created_at              TEXT NOT NULL,
    updated_at              TEXT NOT NULL,
    is_deleted              INTEGER NOT NULL DEFAULT 0
);");

            Exec(conn, @"
CREATE TABLE IF NOT EXISTS subtitle_assets (
    id              TEXT PRIMARY KEY,
    audio_item_id   TEXT    NOT NULL,
    subtitle_type   TEXT    NOT NULL DEFAULT '',
    file_path       TEXT    NOT NULL,
    file_name       TEXT    NOT NULL DEFAULT '',
    cue_count       INTEGER,
    updated_at      TEXT    NOT NULL,
    FOREIGN KEY (audio_item_id) REFERENCES audio_library_items(id)
);");

            Exec(conn, @"
CREATE TABLE IF NOT EXISTS translation_history (
    id              TEXT PRIMARY KEY,
    source_text     TEXT    NOT NULL DEFAULT '',
    translated_text TEXT    NOT NULL DEFAULT '',
    source_language TEXT,
    target_language TEXT,
    created_at      TEXT    NOT NULL,
    is_deleted      INTEGER NOT NULL DEFAULT 0
);");

            Exec(conn, @"
CREATE TABLE IF NOT EXISTS audio_lifecycle (
    id              TEXT    NOT NULL,
    audio_item_id   TEXT    NOT NULL,
    stage           TEXT    NOT NULL,
    content_json    TEXT,
    file_path       TEXT,
    is_stale        INTEGER NOT NULL DEFAULT 0,
    generated_at    TEXT    NOT NULL,
    updated_at      TEXT    NOT NULL,
    PRIMARY KEY (audio_item_id, stage),
    FOREIGN KEY (audio_item_id) REFERENCES audio_library_items(id)
);");

            Exec(conn, @"
CREATE TABLE IF NOT EXISTS audio_task_queue (
    task_id         TEXT    NOT NULL PRIMARY KEY,
    audio_item_id   TEXT    NOT NULL,
    stage           TEXT    NOT NULL,
    status          TEXT    NOT NULL DEFAULT 'Pending',
    priority        INTEGER NOT NULL DEFAULT 0,
    depends_on      TEXT,
    error_message   TEXT,
    progress_message TEXT,
    retry_count     INTEGER NOT NULL DEFAULT 0,
    submitted_at    TEXT    NOT NULL,
    started_at      TEXT,
    completed_at    TEXT,
    submitted_by    TEXT,
    FOREIGN KEY (audio_item_id) REFERENCES audio_library_items(id)
);");
        }

        private static void CreateAllIndexes(SqliteConnection conn)
        {
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_sessions_updated ON sessions(updated_at DESC);");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_sessions_created ON sessions(created_at DESC);");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_sessions_type ON sessions(session_type);");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_messages_session_seq ON session_messages(session_id, sequence_no DESC);");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_tasks_session_created ON session_tasks(session_id, created_at DESC);");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_assets_session_created ON session_assets(session_id, created_at DESC);");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_refs_session ON reference_images(session_id);");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_media_refs_message ON message_media_refs(message_id);");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_citations_message ON message_citations(message_id);");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_audio_updated ON audio_library_items(updated_at DESC);");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_subtitles_audio ON subtitle_assets(audio_item_id);");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_translation_created ON translation_history(created_at DESC);");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_lifecycle_audio ON audio_lifecycle(audio_item_id);");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_task_queue_status ON audio_task_queue(status);");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_task_queue_audio_stage ON audio_task_queue(audio_item_id, stage, status);");
        }

        private void EnsureSchemaVersion(SqliteConnection conn)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT MAX(version) FROM _schema_version;";
            var result = cmd.ExecuteScalar();

            if (result is null or DBNull)
            {
                using var insert = conn.CreateCommand();
                insert.CommandText = "INSERT INTO _schema_version (version, applied_at) VALUES (@v, @t);";
                insert.Parameters.AddWithValue("@v", CurrentSchemaVersion);
                insert.Parameters.AddWithValue("@t", DateTime.Now.ToString("o"));
                insert.ExecuteNonQuery();
                SchemaVersion = CurrentSchemaVersion;
                SqliteDebugLogger.LogLifecycle($"写入初始 schema version: {CurrentSchemaVersion}");
            }
            else
            {
                SchemaVersion = Convert.ToInt32(result);
                if (SchemaVersion < CurrentSchemaVersion)
                {
                    RunMigrations(conn, SchemaVersion);
                }
            }
        }

        private void RunMigrations(SqliteConnection conn, int fromVersion)
        {
            SqliteDebugLogger.LogLifecycle($"执行迁移: v{fromVersion} → v{CurrentSchemaVersion}");

            if (fromVersion < 2) MigrateToV2(conn);
            if (fromVersion < 3) MigrateToV3(conn);
            if (fromVersion < 4) MigrateToV4(conn);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO _schema_version (version, applied_at) VALUES (@v, @t);";
            cmd.Parameters.AddWithValue("@v", CurrentSchemaVersion);
            cmd.Parameters.AddWithValue("@t", DateTime.Now.ToString("o"));
            cmd.ExecuteNonQuery();
            SchemaVersion = CurrentSchemaVersion;
        }

        private static void MigrateToV2(SqliteConnection conn)
        {
            Exec(conn, @"
CREATE TABLE IF NOT EXISTS audio_lifecycle (
    id              TEXT    NOT NULL,
    audio_item_id   TEXT    NOT NULL,
    stage           TEXT    NOT NULL,
    content_json    TEXT,
    file_path       TEXT,
    is_stale        INTEGER NOT NULL DEFAULT 0,
    generated_at    TEXT    NOT NULL,
    updated_at      TEXT    NOT NULL,
    PRIMARY KEY (audio_item_id, stage),
    FOREIGN KEY (audio_item_id) REFERENCES audio_library_items(id)
);");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_lifecycle_audio ON audio_lifecycle(audio_item_id);");
            SqliteDebugLogger.LogLifecycle("已迁移到 schema v2: audio_lifecycle 表");
        }

        private static void MigrateToV3(SqliteConnection conn)
        {
            Exec(conn, @"
CREATE TABLE IF NOT EXISTS audio_task_queue (
    task_id         TEXT    NOT NULL PRIMARY KEY,
    audio_item_id   TEXT    NOT NULL,
    stage           TEXT    NOT NULL,
    status          TEXT    NOT NULL DEFAULT 'Pending',
    priority        INTEGER NOT NULL DEFAULT 0,
    depends_on      TEXT,
    error_message   TEXT,
    retry_count     INTEGER NOT NULL DEFAULT 0,
    submitted_at    TEXT    NOT NULL,
    started_at      TEXT,
    completed_at    TEXT,
    submitted_by    TEXT,
    FOREIGN KEY (audio_item_id) REFERENCES audio_library_items(id)
);");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_task_queue_status ON audio_task_queue(status);");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_task_queue_audio_stage ON audio_task_queue(audio_item_id, stage, status);");
            SqliteDebugLogger.LogLifecycle("已迁移到 schema v3: audio_task_queue 表");
        }

        private static void MigrateToV4(SqliteConnection conn)
        {
            // 为 audio_task_queue 添加 progress_message 列
            try
            {
                Exec(conn, "ALTER TABLE audio_task_queue ADD COLUMN progress_message TEXT;");
            }
            catch
            {
                // 列可能已存在（例如新建的数据库已包含该列）
            }
            SqliteDebugLogger.LogLifecycle("已迁移到 schema v4: audio_task_queue 增加 progress_message 列");
        }

        public string? GetMeta(string key)
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM _meta WHERE key = @k;";
            cmd.Parameters.AddWithValue("@k", key);
            var result = cmd.ExecuteScalar();
            return result is string s ? s : null;
        }

        public void SetMeta(string key, string value)
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO _meta (key, value) VALUES (@k, @v) ON CONFLICT(key) DO UPDATE SET value = @v;";
            cmd.Parameters.AddWithValue("@k", key);
            cmd.Parameters.AddWithValue("@v", value);
            cmd.ExecuteNonQuery();
        }

        private static void Exec(SqliteConnection conn, string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }
}
