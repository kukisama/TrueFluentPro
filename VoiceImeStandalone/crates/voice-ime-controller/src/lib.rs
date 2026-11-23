//! 界面业务控制器层（供 Tauri 2 IPC 薄壳调用，严格遵循关注点分离）。

pub mod install_ctrl;
pub mod language_ctrl;
pub mod settings_ctrl;
pub mod usage_ctrl;

use std::path::PathBuf;
use voice_ime_config::ConfigStore;
use voice_ime_speech::StsTokenCache;
use voice_ime_storage::UsageReportService;
use voice_ime_win32::VoiceImeInstaller;

/// 集中持有四个子域无状态服务句柄的轻量应用上下文。
pub struct ToolsAppContext {
    pub config_store: ConfigStore,
    pub report_service: UsageReportService,
    pub sts_cache: StsTokenCache,
    pub installer: VoiceImeInstaller,
}

impl Default for ToolsAppContext {
    fn default() -> Self {
        Self::new_production()
    }
}

impl ToolsAppContext {
    pub fn new_production() -> Self {
        Self {
            config_store: ConfigStore::default_store(),
            report_service: UsageReportService::default_service(),
            sts_cache: StsTokenCache::new(),
            installer: VoiceImeInstaller::from_current_exe(),
        }
    }

    pub fn with_paths(config_path: PathBuf, db_path: PathBuf, app_base_dir: PathBuf) -> Self {
        Self {
            config_store: ConfigStore::new(config_path),
            report_service: UsageReportService::new(db_path),
            sts_cache: StsTokenCache::new(),
            installer: VoiceImeInstaller::new(app_base_dir),
        }
    }
}
