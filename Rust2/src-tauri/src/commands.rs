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

/// 保存图片到本地文件 + 记录到数据库（对齐 C# GenerateAndSaveImagesAsync）
#[tauri::command]
pub async fn save_image(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    request: SaveImageRequest,
) -> Result<SavedImage, String> {
    use base64::Engine;
    use std::io::Write;

    // 解码 base64
    let bytes = base64::engine::general_purpose::STANDARD
        .decode(&request.base64)
        .map_err(|e| format!("base64 解码失败: {e}"))?;

    // 确定存储目录: {app_data_dir}/images/
    let data_dir = app.path().app_data_dir()
        .map_err(|e| format!("获取数据目录失败: {e}"))?;
    let img_dir = data_dir.join("images");
    std::fs::create_dir_all(&img_dir).map_err(|e| format!("创建目录失败: {e}"))?;

    // 文件名: img_{timestamp}_{random}.{ext}（对齐 C# img_{seq:D3}_{randomId:N}.{ext}）
    let ext = match request.format.as_str() {
        "jpeg" | "jpg" => "jpg",
        "webp" => "webp",
        _ => "png",
    };
    let file_name = format!(
        "img_{}_{}.{}",
        chrono::Utc::now().format("%Y%m%d_%H%M%S"),
        &uuid::Uuid::new_v4().to_string()[..8],
        ext
    );
    let file_path = img_dir.join(&file_name);

    // 原子写入: .tmp → rename（对齐 C# 的 .tmp + File.Move 模式）
    let tmp_path = file_path.with_extension(format!("{ext}.tmp"));
    {
        let mut f = std::fs::File::create(&tmp_path)
            .map_err(|e| format!("创建临时文件失败: {e}"))?;
        f.write_all(&bytes).map_err(|e| format!("写入失败: {e}"))?;
        f.flush().map_err(|e| format!("flush 失败: {e}"))?;
    }
    std::fs::rename(&tmp_path, &file_path)
        .map_err(|e| format!("重命名失败: {e}"))?;

    let file_size = bytes.len() as i64;

    // 写入数据库
    let record = SavedImage {
        id: uuid::Uuid::new_v4().to_string(),
        prompt: request.prompt,
        revised_prompt: request.revised_prompt,
        file_path: file_path.to_string_lossy().to_string(),
        file_size,
        width: request.width,
        height: request.height,
        model_id: request.model_id,
        endpoint_id: request.endpoint_id,
        generate_seconds: request.generate_seconds,
        source: request.source,
        created_at: chrono::Utc::now().to_rfc3339(),
    };
    state.db.add_saved_image(&record).map_err(|e| e.to_string())?;

    tracing::info!("✓ 图片已保存: {} ({} bytes)", file_name, file_size);
    Ok(record)
}

/// 列出已保存的图片记录
#[tauri::command]
pub async fn list_saved_images(
    state: State<'_, AppState>,
    limit: Option<u32>,
) -> Result<Vec<SavedImage>, String> {
    state.db.list_saved_images(limit.unwrap_or(50)).map_err(|e| e.to_string())
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

    crate::register_providers_from_config_async(&*state, &endpoints).await;

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

/// 测试端点连通性 — 逐模型逐能力测试，通过事件实时推送进度
#[tauri::command]
pub async fn test_endpoint(
    app: tauri::AppHandle,
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

    let profiles = build_vendor_profiles();
    let profile = profiles.iter().find(|p| p.endpoint_type == ep.endpoint_type);
    let started_at = chrono::Utc::now().to_rfc3339();
    let start = std::time::Instant::now();

    // ── Speech 端点走独立逻辑 ──
    if ep.is_speech() {
        let items = test_speech_endpoint(&ep).await;
        let report = build_report(&ep, items, start.elapsed().as_millis() as u64);
        return Ok(report);
    }

    // ── AI 端点前置校验 ──
    if ep.url.trim().is_empty() {
        return Err("端点 URL 为空，请先填写".into());
    }
    if ep.api_key.trim().is_empty() {
        return Err("API Key 为空，请先填写".into());
    }
    if ep.models.is_empty() {
        return Err("模型列表为空，请至少添加一个模型".into());
    }

    // ── 计算总测试项并初始化进度 ──
    let mut plan: Vec<(String, crate::models::ModelCapability)> = Vec::new();
    for model in &ep.models {
        for cap in &model.capabilities {
            plan.push((model.model_id.clone(), cap.clone()));
        }
    }
    let total = plan.len();

    // 初始化 items 为 Running（全部并发，无 Pending 排队）
    let items: Vec<crate::models::EndpointTestItem> = plan
        .iter()
        .map(|(mid, cap)| crate::models::EndpointTestItem {
            model_id: mid.clone(),
            capability: cap_label(cap),
            status: crate::models::TestStatus::Running,
            summary: format!("正在测试 {}...", cap_label(cap)),
            detail: None,
            request_url: None,
            request_summary: None,
            duration_ms: 0,
            test_branch: None,
            urls_tried: vec![],
        })
        .collect();

    // 推送初始进度——全部 Running
    emit_progress(&app, &ep, &items, &started_at, false);

    let client = reqwest::Client::builder()
        .timeout(std::time::Duration::from_secs(25))
        .build()
        .map_err(|e| e.to_string())?;

    // ── 并发测试：所有 (model, capability) 一口气发出 ──
    let items_shared = std::sync::Arc::new(tokio::sync::Mutex::new(items));
    let mut handles = Vec::new();

    for (idx, (model_id, cap)) in plan.iter().enumerate() {
        let client = client.clone();
        let ep = ep.clone();
        let profile = profile.cloned();
        let model = ep.models.iter().find(|m| m.model_id == *model_id).unwrap().clone();
        let cap = cap.clone();
        let items_shared = items_shared.clone();
        let app = app.clone();
        let started_at = started_at.clone();

        let handle = tokio::spawn(async move {
            let t0 = std::time::Instant::now();
            let mut result = test_single_capability_v2(&client, &ep, &model, &cap, profile.as_ref()).await;
            result.duration_ms = t0.elapsed().as_millis() as u64;

            // 更新共享 items 并推送进度
            let mut items = items_shared.lock().await;
            items[idx] = result;
            emit_progress(&app, &ep, &items, &started_at, false);
        });
        handles.push(handle);
    }

    // 等待全部完成
    for h in handles {
        let _ = h.await;
    }

    let items = match std::sync::Arc::try_unwrap(items_shared) {
        Ok(mutex) => mutex.into_inner(),
        Err(arc) => arc.lock().await.clone(),
    };

    // 推送完成
    emit_progress(&app, &ep, &items, &started_at, true);

    Ok(build_report(&ep, items, start.elapsed().as_millis() as u64))
}

fn emit_progress(
    app: &tauri::AppHandle,
    ep: &crate::models::AiEndpoint,
    items: &[crate::models::EndpointTestItem],
    started_at: &str,
    is_completed: bool,
) {
    let progress = crate::models::EndpointTestProgress {
        endpoint_id: ep.id.clone(),
        endpoint_name: ep.name.clone(),
        total_count: items.len(),
        pending_count: items.iter().filter(|i| i.status == crate::models::TestStatus::Pending).count(),
        running_count: items.iter().filter(|i| i.status == crate::models::TestStatus::Running).count(),
        success_count: items.iter().filter(|i| i.status == crate::models::TestStatus::Success).count(),
        failed_count: items.iter().filter(|i| i.status == crate::models::TestStatus::Failed).count(),
        skipped_count: items.iter().filter(|i| i.status == crate::models::TestStatus::Skipped).count(),
        items: items.to_vec(),
        is_completed,
        started_at: started_at.to_string(),
    };
    let _ = app.emit("endpoint-test-progress", &progress);
}

fn build_report(
    ep: &crate::models::AiEndpoint,
    items: Vec<crate::models::EndpointTestItem>,
    duration_ms: u64,
) -> crate::models::EndpointTestReport {
    let success_count = items.iter().filter(|i| i.status == crate::models::TestStatus::Success).count();
    let failed_count = items.iter().filter(|i| i.status == crate::models::TestStatus::Failed).count();
    let skipped_count = items.iter().filter(|i| i.status == crate::models::TestStatus::Skipped).count();
    crate::models::EndpointTestReport {
        endpoint_id: ep.id.clone(),
        endpoint_name: ep.name.clone(),
        endpoint_type_name: format!("{:?}", ep.endpoint_type),
        items: items.clone(),
        duration_ms,
        total_count: items.len(),
        success_count,
        failed_count,
        skipped_count,
    }
}

fn cap_label(cap: &crate::models::ModelCapability) -> String {
    use crate::models::ModelCapability;
    match cap {
        ModelCapability::Text => "文字".into(),
        ModelCapability::Image => "图片".into(),
        ModelCapability::Video => "视频".into(),
        ModelCapability::SpeechToText => "语音识别".into(),
        ModelCapability::TextToSpeech => "语音合成".into(),
    }
}

/// 解析最终认证头模式（对齐 C# GetEffectiveApiKeyHeaderMode 四级级联）
fn resolve_auth_mode(ep: &crate::models::AiEndpoint, profile: Option<&crate::models::VendorProfile>) -> String {
    let mode = ep.auth_header_mode.as_str();
    if mode != "auto" && !mode.is_empty() {
        return mode.to_string();
    }
    // auto → 从 profile 取默认
    if let Some(p) = profile {
        return p.default_auth_header.clone();
    }
    // 无 profile 时的平台默认
    if ep.is_azure() && ep.endpoint_type != crate::models::EndpointType::ApiManagementGateway {
        "api_key".into()
    } else {
        "bearer".into()
    }
}

fn resolve_api_version(ep: &crate::models::AiEndpoint, profile: Option<&crate::models::VendorProfile>) -> String {
    if let Some(v) = &ep.api_version {
        if !v.is_empty() {
            return v.clone();
        }
    }
    if let Some(p) = profile {
        if !p.default_api_version.is_empty() {
            return p.default_api_version.clone();
        }
    }
    "2025-03-01-preview".into()
}

fn build_authed_request(
    req: reqwest::RequestBuilder,
    ep: &crate::models::AiEndpoint,
    profile: Option<&crate::models::VendorProfile>,
) -> reqwest::RequestBuilder {
    let mode = resolve_auth_mode(ep, profile);
    match mode.as_str() {
        "bearer" => req.header("Authorization", format!("Bearer {}", ep.api_key)),
        "api_key" | "api_key_header" => req.header("api-key", &ep.api_key),
        _ => {
            // fallback: 所有 Azure 系（含 APIM）统一 api-key，非 Azure 用 Bearer
            if ep.is_azure() {
                req.header("api-key", &ep.api_key)
            } else {
                req.header("Authorization", format!("Bearer {}", ep.api_key))
            }
        }
    }
}

fn auth_mode_display(mode: &str) -> &str {
    match mode {
        "bearer" => "Bearer Token",
        "api_key" | "api_key_header" => "api-key Header",
        _ => "auto",
    }
}

/// 构建 URL 候选列表（对齐 C# EndpointProfileUrlBuilder.BuildConfiguredTextUrlCandidates）
fn build_url_candidates(
    ep: &crate::models::AiEndpoint,
    model: &crate::models::AiModelEntry,
    cap: &crate::models::ModelCapability,
    profile: Option<&crate::models::VendorProfile>,
) -> Vec<String> {
    use crate::models::{EndpointType, ModelCapability};
    let base = ep.url.trim_end_matches('/');
    let deploy = model.effective_deployment();
    let api_ver = resolve_api_version(ep, profile);

    // 如果 profile 有候选列表，使用它
    if let Some(p) = profile {
        let empty = Vec::new();
        let templates = match cap {
            ModelCapability::Text => &p.text_url_candidates,
            ModelCapability::Image => &p.image_url_candidates,
            ModelCapability::Video => &p.video_url_candidates,
            ModelCapability::SpeechToText => &p.audio_url_candidates,
            ModelCapability::TextToSpeech => &p.speech_url_candidates,
        };
        let templates = if templates.is_empty() { &empty } else { templates };
        if !templates.is_empty() {
            return templates
                .iter()
                .map(|t| {
                    t.replace("{baseUrl}", base)
                        .replace("{deployment}", deploy)
                        .replace("{apiVersion}", &api_ver)
                        .replace("{model}", &model.model_id)
                })
                .collect();
        }
    }

    // 无 profile 或无候选时的默认构建
    match (cap, &ep.endpoint_type) {
        (ModelCapability::Text, EndpointType::AzureOpenAi) => {
            vec![format!("{base}/openai/deployments/{deploy}/chat/completions?api-version={api_ver}")]
        }
        (ModelCapability::Text, _) => {
            vec![format!("{base}/v1/chat/completions")]
        }
        (ModelCapability::Image, EndpointType::AzureOpenAi) => {
            vec![format!("{base}/openai/deployments/{deploy}/images/generations?api-version={api_ver}")]
        }
        (ModelCapability::Image, _) => {
            vec![format!("{base}/v1/images/generations")]
        }
        (ModelCapability::Video, _) => {
            vec![format!("{base}/v1/video/generations")]
        }
        (ModelCapability::SpeechToText, EndpointType::AzureOpenAi) => {
            vec![format!("{base}/openai/deployments/{deploy}/audio/transcriptions?api-version={api_ver}")]
        }
        (ModelCapability::SpeechToText, _) => {
            vec![format!("{base}/v1/audio/transcriptions")]
        }
        (ModelCapability::TextToSpeech, _) => {
            vec![]
        }
    }
}

/// 构建请求摘要（对齐 C# RequestSummary）
fn build_request_summary(
    ep: &crate::models::AiEndpoint,
    model: &crate::models::AiModelEntry,
    cap: &crate::models::ModelCapability,
    profile: Option<&crate::models::VendorProfile>,
    url_idx: usize,
    total_urls: usize,
) -> String {
    let auth_mode = resolve_auth_mode(ep, profile);
    let auth = auth_mode_display(&auth_mode);
    let api_ver = resolve_api_version(ep, profile);
    let protocol = if let Some(p) = profile {
        if !p.text_protocol.is_empty() { p.text_protocol.as_str() } else { "chat_completions" }
    } else {
        "chat_completions"
    };
    let source = if profile.is_some() { "资料包" } else { "默认" };
    let branch = if total_urls > 1 {
        format!("候选 {}/{}", url_idx + 1, total_urls)
    } else {
        "唯一候选".into()
    };

    let mut lines = Vec::new();
    lines.push(format!("认证: {auth}"));
    lines.push(format!("基础地址: {}", ep.url));
    lines.push(format!("模型: {}", model.model_id));
    if !api_ver.is_empty() {
        lines.push(format!("API版本: {api_ver}"));
    }
    if matches!(cap, crate::models::ModelCapability::Text) {
        lines.push(format!("文本协议: {protocol} ({})", source));
    }
    lines.push(format!("测试来源: {source}"));
    lines.push(format!("测试分支: {} ({source}第 {} 条候选)", if url_idx == 0 { "主测试" } else { "回退测试" }, url_idx + 1));
    lines.join("\n")
}

/// V2 单能力测试——支持候选 URL 回退、流式、推理检测
async fn test_single_capability_v2(
    client: &reqwest::Client,
    ep: &crate::models::AiEndpoint,
    model: &crate::models::AiModelEntry,
    cap: &crate::models::ModelCapability,
    profile: Option<&crate::models::VendorProfile>,
) -> crate::models::EndpointTestItem {
    use crate::models::ModelCapability;

    let label = cap_label(cap);

    // STT / TTS 暂不支持
    if matches!(cap, ModelCapability::SpeechToText) {
        return crate::models::EndpointTestItem {
            model_id: model.model_id.clone(),
            capability: label.clone(),
            status: crate::models::TestStatus::Skipped,
            summary: "⏭ 语音识别测试暂未实现（需上传音频）".into(),
            detail: Some("语音识别测试需要上传音频文件，暂不支持一键测试".into()),
            request_url: None,
            request_summary: None,
            duration_ms: 0,
            test_branch: None,
            urls_tried: vec![],
        };
    }
    if matches!(cap, ModelCapability::TextToSpeech) {
        return crate::models::EndpointTestItem {
            model_id: model.model_id.clone(),
            capability: label.clone(),
            status: crate::models::TestStatus::Skipped,
            summary: "⏭ 语音合成测试暂未实现".into(),
            detail: None,
            request_url: None,
            request_summary: None,
            duration_ms: 0,
            test_branch: None,
            urls_tried: vec![],
        };
    }

    let candidates = build_url_candidates(ep, model, cap, profile);
    if candidates.is_empty() {
        let summary = format!("❌ {}无可用 URL 候选", &label);
        return crate::models::EndpointTestItem {
            model_id: model.model_id.clone(),
            capability: label,
            status: crate::models::TestStatus::Failed,
            summary,
            detail: Some("资料包中未声明该能力的 URL 候选列表".into()),
            request_url: None,
            request_summary: None,
            duration_ms: 0,
            test_branch: None,
            urls_tried: vec![],
        };
    }

    let total_urls = candidates.len();
    let mut urls_tried = Vec::new();

    // 逐候选测试，主 URL 成功即返回
    for (url_idx, url) in candidates.iter().enumerate() {
        urls_tried.push(url.clone());
        let request_summary = build_request_summary(ep, model, cap, profile, url_idx, total_urls);
        let branch = if url_idx == 0 {
            format!("主测试 (资料包第 1 条候选)")
        } else {
            format!("回退测试 (资料包第 {} 条候选)", url_idx + 1)
        };

        let result = match cap {
            ModelCapability::Text => {
                test_text_capability(client, ep, model, url, profile).await
            }
            ModelCapability::Image => {
                test_image_capability(client, ep, model, url, profile).await
            }
            ModelCapability::Video => {
                test_video_capability(client, ep, model, url, profile).await
            }
            _ => unreachable!(),
        };

        match result {
            Ok((summary, detail)) => {
                return crate::models::EndpointTestItem {
                    model_id: model.model_id.clone(),
                    capability: label.clone(),
                    status: crate::models::TestStatus::Success,
                    summary,
                    detail,
                    request_url: Some(format!("POST {url}")),
                    request_summary: Some(request_summary),
                    duration_ms: 0,
                    test_branch: Some(branch),
                    urls_tried: urls_tried.clone(),
                };
            }
            Err((summary, detail)) => {
                // 如果是主 URL 且有回退候选，继续尝试
                if url_idx < total_urls - 1 {
                    continue;
                }
                // 最后一个候选也失败了
                return crate::models::EndpointTestItem {
                    model_id: model.model_id.clone(),
                    capability: label.clone(),
                    status: crate::models::TestStatus::Failed,
                    summary,
                    detail: Some(detail),
                    request_url: Some(format!("POST {url}")),
                    request_summary: Some(request_summary),
                    duration_ms: 0,
                    test_branch: Some(format!("全部 {total_urls} 条候选均失败")),
                    urls_tried,
                };
            }
        }
    }

    // 不应到这里
    crate::models::EndpointTestItem {
        model_id: model.model_id.clone(),
        capability: label,
        status: crate::models::TestStatus::Failed,
        summary: "❌ 未知错误".into(),
        detail: None,
        request_url: None,
        request_summary: None,
        duration_ms: 0,
        test_branch: None,
        urls_tried,
    }
}

/// 文字能力测试——流式请求 + 推理检测
async fn test_text_capability(
    client: &reqwest::Client,
    ep: &crate::models::AiEndpoint,
    model: &crate::models::AiModelEntry,
    url: &str,
    profile: Option<&crate::models::VendorProfile>,
) -> Result<(String, Option<String>), (String, String)> {
    let is_responses = url.contains("/responses");

    let body = if is_responses {
        // Responses API 格式
        let mut b = serde_json::json!({
            "model": model.model_id,
            "input": "计算 2+3 的结果，简短直接回复",
            "stream": true,
        });
        // Azure 部署时不需要 model 字段（由 URL 指定）
        if ep.endpoint_type == crate::models::EndpointType::AzureOpenAi {
            b.as_object_mut().unwrap().remove("model");
        }
        b
    } else {
        // ChatCompletions 格式
        let mut b = serde_json::json!({
            "messages": [
                {"role": "system", "content": "你是连通性测试助手，请用简短中文直接回复。"},
                {"role": "user", "content": "计算 2+3 的结果"}
            ],
            "max_tokens": 50,
            "stream": true,
        });
        // 非 Azure 时需要 model 字段
        if !ep.is_azure() || ep.endpoint_type == crate::models::EndpointType::ApiManagementGateway {
            b["model"] = serde_json::Value::String(model.model_id.clone());
        }
        b
    };

    let req = build_authed_request(client.post(url), ep, profile).json(&body);

    match req.send().await {
        Ok(resp) => {
            let status = resp.status();
            let status_code = status.as_u16();
            if !status.is_success() {
                let body = resp.text().await.unwrap_or_default();
                let detail = parse_error_body(status_code, &body);
                return Err((
                    format!("❌ 文字测试失败 (HTTP {status_code})"),
                    detail,
                ));
            }

            // 读取流式响应
            let body_text = resp.text().await.unwrap_or_default();
            let (text_chunks, reasoning_chunks, model_name) =
                parse_stream_response(&body_text, is_responses);

            let total_chunks = text_chunks + reasoning_chunks;
            let has_reasoning = reasoning_chunks > 0;

            let mut summary = format!("✅ 文字测试通过");
            if has_reasoning {
                summary.push_str("。⚡ 推理可用");
            } else {
                summary.push_str("。⚠ 推理未返回（模型可能不支持 reasoning）");
            }

            let mut detail_lines = Vec::new();
            detail_lines.push(format!("返回片段: {total_chunks}"));
            if has_reasoning {
                detail_lines.push(format!("推理片段: {reasoning_chunks}"));
            }
            if let Some(m) = &model_name {
                detail_lines.push(format!("响应模型: {m}"));
            }

            Ok((summary, Some(detail_lines.join("\n"))))
        }
        Err(e) => {
            let detail = if e.is_timeout() {
                "请求超时（25秒），请检查网络或终结点地址是否可达".into()
            } else if e.is_connect() {
                format!("无法连接到服务器: {e}")
            } else {
                format!("网络错误: {e}")
            };
            Err((
                "❌ 文字连接失败".into(),
                detail,
            ))
        }
    }
}

/// 解析 SSE 流式响应，统计文字/推理片段数
fn parse_stream_response(body: &str, is_responses: bool) -> (usize, usize, Option<String>) {
    let mut text_chunks = 0usize;
    let mut reasoning_chunks = 0usize;
    let mut model_name: Option<String> = None;

    for line in body.lines() {
        let line = line.trim();
        if !line.starts_with("data: ") {
            continue;
        }
        let data = &line[6..];
        if data == "[DONE]" {
            break;
        }
        if let Ok(v) = serde_json::from_str::<serde_json::Value>(data) {
            // 提取 model
            if model_name.is_none() {
                if let Some(m) = v.get("model").and_then(|m| m.as_str()) {
                    model_name = Some(m.to_string());
                }
            }

            if is_responses {
                // Responses API: output_text.delta / reasoning.delta 等
                if let Some(t) = v.get("type").and_then(|t| t.as_str()) {
                    match t {
                        "response.output_text.delta" => text_chunks += 1,
                        "response.reasoning.delta" | "response.reasoning_summary_text.delta" => reasoning_chunks += 1,
                        _ => {}
                    }
                }
            } else {
                // ChatCompletions SSE: choices[0].delta
                if let Some(choices) = v.get("choices").and_then(|c| c.as_array()) {
                    if let Some(delta) = choices.first().and_then(|c| c.get("delta")) {
                        if delta.get("content").and_then(|c| c.as_str()).map_or(false, |s| !s.is_empty()) {
                            text_chunks += 1;
                        }
                        if delta.get("reasoning_content").and_then(|c| c.as_str()).map_or(false, |s| !s.is_empty()) {
                            reasoning_chunks += 1;
                        }
                    }
                }
            }
        }
    }

    (text_chunks, reasoning_chunks, model_name)
}

/// 图片能力测试
async fn test_image_capability(
    client: &reqwest::Client,
    ep: &crate::models::AiEndpoint,
    model: &crate::models::AiModelEntry,
    url: &str,
    profile: Option<&crate::models::VendorProfile>,
) -> Result<(String, Option<String>), (String, String)> {
    // 对齐 C# AiImageGenService.SendImageGenerateRequestAsync body 格式
    let body = serde_json::json!({
        "prompt": "一只卡通兔子",
        "model": model.model_id,
        "size": "1024x1024",
        "quality": "low",
        "output_format": "png",
    });
    let req = build_authed_request(client.post(url), ep, profile).json(&body);

    match req.send().await {
        Ok(resp) => {
            let status = resp.status();
            let status_code = status.as_u16();
            if !status.is_success() {
                let body = resp.text().await.unwrap_or_default();
                return Err((
                    format!("❌ 图片测试失败 (HTTP {status_code})"),
                    parse_error_body(status_code, &body),
                ));
            }
            let body = resp.text().await.unwrap_or_default();
            let image_count = serde_json::from_str::<serde_json::Value>(&body)
                .ok()
                .and_then(|v| v.get("data").and_then(|d| d.as_array()).map(|a| a.len()))
                .unwrap_or(0);
            Ok((
                format!("✅ 图片生成成功 (返回 {image_count} 张)"),
                Some(format!("图片数量: {image_count}")),
            ))
        }
        Err(e) => Err((
            "❌ 图片连接失败".into(),
            format!("网络错误: {e}"),
        )),
    }
}

/// 视频能力测试——仅验证创建接口可达
async fn test_video_capability(
    client: &reqwest::Client,
    ep: &crate::models::AiEndpoint,
    model: &crate::models::AiModelEntry,
    url: &str,
    profile: Option<&crate::models::VendorProfile>,
) -> Result<(String, Option<String>), (String, String)> {
    let body = serde_json::json!({
        "prompt": "一只卡通兔子在草地上跳跃",
        "model": model.model_id,
    });
    let req = build_authed_request(client.post(url), ep, profile).json(&body);

    match req.send().await {
        Ok(resp) => {
            let status = resp.status();
            let status_code = status.as_u16();
            // 视频创建通常返回 200 或 202
            if status.is_success() || status_code == 202 {
                let body = resp.text().await.unwrap_or_default();
                let video_id = serde_json::from_str::<serde_json::Value>(&body)
                    .ok()
                    .and_then(|v| {
                        v.get("id").or_else(|| v.get("video_id"))
                            .and_then(|id| id.as_str().map(String::from))
                    });
                let summary = if let Some(vid) = &video_id {
                    format!("✅ 视频创建已提交 (ID: {vid})")
                } else {
                    "✅ 视频接口连通成功".into()
                };
                Ok((summary, video_id.map(|v| format!("video_id: {v}"))))
            } else {
                let body = resp.text().await.unwrap_or_default();
                Err((
                    format!("❌ 视频测试失败 (HTTP {status_code})"),
                    parse_error_body(status_code, &body),
                ))
            }
        }
        Err(e) => Err((
            "❌ 视频连接失败".into(),
            format!("网络错误: {e}"),
        )),
    }
}

/// Speech 端点独立测试
async fn test_speech_endpoint(ep: &crate::models::AiEndpoint) -> Vec<crate::models::EndpointTestItem> {
    let mut items = Vec::new();
    let key = if !ep.speech_subscription_key.is_empty() {
        &ep.speech_subscription_key
    } else {
        &ep.api_key
    };
    let region = &ep.speech_region;
    let endpoint = &ep.speech_endpoint;

    if key.is_empty() {
        items.push(crate::models::EndpointTestItem {
            model_id: "speech-sdk".into(),
            capability: "语音翻译".into(),
            status: crate::models::TestStatus::Failed,
            summary: "❌ 订阅密钥为空".into(),
            detail: Some("请在终结点配置中填写 Speech 订阅密钥".into()),
            request_url: None,
            request_summary: None,
            duration_ms: 0,
            test_branch: None,
            urls_tried: vec![],
        });
        return items;
    }

    let t0 = std::time::Instant::now();
    let test_url = if !endpoint.is_empty() {
        let base = endpoint.trim_end_matches('/');
        if base.contains("/sts/") { base.to_string() } else { format!("{base}/sts/v1.0/issuetoken") }
    } else if !region.is_empty() {
        format!("https://{region}.api.cognitive.microsoft.com/sts/v1.0/issuetoken")
    } else {
        items.push(crate::models::EndpointTestItem {
            model_id: "speech-sdk".into(),
            capability: "语音翻译".into(),
            status: crate::models::TestStatus::Failed,
            summary: "❌ 区域和终结点均为空".into(),
            detail: Some("请至少填写区域或终结点".into()),
            request_url: None,
            request_summary: None,
            duration_ms: 0,
            test_branch: None,
            urls_tried: vec![],
        });
        return items;
    };

    let client = reqwest::Client::builder()
        .timeout(std::time::Duration::from_secs(15))
        .build()
        .unwrap();

    let resp = client
        .post(&test_url)
        .header("Ocp-Apim-Subscription-Key", key)
        .header("Content-Length", "0")
        .send()
        .await;

    let dur = t0.elapsed().as_millis() as u64;
    let region_display = if !region.is_empty() { region.as_str() } else { "自定义终结点" };

    let request_summary = format!(
        "认证: Ocp-Apim-Subscription-Key\n\
         终结点: {test_url}\n\
         区域: {region_display}"
    );

    match resp {
        Ok(r) if r.status().is_success() => {
            items.push(crate::models::EndpointTestItem {
                model_id: "speech-sdk".into(),
                capability: "语音翻译".into(),
                status: crate::models::TestStatus::Success,
                summary: format!("✅ Speech 连通成功 (区域: {region_display})"),
                detail: None,
                request_url: Some(format!("POST {test_url}")),
                request_summary: Some(request_summary),
                duration_ms: dur,
                test_branch: Some("Token Issue 接口".into()),
                urls_tried: vec![test_url],
            });
        }
        Ok(r) => {
            let status = r.status().as_u16();
            let body = r.text().await.unwrap_or_default();
            items.push(crate::models::EndpointTestItem {
                model_id: "speech-sdk".into(),
                capability: "语音翻译".into(),
                status: crate::models::TestStatus::Failed,
                summary: format!("❌ Speech 认证失败 (HTTP {status})"),
                detail: Some(if status == 401 {
                    "订阅密钥无效或已过期，请检查密钥和区域是否匹配".into()
                } else {
                    body
                }),
                request_url: Some(format!("POST {test_url}")),
                request_summary: Some(request_summary),
                duration_ms: dur,
                test_branch: Some("Token Issue 接口".into()),
                urls_tried: vec![test_url],
            });
        }
        Err(e) => {
            items.push(crate::models::EndpointTestItem {
                model_id: "speech-sdk".into(),
                capability: "语音翻译".into(),
                status: crate::models::TestStatus::Failed,
                summary: "❌ Speech 连接失败".into(),
                detail: Some(format!("无法连接: {e}")),
                request_url: Some(format!("POST {test_url}")),
                request_summary: Some(request_summary),
                duration_ms: dur,
                test_branch: Some("Token Issue 接口".into()),
                urls_tried: vec![test_url],
            });
        }
    }

    items
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
/// 获取厂商资料包列表（从嵌入的 JSON 解析，与 C# 共用同一份 JSON）
#[tauri::command]
pub async fn get_vendor_profiles() -> Result<Vec<crate::models::VendorProfile>, String> {
    Ok(build_vendor_profiles())
}

fn build_vendor_profiles() -> Vec<crate::models::VendorProfile> {
    crate::profile_loader::load_profiles()
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

    let profiles = build_vendor_profiles();
    let profile = profiles.iter().find(|p| p.endpoint_type == ep.endpoint_type);

    for url in &candidates {
        let req = build_authed_request(client.get(url), &ep, profile);
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
