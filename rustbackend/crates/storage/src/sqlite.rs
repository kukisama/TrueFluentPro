//! SQLite storage backend implementation.

use crate::{StorageBackend, StorageResult, EncryptedCredential, CredentialMeta};
use crate::error::StorageError;
use domain::models::*;
use sqlx::sqlite::{SqlitePool, SqlitePoolOptions, SqliteConnectOptions};
use std::str::FromStr;
use tracing::info;

pub struct SqliteBackend {
    pool: SqlitePool,
}

impl SqliteBackend {
    pub async fn new(db_path: &str) -> Result<Self, StorageError> {
        // Ensure parent directory exists
        if let Some(parent) = std::path::Path::new(db_path).parent() {
            std::fs::create_dir_all(parent)
                .map_err(|e| StorageError::Internal(format!("failed to create db dir: {e}")))?;
        }

        let opts = SqliteConnectOptions::from_str(&format!("sqlite:{db_path}"))
            .map_err(|e| StorageError::Internal(e.to_string()))?
            .create_if_missing(true)
            .journal_mode(sqlx::sqlite::SqliteJournalMode::Wal)
            .foreign_keys(true);

        let pool = SqlitePoolOptions::new()
            .max_connections(5)
            .connect_with(opts)
            .await?;

        Ok(Self { pool })
    }

    /// Run all migration SQL inline.
    async fn run_migrations(&self) -> StorageResult<()> {
        let migration_sql = include_str!("migrations/001_initial.sql");
        for statement in migration_sql.split(';') {
            let s = statement.trim();
            if !s.is_empty() {
                sqlx::query(s).execute(&self.pool).await.map_err(|e| {
                    StorageError::Migration(format!("migration failed: {e} — SQL: {s}"))
                })?;
            }
        }
        info!("SQLite migrations complete");
        Ok(())
    }
}

#[async_trait::async_trait]
impl StorageBackend for SqliteBackend {
    async fn initialize(&self) -> StorageResult<()> {
        self.run_migrations().await
    }

    async fn health_check(&self) -> StorageResult<()> {
        sqlx::query("SELECT 1").execute(&self.pool).await?;
        Ok(())
    }

    async fn is_initialized(&self) -> StorageResult<bool> {
        let row: Option<(String,)> =
            sqlx::query_as("SELECT value FROM system_config WHERE key = 'initialized'")
                .fetch_optional(&self.pool)
                .await?;
        Ok(row.map(|r| r.0 == "true").unwrap_or(false))
    }

    // ─── System Config ───

    async fn get_config(&self, key: &str) -> StorageResult<Option<String>> {
        let row: Option<(String,)> =
            sqlx::query_as("SELECT value FROM system_config WHERE key = ?1")
                .bind(key)
                .fetch_optional(&self.pool)
                .await?;
        Ok(row.map(|r| r.0))
    }

    async fn set_config(&self, key: &str, value: &str) -> StorageResult<()> {
        sqlx::query(
            "INSERT INTO system_config (key, value) VALUES (?1, ?2) \
             ON CONFLICT(key) DO UPDATE SET value = excluded.value",
        )
        .bind(key)
        .bind(value)
        .execute(&self.pool)
        .await?;
        Ok(())
    }

    // ─── Users ───

    async fn get_user(&self, id: &str) -> StorageResult<Option<User>> {
        let row: Option<UserRow> = sqlx::query_as(
            "SELECT id, username, display_name, email, role, plan_id, is_active, \
             auth_provider, tenant_id, first_seen_at, last_seen_at FROM users WHERE id = ?1",
        )
        .bind(id)
        .fetch_optional(&self.pool)
        .await?;
        Ok(row.map(Into::into))
    }

    async fn get_user_by_username(&self, username: &str) -> StorageResult<Option<User>> {
        let row: Option<UserRow> = sqlx::query_as(
            "SELECT id, username, display_name, email, role, plan_id, is_active, \
             auth_provider, tenant_id, first_seen_at, last_seen_at \
             FROM users WHERE username = ?1",
        )
        .bind(username)
        .fetch_optional(&self.pool)
        .await?;
        Ok(row.map(Into::into))
    }

    async fn upsert_user(&self, user: &User) -> StorageResult<()> {
        sqlx::query(
            "INSERT INTO users (id, username, display_name, email, role, plan_id, is_active, \
             auth_provider, tenant_id, first_seen_at, last_seen_at) \
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11) \
             ON CONFLICT(id) DO UPDATE SET \
             display_name = excluded.display_name, \
             email = excluded.email, \
             role = excluded.role, \
             plan_id = excluded.plan_id, \
             is_active = excluded.is_active, \
             last_seen_at = excluded.last_seen_at",
        )
        .bind(&user.id)
        .bind(&user.username)
        .bind(&user.display_name)
        .bind(&user.email)
        .bind(user.role.as_str())
        .bind(&user.plan_id)
        .bind(user.is_active)
        .bind(user.auth_provider.as_str())
        .bind(&user.tenant_id)
        .bind(user.first_seen_at.to_rfc3339())
        .bind(user.last_seen_at.to_rfc3339())
        .execute(&self.pool)
        .await?;
        Ok(())
    }

    async fn list_users(&self, offset: i64, limit: i64) -> StorageResult<Vec<User>> {
        let rows: Vec<UserRow> = sqlx::query_as(
            "SELECT id, username, display_name, email, role, plan_id, is_active, \
             auth_provider, tenant_id, first_seen_at, last_seen_at \
             FROM users ORDER BY first_seen_at DESC LIMIT ?1 OFFSET ?2",
        )
        .bind(limit)
        .bind(offset)
        .fetch_all(&self.pool)
        .await?;
        Ok(rows.into_iter().map(Into::into).collect())
    }

    async fn update_user_last_seen(&self, id: &str) -> StorageResult<()> {
        sqlx::query("UPDATE users SET last_seen_at = datetime('now') WHERE id = ?1")
            .bind(id)
            .execute(&self.pool)
            .await?;
        Ok(())
    }

    async fn count_users(&self) -> StorageResult<i64> {
        let row: (i64,) = sqlx::query_as("SELECT COUNT(*) FROM users")
            .fetch_one(&self.pool)
            .await?;
        Ok(row.0)
    }

    // ─── Capabilities ───

    async fn get_capabilities(&self) -> StorageResult<Vec<Capability>> {
        let rows: Vec<CapabilityRow> = sqlx::query_as(
            "SELECT id, display_name, category, is_enabled, description FROM capabilities",
        )
        .fetch_all(&self.pool)
        .await?;
        Ok(rows.into_iter().map(Into::into).collect())
    }

    async fn upsert_capability(&self, cap: &Capability) -> StorageResult<()> {
        sqlx::query(
            "INSERT INTO capabilities (id, display_name, category, is_enabled, description) \
             VALUES (?1, ?2, ?3, ?4, ?5) \
             ON CONFLICT(id) DO UPDATE SET \
             display_name = excluded.display_name, \
             is_enabled = excluded.is_enabled, \
             description = excluded.description",
        )
        .bind(&cap.id)
        .bind(&cap.display_name)
        .bind(&cap.category)
        .bind(cap.is_enabled)
        .bind(&cap.description)
        .execute(&self.pool)
        .await?;
        Ok(())
    }

    // ─── Providers ───

    async fn get_providers(&self) -> StorageResult<Vec<ProviderInfo>> {
        let rows: Vec<ProviderRow> = sqlx::query_as(
            "SELECT id, vendor, display_name, is_enabled, config_json FROM providers",
        )
        .fetch_all(&self.pool)
        .await?;
        Ok(rows.into_iter().map(Into::into).collect())
    }

    async fn upsert_provider(&self, provider: &ProviderInfo) -> StorageResult<()> {
        let config_str = provider
            .config_json
            .as_ref()
            .map(|v| v.to_string());
        sqlx::query(
            "INSERT INTO providers (id, vendor, display_name, is_enabled, config_json) \
             VALUES (?1, ?2, ?3, ?4, ?5) \
             ON CONFLICT(id) DO UPDATE SET \
             display_name = excluded.display_name, \
             is_enabled = excluded.is_enabled, \
             config_json = excluded.config_json",
        )
        .bind(&provider.id)
        .bind(&provider.vendor)
        .bind(&provider.display_name)
        .bind(provider.is_enabled)
        .bind(&config_str)
        .execute(&self.pool)
        .await?;
        Ok(())
    }

    // ─── Plans ───

    async fn get_plan(&self, id: &str) -> StorageResult<Option<SubscriptionPlan>> {
        let row: Option<PlanRow> = sqlx::query_as(
            "SELECT id, display_name, price_monthly, limits_json, is_active \
             FROM subscription_plans WHERE id = ?1",
        )
        .bind(id)
        .fetch_optional(&self.pool)
        .await?;
        Ok(row.map(Into::into))
    }

    async fn list_plans(&self) -> StorageResult<Vec<SubscriptionPlan>> {
        let rows: Vec<PlanRow> = sqlx::query_as(
            "SELECT id, display_name, price_monthly, limits_json, is_active \
             FROM subscription_plans",
        )
        .fetch_all(&self.pool)
        .await?;
        Ok(rows.into_iter().map(Into::into).collect())
    }

    async fn upsert_plan(&self, plan: &SubscriptionPlan) -> StorageResult<()> {
        sqlx::query(
            "INSERT INTO subscription_plans (id, display_name, price_monthly, limits_json, is_active) \
             VALUES (?1, ?2, ?3, ?4, ?5) \
             ON CONFLICT(id) DO UPDATE SET \
             display_name = excluded.display_name, \
             price_monthly = excluded.price_monthly, \
             limits_json = excluded.limits_json, \
             is_active = excluded.is_active",
        )
        .bind(&plan.id)
        .bind(&plan.display_name)
        .bind(plan.price_monthly)
        .bind(plan.limits_json.to_string())
        .bind(plan.is_active)
        .execute(&self.pool)
        .await?;
        Ok(())
    }

    // ─── Credentials ───

    async fn get_credential(&self, provider_id: &str, key: &str) -> StorageResult<Option<EncryptedCredential>> {
        let row: Option<CredRow> = sqlx::query_as(
            "SELECT provider_id, credential_key, encrypted_value, nonce, version, is_active \
             FROM provider_credentials \
             WHERE provider_id = ?1 AND credential_key = ?2 AND is_active = 1 \
             ORDER BY version DESC LIMIT 1",
        )
        .bind(provider_id)
        .bind(key)
        .fetch_optional(&self.pool)
        .await?;
        Ok(row.map(Into::into))
    }

    async fn upsert_credential(&self, cred: &EncryptedCredential) -> StorageResult<()> {
        sqlx::query(
            "INSERT INTO provider_credentials \
             (provider_id, credential_key, encrypted_value, nonce, version, is_active) \
             VALUES (?1, ?2, ?3, ?4, ?5, ?6) \
             ON CONFLICT(provider_id, credential_key, version) DO UPDATE SET \
             encrypted_value = excluded.encrypted_value, \
             nonce = excluded.nonce, \
             is_active = excluded.is_active",
        )
        .bind(&cred.provider_id)
        .bind(&cred.credential_key)
        .bind(&cred.encrypted_value)
        .bind(&cred.nonce)
        .bind(cred.version)
        .bind(cred.is_active)
        .execute(&self.pool)
        .await?;
        Ok(())
    }

    async fn list_credentials(&self, provider_id: &str) -> StorageResult<Vec<CredentialMeta>> {
        let rows: Vec<CredMetaRow> = sqlx::query_as(
            "SELECT provider_id, credential_key, version, is_active \
             FROM provider_credentials WHERE provider_id = ?1",
        )
        .bind(provider_id)
        .fetch_all(&self.pool)
        .await?;
        Ok(rows.into_iter().map(Into::into).collect())
    }

    // ─── Password ───

    async fn get_password_hash(&self, user_id: &str) -> StorageResult<Option<String>> {
        let row: Option<(String,)> =
            sqlx::query_as("SELECT password_hash FROM user_passwords WHERE user_id = ?1")
                .bind(user_id)
                .fetch_optional(&self.pool)
                .await?;
        Ok(row.map(|r| r.0))
    }

    async fn set_password_hash(&self, user_id: &str, hash: &str) -> StorageResult<()> {
        sqlx::query(
            "INSERT INTO user_passwords (user_id, password_hash) VALUES (?1, ?2) \
             ON CONFLICT(user_id) DO UPDATE SET password_hash = excluded.password_hash",
        )
        .bind(user_id)
        .bind(hash)
        .execute(&self.pool)
        .await?;
        Ok(())
    }

    // ─── Usage Tracking ───

    async fn record_usage(&self, user_id: &str, capability_id: &str, resource_type: &str, amount: i64) -> StorageResult<()> {
        let now = chrono::Utc::now().to_rfc3339();
        let year_month = chrono::Utc::now().format("%Y-%m").to_string();

        // Insert usage event
        sqlx::query(
            "INSERT INTO usage_events (user_id, capability_id, resource_type, amount, created_at) \
             VALUES (?1, ?2, ?3, ?4, ?5)",
        )
        .bind(user_id)
        .bind(capability_id)
        .bind(resource_type)
        .bind(amount)
        .bind(&now)
        .execute(&self.pool)
        .await?;

        // Update monthly summary
        sqlx::query(
            "INSERT INTO monthly_usage (user_id, resource_type, year_month, total_amount, updated_at) \
             VALUES (?1, ?2, ?3, ?4, ?5) \
             ON CONFLICT(user_id, resource_type, year_month) \
             DO UPDATE SET total_amount = total_amount + excluded.total_amount, updated_at = excluded.updated_at",
        )
        .bind(user_id)
        .bind(resource_type)
        .bind(&year_month)
        .bind(amount)
        .bind(&now)
        .execute(&self.pool)
        .await?;

        Ok(())
    }

    async fn get_usage_total(&self, user_id: &str, resource_type: &str, since: chrono::DateTime<chrono::Utc>) -> StorageResult<i64> {
        let since_str = since.to_rfc3339();
        let row: Option<(i64,)> = sqlx::query_as(
            "SELECT COALESCE(SUM(amount), 0) FROM usage_events \
             WHERE user_id = ?1 AND resource_type = ?2 AND created_at >= ?3",
        )
        .bind(user_id)
        .bind(resource_type)
        .bind(&since_str)
        .fetch_optional(&self.pool)
        .await?;
        Ok(row.map(|r| r.0).unwrap_or(0))
    }

    async fn get_usage_records(&self, user_id: &str, offset: i64, limit: i64) -> StorageResult<Vec<domain::models::UsageRecord>> {
        let rows: Vec<UsageRow> = sqlx::query_as(
            "SELECT id, user_id, capability_id, resource_type, amount, created_at \
             FROM usage_events WHERE user_id = ?1 ORDER BY created_at DESC LIMIT ?2 OFFSET ?3",
        )
        .bind(user_id)
        .bind(limit)
        .bind(offset)
        .fetch_all(&self.pool)
        .await?;
        Ok(rows.into_iter().map(Into::into).collect())
    }

    async fn write_audit_log(&self, user_id: &str, action: &str, detail: Option<&str>, ip_address: Option<&str>) -> StorageResult<()> {
        sqlx::query("INSERT INTO audit_log (user_id, action, detail, ip_address) VALUES (?, ?, ?, ?)")
            .bind(user_id)
            .bind(action)
            .bind(detail)
            .bind(ip_address)
            .execute(&self.pool)
            .await
            .map_err(|e| StorageError::Internal(format!("audit_log write failed: {e}")))?;
        Ok(())
    }

    async fn list_audit_logs(&self, offset: i64, limit: i64) -> StorageResult<Vec<domain::models::AuditLogEntry>> {
        let rows: Vec<AuditLogRow> = sqlx::query_as(
            "SELECT id, user_id, action, detail, ip_address, created_at \
             FROM audit_log ORDER BY created_at DESC LIMIT ?1 OFFSET ?2",
        )
        .bind(limit)
        .bind(offset)
        .fetch_all(&self.pool)
        .await?;

        Ok(rows.into_iter().map(|r| domain::models::AuditLogEntry {
            id: r.id,
            user_id: r.user_id,
            action: r.action,
            detail: r.detail,
            ip_address: r.ip_address,
            created_at: chrono::DateTime::parse_from_rfc3339(&r.created_at)
                .map(|dt| dt.with_timezone(&chrono::Utc))
                .unwrap_or_else(|_| chrono::Utc::now()),
        }).collect())
    }
}

// ═══ Row types for sqlx ═══

#[derive(sqlx::FromRow)]
struct UserRow {
    id: String,
    username: Option<String>,
    display_name: String,
    email: Option<String>,
    role: String,
    plan_id: String,
    is_active: bool,
    auth_provider: String,
    tenant_id: String,
    first_seen_at: String,
    last_seen_at: String,
}

impl From<UserRow> for User {
    fn from(r: UserRow) -> Self {
        Self {
            id: r.id,
            username: r.username,
            display_name: r.display_name,
            email: r.email,
            role: r.role.parse().unwrap_or(UserRole::User),
            plan_id: r.plan_id,
            is_active: r.is_active,
            auth_provider: r.auth_provider.parse().unwrap_or(AuthProvider::Local),
            tenant_id: r.tenant_id,
            first_seen_at: chrono::DateTime::parse_from_rfc3339(&r.first_seen_at)
                .map(|d| d.with_timezone(&chrono::Utc))
                .unwrap_or_else(|_| chrono::Utc::now()),
            last_seen_at: chrono::DateTime::parse_from_rfc3339(&r.last_seen_at)
                .map(|d| d.with_timezone(&chrono::Utc))
                .unwrap_or_else(|_| chrono::Utc::now()),
        }
    }
}

#[derive(sqlx::FromRow)]
struct CapabilityRow {
    id: String,
    display_name: String,
    category: String,
    is_enabled: bool,
    description: Option<String>,
}

impl From<CapabilityRow> for Capability {
    fn from(r: CapabilityRow) -> Self {
        Self {
            id: r.id,
            display_name: r.display_name,
            category: r.category,
            is_enabled: r.is_enabled,
            description: r.description,
        }
    }
}

#[derive(sqlx::FromRow)]
struct ProviderRow {
    id: String,
    vendor: String,
    display_name: String,
    is_enabled: bool,
    config_json: Option<String>,
}

impl From<ProviderRow> for ProviderInfo {
    fn from(r: ProviderRow) -> Self {
        Self {
            id: r.id,
            vendor: r.vendor,
            display_name: r.display_name,
            is_enabled: r.is_enabled,
            config_json: r.config_json.and_then(|s| serde_json::from_str(&s).ok()),
        }
    }
}

#[derive(sqlx::FromRow)]
struct PlanRow {
    id: String,
    display_name: String,
    price_monthly: Option<f64>,
    limits_json: String,
    is_active: bool,
}

impl From<PlanRow> for SubscriptionPlan {
    fn from(r: PlanRow) -> Self {
        Self {
            id: r.id,
            display_name: r.display_name,
            price_monthly: r.price_monthly,
            limits_json: serde_json::from_str(&r.limits_json).unwrap_or(serde_json::json!({})),
            is_active: r.is_active,
        }
    }
}

#[derive(sqlx::FromRow)]
struct CredRow {
    provider_id: String,
    credential_key: String,
    encrypted_value: Vec<u8>,
    nonce: Vec<u8>,
    version: i32,
    is_active: bool,
}

impl From<CredRow> for EncryptedCredential {
    fn from(r: CredRow) -> Self {
        Self {
            provider_id: r.provider_id,
            credential_key: r.credential_key,
            encrypted_value: r.encrypted_value,
            nonce: r.nonce,
            version: r.version,
            is_active: r.is_active,
        }
    }
}

#[derive(sqlx::FromRow)]
struct CredMetaRow {
    provider_id: String,
    credential_key: String,
    version: i32,
    is_active: bool,
}

impl From<CredMetaRow> for CredentialMeta {
    fn from(r: CredMetaRow) -> Self {
        Self {
            provider_id: r.provider_id,
            credential_key: r.credential_key,
            version: r.version,
            is_active: r.is_active,
        }
    }
}

#[derive(sqlx::FromRow)]
struct UsageRow {
    id: i64,
    user_id: String,
    capability_id: String,
    resource_type: String,
    amount: i64,
    created_at: String,
}

impl From<UsageRow> for domain::models::UsageRecord {
    fn from(r: UsageRow) -> Self {
        Self {
            id: r.id.to_string(),
            user_id: r.user_id,
            capability_id: r.capability_id,
            resource_type: r.resource_type,
            amount: r.amount,
            recorded_at: chrono::DateTime::parse_from_rfc3339(&r.created_at)
                .map(|d| d.with_timezone(&chrono::Utc))
                .unwrap_or_else(|_| chrono::Utc::now()),
        }
    }
}

#[derive(sqlx::FromRow)]
struct AuditLogRow {
    id: i64,
    user_id: Option<String>,
    action: String,
    detail: Option<String>,
    ip_address: Option<String>,
    created_at: String,
}

#[cfg(test)]
mod tests {
    use super::*;
    use domain::models::*;

    async fn setup_test_db() -> SqliteBackend {
        let backend = SqliteBackend::new(":memory:").await.unwrap();
        backend.initialize().await.unwrap();
        backend
    }

    #[tokio::test]
    async fn test_initialize() {
        let db = setup_test_db().await;
        assert!(db.is_initialized().await.is_ok());
    }

    #[tokio::test]
    async fn test_config_set_get() {
        let db = setup_test_db().await;
        db.set_config("test.key", "test.value").await.unwrap();
        let val = db.get_config("test.key").await.unwrap();
        assert_eq!(val, Some("test.value".to_string()));
    }

    #[tokio::test]
    async fn test_config_get_missing() {
        let db = setup_test_db().await;
        let val = db.get_config("nonexistent").await.unwrap();
        assert_eq!(val, None);
    }

    #[tokio::test]
    async fn test_user_upsert_and_get() {
        let db = setup_test_db().await;
        let user = User {
            id: "u1".into(),
            username: Some("testuser".into()),
            display_name: "Test User".into(),
            email: Some("test@example.com".into()),
            role: UserRole::User,
            plan_id: "free".into(),
            is_active: true,
            auth_provider: AuthProvider::Local,
            tenant_id: "default".into(),
            first_seen_at: chrono::Utc::now(),
            last_seen_at: chrono::Utc::now(),
        };
        db.upsert_user(&user).await.unwrap();
        let fetched = db.get_user("u1").await.unwrap().unwrap();
        assert_eq!(fetched.display_name, "Test User");
        assert_eq!(fetched.username, Some("testuser".into()));
    }

    #[tokio::test]
    async fn test_user_by_username() {
        let db = setup_test_db().await;
        let user = User {
            id: "u2".into(),
            username: Some("alice".into()),
            display_name: "Alice".into(),
            email: None,
            role: UserRole::Admin,
            plan_id: "pro".into(),
            is_active: true,
            auth_provider: AuthProvider::Local,
            tenant_id: "default".into(),
            first_seen_at: chrono::Utc::now(),
            last_seen_at: chrono::Utc::now(),
        };
        db.upsert_user(&user).await.unwrap();
        let fetched = db.get_user_by_username("alice").await.unwrap().unwrap();
        assert_eq!(fetched.id, "u2");
    }

    #[tokio::test]
    async fn test_list_users() {
        let db = setup_test_db().await;
        for i in 0..5 {
            let user = User {
                id: format!("u{i}"),
                username: Some(format!("user{i}")),
                display_name: format!("User {i}"),
                email: None,
                role: UserRole::User,
                plan_id: "free".into(),
                is_active: true,
                auth_provider: AuthProvider::Local,
                tenant_id: "default".into(),
                first_seen_at: chrono::Utc::now(),
                last_seen_at: chrono::Utc::now(),
            };
            db.upsert_user(&user).await.unwrap();
        }
        let users = db.list_users(0, 3).await.unwrap();
        assert_eq!(users.len(), 3);
        let users = db.list_users(3, 10).await.unwrap();
        assert_eq!(users.len(), 2);
    }

    #[tokio::test]
    async fn test_count_users() {
        let db = setup_test_db().await;
        assert_eq!(db.count_users().await.unwrap(), 0);
        let user = User {
            id: "u1".into(),
            username: None,
            display_name: "Test".into(),
            email: None,
            role: UserRole::User,
            plan_id: "free".into(),
            is_active: true,
            auth_provider: AuthProvider::Local,
            tenant_id: "default".into(),
            first_seen_at: chrono::Utc::now(),
            last_seen_at: chrono::Utc::now(),
        };
        db.upsert_user(&user).await.unwrap();
        assert_eq!(db.count_users().await.unwrap(), 1);
    }

    #[tokio::test]
    async fn test_capabilities_default() {
        let db = setup_test_db().await;
        let caps = db.get_capabilities().await.unwrap();
        assert!(caps.len() >= 7);
    }

    #[tokio::test]
    async fn test_plans_default() {
        let db = setup_test_db().await;
        let plans = db.list_plans().await.unwrap();
        assert_eq!(plans.len(), 4);
    }

    #[tokio::test]
    async fn test_plan_get() {
        let db = setup_test_db().await;
        let plan = db.get_plan("free").await.unwrap().unwrap();
        assert_eq!(plan.display_name, "Free");
    }

    #[tokio::test]
    async fn test_health_check() {
        let db = setup_test_db().await;
        assert!(db.health_check().await.is_ok());
    }

    #[tokio::test]
    async fn test_password_hash() {
        let db = setup_test_db().await;
        let user = User {
            id: "u1".into(),
            username: Some("testuser".into()),
            display_name: "Test".into(),
            email: None,
            role: UserRole::User,
            plan_id: "free".into(),
            is_active: true,
            auth_provider: AuthProvider::Local,
            tenant_id: "default".into(),
            first_seen_at: chrono::Utc::now(),
            last_seen_at: chrono::Utc::now(),
        };
        db.upsert_user(&user).await.unwrap();
        db.set_password_hash("u1", "$argon2id$hash123").await.unwrap();
        let hash = db.get_password_hash("u1").await.unwrap().unwrap();
        assert_eq!(hash, "$argon2id$hash123");
    }

    #[tokio::test]
    async fn test_audit_log() {
        let db = setup_test_db().await;
        assert!(db.write_audit_log("u1", "test.action", Some("detail"), Some("127.0.0.1")).await.is_ok());
    }

    #[tokio::test]
    async fn test_list_audit_logs() {
        let db = setup_test_db().await;
        db.write_audit_log("u1", "action1", Some("detail1"), Some("127.0.0.1")).await.unwrap();
        db.write_audit_log("u2", "action2", None, None).await.unwrap();
        let logs = db.list_audit_logs(0, 10).await.unwrap();
        assert_eq!(logs.len(), 2);
        let logs = db.list_audit_logs(0, 1).await.unwrap();
        assert_eq!(logs.len(), 1);
    }
}
