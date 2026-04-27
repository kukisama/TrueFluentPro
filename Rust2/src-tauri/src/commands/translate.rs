use tauri::{Emitter, State};

use crate::models::*;
use crate::state::AppState;
use crate::providers::ProviderInfo;

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  翻译命令
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

#[tauri::command]
pub async fn translate_text(
    state: State<'_, AppState>,
    request: TranslateRequest,
) -> Result<TranslateResponse, String> {
    let providers = state.providers.read().await;
    let provider_id = request
        .endpoint_id
        .as_deref()
        .unwrap_or("default");
    let provider = providers
        .get_text_translation(provider_id)
        .ok_or_else(|| format!("未找到翻译 Provider: {provider_id}"))?;

    provider
        .translate(&request)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn start_realtime_translation(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    config: RealtimeSessionConfig,
) -> Result<String, String> {
    let providers = state.providers.read().await;
    let provider = providers
        .get_realtime_speech(&config.endpoint_id)
        .ok_or_else(|| {
            format!(
                "未找到实时语音翻译 Provider: {}。请在设置中添加 Azure Speech 类型端点。",
                config.endpoint_id
            )
        })?;

    let (mut rx, handle) = provider
        .create_session(&config)
        .await
        .map_err(|e| e.to_string())?;

    let session_id = uuid::Uuid::new_v4().to_string();
    let sid = session_id.clone();

    {
        let mut sessions = state.active_speech_sessions.write().await;
        sessions.insert(sid.clone(), handle);
    }

    tauri::async_runtime::spawn(async move {
        while let Some(event) = rx.recv().await {
            let _ = app.emit("realtime-event", &event);
        }
    });

    Ok(sid)
}

#[tauri::command]
pub async fn stop_realtime_translation(
    state: State<'_, AppState>,
    session_id: String,
) -> Result<(), String> {
    let handle = {
        let mut sessions = state.active_speech_sessions.write().await;
        sessions.remove(&session_id)
    };

    if let Some(h) = handle {
        h.stop().await.map_err(|e| e.to_string())?;
    }

    Ok(())
}

/// O-34: 返回支持的语言列表
#[tauri::command]
pub async fn get_supported_languages() -> Result<Vec<(String, String)>, String> {
    Ok(vec![
        ("zh-Hans".into(), "中文（简体）".into()),
        ("zh-Hant".into(), "中文（繁體）".into()),
        ("en".into(), "English".into()),
        ("ja".into(), "日本語".into()),
        ("ko".into(), "한국어".into()),
        ("fr".into(), "Français".into()),
        ("de".into(), "Deutsch".into()),
        ("es".into(), "Español".into()),
        ("ru".into(), "Русский".into()),
        ("pt".into(), "Português".into()),
        ("it".into(), "Italiano".into()),
        ("ar".into(), "العربية".into()),
        ("hi".into(), "हिन्दी".into()),
        ("th".into(), "ภาษาไทย".into()),
        ("vi".into(), "Tiếng Việt".into()),
    ])
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  Provider 查询命令
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

#[tauri::command]
pub async fn list_providers(state: State<'_, AppState>) -> Result<Vec<ProviderInfo>, String> {
    let providers = state.providers.read().await;
    Ok(providers.list_providers())
}
