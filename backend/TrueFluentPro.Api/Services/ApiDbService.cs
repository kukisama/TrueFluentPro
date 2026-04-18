using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TrueFluentPro.Api.Models;

namespace TrueFluentPro.Api.Services;

/// <summary>
/// Database abstraction for the API backend.
/// </summary>
public interface IApiDbService
{
    Task InitializeAsync();

    // Users
    Task<UserRecord?> GetUserAsync(string userId);
    Task UpsertUserAsync(UserRecord user);
    Task<List<UserRecord>> ListUsersAsync(int limit = 100, int offset = 0);
    Task UpdateUserAsync(string userId, string? subscription = null, bool? isActive = null, bool? isAdmin = null);

    // Usage
    Task RecordUsageEventAsync(UsageEvent evt);
    Task<long> GetMonthlyUsageAsync(string userId, string resourceType, string yearMonth);
    Task UpsertMonthlyUsageAsync(string userId, string resourceType, string yearMonth, long totalAmount);
    Task<Dictionary<string, long>> GetAllMonthlyUsageAsync(string userId, string yearMonth);
    Task<List<UsageEvent>> GetUsageEventsAsync(string userId, int limit = 50, int offset = 0);

    // Plans
    Task<SubscriptionPlan?> GetPlanAsync(string planId);
    Task<List<SubscriptionPlan>> ListPlansAsync();
    Task UpsertPlanAsync(SubscriptionPlan plan);

    // Credentials
    Task<string?> GetDecryptedCredentialAsync(string id);
    Task UpsertCredentialAsync(string id, string serviceType, string plainValue, string? metadataJson = null);
    Task<List<CredentialSummary>> ListCredentialsAsync();

    // Stats
    Task<SystemStats> GetSystemStatsAsync();
}

public sealed class CredentialSummary
{
    public string Id { get; set; } = string.Empty;
    public string ServiceType { get; set; } = string.Empty;
    public string? MetadataJson { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class SystemStats
{
    public int TotalUsers { get; set; }
    public int ActiveUsers { get; set; }
    public long TotalUsageEvents { get; set; }
    public Dictionary<string, long> UsageByResourceThisMonth { get; set; } = new();
}

/// <summary>
/// SQLite implementation for the API backend (극简 / 极简 mode).
/// </summary>
public sealed class SqliteApiDbService : IApiDbService, IDisposable
{
    private readonly string _connectionString;
    private readonly byte[]? _encryptionKey;
    private readonly ILogger<SqliteApiDbService> _logger;

    public SqliteApiDbService(string dbPath, ILogger<SqliteApiDbService> logger)
    {
        _connectionString = $"Data Source={dbPath}";
        _logger = logger;

        var keyBase64 = Environment.GetEnvironmentVariable("ENCRYPTION_KEY");
        if (!string.IsNullOrEmpty(keyBase64))
        {
            try { _encryptionKey = Convert.FromBase64String(keyBase64); }
            catch { _logger.LogWarning("ENCRYPTION_KEY is not valid base64; credential encryption disabled"); }
        }
    }

    public async Task InitializeAsync()
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        // Enable WAL mode
        await ExecuteNonQueryAsync(conn, "PRAGMA journal_mode=WAL;");
        await ExecuteNonQueryAsync(conn, "PRAGMA foreign_keys=ON;");

        // Create tables
        await ExecuteNonQueryAsync(conn, """
            CREATE TABLE IF NOT EXISTS users (
                id              TEXT PRIMARY KEY,
                display_name    TEXT NOT NULL,
                email           TEXT,
                subscription    TEXT NOT NULL DEFAULT 'free',
                is_active       INTEGER NOT NULL DEFAULT 1,
                is_admin        INTEGER NOT NULL DEFAULT 0,
                created_at      TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at      TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS usage_events (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                user_id         TEXT NOT NULL REFERENCES users(id),
                resource_type   TEXT NOT NULL,
                amount          INTEGER NOT NULL,
                model           TEXT,
                endpoint_path   TEXT,
                status_code     INTEGER,
                duration_ms     INTEGER,
                created_at      TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE INDEX IF NOT EXISTS idx_usage_events_user_date ON usage_events(user_id, created_at);

            CREATE TABLE IF NOT EXISTS monthly_usage (
                user_id         TEXT NOT NULL REFERENCES users(id),
                resource_type   TEXT NOT NULL,
                year_month      TEXT NOT NULL,
                total_amount    INTEGER NOT NULL DEFAULT 0,
                updated_at      TEXT NOT NULL DEFAULT (datetime('now')),
                PRIMARY KEY (user_id, resource_type, year_month)
            );

            CREATE TABLE IF NOT EXISTS subscription_plans (
                plan_id         TEXT PRIMARY KEY,
                display_name    TEXT NOT NULL,
                price_monthly   REAL,
                limits_json     TEXT NOT NULL,
                is_active       INTEGER NOT NULL DEFAULT 1
            );

            CREATE TABLE IF NOT EXISTS credentials (
                id              TEXT PRIMARY KEY,
                service_type    TEXT NOT NULL,
                encrypted_value BLOB NOT NULL,
                metadata_json   TEXT,
                created_at      TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at      TEXT NOT NULL DEFAULT (datetime('now'))
            );
        """);

        // Seed default plans if empty
        var planCount = await ExecuteScalarAsync<long>(conn, "SELECT COUNT(*) FROM subscription_plans");
        if (planCount == 0)
        {
            await SeedDefaultPlansAsync(conn);
        }

        _logger.LogInformation("API database initialized (SQLite)");
    }

    // ───── Users ─────

    public async Task<UserRecord?> GetUserAsync(string userId)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, display_name, email, subscription, is_active, is_admin, created_at, updated_at FROM users WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", userId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return ReadUser(reader);
    }

    public async Task UpsertUserAsync(UserRecord user)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO users (id, display_name, email, subscription, is_active, is_admin, created_at, updated_at)
            VALUES (@id, @name, @email, @sub, @active, @admin, @created, @updated)
            ON CONFLICT(id) DO UPDATE SET
                display_name = excluded.display_name,
                email = excluded.email,
                updated_at = datetime('now')
        """;
        cmd.Parameters.AddWithValue("@id", user.Id);
        cmd.Parameters.AddWithValue("@name", user.DisplayName);
        cmd.Parameters.AddWithValue("@email", (object?)user.Email ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sub", user.Subscription);
        cmd.Parameters.AddWithValue("@active", user.IsActive ? 1 : 0);
        cmd.Parameters.AddWithValue("@admin", user.IsAdmin ? 1 : 0);
        cmd.Parameters.AddWithValue("@created", user.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@updated", user.UpdatedAt.ToString("o"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<UserRecord>> ListUsersAsync(int limit = 100, int offset = 0)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, display_name, email, subscription, is_active, is_admin, created_at, updated_at FROM users ORDER BY created_at DESC LIMIT @limit OFFSET @offset";
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", offset);
        var list = new List<UserRecord>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            list.Add(ReadUser(reader));
        return list;
    }

    public async Task UpdateUserAsync(string userId, string? subscription = null, bool? isActive = null, bool? isAdmin = null)
    {
        var sets = new List<string>();
        var parms = new List<SqliteParameter> { new("@id", userId) };

        if (subscription != null) { sets.Add("subscription = @sub"); parms.Add(new("@sub", subscription)); }
        if (isActive.HasValue) { sets.Add("is_active = @active"); parms.Add(new("@active", isActive.Value ? 1 : 0)); }
        if (isAdmin.HasValue) { sets.Add("is_admin = @admin"); parms.Add(new("@admin", isAdmin.Value ? 1 : 0)); }
        if (sets.Count == 0) return;

        sets.Add("updated_at = datetime('now')");

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE users SET {string.Join(", ", sets)} WHERE id = @id";
        cmd.Parameters.AddRange(parms);
        await cmd.ExecuteNonQueryAsync();
    }

    // ───── Usage ─────

    public async Task RecordUsageEventAsync(UsageEvent evt)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO usage_events (user_id, resource_type, amount, model, endpoint_path, status_code, duration_ms, created_at)
            VALUES (@uid, @rt, @amt, @model, @ep, @sc, @dur, @created)
        """;
        cmd.Parameters.AddWithValue("@uid", evt.UserId);
        cmd.Parameters.AddWithValue("@rt", evt.ResourceType);
        cmd.Parameters.AddWithValue("@amt", evt.Amount);
        cmd.Parameters.AddWithValue("@model", (object?)evt.Model ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ep", (object?)evt.EndpointPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sc", evt.StatusCode.HasValue ? evt.StatusCode.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@dur", evt.DurationMs.HasValue ? evt.DurationMs.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@created", evt.CreatedAt.ToString("o"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<long> GetMonthlyUsageAsync(string userId, string resourceType, string yearMonth)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        return await ExecuteScalarAsync<long>(conn,
            "SELECT COALESCE(total_amount, 0) FROM monthly_usage WHERE user_id = @uid AND resource_type = @rt AND year_month = @ym",
            new SqliteParameter("@uid", userId),
            new SqliteParameter("@rt", resourceType),
            new SqliteParameter("@ym", yearMonth));
    }

    public async Task UpsertMonthlyUsageAsync(string userId, string resourceType, string yearMonth, long totalAmount)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO monthly_usage (user_id, resource_type, year_month, total_amount, updated_at)
            VALUES (@uid, @rt, @ym, @amt, datetime('now'))
            ON CONFLICT(user_id, resource_type, year_month) DO UPDATE SET
                total_amount = excluded.total_amount,
                updated_at = datetime('now')
        """;
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@rt", resourceType);
        cmd.Parameters.AddWithValue("@ym", yearMonth);
        cmd.Parameters.AddWithValue("@amt", totalAmount);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<Dictionary<string, long>> GetAllMonthlyUsageAsync(string userId, string yearMonth)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT resource_type, total_amount FROM monthly_usage WHERE user_id = @uid AND year_month = @ym";
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@ym", yearMonth);
        var dict = new Dictionary<string, long>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            dict[reader.GetString(0)] = reader.GetInt64(1);
        return dict;
    }

    public async Task<List<UsageEvent>> GetUsageEventsAsync(string userId, int limit = 50, int offset = 0)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, user_id, resource_type, amount, model, endpoint_path, status_code, duration_ms, created_at
            FROM usage_events WHERE user_id = @uid ORDER BY created_at DESC LIMIT @limit OFFSET @offset
        """;
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", offset);
        var list = new List<UsageEvent>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new UsageEvent
            {
                Id = reader.GetInt64(0),
                UserId = reader.GetString(1),
                ResourceType = reader.GetString(2),
                Amount = reader.GetInt64(3),
                Model = reader.IsDBNull(4) ? null : reader.GetString(4),
                EndpointPath = reader.IsDBNull(5) ? null : reader.GetString(5),
                StatusCode = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                DurationMs = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                CreatedAt = DateTime.Parse(reader.GetString(8))
            });
        }
        return list;
    }

    // ───── Plans ─────

    public async Task<SubscriptionPlan?> GetPlanAsync(string planId)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT plan_id, display_name, price_monthly, limits_json, is_active FROM subscription_plans WHERE plan_id = @id";
        cmd.Parameters.AddWithValue("@id", planId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return ReadPlan(reader);
    }

    public async Task<List<SubscriptionPlan>> ListPlansAsync()
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT plan_id, display_name, price_monthly, limits_json, is_active FROM subscription_plans ORDER BY price_monthly";
        var list = new List<SubscriptionPlan>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            list.Add(ReadPlan(reader));
        return list;
    }

    public async Task UpsertPlanAsync(SubscriptionPlan plan)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO subscription_plans (plan_id, display_name, price_monthly, limits_json, is_active)
            VALUES (@id, @name, @price, @limits, @active)
            ON CONFLICT(plan_id) DO UPDATE SET
                display_name = excluded.display_name,
                price_monthly = excluded.price_monthly,
                limits_json = excluded.limits_json,
                is_active = excluded.is_active
        """;
        cmd.Parameters.AddWithValue("@id", plan.PlanId);
        cmd.Parameters.AddWithValue("@name", plan.DisplayName);
        cmd.Parameters.AddWithValue("@price", (double)plan.PriceMonthly);
        cmd.Parameters.AddWithValue("@limits", JsonSerializer.Serialize(plan.Limits));
        cmd.Parameters.AddWithValue("@active", plan.IsActive ? 1 : 0);
        await cmd.ExecuteNonQueryAsync();
    }

    // ───── Credentials ─────

    public async Task<string?> GetDecryptedCredentialAsync(string id)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT encrypted_value FROM credentials WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        var result = await cmd.ExecuteScalarAsync();
        if (result is not byte[] encrypted || encrypted.Length == 0) return null;
        return Decrypt(encrypted);
    }

    public async Task UpsertCredentialAsync(string id, string serviceType, string plainValue, string? metadataJson = null)
    {
        var encrypted = Encrypt(plainValue);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO credentials (id, service_type, encrypted_value, metadata_json, created_at, updated_at)
            VALUES (@id, @svc, @enc, @meta, datetime('now'), datetime('now'))
            ON CONFLICT(id) DO UPDATE SET
                service_type = excluded.service_type,
                encrypted_value = excluded.encrypted_value,
                metadata_json = excluded.metadata_json,
                updated_at = datetime('now')
        """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@svc", serviceType);
        cmd.Parameters.AddWithValue("@enc", encrypted);
        cmd.Parameters.AddWithValue("@meta", (object?)metadataJson ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<CredentialSummary>> ListCredentialsAsync()
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, service_type, metadata_json, updated_at FROM credentials ORDER BY id";
        var list = new List<CredentialSummary>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new CredentialSummary
            {
                Id = reader.GetString(0),
                ServiceType = reader.GetString(1),
                MetadataJson = reader.IsDBNull(2) ? null : reader.GetString(2),
                UpdatedAt = DateTime.Parse(reader.GetString(3))
            });
        }
        return list;
    }

    // ───── Stats ─────

    public async Task<SystemStats> GetSystemStatsAsync()
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var totalUsers = await ExecuteScalarAsync<long>(conn, "SELECT COUNT(*) FROM users");
        var activeUsers = await ExecuteScalarAsync<long>(conn, "SELECT COUNT(*) FROM users WHERE is_active = 1");
        var totalEvents = await ExecuteScalarAsync<long>(conn, "SELECT COUNT(*) FROM usage_events");

        var yearMonth = DateTime.UtcNow.ToString("yyyy-MM");
        var usageByResource = new Dictionary<string, long>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT resource_type, SUM(total_amount) FROM monthly_usage WHERE year_month = @ym GROUP BY resource_type";
        cmd.Parameters.AddWithValue("@ym", yearMonth);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            usageByResource[reader.GetString(0)] = reader.GetInt64(1);

        return new SystemStats
        {
            TotalUsers = (int)totalUsers,
            ActiveUsers = (int)activeUsers,
            TotalUsageEvents = totalEvents,
            UsageByResourceThisMonth = usageByResource
        };
    }

    // ───── Encryption helpers ─────

    private byte[] Encrypt(string plainText)
    {
        if (_encryptionKey == null || _encryptionKey.Length < 16)
            return Encoding.UTF8.GetBytes(plainText); // Fallback: no encryption

        using var aes = Aes.Create();
        aes.Key = _encryptionKey.Length >= 32 ? _encryptionKey[..32] : PadKey(_encryptionKey, 32);
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        // Prefix IV to ciphertext
        var result = new byte[aes.IV.Length + cipherBytes.Length];
        aes.IV.CopyTo(result, 0);
        cipherBytes.CopyTo(result, aes.IV.Length);
        return result;
    }

    private string? Decrypt(byte[] encrypted)
    {
        if (_encryptionKey == null || _encryptionKey.Length < 16)
            return Encoding.UTF8.GetString(encrypted); // Fallback: stored as plain text

        using var aes = Aes.Create();
        aes.Key = _encryptionKey.Length >= 32 ? _encryptionKey[..32] : PadKey(_encryptionKey, 32);

        var ivLength = aes.BlockSize / 8;
        if (encrypted.Length <= ivLength) return null;

        aes.IV = encrypted[..ivLength];
        var cipherBytes = encrypted[ivLength..];

        using var decryptor = aes.CreateDecryptor();
        var plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
        return Encoding.UTF8.GetString(plainBytes);
    }

    private static byte[] PadKey(byte[] key, int targetLength)
    {
        var padded = new byte[targetLength];
        Array.Copy(key, padded, Math.Min(key.Length, targetLength));
        return padded;
    }

    // ───── Seed data ─────

    private static async Task SeedDefaultPlansAsync(SqliteConnection conn)
    {
        var plans = new[]
        {
            ("free", "Free", 0.0, new Dictionary<string, long>
            {
                ["chat_token"] = 50_000, ["speech_minute"] = 10, ["image"] = 5,
                ["video_second"] = 0, ["translate_minute"] = 10
            }),
            ("basic", "Basic", 49.0, new Dictionary<string, long>
            {
                ["chat_token"] = 500_000, ["speech_minute"] = 60, ["image"] = 50,
                ["video_second"] = 60, ["translate_minute"] = 60
            }),
            ("pro", "Pro", 149.0, new Dictionary<string, long>
            {
                ["chat_token"] = 2_000_000, ["speech_minute"] = 300, ["image"] = 200,
                ["video_second"] = 300, ["translate_minute"] = 300
            })
        };

        foreach (var (id, name, price, limits) in plans)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO subscription_plans (plan_id, display_name, price_monthly, limits_json, is_active)
                VALUES (@id, @name, @price, @limits, 1)
            """;
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@name", name);
            cmd.Parameters.AddWithValue("@price", price);
            cmd.Parameters.AddWithValue("@limits", JsonSerializer.Serialize(limits));
            await cmd.ExecuteNonQueryAsync();
        }
    }

    // ───── SQL helpers ─────

    private static async Task ExecuteNonQueryAsync(SqliteConnection conn, string sql, params SqliteParameter[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddRange(parameters);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T> ExecuteScalarAsync<T>(SqliteConnection conn, string sql, params SqliteParameter[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddRange(parameters);
        var result = await cmd.ExecuteScalarAsync();
        if (result == null || result == DBNull.Value) return default!;
        return (T)Convert.ChangeType(result, typeof(T));
    }

    private static UserRecord ReadUser(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        DisplayName = reader.GetString(1),
        Email = reader.IsDBNull(2) ? null : reader.GetString(2),
        Subscription = reader.GetString(3),
        IsActive = reader.GetInt32(4) != 0,
        IsAdmin = reader.GetInt32(5) != 0,
        CreatedAt = DateTime.Parse(reader.GetString(6)),
        UpdatedAt = DateTime.Parse(reader.GetString(7))
    };

    private static SubscriptionPlan ReadPlan(SqliteDataReader reader)
    {
        var limitsJson = reader.GetString(3);
        return new()
        {
            PlanId = reader.GetString(0),
            DisplayName = reader.GetString(1),
            PriceMonthly = (decimal)reader.GetDouble(2),
            Limits = JsonSerializer.Deserialize<Dictionary<string, long>>(limitsJson) ?? new(),
            IsActive = reader.GetInt32(4) != 0
        };
    }

    public void Dispose() { /* SQLite connections are opened/closed per-operation */ }
}
