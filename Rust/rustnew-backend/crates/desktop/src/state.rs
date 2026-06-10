//! 应用运行时状态。

use std::sync::Mutex;

use tfp_core::{AppConfig, EndpointProfileCatalog};

/// 全局应用状态，由 Tauri 托管。
pub struct AppState {
    /// 当前配置（受 Mutex 保护，命令读写）。
    pub config: Mutex<AppConfig>,
    /// 内置厂商资料包目录（只读，启动时加载一次）。
    pub profile_catalog: EndpointProfileCatalog,
}

impl AppState {
    pub fn new(config: AppConfig) -> Self {
        Self {
            config: Mutex::new(config),
            profile_catalog: EndpointProfileCatalog::builtin(),
        }
    }
}
