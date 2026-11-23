//! 独立配置文件的原子读写、`.bak` 备份自愈、局部语言字段 Patch 与内存热重载缓存。

use std::fs;
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::time::SystemTime;

use serde_json::{Map, Value};
use thiserror::Error;

use crate::model::{LanguageDraft, StandaloneConfig};
use crate::secret;

const MAX_CONFIG_FILE_BYTES: u64 = 4 * 1024 * 1024;

#[derive(Debug, Error)]
pub enum ConfigError {
    #[error("配置文件过大或损坏")]
    CorruptedOrTooLarge,
    #[error("配置验证失败: {0}")]
    Validation(String),
    #[error("读写配置文件失败: {0}")]
    Io(String),
}

/// 独立版默认应用数据根目录：`%LocalAppData%\TrueFluentVoiceIme`。
pub fn default_standalone_data_dir() -> PathBuf {
    dirs::data_local_dir()
        .or_else(dirs::config_dir)
        .unwrap_or_else(|| PathBuf::from("."))
        .join("TrueFluentVoiceIme")
}

#[derive(Debug, Clone)]
pub struct ConfigStore {
    config_path: PathBuf,
    backup_path: PathBuf,
}

impl ConfigStore {
    pub fn new(config_path: impl Into<PathBuf>) -> Self {
        let config_path = config_path.into();
        let mut backup_os = config_path.as_os_str().to_os_string();
        backup_os.push(".bak");
        Self {
            config_path,
            backup_path: PathBuf::from(backup_os),
        }
    }

    pub fn default_store() -> Self {
        Self::new(default_standalone_data_dir().join("voice-ime-config.json"))
    }

    pub fn path(&self) -> &Path {
        &self.config_path
    }

    /// 守护进程专用只读加载：文件不存在时返回默认内存对象，**绝不在后台擅自写盘**。
    pub fn load_readonly(&self) -> Result<StandaloneConfig, ConfigError> {
        if !self.config_path.exists() {
            if self.backup_path.exists() {
                return read_typed_config(&self.backup_path);
            }
            return Ok(StandaloneConfig::default());
        }
        match read_typed_config(&self.config_path) {
            Ok(cfg) => Ok(cfg),
            Err(primary_err) => {
                if self.backup_path.exists() {
                    if let Ok(backup_cfg) = read_typed_config(&self.backup_path) {
                        return Ok(backup_cfg);
                    }
                }
                Err(primary_err)
            }
        }
    }

    /// 完整原子保存（自动加密明文订阅密钥、保留原 JSON 中未来扩展的未知顶层字段、写 `.bak` 备份）。
    pub fn save(&self, config: &StandaloneConfig) -> Result<(), ConfigError> {
        let mut normalized = config.clone();
        for sub in &mut normalized.subscriptions {
            if !sub.encrypted_key.trim().is_empty() && !secret::is_protected_secret(&sub.encrypted_key) {
                if let Ok(enc) = secret::protect_secret(sub.encrypted_key.trim()) {
                    sub.encrypted_key = enc;
                }
            }
        }

        let mut base_obj = self
            .read_raw_json_object()
            .unwrap_or_else(|_| Map::new());
        let serialized = serde_json::to_value(&normalized)
            .map_err(|e| ConfigError::Io(e.to_string()))?;
        if let Value::Object(fields) = serialized {
            for (k, v) in fields {
                base_obj.insert(k, v);
            }
        }
        self.write_json_object_atomically(&base_obj)
    }

    /// 语言页签专用局部 Patch：重读最新磁盘 JSON，仅修改 `recognitionLanguage`、`enableTranslation`、`targetLanguage`，
    /// 保留并发修改的其他设置字段与未知字段（对齐 `VoiceImeLanguagePage` 契约）。
    pub fn patch_language_fields(&self, draft: &LanguageDraft) -> Result<StandaloneConfig, ConfigError> {
        if draft.enable_translation && draft.target_language.trim().eq_ignore_ascii_case("auto") {
            return Err(ConfigError::Validation(
                "开启翻译时目标语言不能为自动检测。".into(),
            ));
        }
        let mut obj = match self.read_raw_json_object() {
            Ok(existing) => existing,
            Err(_) => {
                let Value::Object(default_map) = serde_json::to_value(StandaloneConfig::default())
                    .map_err(|e| ConfigError::Io(e.to_string()))?
                else {
                    unreachable!()
                };
                default_map
            }
        };
        let src = if draft.recognition_language.trim().is_empty() {
            "auto".to_string()
        } else {
            draft.recognition_language.trim().to_string()
        };
        let tgt = if draft.target_language.trim().is_empty() {
            "en-US".to_string()
        } else {
            draft.target_language.trim().to_string()
        };
        obj.insert("recognitionLanguage".into(), Value::String(src));
        obj.insert("enableTranslation".into(), Value::Bool(draft.enable_translation));
        obj.insert("targetLanguage".into(), Value::String(tgt));

        self.write_json_object_atomically(&obj)?;
        self.load_readonly()
    }

    fn read_raw_json_object(&self) -> Result<Map<String, Value>, ConfigError> {
        if !self.config_path.exists() {
            return Ok(Map::new());
        }
        let meta = fs::metadata(&self.config_path).map_err(|e| ConfigError::Io(e.to_string()))?;
        if meta.len() > MAX_CONFIG_FILE_BYTES {
            return Err(ConfigError::CorruptedOrTooLarge);
        }
        let bytes = fs::read(&self.config_path).map_err(|e| ConfigError::Io(e.to_string()))?;
        let val: Value = serde_json::from_slice(&bytes).map_err(|_| ConfigError::CorruptedOrTooLarge)?;
        match val {
            Value::Object(map) => Ok(map),
            _ => Err(ConfigError::CorruptedOrTooLarge),
        }
    }

    fn write_json_object_atomically(&self, obj: &Map<String, Value>) -> Result<(), ConfigError> {
        if let Some(parent) = self.config_path.parent() {
            fs::create_dir_all(parent).map_err(|e| ConfigError::Io(e.to_string()))?;
        }
        let formatted = serde_json::to_vec_pretty(&Value::Object(obj.clone()))
            .map_err(|e| ConfigError::Io(e.to_string()))?;

        let tmp_path = self.config_path.with_extension(format!(
            "json.{}.tmp",
            uuid::Uuid::new_v4().simple()
        ));
        fs::write(&tmp_path, &formatted).map_err(|e| ConfigError::Io(e.to_string()))?;

        if self.config_path.exists() {
            let _ = fs::copy(&self.config_path, &self.backup_path);
        }
        if let Err(rename_err) = fs::rename(&tmp_path, &self.config_path) {
            // Windows 上若目标存在且 rename 受限，回退 copy + remove
            fs::copy(&tmp_path, &self.config_path)
                .map_err(|_| ConfigError::Io(rename_err.to_string()))?;
            let _ = fs::remove_file(&tmp_path);
        }
        Ok(())
    }
}

fn read_typed_config(path: &Path) -> Result<StandaloneConfig, ConfigError> {
    let meta = fs::metadata(path).map_err(|e| ConfigError::Io(e.to_string()))?;
    if meta.len() > MAX_CONFIG_FILE_BYTES {
        return Err(ConfigError::CorruptedOrTooLarge);
    }
    let bytes = fs::read(path).map_err(|e| ConfigError::Io(e.to_string()))?;
    serde_json::from_slice::<StandaloneConfig>(&bytes).map_err(|_| ConfigError::CorruptedOrTooLarge)
}

/// 守护进程常驻内存配置缓存（按文件 `modified` 时间戳与字节长度自动失效热重载，消除按 F8 时的重复磁盘读取）。
pub struct RuntimeConfigCache {
    store: ConfigStore,
    state: Mutex<Option<(Option<SystemTime>, u64, StandaloneConfig)>>,
}

impl RuntimeConfigCache {
    pub fn new(store: ConfigStore) -> Self {
        Self {
            store,
            state: Mutex::new(None),
        }
    }

    pub fn get(&self) -> Result<StandaloneConfig, ConfigError> {
        let (mtime, len) = match fs::metadata(self.store.path()) {
            Ok(meta) => (meta.modified().ok(), meta.len()),
            Err(_) => (None, 0),
        };
        if let Ok(guard) = self.state.lock() {
            if let Some((cached_mtime, cached_len, ref cfg)) = *guard {
                if cached_mtime == mtime && cached_len == len {
                    return Ok(cfg.clone());
                }
            }
        }
        let fresh = self.store.load_readonly()?;
        if let Ok(mut guard) = self.state.lock() {
            *guard = Some((mtime, len, fresh.clone()));
        }
        Ok(fresh)
    }

    pub fn invalidate(&self) {
        if let Ok(mut guard) = self.state.lock() {
            *guard = None;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::endpoint::SpeechSubscription;

    #[test]
    fn patch_language_preserves_unknown_fields_and_subscriptions() {
        let dir = std::env::temp_dir().join(format!("voice_ime_cfg_{}", uuid::Uuid::new_v4()));
        fs::create_dir_all(&dir).unwrap();
        let path = dir.join("voice-ime-config.json");
        fs::write(
            &path,
            r#"{"enablePrewarm":false,"futureCustomFlag":42,"recognitionLanguage":"zh-CN"}"#,
        )
        .unwrap();

        let store = ConfigStore::new(&path);
        let updated = store
            .patch_language_fields(&LanguageDraft {
                recognition_language: "ja-JP".into(),
                enable_translation: true,
                target_language: "zh-CN".into(),
            })
            .unwrap();

        assert!(!updated.enable_prewarm);
        assert_eq!(updated.recognition_language, "ja-JP");
        assert!(updated.enable_translation);
        assert_eq!(updated.target_language, "zh-CN");

        // 未知字段 futureCustomFlag 必须原样保留
        let raw: Value = serde_json::from_slice(&fs::read(&path).unwrap()).unwrap();
        assert_eq!(raw["futureCustomFlag"], 42);

        // 添加 21V 订阅后保存并再次通过缓存热重载读取
        let mut full = updated;
        full.subscriptions.push(
            SpeechSubscription::create(
                "21V 华北三",
                "https://chinanorth3.api.cognitive.azure.cn/sts/v1.0/issuetoken",
                "0123456789abcdef0123456789abcdef",
            )
            .unwrap(),
        );
        store.save(&full).unwrap();

        let cache = RuntimeConfigCache::new(store);
        let reloaded = cache.get().unwrap();
        assert_eq!(reloaded.subscriptions.len(), 1);
        assert_eq!(reloaded.subscriptions[0].service_region, "chinanorth3");
        let _ = fs::remove_dir_all(&dir);
    }
}
