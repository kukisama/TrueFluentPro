use std::collections::HashMap;
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

    // 保存 handle 到全局 state 以便后续 stop
    {
        let mut sessions = state.active_speech_sessions.write().await;
        sessions.insert(sid.clone(), handle);
    }

    // 将实时事件通过 Tauri Event 推送到前端
    tauri::async_runtime::spawn(async move {
        while let Some(event) = rx.recv().await {
            let _ = app.emit("realtime-event", &event);
        }
    });

    Ok(sid)
}

/// 停止实时语音翻译会话
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
//  Azure 存储验证
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

#[tauri::command]
pub async fn validate_storage_connection(connection_string: String) -> Result<(), String> {
    // 基本格式校验：必须包含 AccountName 和 AccountKey
    if !connection_string.contains("AccountName=") || !connection_string.contains("AccountKey=") {
        return Err("连接字符串格式不正确，必须包含 AccountName 和 AccountKey".into());
    }
    // 提取 AccountName 并尝试构造 endpoint URL 来验证
    let account_name = connection_string
        .split(';')
        .find_map(|p| p.strip_prefix("AccountName="))
        .ok_or("无法解析 AccountName")?;
    let endpoint_suffix = connection_string
        .split(';')
        .find_map(|p| p.strip_prefix("EndpointSuffix="))
        .unwrap_or("core.windows.net");
    let url = format!("https://{}.blob.{}", account_name, endpoint_suffix);

    // 尝试 HEAD 请求验证域名可达
    let client = reqwest::Client::builder()
        .timeout(std::time::Duration::from_secs(10))
        .build()
        .map_err(|e| e.to_string())?;

    let resp = client.get(&url).send().await.map_err(|e| format!("无法连接到存储端点: {}", e))?;
    // Azure Blob 未带认证会返回 403 或 400，但不会超时或域名不存在
    if resp.status().is_server_error() {
        return Err(format!("存储端点返回服务器错误: {}", resp.status()));
    }
    Ok(())
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

/// 测试端点连通性 — 逐模型逐能力测试，返回详细报告
#[tauri::command]
pub async fn test_endpoint(
    state: State<'_, AppState>,
    endpoint_id: String,
) -> Result<crate::models::EndpointTestReport, String> {
    let config = state.config.read().await;
    let ep = config
        .endpoints
        .iter()
        .find(|e| e.id == endpoint_id)
        .ok_or("端点不存在")?
        .clone();
    drop(config);

    let start = std::time::Instant::now();
    let mut items = Vec::new();

    // Speech 端点走独立测试逻辑
    if ep.is_speech() {
        let key = if !ep.speech_subscription_key.is_empty() {
            &ep.speech_subscription_key
        } else {
            &ep.api_key
        };
        let region = &ep.speech_region;
        let endpoint = &ep.speech_endpoint;

        if key.is_empty() {
            return Err("订阅密钥为空，请先填写".into());
        }
        if region.is_empty() && endpoint.is_empty() {
            return Err("区域和终结点均为空，请至少填写一项".into());
        }

        // 用 token issue 接口测试连通性
        let t0 = std::time::Instant::now();
        let test_url = if !endpoint.is_empty() {
            // 从终结点推导 token issue URL
            let base = endpoint.trim_end_matches('/');
            if base.contains("/sts/") {
                base.to_string()
            } else {
                format!("{base}/sts/v1.0/issuetoken")
            }
        } else {
            format!("https://{region}.api.cognitive.microsoft.com/sts/v1.0/issuetoken")
        };

        let client = reqwest::Client::builder()
            .timeout(std::time::Duration::from_secs(15))
            .build()
            .map_err(|e| e.to_string())?;

        let resp = client
            .post(&test_url)
            .header("Ocp-Apim-Subscription-Key", key)
            .header("Content-Length", "0")
            .send()
            .await;

        let dur = t0.elapsed().as_millis() as u64;

        match resp {
            Ok(r) if r.status().is_success() => {
                items.push(crate::models::EndpointTestItem {
                    model_id: "speech-sdk".into(),
                    capability: "SpeechTranslation".into(),
                    status: crate::models::TestStatus::Success,
                    summary: format!("✅ Speech 连通成功 (区域: {})", if !region.is_empty() { region.as_str() } else { "自定义终结点" }),
                    detail: None,
                    request_url: Some(test_url),
                    duration_ms: dur,
                });
            }
            Ok(r) => {
                let status = r.status().as_u16();
                let body = r.text().await.unwrap_or_default();
                items.push(crate::models::EndpointTestItem {
                    model_id: "speech-sdk".into(),
                    capability: "SpeechTranslation".into(),
                    status: crate::models::TestStatus::Failed,
                    summary: format!("❌ Speech 认证失败 (HTTP {status})"),
                    detail: Some(if status == 401 {
                        "订阅密钥无效或已过期，请检查密钥和区域是否匹配".into()
                    } else {
                        body
                    }),
                    request_url: Some(test_url),
                    duration_ms: dur,
                });
            }
            Err(e) => {
                items.push(crate::models::EndpointTestItem {
                    model_id: "speech-sdk".into(),
                    capability: "SpeechTranslation".into(),
                    status: crate::models::TestStatus::Failed,
                    summary: "❌ Speech 连接失败".into(),
                    detail: Some(format!("无法连接到 Speech 服务: {e}")),
                    request_url: Some(test_url),
                    duration_ms: dur,
                });
            }
        }

        return Ok(crate::models::EndpointTestReport {
            endpoint_id: ep.id.clone(),
            endpoint_name: ep.name.clone(),
            items,
            duration_ms: start.elapsed().as_millis() as u64,
        });
    }

    // AI 端点前置校验
    if ep.url.trim().is_empty() {
        return Err("端点 URL 为空，请先填写".into());
    }
    if ep.api_key.trim().is_empty() {
        return Err("API Key 为空，请先填写".into());
    }
    if ep.models.is_empty() {
        return Err("模型列表为空，请至少添加一个模型".into());
    }

    let client = reqwest::Client::builder()
        .timeout(std::time::Duration::from_secs(20))
        .build()
        .map_err(|e| e.to_string())?;

    for model in &ep.models {
        for cap in &model.capabilities {
            let t0 = std::time::Instant::now();
            let result =
                test_single_capability(&client, &ep, model, cap).await;
            let dur = t0.elapsed().as_millis() as u64;

            match result {
                Ok((summary, url)) => {
                    items.push(crate::models::EndpointTestItem {
                        model_id: model.model_id.clone(),
                        capability: format!("{cap:?}"),
                        status: crate::models::TestStatus::Success,
                        summary,
                        detail: None,
                        request_url: Some(url),
                        duration_ms: dur,
                    });
                }
                Err((summary, detail, url)) => {
                    items.push(crate::models::EndpointTestItem {
                        model_id: model.model_id.clone(),
                        capability: format!("{cap:?}"),
                        status: crate::models::TestStatus::Failed,
                        summary,
                        detail: Some(detail),
                        request_url: url,
                        duration_ms: dur,
                    });
                }
            }
        }
    }

    Ok(crate::models::EndpointTestReport {
        endpoint_id: ep.id.clone(),
        endpoint_name: ep.name.clone(),
        items,
        duration_ms: start.elapsed().as_millis() as u64,
    })
}

/// 单能力测试——返回 Ok((summary, url)) 或 Err((summary, detail, url))
async fn test_single_capability(
    client: &reqwest::Client,
    ep: &crate::models::AiEndpoint,
    model: &crate::models::AiModelEntry,
    cap: &crate::models::ModelCapability,
) -> Result<(String, String), (String, String, Option<String>)> {
    use crate::models::ModelCapability;

    let base = ep.url.trim_end_matches('/');
    let api_ver = ep.api_version.as_deref().unwrap_or("2024-08-01-preview");
    let deploy = model.effective_deployment();

    match cap {
        ModelCapability::Text => {
            let url = build_text_url(base, &ep.endpoint_type, deploy, api_ver);
            let body = serde_json::json!({
                "messages": [
                    {"role": "system", "content": "你是连通性测试助手，请用简短中文直接回复。"},
                    {"role": "user", "content": "计算 2+3 的结果"}
                ],
                "max_tokens": 20,
                "stream": false,
            });
            // 非 Azure 部署时需要 model 字段
            let body = if ep.is_azure() {
                body
            } else {
                let mut b = body;
                b["model"] = serde_json::Value::String(model.model_id.clone());
                b
            };
            let req = build_authed_request(client.post(&url), ep).json(&body);
            execute_and_check(req, &url, "文字").await
        }
        ModelCapability::Image => {
            let url = build_image_url(base, &ep.endpoint_type, deploy, api_ver);
            let body = serde_json::json!({
                "prompt": "一只卡通兔子",
                "model": model.model_id,
                "n": 1,
                "size": "1024x1024",
                "quality": "low",
            });
            let req = build_authed_request(client.post(&url), ep).json(&body);
            execute_and_check(req, &url, "图片").await
        }
        ModelCapability::Video => {
            let url = format!("{base}/v1/video/generations");
            let body = serde_json::json!({
                "prompt": "一只卡通兔子",
                "model": model.model_id,
            });
            let req = build_authed_request(client.post(&url), ep).json(&body);
            execute_and_check(req, &url, "视频").await
        }
        ModelCapability::SpeechToText => {
            // STT 需要上传音频文件，这里仅做 OPTIONS 探活
            let url = build_stt_url(base, &ep.endpoint_type, deploy, api_ver);
            let summary = format!("⏭ STT 端口探测暂未实现（需上传音频）");
            Err((
                summary,
                "STT 测试需要上传音频文件，暂不支持一键测试".into(),
                Some(url),
            ))
        }
        ModelCapability::TextToSpeech => {
            let summary = format!("⏭ TTS 端口探测暂未实现");
            Err((
                summary,
                "TTS 测试暂不支持一键测试".into(),
                None,
            ))
        }
    }
}

fn build_text_url(base: &str, ep_type: &crate::models::EndpointType, deploy: &str, api_ver: &str) -> String {
    use crate::models::EndpointType;
    match ep_type {
        EndpointType::AzureOpenAi => {
            format!("{base}/openai/deployments/{deploy}/chat/completions?api-version={api_ver}")
        }
        EndpointType::ApiManagementGateway => {
            format!("{base}/v1/chat/completions")
        }
        _ => {
            format!("{base}/v1/chat/completions")
        }
    }
}

fn build_image_url(base: &str, ep_type: &crate::models::EndpointType, deploy: &str, api_ver: &str) -> String {
    use crate::models::EndpointType;
    match ep_type {
        EndpointType::AzureOpenAi => {
            format!("{base}/openai/deployments/{deploy}/images/generations?api-version={api_ver}")
        }
        EndpointType::ApiManagementGateway => {
            format!("{base}/v1/images/generations")
        }
        _ => {
            format!("{base}/v1/images/generations")
        }
    }
}

fn build_stt_url(base: &str, ep_type: &crate::models::EndpointType, deploy: &str, api_ver: &str) -> String {
    use crate::models::EndpointType;
    match ep_type {
        EndpointType::AzureOpenAi => {
            format!("{base}/openai/deployments/{deploy}/audio/transcriptions?api-version={api_ver}")
        }
        _ => {
            format!("{base}/v1/audio/transcriptions")
        }
    }
}

fn build_authed_request(
    req: reqwest::RequestBuilder,
    ep: &crate::models::AiEndpoint,
) -> reqwest::RequestBuilder {
    let mode = ep.auth_header_mode.as_str();
    match mode {
        "bearer" => req.header("Authorization", format!("Bearer {}", ep.api_key)),
        "api_key" => req.header("api-key", &ep.api_key),
        _ => {
            // auto: Azure 用 api-key, 其他用 Bearer
            if ep.is_azure() {
                req.header("api-key", &ep.api_key)
            } else {
                req.header("Authorization", format!("Bearer {}", ep.api_key))
            }
        }
    }
}

async fn execute_and_check(
    req: reqwest::RequestBuilder,
    url: &str,
    cap_label: &str,
) -> Result<(String, String), (String, String, Option<String>)> {
    match req.send().await {
        Ok(resp) => {
            let status = resp.status();
            let status_code = status.as_u16();
            if status.is_success() || status_code == 200 {
                let body = resp.text().await.unwrap_or_default();
                // 尝试从响应中提取模型信息
                let model_info = serde_json::from_str::<serde_json::Value>(&body)
                    .ok()
                    .and_then(|v| v.get("model").and_then(|m| m.as_str().map(String::from)));
                let summary = if let Some(m) = model_info {
                    format!("✅ {cap_label}连通成功 (模型: {m})")
                } else {
                    format!("✅ {cap_label}连通成功")
                };
                Ok((summary, url.to_string()))
            } else {
                let body = resp.text().await.unwrap_or_default();
                let detail = parse_error_body(status_code, &body);
                Err((
                    format!("❌ {cap_label}测试失败 (HTTP {status_code})"),
                    detail,
                    Some(url.to_string()),
                ))
            }
        }
        Err(e) => {
            let detail = if e.is_timeout() {
                "请求超时（20秒），请检查网络或终结点地址是否可达".into()
            } else if e.is_connect() {
                format!("无法连接到服务器，请检查 URL 是否正确: {e}")
            } else {
                format!("网络错误: {e}")
            };
            Err((
                format!("❌ {cap_label}连接失败"),
                detail,
                Some(url.to_string()),
            ))
        }
    }
}

/// 解析错误响应体，提取人类可读的错误信息
fn parse_error_body(status_code: u16, body: &str) -> String {
    // 尝试解析 JSON 错误
    if let Ok(v) = serde_json::from_str::<serde_json::Value>(body) {
        // OpenAI 格式: {"error": {"message": "...", "type": "...", "code": "..."}}
        if let Some(err) = v.get("error") {
            let msg = err
                .get("message")
                .and_then(|m| m.as_str())
                .unwrap_or("未知错误");
            let code = err
                .get("code")
                .and_then(|c| c.as_str())
                .unwrap_or("");
            let err_type = err
                .get("type")
                .and_then(|t| t.as_str())
                .unwrap_or("");
            let mut detail = format!("HTTP {status_code}");
            if !code.is_empty() {
                detail.push_str(&format!(" | 错误码: {code}"));
            }
            if !err_type.is_empty() {
                detail.push_str(&format!(" | 类型: {err_type}"));
            }
            detail.push_str(&format!("\n{msg}"));
            return detail;
        }
        // 有些 API 返回 {"message": "..."}
        if let Some(msg) = v.get("message").and_then(|m| m.as_str()) {
            return format!("HTTP {status_code} | {msg}");
        }
        // 有 statusCode 字段的
        if let Some(_status) = v.get("statusCode") {
            let message = v
                .get("message")
                .and_then(|m| m.as_str())
                .unwrap_or("未知错误");
            return format!("HTTP {status_code} | {message}");
        }
    }
    // 非 JSON 或解析失败
    if body.len() > 500 {
        format!("HTTP {status_code} | {}", &body[..500])
    } else if body.is_empty() {
        format!("HTTP {status_code} (无响应体)")
    } else {
        format!("HTTP {status_code} | {body}")
    }
}

/// 获取厂商资料包列表（内置）
#[tauri::command]
pub async fn get_vendor_profiles() -> Result<Vec<crate::models::VendorProfile>, String> {
    use crate::models::{EndpointType, VendorProfile};
    let mut profiles = Vec::new();

    profiles.push(VendorProfile {
        endpoint_type: EndpointType::AzureOpenAi,
        label: "Azure OpenAI".into(),
        badge: "AZ".into(),
        subtitle: "微软 Azure OpenAI 服务".into(),
        glyph: "☁".into(),
        default_auth_header: "api_key".into(),
        default_api_version: "2024-08-01-preview".into(),
        supports_model_discovery: false,
        model_discovery_urls: vec![],
        test_url_templates: [
            ("text".into(), "{baseUrl}/openai/deployments/{deployment}/chat/completions?api-version={apiVersion}".into()),
            ("image".into(), "{baseUrl}/openai/deployments/{deployment}/images/generations?api-version={apiVersion}".into()),
        ].into_iter().collect(),
    });

    profiles.push(VendorProfile {
        endpoint_type: EndpointType::ApiManagementGateway,
        label: "APIM 网关".into(),
        badge: "AP".into(),
        subtitle: "Azure API Management 网关".into(),
        glyph: "⇆".into(),
        default_auth_header: "api_key".into(),
        default_api_version: "2025-03-01-preview".into(),
        supports_model_discovery: true,
        model_discovery_urls: vec![
            "{baseUrl}/models".into(),
            "{baseUrl}/v1/models".into(),
        ],
        test_url_templates: [
            ("text".into(), "{baseUrl}/v1/chat/completions".into()),
            ("image".into(), "{baseUrl}/v1/images/generations".into()),
        ].into_iter().collect(),
    });

    profiles.push(VendorProfile {
        endpoint_type: EndpointType::OpenAiCompatible,
        label: "OpenAI 兼容".into(),
        badge: "OA".into(),
        subtitle: "OpenAI / DeepSeek / Ollama / vLLM 等兼容厂商".into(),
        glyph: "✦".into(),
        default_auth_header: "bearer".into(),
        default_api_version: "".into(),
        supports_model_discovery: true,
        model_discovery_urls: vec![
            "{baseUrl}/v1/models".into(),
            "{baseUrl}/models".into(),
        ],
        test_url_templates: [
            ("text".into(), "{baseUrl}/v1/chat/completions".into()),
            ("image".into(), "{baseUrl}/v1/images/generations".into()),
        ].into_iter().collect(),
    });

    profiles.push(VendorProfile {
        endpoint_type: EndpointType::AzureSpeech,
        label: "Azure Speech".into(),
        badge: "SP".into(),
        subtitle: "微软语音服务（STT / TTS / 实时翻译）".into(),
        glyph: "🎤".into(),
        default_auth_header: "api_key".into(),
        default_api_version: "".into(),
        supports_model_discovery: false,
        model_discovery_urls: vec![],
        test_url_templates: HashMap::new(),
    });

    Ok(profiles)
}

/// 从终结点自动发现可用模型列表
#[tauri::command]
pub async fn discover_models(
    state: State<'_, AppState>,
    endpoint_id: String,
) -> Result<Vec<crate::models::DiscoveredModel>, String> {
    let config = state.config.read().await;
    let ep = config
        .endpoints
        .iter()
        .find(|e| e.id == endpoint_id)
        .ok_or("端点不存在")?
        .clone();
    drop(config);

    if ep.is_azure() && ep.endpoint_type == crate::models::EndpointType::AzureOpenAi {
        return Err("Azure OpenAI 终结点不支持自动发现模型，请手动添加部署名称".into());
    }
    if ep.url.trim().is_empty() {
        return Err("端点 URL 为空".into());
    }
    if ep.api_key.trim().is_empty() {
        return Err("API Key 为空".into());
    }

    let client = reqwest::Client::builder()
        .timeout(std::time::Duration::from_secs(20))
        .build()
        .map_err(|e| e.to_string())?;

    let base = ep.url.trim_end_matches('/');
    let candidates = match ep.endpoint_type {
        crate::models::EndpointType::ApiManagementGateway => {
            vec![format!("{base}/models"), format!("{base}/v1/models")]
        }
        _ => {
            vec![format!("{base}/v1/models"), format!("{base}/models")]
        }
    };

    for url in &candidates {
        let req = build_authed_request(client.get(url), &ep);
        match req.send().await {
            Ok(resp) if resp.status().is_success() => {
                let body = resp.text().await.unwrap_or_default();
                if let Ok(models) = parse_model_list(&body) {
                    if !models.is_empty() {
                        return Ok(models);
                    }
                }
            }
            _ => continue,
        }
    }

    Err(format!(
        "无法从以下地址发现模型: {}",
        candidates.join(", ")
    ))
}

fn parse_model_list(body: &str) -> Result<Vec<crate::models::DiscoveredModel>, ()> {
    let v: serde_json::Value = serde_json::from_str(body).map_err(|_| ())?;

    // OpenAI 格式: {"data": [{"id": "..."}]}
    let arr = v
        .get("data")
        .and_then(|d| d.as_array())
        // {"models": [...]}
        .or_else(|| v.get("models").and_then(|m| m.as_array()))
        // {"value": [...]}
        .or_else(|| v.get("value").and_then(|m| m.as_array()))
        // 顶层数组
        .or_else(|| v.as_array());

    let arr = arr.ok_or(())?;

    let models: Vec<crate::models::DiscoveredModel> = arr
        .iter()
        .filter_map(|item| {
            // 支持字符串或对象
            if let Some(s) = item.as_str() {
                return Some(crate::models::DiscoveredModel {
                    id: s.to_string(),
                    display_name: None,
                    owned_by: None,
                });
            }
            let id = item
                .get("id")
                .or_else(|| item.get("model"))
                .or_else(|| item.get("name"))
                .and_then(|v| v.as_str())?;
            Some(crate::models::DiscoveredModel {
                id: id.to_string(),
                display_name: item
                    .get("display_name")
                    .and_then(|v| v.as_str())
                    .map(String::from),
                owned_by: item
                    .get("owned_by")
                    .and_then(|v| v.as_str())
                    .map(String::from),
            })
        })
        .collect();

    Ok(models)
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  P1.2: 会话 & 消息命令
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

#[tauri::command]
pub async fn list_sessions(
    state: State<'_, AppState>,
    session_type: Option<String>,
) -> Result<Vec<Session>, String> {
    state.db.list_sessions(session_type.as_deref()).map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn create_session(
    state: State<'_, AppState>,
    title: String,
    session_type: String,
) -> Result<Session, String> {
    let now = chrono::Utc::now().to_rfc3339();
    let session = Session {
        id: uuid::Uuid::new_v4().to_string(),
        title,
        session_type,
        message_count: 0,
        token_total: 0,
        created_at: now.clone(),
        updated_at: now,
    };
    state.db.create_session(&session).map_err(|e| e.to_string())?;
    Ok(session)
}

#[tauri::command]
pub async fn delete_session(
    state: State<'_, AppState>,
    session_id: String,
) -> Result<(), String> {
    state.db.delete_session(&session_id).map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn get_session_messages(
    state: State<'_, AppState>,
    session_id: String,
) -> Result<Vec<Message>, String> {
    state.db.get_session_messages(&session_id).map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn add_message(
    state: State<'_, AppState>,
    msg: Message,
) -> Result<Message, String> {
    let mut msg = msg;
    if msg.id.is_empty() {
        msg.id = uuid::Uuid::new_v4().to_string();
    }
    if msg.created_at.is_empty() {
        msg.created_at = chrono::Utc::now().to_rfc3339();
    }
    state.db.add_message(&msg).map_err(|e| e.to_string())?;
    Ok(msg)
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  P1.2: 音频库命令
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

#[tauri::command]
pub async fn list_audio_items(
    state: State<'_, AppState>,
) -> Result<Vec<AudioLibraryItem>, String> {
    state.db.list_audio_items().map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn add_audio_item(
    state: State<'_, AppState>,
    item: AudioLibraryItem,
) -> Result<AudioLibraryItem, String> {
    let mut item = item;
    if item.id.is_empty() {
        item.id = uuid::Uuid::new_v4().to_string();
    }
    let now = chrono::Utc::now().to_rfc3339();
    if item.created_at.is_empty() {
        item.created_at = now.clone();
    }
    if item.updated_at.is_empty() {
        item.updated_at = now;
    }
    state.db.add_audio_item(&item).map_err(|e| e.to_string())?;
    // Initialize 8-stage lifecycle
    state.db.init_lifecycle_stages(&item.id).map_err(|e| e.to_string())?;
    Ok(item)
}

#[tauri::command]
pub async fn delete_audio_item(
    state: State<'_, AppState>,
    item_id: String,
) -> Result<(), String> {
    state.db.delete_audio_item(&item_id).map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn get_audio_lifecycle(
    state: State<'_, AppState>,
    audio_item_id: String,
) -> Result<Vec<AudioLifecycleRow>, String> {
    state.db.get_audio_lifecycle(&audio_item_id).map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn update_lifecycle_stage(
    state: State<'_, AppState>,
    lifecycle: AudioLifecycleRow,
) -> Result<(), String> {
    state.db.upsert_lifecycle(&lifecycle).map_err(|e| e.to_string())
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  P2.1: 任务引擎命令
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

#[tauri::command]
pub async fn submit_task(
    state: State<'_, AppState>,
    task: AudioTaskRow,
) -> Result<AudioTaskRow, String> {
    let mut task = task;
    if task.id.is_empty() {
        task.id = uuid::Uuid::new_v4().to_string();
    }
    if task.submitted_at.is_empty() {
        task.submitted_at = chrono::Utc::now().to_rfc3339();
    }
    state.db.submit_task(&task).map_err(|e| e.to_string())?;
    Ok(task)
}

#[tauri::command]
pub async fn cancel_task(
    state: State<'_, AppState>,
    task_id: String,
) -> Result<(), String> {
    state.db.update_task_status_new(&task_id, "Cancelled", None).map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn retry_task(
    state: State<'_, AppState>,
    task_id: String,
) -> Result<(), String> {
    state.db.update_task_status_new(&task_id, "Queued", None).map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn get_task_engine_stats(
    state: State<'_, AppState>,
) -> Result<TaskEngineStats, String> {
    state.db.get_task_stats().map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn list_tasks(
    state: State<'_, AppState>,
    status: Option<String>,
    limit: Option<u32>,
) -> Result<Vec<AudioTaskRow>, String> {
    state.db.list_tasks(status.as_deref(), limit.unwrap_or(100)).map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn get_task_executions(
    state: State<'_, AppState>,
    task_id: String,
) -> Result<Vec<TaskExecutionRow>, String> {
    state.db.get_task_executions(&task_id).map_err(|e| e.to_string())
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  P3.4: 配置导入/导出
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

#[tauri::command]
pub async fn export_config(state: State<'_, AppState>) -> Result<String, String> {
    let config = state.config.read().await;
    serde_json::to_string_pretty(&*config).map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn import_config(
    state: State<'_, AppState>,
    json: String,
) -> Result<(), String> {
    let new_config: AppConfig = serde_json::from_str(&json)
        .map_err(|e| format!("Invalid config JSON: {e}"))?;
    {
        let mut config = state.config.write().await;
        *config = new_config;
    }
    state.persist_config().await
}

#[tauri::command]
pub async fn write_text_file(path: String, content: String) -> Result<(), String> {
    std::fs::write(&path, &content).map_err(|e| format!("写入文件失败: {e}"))
}

#[tauri::command]
pub async fn read_text_file(path: String) -> Result<String, String> {
    std::fs::read_to_string(&path).map_err(|e| format!("读取文件失败: {e}"))
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  P3.5: 计费查询
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

#[tauri::command]
pub async fn get_billing_records(
    state: State<'_, AppState>,
    limit: Option<u32>,
) -> Result<Vec<BillingRecord>, String> {
    state.db.get_billing_records(limit.unwrap_or(100)).map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn get_billing_summary(
    state: State<'_, AppState>,
) -> Result<BillingSummary, String> {
    state.db.get_billing_summary().map_err(|e| e.to_string())
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  P4.1: 图片管道
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

#[tauri::command]
pub async fn run_image_pipeline(
    app: tauri::AppHandle,
    request: crate::image_pipeline::pipeline::PipelineRequest,
) -> Result<crate::image_pipeline::pipeline::PipelineResult, String> {
    crate::image_pipeline::pipeline::run_pipeline(&app, request).await
}

#[tauri::command]
pub async fn get_image_model_catalog() -> Result<Vec<crate::image_pipeline::catalog::ModelCapabilityEntry>, String> {
    Ok(crate::image_pipeline::catalog::builtin_image_models())
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  P4.2: 视频生成（预留）
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

#[tauri::command]
pub async fn generate_video(
    _prompt: String,
    _model: String,
    _endpoint_id: String,
) -> Result<String, String> {
    Err("Video generation is not yet implemented".into())
}
