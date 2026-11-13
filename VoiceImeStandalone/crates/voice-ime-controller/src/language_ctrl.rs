//! 语言与翻译页签控制器（对齐现有 `VoiceImeLanguagePage`）。

use serde::{Deserialize, Serialize};
use voice_ime_config::{
    builtin_source_languages, builtin_target_languages, ConfigStore, LanguageDraft, LanguageOption,
};

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LanguagePageSnapshot {
    pub source_languages: Vec<LanguageOption>,
    pub target_languages: Vec<LanguageOption>,
    pub recognition_language: String,
    pub enable_translation: bool,
    pub target_language: String,
}

pub fn load_language_snapshot(store: &ConfigStore) -> Result<LanguagePageSnapshot, String> {
    let cfg = store.load_readonly().map_err(|e| e.to_string())?;
    let mut source_languages = builtin_source_languages();
    let mut target_languages = builtin_target_languages();

    if !source_languages
        .iter()
        .any(|o| o.code.eq_ignore_ascii_case(&cfg.recognition_language))
    {
        source_languages.push(LanguageOption {
            code: cfg.recognition_language.clone(),
            name: format!("已保存的语言 ({})", cfg.recognition_language),
        });
    }
    if !target_languages
        .iter()
        .any(|o| o.code.eq_ignore_ascii_case(&cfg.target_language))
    {
        target_languages.push(LanguageOption {
            code: cfg.target_language.clone(),
            name: format!("已保存的语言 ({})", cfg.target_language),
        });
    }

    Ok(LanguagePageSnapshot {
        source_languages,
        target_languages,
        recognition_language: cfg.recognition_language,
        enable_translation: cfg.enable_translation,
        target_language: cfg.target_language,
    })
}

pub fn save_language_draft(
    store: &ConfigStore,
    draft: LanguageDraft,
) -> Result<LanguagePageSnapshot, String> {
    store
        .patch_language_fields(&draft)
        .map_err(|e| e.to_string())?;
    load_language_snapshot(store)
}
