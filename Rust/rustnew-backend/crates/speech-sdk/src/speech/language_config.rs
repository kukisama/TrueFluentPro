//! 语言配置：SourceLanguageConfig 与 AutoDetectSourceLanguageConfig。
//!
//! - `SourceLanguageConfig`：显式指定单一识别语言（可带自定义端点 id）。
//! - `AutoDetectSourceLanguageConfig`：候选语言自动检测。配合识别器使用后，
//!   可从结果属性 `SpeechServiceConnectionAutoDetectSourceLanguageResult` 读取检测到的语言。

use crate::error::{convert_err, Result};
use crate::ffi::{
    add_source_lang_config_to_auto_detect_source_lang_config,
    auto_detect_source_lang_config_release, create_auto_detect_source_lang_config_from_languages,
    create_auto_detect_source_lang_config_from_open_range,
    create_auto_detect_source_lang_config_from_source_lang_config, source_lang_config_from_language,
    source_lang_config_from_language_and_endpointId, source_lang_config_release, SmartHandle,
    SPXAUTODETECTSOURCELANGCONFIGHANDLE, SPXSOURCELANGCONFIGHANDLE,
};
use std::ffi::CString;
use std::mem::MaybeUninit;

/// 单一识别语言配置。
#[derive(Debug)]
pub struct SourceLanguageConfig {
    pub(crate) handle: SmartHandle<SPXSOURCELANGCONFIGHANDLE>,
}

impl SourceLanguageConfig {
    /// 指定语言，例如 "zh-CN"。
    pub fn from_language(language: &str) -> Result<SourceLanguageConfig> {
        let c_lang = CString::new(language)?;
        unsafe {
            let mut handle: MaybeUninit<SPXSOURCELANGCONFIGHANDLE> = MaybeUninit::uninit();
            let ret = source_lang_config_from_language(handle.as_mut_ptr(), c_lang.as_ptr());
            convert_err(ret, "SourceLanguageConfig::from_language error")?;
            Ok(SourceLanguageConfig {
                handle: SmartHandle::create(
                    "SourceLanguageConfig",
                    handle.assume_init(),
                    source_lang_config_release,
                ),
            })
        }
    }

    /// 指定语言与自定义模型端点 id。
    pub fn from_language_and_endpoint_id(
        language: &str,
        endpoint_id: &str,
    ) -> Result<SourceLanguageConfig> {
        let c_lang = CString::new(language)?;
        let c_ep = CString::new(endpoint_id)?;
        unsafe {
            let mut handle: MaybeUninit<SPXSOURCELANGCONFIGHANDLE> = MaybeUninit::uninit();
            let ret = source_lang_config_from_language_and_endpointId(
                handle.as_mut_ptr(),
                c_lang.as_ptr(),
                c_ep.as_ptr(),
            );
            convert_err(ret, "SourceLanguageConfig::from_language_and_endpoint_id error")?;
            Ok(SourceLanguageConfig {
                handle: SmartHandle::create(
                    "SourceLanguageConfig",
                    handle.assume_init(),
                    source_lang_config_release,
                ),
            })
        }
    }
}

/// 自动语言检测配置。
#[derive(Debug)]
pub struct AutoDetectSourceLanguageConfig {
    pub(crate) handle: SmartHandle<SPXAUTODETECTSOURCELANGCONFIGHANDLE>,
}

impl AutoDetectSourceLanguageConfig {
    /// 从候选语言列表创建，例如 ["zh-CN", "en-US", "ja-JP"]。
    pub fn from_languages(languages: &[&str]) -> Result<AutoDetectSourceLanguageConfig> {
        let joined = languages.join(",");
        let c_langs = CString::new(joined)?;
        unsafe {
            let mut handle: MaybeUninit<SPXAUTODETECTSOURCELANGCONFIGHANDLE> = MaybeUninit::uninit();
            let ret = create_auto_detect_source_lang_config_from_languages(
                handle.as_mut_ptr(),
                c_langs.as_ptr(),
            );
            convert_err(ret, "AutoDetectSourceLanguageConfig::from_languages error")?;
            Ok(AutoDetectSourceLanguageConfig {
                handle: SmartHandle::create(
                    "AutoDetectSourceLanguageConfig",
                    handle.assume_init(),
                    auto_detect_source_lang_config_release,
                ),
            })
        }
    }

    /// 开放范围检测（仅用于 TTS 多语言；识别端通常用 `from_languages`）。
    pub fn from_open_range() -> Result<AutoDetectSourceLanguageConfig> {
        unsafe {
            let mut handle: MaybeUninit<SPXAUTODETECTSOURCELANGCONFIGHANDLE> = MaybeUninit::uninit();
            let ret = create_auto_detect_source_lang_config_from_open_range(handle.as_mut_ptr());
            convert_err(ret, "AutoDetectSourceLanguageConfig::from_open_range error")?;
            Ok(AutoDetectSourceLanguageConfig {
                handle: SmartHandle::create(
                    "AutoDetectSourceLanguageConfig",
                    handle.assume_init(),
                    auto_detect_source_lang_config_release,
                ),
            })
        }
    }

    /// 从单个 `SourceLanguageConfig` 创建。
    pub fn from_source_language_config(
        config: &SourceLanguageConfig,
    ) -> Result<AutoDetectSourceLanguageConfig> {
        unsafe {
            let mut handle: MaybeUninit<SPXAUTODETECTSOURCELANGCONFIGHANDLE> = MaybeUninit::uninit();
            let ret = create_auto_detect_source_lang_config_from_source_lang_config(
                handle.as_mut_ptr(),
                config.handle.inner(),
            );
            convert_err(
                ret,
                "AutoDetectSourceLanguageConfig::from_source_language_config error",
            )?;
            Ok(AutoDetectSourceLanguageConfig {
                handle: SmartHandle::create(
                    "AutoDetectSourceLanguageConfig",
                    handle.assume_init(),
                    auto_detect_source_lang_config_release,
                ),
            })
        }
    }

    /// 追加一个 `SourceLanguageConfig`（可带不同端点 id）。
    pub fn add_source_language_config(&self, config: &SourceLanguageConfig) -> Result<()> {
        unsafe {
            let ret = add_source_lang_config_to_auto_detect_source_lang_config(
                self.handle.inner(),
                config.handle.inner(),
            );
            convert_err(ret, "AutoDetectSourceLanguageConfig::add_source_language_config error")
        }
    }
}
