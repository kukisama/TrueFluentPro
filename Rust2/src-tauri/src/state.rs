use std::sync::Arc;
use tokio::sync::RwLock;

use crate::models::AppConfig;
use crate::providers::ProviderRegistry;
use crate::storage::Database;

/// 全局应用状态，通过 Tauri State 注入
pub struct AppState {
    pub config: RwLock<AppConfig>,
    pub db: Arc<Database>,
    pub providers: RwLock<ProviderRegistry>,
}

impl AppState {
    pub fn new(db: Database) -> Self {
        // 从数据库加载保存的配置，不存在则用默认值
        let config = db
            .kv_get("app_config")
            .ok()
            .flatten()
            .and_then(|json| serde_json::from_str::<AppConfig>(&json).ok())
            .unwrap_or_default();

        Self {
            config: RwLock::new(config),
            db: Arc::new(db),
            providers: RwLock::new(ProviderRegistry::new()),
        }
    }

    /// 保存配置到 SQLite
    pub async fn persist_config(&self) -> Result<(), String> {
        let config = self.config.read().await;
        let json = serde_json::to_string(&*config).map_err(|e| e.to_string())?;
        self.db.kv_set("app_config", &json).map_err(|e| e.to_string())
    }
}
