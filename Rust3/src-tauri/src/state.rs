use std::collections::HashMap;
use std::sync::Arc;

use tokio::sync::RwLock;
use tfp_core::AppConfig;
use tfp_providers::{ProviderRegistry, RealtimeSessionHandle};
use tfp_storage::Database;

use crate::image_pipeline::file_cache::FileIdCache;
use crate::task_engine::TaskEngine;
use crate::task_event_bus::TaskEventBus;

/// Global application state, injected via Tauri State
pub struct AppState {
    pub config: RwLock<AppConfig>,
    pub db: Arc<Database>,
    pub providers: RwLock<ProviderRegistry>,
    /// Active realtime speech translation sessions (session_id -> handle)
    pub active_speech_sessions: RwLock<HashMap<String, Box<dyn RealtimeSessionHandle>>>,
    /// AAD refresh_token cache (endpoint_id → refresh_token)
    pub refresh_tokens: RwLock<HashMap<String, String>>,
    /// Background task scheduling engine
    pub task_engine: RwLock<Option<TaskEngine>>,
    /// Task event bus
    pub task_event_bus: TaskEventBus,
    /// File upload deduplication cache for image pipeline
    pub file_id_cache: Arc<FileIdCache>,
}

impl AppState {
    pub fn new(db: Database) -> Self {
        let mut config = db
            .kv_get_blocking("app_config")
            .ok()
            .flatten()
            .and_then(|json| serde_json::from_str::<AppConfig>(&json).ok())
            .unwrap_or_default();

        for ep in &mut config.endpoints {
            ep.migrate_auth_header_mode();
        }

        let refresh_tokens: HashMap<String, String> = db
            .kv_get_blocking("aad_refresh_tokens")
            .ok()
            .flatten()
            .and_then(|json| serde_json::from_str(&json).ok())
            .unwrap_or_default();

        Self {
            config: RwLock::new(config),
            db: Arc::new(db),
            providers: RwLock::new(ProviderRegistry::new()),
            active_speech_sessions: RwLock::new(HashMap::new()),
            refresh_tokens: RwLock::new(refresh_tokens),
            task_engine: RwLock::new(None),
            task_event_bus: TaskEventBus::new(),
            file_id_cache: Arc::new(FileIdCache::new()),
        }
    }

    /// Persist current config to SQLite kv_store
    pub async fn persist_config(&self) -> Result<(), String> {
        let config = self.config.read().await;
        let json = serde_json::to_string(&*config).map_err(|e| e.to_string())?;
        self.db
            .kv_set("app_config", &json)
            .await
            .map_err(|e| e.to_string())
    }

    /// Persist refresh_tokens to SQLite kv_store
    pub async fn persist_refresh_tokens(&self) -> Result<(), String> {
        let tokens = self.refresh_tokens.read().await;
        let json = serde_json::to_string(&*tokens).map_err(|e| e.to_string())?;
        self.db
            .kv_set("aad_refresh_tokens", &json)
            .await
            .map_err(|e| e.to_string())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_new_empty_db() {
        let db = Database::open_in_memory().unwrap();
        let state = AppState::new(db);
        let config = state.config.blocking_read();
        assert!(config.endpoints.is_empty());
        assert_eq!(config.default_source_lang, "zh-Hans");
        assert_eq!(config.default_target_langs, vec!["en"]);
        let tokens = state.refresh_tokens.blocking_read();
        assert!(tokens.is_empty());
        assert!(state.task_engine.blocking_read().is_none());
    }

    #[tokio::test(flavor = "multi_thread", worker_threads = 2)]
    async fn test_persist_and_reload() {
        // AppState::new uses blocking_lock, so run it off the async thread
        let state = tokio::task::spawn_blocking(|| {
            let db = Database::open_in_memory().unwrap();
            AppState::new(db)
        })
        .await
        .unwrap();

        {
            let mut config = state.config.write().await;
            config.default_source_lang = "en".into();
        }
        state.persist_config().await.unwrap();

        // Reload from DB and verify
        let json = state.db.kv_get("app_config").await.unwrap().unwrap();
        let reloaded: AppConfig = serde_json::from_str(&json).unwrap();
        assert_eq!(reloaded.default_source_lang, "en");
    }
}
