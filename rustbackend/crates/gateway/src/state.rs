//! Application state — shared across all handlers.

use domain::config::GatewayConfig;
use storage::StorageBackend;
use storage::sqlite::SqliteBackend;
use cache::CacheBackend;
use cache::memory::InMemoryCache;
use billing::{BillingEngine, DisabledBillingEngine};
use credential_broker::CredentialBroker;
use std::sync::Arc;
use tracing::info;

pub struct AppState {
    pub config: GatewayConfig,
    pub storage: Arc<dyn StorageBackend>,
    pub cache: Arc<dyn CacheBackend>,
    pub billing: Arc<dyn BillingEngine>,
    pub credentials: Arc<CredentialBroker>,
    pub jwt_secret: String,
}

impl AppState {
    pub async fn build(config: GatewayConfig) -> anyhow::Result<Self> {
        // ─── Storage ───
        let storage: Arc<dyn StorageBackend> = if config.database.url.is_empty() {
            let db_path = format!("{}/truefluentpro.db", config.storage.data_dir);
            info!("Using SQLite database: {db_path}");
            let backend = SqliteBackend::new(&db_path).await?;
            Arc::new(backend)
        } else {
            // Future: PostgreSQL support
            anyhow::bail!("PostgreSQL not yet implemented — leave DATABASE_URL empty for SQLite");
        };

        // Initialize database (run migrations)
        storage.initialize().await
            .map_err(|e| anyhow::anyhow!("database initialization failed: {e}"))?;

        // ─── Cache ───
        let cache: Arc<dyn CacheBackend> = match config.cache.mode.as_str() {
            "redis" => {
                anyhow::bail!("Redis cache not yet implemented — use 'memory' mode");
            }
            _ => {
                info!("Using in-memory cache");
                Arc::new(InMemoryCache::new())
            }
        };

        // ─── Billing ───
        let billing: Arc<dyn BillingEngine> = if config.billing.enabled {
            // Future: ActiveBillingEngine
            info!("Billing: enabled (using disabled stub for now)");
            Arc::new(DisabledBillingEngine)
        } else {
            info!("Billing: disabled");
            Arc::new(DisabledBillingEngine)
        };

        // ─── Credentials ───
        let credentials = Arc::new(CredentialBroker::new(
            storage.clone(),
            &config.credentials.master_key_base64,
        ));

        // ─── JWT Secret ───
        let jwt_secret = if config.auth.local.jwt_secret.is_empty() {
            // Auto-generate if not configured
            let secret = generate_random_secret();
            info!("Auto-generated JWT secret (not persisted — set auth.local.jwt_secret for stability)");
            secret
        } else {
            config.auth.local.jwt_secret.clone()
        };

        Ok(Self {
            config,
            storage,
            cache,
            billing,
            credentials,
            jwt_secret,
        })
    }
}

fn generate_random_secret() -> String {
    use rand::RngCore;
    let mut bytes = [0u8; 32];
    rand::thread_rng().fill_bytes(&mut bytes);
    use base64::Engine;
    base64::engine::general_purpose::STANDARD.encode(bytes)
}
