use tauri::{Emitter, Manager, State};

use crate::models::*;
use crate::providers::ProviderInfo;
use crate::state::AppState;

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  配置命令
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

#[tauri::command]
pub async fn get_config(state: State<'_, AppState>) -> Result<AppConfig, String> {
    let config = state.config.read().await;
    Ok(config.clone())
}

#[tauri::command]
pub async fn update_config(state: State<'_, AppState>, config: AppConfig) -> Result<(), String> {
    {
        let mut current = state.config.write().await;
        *current = config;
    }
    state.persist_config().await
}

#[tauri::command]
pub async fn add_endpoint(
    state: State<'_, AppState>,
    endpoint: AiEndpoint,
) -> Result<(), String> {
    {
        let mut config = state.config.write().await;
        config.endpoints.push(endpoint);
    }
    state.persist_config().await
}

#[tauri::command]
pub async fn remove_endpoint(
    state: State<'_, AppState>,
    endpoint_id: String,
) -> Result<(), String> {
    {
        let mut config = state.config.write().await;
        config.endpoints.retain(|e| e.id != endpoint_id);
    }
    state.persist_config().await
}

#[tauri::command]
pub async fn update_endpoint(
    state: State<'_, AppState>,
    endpoint: AiEndpoint,
) -> Result<(), String> {
    {
        let mut config = state.config.write().await;
        if let Some(existing) = config.endpoints.iter_mut().find(|e| e.id == endpoint.id) {
            *existing = endpoint;
        }
    }
    state.persist_config().await
}

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
                "未找到实时语音翻译 Provider: {}。请在设置中配置端点，或安装对应的 Provider 插件。",
                config.endpoint_id
            )
        })?;

    let (mut rx, _handle) = provider
        .create_session(&config)
        .await
        .map_err(|e| e.to_string())?;

    let session_id = uuid::Uuid::new_v4().to_string();
    let sid = session_id.clone();

    // 将实时事件通过 Tauri Event 推送到前端
    tauri::async_runtime::spawn(async move {
        while let Some(event) = rx.recv().await {
            let _ = app.emit("realtime-event", &event);
        }
    });

    Ok(sid)
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  Provider 查询命令
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

#[tauri::command]
pub async fn list_providers(state: State<'_, AppState>) -> Result<Vec<ProviderInfo>, String> {
    let providers = state.providers.read().await;
    Ok(providers.list_providers())
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  AI 媒体命令
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

#[tauri::command]
pub async fn generate_image(
    state: State<'_, AppState>,
    request: ImageGenRequest,
) -> Result<Vec<ImageGenResult>, String> {
    let providers = state.providers.read().await;
    let provider = providers
        .get_image_gen(&request.endpoint_id)
        .ok_or_else(|| format!("未找到图片生成 Provider: {}", request.endpoint_id))?;

    provider
        .generate(&request)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn ai_complete(
    state: State<'_, AppState>,
    request: CompletionRequest,
) -> Result<CompletionResponse, String> {
    let providers = state.providers.read().await;
    let provider = providers
        .get_ai_completion(&request.endpoint_id)
        .ok_or_else(|| format!("未找到 AI 补全 Provider: {}", request.endpoint_id))?;

    provider
        .complete(&request)
        .await
        .map_err(|e| e.to_string())
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  存储命令
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

#[tauri::command]
pub async fn get_translation_history(
    state: State<'_, AppState>,
    limit: Option<u32>,
) -> Result<Vec<TranslationHistory>, String> {
    state
        .db
        .list_translations(limit.unwrap_or(50))
        .map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn get_batch_tasks(
    state: State<'_, AppState>,
    limit: Option<u32>,
) -> Result<Vec<BatchTask>, String> {
    state
        .db
        .list_batch_tasks(limit.unwrap_or(100))
        .map_err(|e| e.to_string())
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  系统信息命令
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

#[derive(serde::Serialize)]
pub struct AppInfo {
    pub version: String,
    pub platform: String,
    pub arch: String,
    pub data_dir: String,
}

#[tauri::command]
pub async fn get_app_info(app: tauri::AppHandle) -> Result<AppInfo, String> {
    let data_dir = app
        .path()
        .app_data_dir()
        .map(|p| p.to_string_lossy().to_string())
        .map_err(|e| e.to_string())?;

    Ok(AppInfo {
        version: env!("CARGO_PKG_VERSION").to_string(),
        platform: std::env::consts::OS.to_string(),
        arch: std::env::consts::ARCH.to_string(),
        data_dir,
    })
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  Provider 热重载
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

/// 端点配置变更后调用，重新注册所有 Provider
#[tauri::command]
pub async fn refresh_providers(state: State<'_, AppState>) -> Result<Vec<ProviderInfo>, String> {
    let config = state.config.read().await;
    let endpoints = config.endpoints.clone();
    drop(config);

    crate::register_providers_from_config(&*state, &endpoints);

    let providers = state.providers.read().await;
    Ok(providers.list_providers())
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  流式 AI 补全（通过 Tauri Event 推送 token）
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

#[tauri::command]
pub async fn ai_complete_stream(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    request: CompletionRequest,
) -> Result<String, String> {
    let providers = state.providers.read().await;
    let provider = providers
        .get_ai_completion(&request.endpoint_id)
        .ok_or_else(|| format!("未找到 AI Provider: {}", request.endpoint_id))?;

    let mut rx = provider
        .complete_stream(&request)
        .await
        .map_err(|e| e.to_string())?;

    let stream_id = uuid::Uuid::new_v4().to_string();
    let sid = stream_id.clone();

    tauri::async_runtime::spawn(async move {
        while let Some(result) = rx.recv().await {
            match result {
                Ok(token) => {
                    let _ = app.emit("ai-stream-token", serde_json::json!({
                        "stream_id": &sid,
                        "token": token,
                    }));
                }
                Err(e) => {
                    let _ = app.emit("ai-stream-token", serde_json::json!({
                        "stream_id": &sid,
                        "error": e.to_string(),
                    }));
                    break;
                }
            }
        }
        let _ = app.emit("ai-stream-token", serde_json::json!({
            "stream_id": &sid,
            "done": true,
        }));
    });

    Ok(stream_id)
}

/// 测试端点连通性
#[tauri::command]
pub async fn test_endpoint(
    state: State<'_, AppState>,
    endpoint_id: String,
) -> Result<String, String> {
    let config = state.config.read().await;
    let ep = config
        .endpoints
        .iter()
        .find(|e| e.id == endpoint_id)
        .ok_or("端点不存在")?
        .clone();
    drop(config);

    // 简单测试: 发送一个最小请求
    let client = reqwest::Client::builder()
        .timeout(std::time::Duration::from_secs(15))
        .build()
        .map_err(|e| e.to_string())?;

    let base = ep.url.trim_end_matches('/');
    let url = match ep.endpoint_type {
        crate::models::EndpointType::AzureOpenAi => {
            if let Some(ref deploy) = ep.deployment {
                format!("{base}/openai/deployments/{deploy}/chat/completions?api-version=2024-08-01-preview")
            } else {
                format!("{base}/openai/v1/chat/completions")
            }
        }
        _ => format!("{base}/v1/chat/completions"),
    };

    let mut req = client.post(&url).json(&serde_json::json!({
        "messages": [{"role": "user", "content": "Hi"}],
        "max_tokens": 1,
        "stream": false,
    }));

    // 添加 model（非 Azure 部署模式）
    if ep.endpoint_type != crate::models::EndpointType::AzureOpenAi || ep.deployment.is_none() {
        req = req.json(&serde_json::json!({
            "messages": [{"role": "user", "content": "Hi"}],
            "model": "gpt-4o-mini",
            "max_tokens": 1,
        }));
    }

    req = match ep.endpoint_type {
        crate::models::EndpointType::AzureOpenAi => req.header("api-key", &ep.api_key),
        _ => req.header("Authorization", format!("Bearer {}", ep.api_key)),
    };

    let resp = req.send().await.map_err(|e| format!("连接失败: {e}"))?;
    let status = resp.status();

    if status.is_success() || status.as_u16() == 200 {
        Ok("连接成功".into())
    } else {
        let body = resp.text().await.unwrap_or_default();
        Err(format!("HTTP {status}: {body}"))
    }
}
