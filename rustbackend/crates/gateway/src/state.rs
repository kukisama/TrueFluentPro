//! Application state — shared across all handlers.

use domain::config::GatewayConfig;
use storage::StorageBackend;
use storage::sqlite::SqliteBackend;
use cache::CacheBackend;
use cache::memory::InMemoryCache;
use billing::{BillingEngine, DisabledBillingEngine, ActiveBillingEngine};
use credential_broker::CredentialBroker;
use providers::registry::ProviderRegistry;
use std::sync::Arc;
use tokio::sync::RwLock;
use tracing::info;

pub struct AppState {
    pub config: GatewayConfig,
    pub storage: Arc<dyn StorageBackend>,
    pub cache: Arc<dyn CacheBackend>,
    pub billing: Arc<dyn BillingEngine>,
    pub credentials: Arc<CredentialBroker>,
    pub providers: Arc<RwLock<ProviderRegistry>>,
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
            info!("Billing: enabled (ActiveBillingEngine with plan-based quotas)");
            Arc::new(ActiveBillingEngine::new(storage.clone()))
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

        // ─── Provider Registry ───
        let db_providers = storage.get_providers().await
            .map_err(|e| anyhow::anyhow!("failed to load providers: {e}"))?;
        let registry = ProviderRegistry::build(&db_providers, credentials.clone());
        info!(
            chat = registry.chat_count(),
            image = registry.image_count(),
            tts = registry.tts_count(),
            translate = registry.translate_count(),
            "Provider registry initialized"
        );
        let providers = Arc::new(RwLock::new(registry));

        Ok(Self {
            config,
            storage,
            cache,
            billing,
            credentials,
            providers,
            jwt_secret,
        })
    }

    /// Reload provider registry from DB (call after admin changes providers).
    pub async fn reload_providers(&self) -> anyhow::Result<()> {
        let db_providers = self.storage.get_providers().await
            .map_err(|e| anyhow::anyhow!("failed to load providers for registry reload: {e}"))?;
        let mut reg = self.providers.write().await;
        reg.rebuild(&db_providers, self.credentials.clone());
        info!(
            chat = reg.chat_count(),
            image = reg.image_count(),
            tts = reg.tts_count(),
            translate = reg.translate_count(),
            "Provider registry reloaded"
        );
        Ok(())
    }
}

fn generate_random_secret() -> String {
    use rand::RngCore;
    let mut bytes = [0u8; 32];
    rand::thread_rng().fill_bytes(&mut bytes);
    use base64::Engine;
    base64::engine::general_purpose::STANDARD.encode(bytes)
}
