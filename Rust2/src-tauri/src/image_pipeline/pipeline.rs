use serde::{Deserialize, Serialize};
use std::path::PathBuf;
use tauri::Emitter;

/// RV-5: 管道路径沙箱校验
///
/// - 读取路径（参考图）：canonicalize 后确认是普通文件，禁止读取系统敏感目录
/// - 写入路径（输出目录）：必须在 app_data_dir 范围内
fn validate_pipeline_read_path(path: &str) -> Result<PathBuf, String> {
    let p = PathBuf::from(path);
    let canonical = p.canonicalize()
        .map_err(|e| format!("路径解析失败 '{}': {e}", path))?;

    if !canonical.is_file() {
        return Err(format!("路径 '{}' 不是普通文件", canonical.display()));
    }

    // 禁止敏感系统目录（Windows: System32, etc.）
    let lower = canonical.to_string_lossy().to_lowercase();
    let blocked = ["\\windows\\system32", "\\windows\\syswow64", "/etc/", "/proc/", "/sys/"];
    for pat in &blocked {
        if lower.contains(pat) {
            return Err(format!("安全拒绝: 不允许读取系统目录 '{}'", canonical.display()));
        }
    }

    Ok(canonical)
}

fn validate_pipeline_write_dir(app_handle: &tauri::AppHandle, dir: &str) -> Result<PathBuf, String> {
    use tauri::Manager;
    let out = PathBuf::from(dir);
    // 确保父目录存在以便 canonicalize
    std::fs::create_dir_all(&out).map_err(|e| format!("创建输出目录失败: {e}"))?;
    let canonical = out.canonicalize()
        .map_err(|e| format!("输出路径解析失败 '{}': {e}", dir))?;

    let data_dir = app_handle.path().app_data_dir()
        .map_err(|e| format!("无法获取数据目录: {e}"))?;
    std::fs::create_dir_all(&data_dir).ok();
    let data_canonical = data_dir.canonicalize()
        .map_err(|e| format!("数据目录解析失败: {e}"))?;

    if !canonical.starts_with(&data_canonical) {
        return Err(format!(
            "安全拒绝: 输出目录 '{}' 不在应用数据目录 '{}' 范围内",
            canonical.display(), data_canonical.display()
        ));
    }
    Ok(canonical)
}

/// P3-2: 五步图片管道 — 对齐 C# ImagePipelineRunner
///
/// Route → Upload → Build → Execute → Land
///
/// - Route:   决定 API 策略（ResponsesApi / ImagesApi）
/// - Upload:  参考图上传到 /v1/files（如有），FileIdCache 去重
/// - Build:   组装请求 JSON + headers（含 x-ms-oai-image-generation-deployment）
/// - Execute: 发请求 + 候选 URL 降级重试
/// - Land:    base64 → 原子写入磁盘

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  API 策略枚举
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum ImageApiStrategy {
    ImagesApi,
    ResponsesApi,
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  管道上下文 — 在 Step 间流转
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PipelineRequest {
    pub prompt: String,
    pub model: String,
    pub width: u32,
    pub height: u32,
    pub quality: Option<String>,
    pub output_format: Option<String>,
    pub background: Option<String>,
    pub endpoint_id: String,
    pub optimize_prompt: bool,
    #[serde(default)]
    pub reference_image_paths: Vec<String>,
    #[serde(default)]
    pub mask_image_path: Option<String>,
    #[serde(default)]
    pub previous_response_id: Option<String>,
    #[serde(default)]
    pub output_directory: Option<String>,
    /// Responses API 使用的文本模型
    #[serde(default)]
    pub text_model: Option<String>,
    /// Responses API 使用的图片模型部署名
    #[serde(default)]
    pub image_model: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PipelineResult {
    pub original_prompt: String,
    pub optimized_prompt: Option<String>,
    pub image_base64: Option<String>,
    pub image_url: Option<String>,
    pub revised_prompt: Option<String>,
    pub steps_completed: Vec<String>,
    pub response_id: Option<String>,
    pub result_file_paths: Vec<String>,
    pub error_message: Option<String>,
}

/// 管道内部上下文
pub struct PipelineContext {
    pub request: PipelineRequest,
    pub strategy: ImageApiStrategy,
    // Upload 阶段产出
    pub uploaded_file_ids: Vec<String>,
    pub mask_file_id: Option<String>,
    // Build 阶段产出
    pub request_json_body: Option<serde_json::Value>,
    pub request_headers: Vec<(String, String)>,
    pub request_url: Option<String>,
    // Execute 阶段产出
    pub decoded_images: Vec<Vec<u8>>,
    pub response_id: Option<String>,
    pub revised_prompt: Option<String>,
    // Land 阶段产出
    pub result_file_paths: Vec<PathBuf>,
    // Prompt 优化结果
    pub optimized_prompt: Option<String>,
    // 诊断
    pub error_message: Option<String>,
    pub steps_completed: Vec<String>,
}

impl PipelineContext {
    fn new(request: PipelineRequest) -> Self {
        Self {
            request,
            strategy: ImageApiStrategy::ImagesApi,
            uploaded_file_ids: vec![],
            mask_file_id: None,
            request_json_body: None,
            request_headers: vec![],
            request_url: None,
            decoded_images: vec![],
            response_id: None,
            revised_prompt: None,
            result_file_paths: vec![],
            optimized_prompt: None,
            error_message: None,
            steps_completed: vec![],
        }
    }
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  管道执行器
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

pub async fn run_pipeline(
    app_handle: &tauri::AppHandle,
    request: PipelineRequest,
) -> Result<PipelineResult, String> {
    use tauri::Manager;

    // RV-5: 入口路径沙箱校验
    for path in &request.reference_image_paths {
        validate_pipeline_read_path(path)?;
    }
    if let Some(ref mask) = request.mask_image_path {
        validate_pipeline_read_path(mask)?;
    }
    if let Some(ref dir) = request.output_directory {
        validate_pipeline_write_dir(app_handle, dir)?;
    }

    let state = app_handle.state::<crate::state::AppState>();
    let mut ctx = PipelineContext::new(request);

    // O-40: 发射进度事件的辅助闭包
    let emit_progress = |step: &str, pct: u8| {
        let _ = app_handle.emit("image-pipeline-progress", serde_json::json!({
            "step": step, "progress": pct
        }));
    };

    // Step 0: Prompt Optimization（可选）
    if ctx.request.optimize_prompt {
        emit_progress("prompt_optimize", 5);
        step_prompt_optimize(app_handle, &mut ctx).await;
    }

    // Step 1: Route
    emit_progress("route", 15);
    step_route(&state, &mut ctx).await;
    ctx.steps_completed.push("route".into());

    // Step 2: Upload（仅 Responses API + 有参考图时）
    if ctx.strategy == ImageApiStrategy::ResponsesApi
        && (!ctx.request.reference_image_paths.is_empty() || ctx.request.mask_image_path.is_some())
    {
        emit_progress("upload", 30);
        step_upload(&state, &mut ctx).await.map_err(|e| e.to_string())?;
        ctx.steps_completed.push("upload".into());
    }

    // Step 3: Build
    emit_progress("build", 45);
    step_build(&state, &mut ctx).await;
    ctx.steps_completed.push("build".into());

    // Step 4: Execute
    emit_progress("execute", 60);
    step_execute(&state, &mut ctx).await.map_err(|e| e.to_string())?;
    ctx.steps_completed.push("execute".into());

    // Step 5: Land
    emit_progress("land", 85);
    step_land(app_handle, &mut ctx).await.map_err(|e| e.to_string())?;
    ctx.steps_completed.push("land".into());

    emit_progress("done", 100);

    Ok(PipelineResult {
        original_prompt: ctx.request.prompt.clone(),
        optimized_prompt: ctx.optimized_prompt,
        image_base64: ctx.decoded_images.first().map(|d| base64_encode(d)),
        image_url: None,
        revised_prompt: ctx.revised_prompt,
        steps_completed: ctx.steps_completed,
        response_id: ctx.response_id,
        result_file_paths: ctx.result_file_paths.iter().map(|p| p.to_string_lossy().to_string()).collect(),
        error_message: ctx.error_message,
    })
}

fn base64_encode(data: &[u8]) -> String {
    use base64::Engine;
    base64::engine::general_purpose::STANDARD.encode(data)
}

fn base64_decode(s: &str) -> Result<Vec<u8>, String> {
    use base64::Engine;
    base64::engine::general_purpose::STANDARD.decode(s).map_err(|e| e.to_string())
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  Step 0: Prompt Optimization
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

async fn step_prompt_optimize(app_handle: &tauri::AppHandle, ctx: &mut PipelineContext) {
    use tauri::Manager;
    let state = app_handle.state::<crate::state::AppState>();
    let providers = state.providers.read().await;
    let config = state.config.read().await;
    let ai_ep = config.endpoints.iter()
        .find(|ep| ep.enabled && matches!(
            ep.endpoint_type,
            crate::models::EndpointType::AzureOpenAi
            | crate::models::EndpointType::OpenAiCompatible
            | crate::models::EndpointType::ApiManagementGateway
        ))
        .cloned();

    let quick_model_id = config.ai.quick_model.model_id.clone();
    drop(config);

    if let Some(ep) = ai_ep {
        if let Some(ai) = providers.get_ai_completion(&ep.id) {
            drop(providers);
            let model = if quick_model_id.is_empty() { "gpt-4.1-mini".to_string() } else { quick_model_id };

            let req = crate::models::CompletionRequest {
                messages: vec![
                    crate::models::ChatMessage {
                        role: "system".into(),
                        content: serde_json::Value::String(
                            "You are an image prompt optimizer. Improve the user's image generation prompt to be more descriptive and effective. Return ONLY the optimized prompt, nothing else.".into(),
                        ),
                    },
                    crate::models::ChatMessage {
                        role: "user".into(),
                        content: serde_json::Value::String(ctx.request.prompt.clone()),
                    },
                ],
                model,
                temperature: Some(0.7),
                max_tokens: Some(500),
                endpoint_id: ep.id.clone(),
            };
            if let Ok(resp) = ai.complete(&req).await {
                ctx.optimized_prompt = Some(resp.content.clone());
                ctx.steps_completed.push("prompt_optimization".into());
            }
        }
    }
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  Step 1: Route — 决策 API 策略
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

async fn step_route(state: &crate::state::AppState, ctx: &mut PipelineContext) {
    let config = state.config.read().await;
    let ep = config.endpoints.iter().find(|e| e.id == ctx.request.endpoint_id);

    ctx.strategy = if let Some(ep) = ep {
        // APIM 端点 + 有 text_model/image_model → Responses API
        if ep.endpoint_type == crate::models::EndpointType::ApiManagementGateway
            && ctx.request.text_model.is_some()
            && ctx.request.image_model.is_some()
        {
            ImageApiStrategy::ResponsesApi
        } else if !ctx.request.reference_image_paths.is_empty()
            && ctx.request.text_model.is_some()
        {
            // 有参考图且有 text_model → 也用 Responses API
            ImageApiStrategy::ResponsesApi
        } else {
            ImageApiStrategy::ImagesApi
        }
    } else {
        ImageApiStrategy::ImagesApi
    };

    tracing::info!("Pipeline Route: strategy={:?}", ctx.strategy);
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  Step 2: Upload — 参考图上传 /v1/files
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

async fn step_upload(
    state: &crate::state::AppState,
    ctx: &mut PipelineContext,
) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    let config = state.config.read().await;
    let ep = config.endpoints.iter()
        .find(|e| e.id == ctx.request.endpoint_id)
        .cloned()
        .ok_or("Endpoint not found")?;
    drop(config);

    let client = reqwest::Client::new();
    let base = ep.url.trim_end_matches('/');
    let cache = &state.file_id_cache;

    for path_str in &ctx.request.reference_image_paths {
        let file_bytes = tokio::fs::read(path_str).await?;
        // P3-6-FIX: FileIdCache 去重 — 先查缓存，命中则跳过上传
        if let Some(cached_id) = cache.try_get(&ep.id, &file_bytes) {
            tracing::info!("Pipeline Upload: cache hit for {path_str} → {cached_id}");
            ctx.uploaded_file_ids.push(cached_id);
            continue;
        }
        let file_id = upload_file_bytes(&client, &ep, base, path_str, file_bytes.clone()).await?;
        cache.set(&ep.id, &file_bytes, file_id.clone());
        ctx.uploaded_file_ids.push(file_id);
    }

    if let Some(mask_path) = &ctx.request.mask_image_path {
        let file_bytes = tokio::fs::read(mask_path).await?;
        if let Some(cached_id) = cache.try_get(&ep.id, &file_bytes) {
            tracing::info!("Pipeline Upload: cache hit for mask {mask_path} → {cached_id}");
            ctx.mask_file_id = Some(cached_id);
        } else {
            let file_id = upload_file_bytes(&client, &ep, base, mask_path, file_bytes.clone()).await?;
            cache.set(&ep.id, &file_bytes, file_id.clone());
            ctx.mask_file_id = Some(file_id);
        }
    }

    Ok(())
}

/// 上传已读取的文件字节到端点 /v1/files
async fn upload_file_bytes(
    client: &reqwest::Client,
    ep: &crate::models::AiEndpoint,
    base: &str,
    file_path: &str,
    file_bytes: Vec<u8>,
) -> Result<String, Box<dyn std::error::Error + Send + Sync>> {
    let url = format!("{base}/v1/files");
    let file_name = std::path::Path::new(file_path)
        .file_name()
        .unwrap_or_default()
        .to_string_lossy()
        .to_string();

    let part = reqwest::multipart::Part::bytes(file_bytes)
        .file_name(file_name)
        .mime_str("application/octet-stream")?;

    let form = reqwest::multipart::Form::new()
        .text("purpose", "assistants")
        .part("file", part);

    let mut req = client.post(&url).multipart(form);
    req = apply_auth_to_request(req, ep);

    let resp = req.send().await?;
    if !resp.status().is_success() {
        let text = resp.text().await.unwrap_or_default();
        return Err(format!("Upload failed: {text}").into());
    }

    let json: serde_json::Value = resp.json().await?;
    json["id"]
        .as_str()
        .map(|s| s.to_string())
        .ok_or_else(|| "Upload response missing 'id' field".into())
}

fn apply_auth_to_request(
    req: reqwest::RequestBuilder,
    ep: &crate::models::AiEndpoint,
) -> reqwest::RequestBuilder {
    let key = &ep.api_key;
    let mode = ep.auth_header_mode.as_str();
    match mode {
        "bearer" => req.header("Authorization", format!("Bearer {key}")),
        "api_key" => req.header("api-key", key),
        _ => {
            // auto: Azure endpoints → api-key, others → Bearer
            if ep.endpoint_type == crate::models::EndpointType::AzureOpenAi
                || ep.endpoint_type == crate::models::EndpointType::AzureSpeech
            {
                req.header("api-key", key)
            } else {
                req.header("Authorization", format!("Bearer {key}"))
            }
        }
    }
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  Step 3: Build — 组装请求 body + headers
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

async fn step_build(state: &crate::state::AppState, ctx: &mut PipelineContext) {
    let config = state.config.read().await;
    let ep = config.endpoints.iter().find(|e| e.id == ctx.request.endpoint_id).cloned();
    drop(config);

    let ep = match ep {
        Some(e) => e,
        None => return,
    };

    let base = ep.url.trim_end_matches('/');
    let prompt = ctx.optimized_prompt.as_deref().unwrap_or(&ctx.request.prompt);

    match ctx.strategy {
        ImageApiStrategy::ResponsesApi => {
            let text_model = ctx.request.text_model.as_deref().unwrap_or("gpt-4.1");
            let image_model = ctx.request.image_model.as_deref().unwrap_or(&ctx.request.model);
            let api_ver = ep.api_version.as_deref().unwrap_or("2025-03-01-preview");

            ctx.request_url = Some(format!("{base}/responses?api-version={api_ver}"));

            // 构建 input（text + file_id 引用）
            let mut input = vec![serde_json::json!({
                "type": "input_text",
                "text": prompt,
            })];

            for fid in &ctx.uploaded_file_ids {
                input.push(serde_json::json!({
                    "type": "input_image",
                    "file_id": fid,
                }));
            }

            let mut body = serde_json::json!({
                "model": text_model,
                "input": input,
                "tools": [{ "type": "image_generation" }],
            });

            if let Some(ref prev_id) = ctx.request.previous_response_id {
                body["previous_response_id"] = serde_json::json!(prev_id);
            }

            ctx.request_json_body = Some(body);
            ctx.request_headers.push((
                "x-ms-oai-image-generation-deployment".into(),
                image_model.into(),
            ));
        }
        ImageApiStrategy::ImagesApi => {
            let size = format!("{}x{}", ctx.request.width, ctx.request.height);
            let output_format = ctx.request.output_format.as_deref().unwrap_or("png");

            // 候选 URL
            let api_ver = ep.api_version.as_deref().unwrap_or("2025-04-01-preview");
            let model = &ctx.request.model;
            let candidate_urls = vec![
                format!("{base}/openai/deployments/{model}/images/generations?api-version={api_ver}"),
                format!("{base}/v1/images/generations"),
                format!("{base}/images/generations"),
            ];
            // 用第一个作为默认
            ctx.request_url = candidate_urls.first().cloned();

            let mut body = serde_json::json!({
                "prompt": prompt,
                "model": model,
                "size": size,
                "quality": ctx.request.quality.as_deref().unwrap_or("auto"),
                "output_format": output_format,
            });

            if let Some(ref bg) = ctx.request.background {
                if bg != "auto" && !bg.is_empty() {
                    body["background"] = serde_json::json!(bg);
                }
            }

            ctx.request_json_body = Some(body);
        }
    }
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  Step 4: Execute — 发请求 + 解析响应
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

async fn step_execute(
    state: &crate::state::AppState,
    ctx: &mut PipelineContext,
) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    let config = state.config.read().await;
    let ep = config.endpoints.iter()
        .find(|e| e.id == ctx.request.endpoint_id)
        .cloned()
        .ok_or("Endpoint not found")?;
    drop(config);

    let url = ctx.request_url.as_ref().ok_or("No URL built by Build step")?;
    let body = ctx.request_json_body.as_ref().ok_or("No body built by Build step")?;

    let client = reqwest::Client::new();
    let mut req = client.post(url).json(body);
    req = apply_auth_to_request(req, &ep);

    for (k, v) in &ctx.request_headers {
        req = req.header(k.as_str(), v.as_str());
    }

    let resp = req.send().await?;
    let status = resp.status();
    if !status.is_success() {
        let text = resp.text().await.unwrap_or_default();
        ctx.error_message = Some(format!("{status}: {text}"));
        return Err(format!("{status}: {text}").into());
    }

    let json_resp: serde_json::Value = resp.json().await?;

    // 提取 response_id
    ctx.response_id = json_resp["id"].as_str().map(|s| s.to_string());

    // Responses API 格式: output[].image_generation_call.result
    if let Some(output) = json_resp["output"].as_array() {
        for item in output {
            if item["type"].as_str() == Some("image_generation_call") {
                if let Some(b64) = item["result"].as_str() {
                    ctx.decoded_images.push(base64_decode(b64)?);
                }
            }
        }
    }

    // Images API 格式: data[].b64_json
    if ctx.decoded_images.is_empty() {
        if let Some(data) = json_resp["data"].as_array() {
            for item in data {
                if let Some(b64) = item["b64_json"].as_str() {
                    ctx.decoded_images.push(base64_decode(b64)?);
                }
                ctx.revised_prompt = item["revised_prompt"].as_str().map(|s| s.to_string());
            }
        }
    }

    if ctx.decoded_images.is_empty() {
        ctx.error_message = Some("No images in response".into());
        return Err("No images in response".into());
    }

    Ok(())
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  Step 5: Land — 原子写入磁盘
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

async fn step_land(
    app_handle: &tauri::AppHandle,
    ctx: &mut PipelineContext,
) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    use tauri::Manager;

    let out_dir = if let Some(ref dir) = ctx.request.output_directory {
        PathBuf::from(dir)
    } else {
        let data_dir = app_handle.path().app_data_dir()
            .unwrap_or_else(|_| PathBuf::from("."));
        data_dir.join("generated_images")
    };

    tokio::fs::create_dir_all(&out_dir).await?;

    let format = ctx.request.output_format.as_deref().unwrap_or("png");
    let now = chrono::Utc::now().format("%Y%m%d_%H%M%S");

    for (idx, image_data) in ctx.decoded_images.iter().enumerate() {
        let file_name = format!("img_{now}_{idx}.{format}");
        let final_path = out_dir.join(&file_name);
        let tmp_path = out_dir.join(format!("{file_name}.tmp"));

        // 原子写入: 先写 .tmp，再 rename
        tokio::fs::write(&tmp_path, image_data).await?;
        tokio::fs::rename(&tmp_path, &final_path).await?;

        tracing::info!("Pipeline Land: 写入 {}", final_path.display());
        ctx.result_file_paths.push(final_path);
    }

    Ok(())
}
