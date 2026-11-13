//! 独立版配置与凭据管理层。
//!
//! 模块职责划分：
//! - [`endpoint`]：传统微软 Speech 终结点（中国区 21Vianet `azure.cn` 与国际版 Global `microsoft.com`）解析与路由推导；
//! - [`secret`]：Windows DPAPI (`CryptProtectData`) 订阅密钥加密存储与运行时解密；
//! - [`model`]：独立配置 `StandaloneConfig`、语言选项与运行时冻结快照；
//! - [`store`]：原子文件读写、`.bak` 备份恢复、语言三字段冲突安全 Patch 与 `mtime` 热重载缓存；
//! - [`legacy_import`]：从主程序 `TrueFluentPro` 配置文件单向只读导入微软 Speech 订阅。

pub mod endpoint;
pub mod legacy_import;
pub mod model;
pub mod secret;
pub mod store;

pub use endpoint::{
    parse_speech_endpoint, ResolvedSpeechEndpoint, SpeechCloud, SpeechEndpointError,
    SpeechSubscription,
};
pub use legacy_import::{import_from_truefluent_config_json, LegacyImportSummary};
pub use model::{
    builtin_source_languages, builtin_target_languages, LanguageDraft, LanguageOption,
    RuntimeSpeechSessionConfig, StandaloneConfig,
};
pub use store::{ConfigError, ConfigStore, RuntimeConfigCache};
