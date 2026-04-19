//! Storage crate — database abstraction layer with SQLite implementation.

pub mod sqlite;
pub mod error;

use domain::models::*;
use async_trait::async_trait;

/// Result type for storage operations.
pub type StorageResult<T> = Result<T, error::StorageError>;

/// Abstract storage backend — all database operations go through this trait.
#[async_trait]
pub trait StorageBackend: Send + Sync {
    // ─── Lifecycle ───
    async fn initialize(&self) -> StorageResult<()>;
    async fn health_check(&self) -> StorageResult<()>;
    async fn is_initialized(&self) -> StorageResult<bool>;

    // ─── System Config (KV) ───
    async fn get_config(&self, key: &str) -> StorageResult<Option<String>>;
    async fn set_config(&self, key: &str, value: &str) -> StorageResult<()>;

    // ─── Users ───
    async fn get_user(&self, id: &str) -> StorageResult<Option<User>>;
    async fn get_user_by_username(&self, username: &str) -> StorageResult<Option<User>>;
    async fn upsert_user(&self, user: &User) -> StorageResult<()>;
    async fn list_users(&self, offset: i64, limit: i64) -> StorageResult<Vec<User>>;
    async fn update_user_last_seen(&self, id: &str) -> StorageResult<()>;
    async fn count_users(&self) -> StorageResult<i64>;

    // ─── Capabilities ───
    async fn get_capabilities(&self) -> StorageResult<Vec<Capability>>;
    async fn upsert_capability(&self, cap: &Capability) -> StorageResult<()>;

    // ─── Providers ───
    async fn get_providers(&self) -> StorageResult<Vec<ProviderInfo>>;
    async fn upsert_provider(&self, provider: &ProviderInfo) -> StorageResult<()>;

    // ─── Plans ───
    async fn get_plan(&self, id: &str) -> StorageResult<Option<SubscriptionPlan>>;
    async fn list_plans(&self) -> StorageResult<Vec<SubscriptionPlan>>;
    async fn upsert_plan(&self, plan: &SubscriptionPlan) -> StorageResult<()>;

    // ─── Credentials (encrypted) ───
    async fn get_credential(&self, provider_id: &str, key: &str) -> StorageResult<Option<EncryptedCredential>>;
    async fn upsert_credential(&self, cred: &EncryptedCredential) -> StorageResult<()>;
    async fn list_credentials(&self, provider_id: &str) -> StorageResult<Vec<CredentialMeta>>;

    // ─── Password hash (local auth) ───
    async fn get_password_hash(&self, user_id: &str) -> StorageResult<Option<String>>;
    async fn set_password_hash(&self, user_id: &str, hash: &str) -> StorageResult<()>;

    // ─── Usage Tracking (billing) ───
    async fn record_usage(&self, user_id: &str, capability_id: &str, resource_type: &str, amount: i64) -> StorageResult<()>;
    async fn get_usage_total(&self, user_id: &str, resource_type: &str, since: chrono::DateTime<chrono::Utc>) -> StorageResult<i64>;
    async fn get_usage_records(&self, user_id: &str, offset: i64, limit: i64) -> StorageResult<Vec<domain::models::UsageRecord>>;
}

/// Encrypted credential stored in DB.
#[derive(Debug, Clone)]
pub struct EncryptedCredential {
    pub provider_id: String,
    pub credential_key: String,
    pub encrypted_value: Vec<u8>,
    pub nonce: Vec<u8>,
    pub version: i32,
    pub is_active: bool,
}

/// Credential metadata (no secret values).
#[derive(Debug, Clone, serde::Serialize)]
pub struct CredentialMeta {
    pub provider_id: String,
    pub credential_key: String,
    pub version: i32,
    pub is_active: bool,
}
