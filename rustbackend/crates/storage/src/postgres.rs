//! PostgreSQL storage backend implementation.
//!
//! Activated when `database.url` starts with `postgres://` or `postgresql://`.
//! Suitable for production / HA deployments with connection pooling.

use crate::{StorageBackend, StorageResult, EncryptedCredential, CredentialMeta};
use crate::error::StorageError;
use domain::models::*;
use sqlx::postgres::{PgPool, PgPoolOptions};
use tracing::info;

pub struct PostgresBackend {
    pool: PgPool,
}

impl PostgresBackend {
    pub async fn new(database_url: &str, max_connections: u32) -> Result<Self, StorageError> {
        let pool = PgPoolOptions::new()
            .max_connections(max_connections)
            .connect(database_url)
            .await
            .map_err(|e| StorageError::Internal(format!("PostgreSQL connection failed: {e}")))?;

        Ok(Self { pool })
    }

    /// Run all migration SQL inline.
    async fn run_migrations(&self) -> StorageResult<()> {
        let migration_sql = include_str!("migrations/001_initial_pg.sql");
        for statement in migration_sql.split(';') {
            let s = statement.trim();
            if !s.is_empty() {
                sqlx::query(s).execute(&self.pool).await.map_err(|e| {
                    StorageError::Migration(format!("PG migration failed: {e} — SQL: {s}"))
                })?;
            }
        }
        info!("PostgreSQL migrations complete");
        Ok(())
    }
}

#[async_trait::async_trait]
impl StorageBackend for PostgresBackend {
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
            sqlx::query_as("SELECT value FROM system_config WHERE key = $1")
                .bind(key)
                .fetch_optional(&self.pool)
                .await?;
        Ok(row.map(|r| r.0))
    }

    async fn set_config(&self, key: &str, value: &str) -> StorageResult<()> {
        sqlx::query(
            "INSERT INTO system_config (key, value) VALUES ($1, $2) \
             ON CONFLICT(key) DO UPDATE SET value = EXCLUDED.value",
        )
        .bind(key)
        .bind(value)
        .execute(&self.pool)
        .await?;
        Ok(())
    }

    // ─── Users ───

    async fn get_user(&self, id: &str) -> StorageResult<Option<User>> {
        let row: Option<PgUserRow> = sqlx::query_as(
            "SELECT id, username, display_name, email, role, plan_id, is_active, \
             auth_provider, tenant_id, first_seen_at, last_seen_at FROM users WHERE id = $1",
        )
        .bind(id)
        .fetch_optional(&self.pool)
        .await?;
        Ok(row.map(Into::into))
    }

    async fn get_user_by_username(&self, username: &str) -> StorageResult<Option<User>> {
        let row: Option<PgUserRow> = sqlx::query_as(
            "SELECT id, username, display_name, email, role, plan_id, is_active, \
             auth_provider, tenant_id, first_seen_at, last_seen_at \
             FROM users WHERE username = $1",
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
             VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11) \
             ON CONFLICT(id) DO UPDATE SET \
             display_name = EXCLUDED.display_name, \
             email = EXCLUDED.email, \
             role = EXCLUDED.role, \
             plan_id = EXCLUDED.plan_id, \
             is_active = EXCLUDED.is_active, \
             last_seen_at = EXCLUDED.last_seen_at",
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
        .bind(user.first_seen_at)
        .bind(user.last_seen_at)
        .execute(&self.pool)
        .await?;
        Ok(())
    }

    async fn list_users(&self, offset: i64, limit: i64) -> StorageResult<Vec<User>> {
        let rows: Vec<PgUserRow> = sqlx::query_as(
            "SELECT id, username, display_name, email, role, plan_id, is_active, \
             auth_provider, tenant_id, first_seen_at, last_seen_at \
             FROM users ORDER BY first_seen_at DESC LIMIT $1 OFFSET $2",
        )
        .bind(limit)
        .bind(offset)
        .fetch_all(&self.pool)
        .await?;
        Ok(rows.into_iter().map(Into::into).collect())
    }

    async fn update_user_last_seen(&self, id: &str) -> StorageResult<()> {
        sqlx::query("UPDATE users SET last_seen_at = NOW() WHERE id = $1")
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
        let rows: Vec<PgCapabilityRow> = sqlx::query_as(
            "SELECT id, display_name, category, is_enabled, description FROM capabilities",
        )
        .fetch_all(&self.pool)
        .await?;
        Ok(rows.into_iter().map(Into::into).collect())
    }

    async fn upsert_capability(&self, cap: &Capability) -> StorageResult<()> {
        sqlx::query(
            "INSERT INTO capabilities (id, display_name, category, is_enabled, description) \
             VALUES ($1, $2, $3, $4, $5) \
             ON CONFLICT(id) DO UPDATE SET \
             display_name = EXCLUDED.display_name, \
             is_enabled = EXCLUDED.is_enabled, \
             description = EXCLUDED.description",
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
        let rows: Vec<PgProviderRow> = sqlx::query_as(
            "SELECT id, vendor, display_name, is_enabled, config_json FROM providers",
        )
        .fetch_all(&self.pool)
        .await?;
        Ok(rows.into_iter().map(Into::into).collect())
    }

    async fn upsert_provider(&self, provider: &ProviderInfo) -> StorageResult<()> {
        let config_str = provider.config_json.as_ref().map(|v| v.to_string());
        sqlx::query(
            "INSERT INTO providers (id, vendor, display_name, is_enabled, config_json) \
             VALUES ($1, $2, $3, $4, $5) \
             ON CONFLICT(id) DO UPDATE SET \
             display_name = EXCLUDED.display_name, \
             is_enabled = EXCLUDED.is_enabled, \
             config_json = EXCLUDED.config_json",
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
        let row: Option<PgPlanRow> = sqlx::query_as(
            "SELECT id, display_name, price_monthly, limits_json, is_active \
             FROM subscription_plans WHERE id = $1",
        )
        .bind(id)
        .fetch_optional(&self.pool)
        .await?;
        Ok(row.map(Into::into))
    }

    async fn list_plans(&self) -> StorageResult<Vec<SubscriptionPlan>> {
        let rows: Vec<PgPlanRow> = sqlx::query_as(
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
             VALUES ($1, $2, $3, $4, $5) \
             ON CONFLICT(id) DO UPDATE SET \
             display_name = EXCLUDED.display_name, \
             price_monthly = EXCLUDED.price_monthly, \
             limits_json = EXCLUDED.limits_json, \
             is_active = EXCLUDED.is_active",
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
        let row: Option<PgCredRow> = sqlx::query_as(
            "SELECT provider_id, credential_key, encrypted_value, nonce, version, is_active \
             FROM provider_credentials \
             WHERE provider_id = $1 AND credential_key = $2 AND is_active = TRUE \
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
             VALUES ($1, $2, $3, $4, $5, $6) \
             ON CONFLICT(provider_id, credential_key, version) DO UPDATE SET \
             encrypted_value = EXCLUDED.encrypted_value, \
             nonce = EXCLUDED.nonce, \
             is_active = EXCLUDED.is_active",
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
        let rows: Vec<PgCredMetaRow> = sqlx::query_as(
            "SELECT provider_id, credential_key, version, is_active \
             FROM provider_credentials WHERE provider_id = $1",
        )
        .bind(provider_id)
        .fetch_all(&self.pool)
        .await?;
        Ok(rows.into_iter().map(Into::into).collect())
    }

    // ─── Password ───

    async fn get_password_hash(&self, user_id: &str) -> StorageResult<Option<String>> {
        let row: Option<(String,)> =
            sqlx::query_as("SELECT password_hash FROM user_passwords WHERE user_id = $1")
                .bind(user_id)
                .fetch_optional(&self.pool)
                .await?;
        Ok(row.map(|r| r.0))
    }

    async fn set_password_hash(&self, user_id: &str, hash: &str) -> StorageResult<()> {
        sqlx::query(
            "INSERT INTO user_passwords (user_id, password_hash) VALUES ($1, $2) \
             ON CONFLICT(user_id) DO UPDATE SET password_hash = EXCLUDED.password_hash",
        )
        .bind(user_id)
        .bind(hash)
        .execute(&self.pool)
        .await?;
        Ok(())
    }

    // ─── Usage Tracking ───

    async fn record_usage(&self, user_id: &str, capability_id: &str, resource_type: &str, amount: i64) -> StorageResult<()> {
        let year_month = chrono::Utc::now().format("%Y-%m").to_string();

        // Insert usage event
        sqlx::query(
            "INSERT INTO usage_events (user_id, capability_id, resource_type, amount) \
             VALUES ($1, $2, $3, $4)",
        )
        .bind(user_id)
        .bind(capability_id)
        .bind(resource_type)
        .bind(amount)
        .execute(&self.pool)
        .await?;

        // Upsert monthly summary
        sqlx::query(
            "INSERT INTO monthly_usage (user_id, resource_type, year_month, total_amount) \
             VALUES ($1, $2, $3, $4) \
             ON CONFLICT(user_id, resource_type, year_month) \
             DO UPDATE SET total_amount = monthly_usage.total_amount + EXCLUDED.total_amount, \
             updated_at = NOW()",
        )
        .bind(user_id)
        .bind(resource_type)
        .bind(&year_month)
        .bind(amount)
        .execute(&self.pool)
        .await?;

        Ok(())
    }

    async fn get_usage_total(&self, user_id: &str, resource_type: &str, since: chrono::DateTime<chrono::Utc>) -> StorageResult<i64> {
        let row: Option<(i64,)> = sqlx::query_as(
            "SELECT COALESCE(SUM(amount), 0) FROM usage_events \
             WHERE user_id = $1 AND resource_type = $2 AND created_at >= $3",
        )
        .bind(user_id)
        .bind(resource_type)
        .bind(since)
        .fetch_optional(&self.pool)
        .await?;
        Ok(row.map(|r| r.0).unwrap_or(0))
    }

    async fn get_usage_records(&self, user_id: &str, offset: i64, limit: i64) -> StorageResult<Vec<UsageRecord>> {
        let rows: Vec<PgUsageRow> = sqlx::query_as(
            "SELECT id, user_id, capability_id, resource_type, amount, created_at \
             FROM usage_events WHERE user_id = $1 ORDER BY created_at DESC LIMIT $2 OFFSET $3",
        )
        .bind(user_id)
        .bind(limit)
        .bind(offset)
        .fetch_all(&self.pool)
        .await?;
        Ok(rows.into_iter().map(Into::into).collect())
    }

    // ─── Audit Log ───

    async fn write_audit_log(&self, user_id: &str, action: &str, detail: Option<&str>, ip_address: Option<&str>) -> StorageResult<()> {
        sqlx::query("INSERT INTO audit_log (user_id, action, detail, ip_address) VALUES ($1, $2, $3, $4)")
            .bind(user_id)
            .bind(action)
            .bind(detail)
            .bind(ip_address)
            .execute(&self.pool)
            .await
            .map_err(|e| StorageError::Internal(format!("audit_log write failed: {e}")))?;
        Ok(())
    }

    async fn list_audit_logs(&self, offset: i64, limit: i64) -> StorageResult<Vec<AuditLogEntry>> {
        let rows: Vec<PgAuditLogRow> = sqlx::query_as(
            "SELECT id, user_id, action, detail, ip_address, created_at \
             FROM audit_log ORDER BY created_at DESC LIMIT $1 OFFSET $2",
        )
        .bind(limit)
        .bind(offset)
        .fetch_all(&self.pool)
        .await?;

        Ok(rows.into_iter().map(|r| AuditLogEntry {
            id: r.id,
            user_id: r.user_id,
            action: r.action,
            detail: r.detail,
            ip_address: r.ip_address,
            created_at: r.created_at,
        }).collect())
    }
}

// ═══ Row types for sqlx (PostgreSQL) ═══

#[derive(sqlx::FromRow)]
struct PgUserRow {
    id: String,
    username: Option<String>,
    display_name: String,
    email: Option<String>,
    role: String,
    plan_id: String,
    is_active: bool,
    auth_provider: String,
    tenant_id: String,
    first_seen_at: chrono::DateTime<chrono::Utc>,
    last_seen_at: chrono::DateTime<chrono::Utc>,
}

impl From<PgUserRow> for User {
    fn from(r: PgUserRow) -> Self {
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
            first_seen_at: r.first_seen_at,
            last_seen_at: r.last_seen_at,
        }
    }
}

#[derive(sqlx::FromRow)]
struct PgCapabilityRow {
    id: String,
    display_name: String,
    category: String,
    is_enabled: bool,
    description: Option<String>,
}

impl From<PgCapabilityRow> for Capability {
    fn from(r: PgCapabilityRow) -> Self {
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
struct PgProviderRow {
    id: String,
    vendor: String,
    display_name: String,
    is_enabled: bool,
    config_json: Option<String>,
}

impl From<PgProviderRow> for ProviderInfo {
    fn from(r: PgProviderRow) -> Self {
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
struct PgPlanRow {
    id: String,
    display_name: String,
    price_monthly: Option<f64>,
    limits_json: String,
    is_active: bool,
}

impl From<PgPlanRow> for SubscriptionPlan {
    fn from(r: PgPlanRow) -> Self {
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
struct PgCredRow {
    provider_id: String,
    credential_key: String,
    encrypted_value: Vec<u8>,
    nonce: Vec<u8>,
    version: i32,
    is_active: bool,
}

impl From<PgCredRow> for EncryptedCredential {
    fn from(r: PgCredRow) -> Self {
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
struct PgCredMetaRow {
    provider_id: String,
    credential_key: String,
    version: i32,
    is_active: bool,
}

impl From<PgCredMetaRow> for CredentialMeta {
    fn from(r: PgCredMetaRow) -> Self {
        Self {
            provider_id: r.provider_id,
            credential_key: r.credential_key,
            version: r.version,
            is_active: r.is_active,
        }
    }
}

#[derive(sqlx::FromRow)]
struct PgUsageRow {
    id: i64,
    user_id: String,
    capability_id: String,
    resource_type: String,
    amount: i64,
    created_at: chrono::DateTime<chrono::Utc>,
}

impl From<PgUsageRow> for UsageRecord {
    fn from(r: PgUsageRow) -> Self {
        Self {
            id: r.id.to_string(),
            user_id: r.user_id,
            capability_id: r.capability_id,
            resource_type: r.resource_type,
            amount: r.amount,
            recorded_at: r.created_at,
        }
    }
}

#[derive(sqlx::FromRow)]
struct PgAuditLogRow {
    id: i64,
    user_id: Option<String>,
    action: String,
    detail: Option<String>,
    ip_address: Option<String>,
    created_at: chrono::DateTime<chrono::Utc>,
}
