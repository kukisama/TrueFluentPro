//! 独立配置文件的原子读写、`.bak` 备份自愈、局部语言字段 Patch 与内存热重载缓存。

use std::fs;
use std::io::{self, Read, Write};
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
        decode_config(self.read_effective()?.0)
    }

    /// 完整原子保存（自动加密明文订阅密钥、保留原 JSON 中未来扩展的未知顶层字段、写 `.bak` 备份）。
    pub fn save(&self, config: &StandaloneConfig) -> Result<(), ConfigError> {
        self.update(|current| { *current = config.clone(); Ok(()) }).map(drop)
    }

    /// Serialize read-modify-write across tool windows; the dictation reader never takes this lock.
    pub fn update(&self, edit: impl FnOnce(&mut StandaloneConfig) -> Result<(), String>) -> Result<StandaloneConfig, ConfigError> {
        if let Some(parent) = self.config_path.parent() {
            fs::create_dir_all(parent).map_err(io_error)?;
        }
        let mut pathname = self.config_path.as_os_str().to_os_string();
        pathname.push(".lock");
        let lock = fs::File::options().write(true).create(true).truncate(false)
            .open(PathBuf::from(pathname)).map_err(io_error)?;
        // Do not queue indefinitely behind another window's potentially blocked disk.
        lock.try_lock().map_err(|_| ConfigError::Validation("另一窗口正在保存配置，请稍后重试。".into()))?;
        let (mut base_obj, primary) = self.read_effective()?;
        let mut config = decode_config(base_obj.clone())?;
        edit(&mut config).map_err(ConfigError::Validation)?;
        Self::normalize(&mut config)?;
        let Value::Object(fields) = serde_json::to_value(&config).map_err(|e| ConfigError::Io(e.to_string()))? else {
            return Err(ConfigError::CorruptedOrTooLarge);
        };
        base_obj.extend(fields);
        let formatted = serde_json::to_vec_pretty(&Value::Object(base_obj)).map_err(|e| ConfigError::Io(e.to_string()))?;
        if let Some(valid_primary) = primary {
            atomic_write(&self.backup_path, &valid_primary)?;
        }
        atomic_write(&self.config_path, &formatted)?;
        Ok(config)
    }

    fn normalize(normalized: &mut StandaloneConfig) -> Result<(), ConfigError> {
        for sub in &mut normalized.subscriptions {
            if !sub.encrypted_key.trim().is_empty() && !secret::is_protected_secret(&sub.encrypted_key) {
                sub.encrypted_key = secret::protect_secret(sub.encrypted_key.trim())
                    .map_err(|e| ConfigError::Validation(e.to_string()))?;
            }
        }

        Ok(())
    }

    /// 语言页签专用局部 Patch：重读最新磁盘 JSON，仅修改 `recognitionLanguage`、`enableTranslation`、`targetLanguage`，
    /// 保留并发修改的其他设置字段与未知字段（对齐 `VoiceImeLanguagePage` 契约）。
    pub fn patch_language_fields(&self, draft: &LanguageDraft) -> Result<StandaloneConfig, ConfigError> {
        if draft.enable_translation && draft.target_language.trim().eq_ignore_ascii_case("auto") {
            return Err(ConfigError::Validation(
                "开启翻译时目标语言不能为自动检测。".into(),
            ));
        }
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
        self.update(|cfg| {
            cfg.recognition_language = src;
            cfg.enable_translation = draft.enable_translation;
            cfg.target_language = tgt;
            Ok(())
        })
    }

    fn read_effective(&self) -> Result<(Map<String, Value>, Option<Vec<u8>>), ConfigError> {
        match read_valid_object(&self.config_path) {
            Ok(Some((map, bytes))) => Ok((map, Some(bytes))),
            primary => match read_valid_object(&self.backup_path) {
                Ok(Some((map, _))) => Ok((map, None)),
                Ok(None) => primary.map(|_| (Map::new(), None)),
                Err(err) => Err(primary.err().unwrap_or(err)),
            },
        }
    }
}

fn atomic_write(path: &Path, bytes: &[u8]) -> Result<(), ConfigError> {
    let tmp = path.with_extension(format!("{}.tmp", uuid::Uuid::new_v4().simple()));
    let result = (|| -> io::Result<()> {
        let mut file = fs::File::create_new(&tmp)?;
        file.write_all(bytes)?;
        file.sync_data()?;
        drop(file);
        fs::rename(&tmp, path)
    })();
    if result.is_err() {
        let _ = fs::remove_file(tmp);
    }
    result.map_err(io_error)
}

fn io_error(e: io::Error) -> ConfigError { ConfigError::Io(e.to_string()) }

fn decode_config(map: Map<String, Value>) -> Result<StandaloneConfig, ConfigError> {
    serde_json::from_value(Value::Object(map)).map_err(|_| ConfigError::CorruptedOrTooLarge)
}

fn read_valid_object(path: &Path) -> Result<Option<(Map<String, Value>, Vec<u8>)>, ConfigError> {
    let file = match fs::File::open(path) {
        Ok(file) => file,
        Err(e) if e.kind() == io::ErrorKind::NotFound => return Ok(None),
        Err(e) => return Err(io_error(e)),
    };
    let mut bytes = Vec::new();
    file.take(MAX_CONFIG_FILE_BYTES + 1).read_to_end(&mut bytes).map_err(io_error)?;
    if bytes.len() as u64 > MAX_CONFIG_FILE_BYTES {
        return Err(ConfigError::CorruptedOrTooLarge);
    }
    let map = serde_json::from_slice::<Map<String, Value>>(&bytes).map_err(|_| ConfigError::CorruptedOrTooLarge)?;
    decode_config(map.clone())?;
    Ok(Some((map, bytes)))
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

    #[test]
    fn language_update_recovers_valid_backup_without_replacing_it_with_corruption() {
        let dir = std::env::temp_dir().join(format!("voice_ime_recovery_{}", uuid::Uuid::new_v4()));
        fs::create_dir_all(&dir).unwrap();
        let store = ConfigStore::new(dir.join("config.json"));
        let backup = br#"{"microphoneDeviceId":"saved microphone","futureFlag":17}"#;
        fs::write(&store.backup_path, backup).unwrap();
        fs::write(store.path(), br#"{"microphoneDeviceId":17}"#).unwrap();
        let draft = LanguageDraft { recognition_language: "zh-CN".into(), enable_translation: false, target_language: "en-US".into() };
        assert_eq!(store.patch_language_fields(&draft).unwrap().microphone_device_id, "saved microphone");
        assert_eq!(fs::read(&store.backup_path).unwrap(), backup);
        assert_eq!(serde_json::from_slice::<Value>(&fs::read(store.path()).unwrap()).unwrap()["futureFlag"], 17);
        fs::write(&store.backup_path, b"bad backup").unwrap();
        fs::write(store.path(), b"bad primary").unwrap();
        assert!(store.patch_language_fields(&draft).is_err());
        assert_eq!(fs::read(store.path()).unwrap(), b"bad primary");
        let _ = fs::remove_dir_all(dir);
    }
}
