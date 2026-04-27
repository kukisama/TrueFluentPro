use std::collections::HashMap;
use std::sync::Arc;
use tokio::sync::RwLock;

use crate::image_pipeline::file_cache::FileIdCache;
use crate::models::AppConfig;
use crate::providers::{ProviderRegistry, RealtimeSessionHandle};
use crate::storage::Database;
use crate::task_engine::TaskEngine;

/// 全局应用状态，通过 Tauri State 注入
pub struct AppState {
    pub config: RwLock<AppConfig>,
    pub db: Arc<Database>,
    pub providers: RwLock<ProviderRegistry>,
    /// 活跃的实时语音翻译会话（session_id → handle）
    pub active_speech_sessions: RwLock<HashMap<String, Box<dyn RealtimeSessionHandle>>>,
    /// 后台任务引擎
    pub task_engine: RwLock<Option<TaskEngine>>,
    /// P3-6: 文件上传去重缓存
    pub file_id_cache: Arc<FileIdCache>,
    /// AAD refresh_token 缓存（endpoint_id → refresh_token）— 用于租户切换和 token 刷新
    pub refresh_tokens: RwLock<HashMap<String, String>>,
}

impl AppState {
    pub fn new(db: Database) -> Self {
        // 从数据库加载保存的配置，不存在则用默认值（使用 blocking 版本，此时无并发）
        let mut config = db
            .kv_get_blocking("app_config")
            .ok()
            .flatten()
            .and_then(|json| serde_json::from_str::<AppConfig>(&json).ok())
            .unwrap_or_default();

        // 迁移遗留的 "auto" auth_header_mode 为明确值
        for ep in &mut config.endpoints {
            ep.migrate_auth_header_mode();
        }

        // 从数据库加载持久化的 refresh_tokens
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
            task_engine: RwLock::new(None),
            file_id_cache: Arc::new(FileIdCache::new()),
            refresh_tokens: RwLock::new(refresh_tokens),
        }
    }

    /// 保存配置到 SQLite
    pub async fn persist_config(&self) -> Result<(), String> {
        let config = self.config.read().await;
        let json = serde_json::to_string(&*config).map_err(|e| e.to_string())?;
        self.db.kv_set("app_config", &json).await.map_err(|e| e.to_string())
    }

    /// 持久化 refresh_tokens 到 SQLite
    pub async fn persist_refresh_tokens(&self) -> Result<(), String> {
        let tokens = self.refresh_tokens.read().await;
        let json = serde_json::to_string(&*tokens).map_err(|e| e.to_string())?;
        self.db.kv_set("aad_refresh_tokens", &json).await.map_err(|e| e.to_string())
    }
}
