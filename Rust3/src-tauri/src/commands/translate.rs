use tauri::State;
use tfp_core::{LanguageInfo, TranslateRequest, TranslateResponse};
use tfp_providers::ProviderCapability;

use crate::state::AppState;

#[tauri::command]
pub async fn translate_text(
    state: State<'_, AppState>,
    request: TranslateRequest,
) -> Result<TranslateResponse, String> {
    let providers = state.providers.read().await;

    let provider_id = match request.endpoint_id.as_deref() {
        Some(id) if !id.is_empty() => id.to_string(),
        _ => {
            // Pick the first provider that has TextTranslation capability
            let list = providers.list_providers();
            list.iter()
                .find(|p| p.capabilities.contains(&ProviderCapability::TextTranslation))
                .map(|p| p.id.clone())
                .ok_or_else(|| "No text translation provider available".to_string())?
        }
    };

    let provider = providers
        .get_text_translation(&provider_id)
        .ok_or_else(|| format!("Text translation provider not found: {provider_id}"))?;

    provider
        .translate(&request)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn get_supported_languages() -> Result<Vec<LanguageInfo>, String> {
    Ok(built_in_languages())
}

fn built_in_languages() -> Vec<LanguageInfo> {
    [
        ("zh-Hans", "Chinese (Simplified)", "中文（简体）"),
        ("zh-Hant", "Chinese (Traditional)", "中文（繁體）"),
        ("en", "English", "English"),
        ("ja", "Japanese", "日本語"),
        ("ko", "Korean", "한국어"),
        ("fr", "French", "Français"),
        ("de", "German", "Deutsch"),
        ("es", "Spanish", "Español"),
        ("ru", "Russian", "Русский"),
        ("pt", "Portuguese", "Português"),
        ("it", "Italian", "Italiano"),
        ("ar", "Arabic", "العربية"),
        ("hi", "Hindi", "हिन्दी"),
        ("th", "Thai", "ภาษาไทย"),
        ("vi", "Vietnamese", "Tiếng Việt"),
    ]
    .into_iter()
    .map(|(code, name, native)| LanguageInfo {
        code: code.into(),
        name: name.into(),
        native_name: native.into(),
    })
    .collect()
}
