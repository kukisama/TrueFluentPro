use serde::{Deserialize, Serialize};
use std::path::PathBuf;
use std::sync::Arc;

use tfp_core::{AiEndpoint, EndpointType, EventSink};

use crate::file_cache::FileIdCache;

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  Types
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum ImageApiStrategy {
    ImagesApi,
    ResponsesApi,
}

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
    #[serde(default)]
    pub text_model: Option<String>,
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

/// Internal pipeline context — flows between steps
pub struct PipelineContext {
    pub request: PipelineRequest,
    pub strategy: ImageApiStrategy,
    pub uploaded_file_ids: Vec<String>,
    pub mask_file_id: Option<String>,
    pub request_json_body: Option<serde_json::Value>,
    pub request_headers: Vec<(String, String)>,
    pub request_url: Option<String>,
    pub decoded_images: Vec<Vec<u8>>,
    pub response_id: Option<String>,
    pub revised_prompt: Option<String>,
    pub result_file_paths: Vec<PathBuf>,
    pub optimized_prompt: Option<String>,
    pub error_message: Option<String>,
    pub steps_completed: Vec<String>,
}

impl PipelineContext {
    pub fn new(request: PipelineRequest) -> Self {
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

/// Dependencies for running the pipeline (injected by the shell)
pub struct PipelineDeps {
    pub endpoint: AiEndpoint,
    pub file_id_cache: Arc<FileIdCache>,
    pub sink: Arc<dyn EventSink>,
    pub data_dir: PathBuf,
    /// AI completion provider for prompt optimization (optional)
    pub ai_provider: Option<Arc<dyn tfp_providers::AiCompletionSlot>>,
    pub quick_model: String,
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  Path validation (RV-5)
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

pub fn validate_pipeline_read_path(path: &str) -> Result<PathBuf, String> {
    let p = PathBuf::from(path);
    let canonical = p.canonicalize()
        .map_err(|e| format!("Path resolution failed '{}': {e}", path))?;

    if !canonical.is_file() {
        return Err(format!("Path '{}' is not a regular file", canonical.display()));
    }

    let lower = canonical.to_string_lossy().to_lowercase();
    let blocked = ["\\windows\\system32", "\\windows\\syswow64", "/etc/", "/proc/", "/sys/"];
    for pat in &blocked {
        if lower.contains(pat) {
            return Err(format!("Security: reading from system directory '{}' is not allowed", canonical.display()));
        }
    }

    Ok(canonical)
}

pub fn validate_pipeline_write_dir(dir: &str, app_data_dir: &std::path::Path) -> Result<PathBuf, String> {
    let out = PathBuf::from(dir);
    std::fs::create_dir_all(&out).map_err(|e| format!("Failed to create output directory: {e}"))?;
    let canonical = out.canonicalize()
        .map_err(|e| format!("Output path resolution failed '{}': {e}", dir))?;

    std::fs::create_dir_all(app_data_dir).ok();
    let data_canonical = app_data_dir.canonicalize()
        .map_err(|e| format!("Data directory resolution failed: {e}"))?;

    if !canonical.starts_with(&data_canonical) {
        return Err(format!(
            "Security: output directory '{}' is not within app data directory '{}'",
            canonical.display(), data_canonical.display()
        ));
    }
    Ok(canonical)
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  Pipeline executor
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

pub async fn run_pipeline(
    deps: &PipelineDeps,
    request: PipelineRequest,
) -> Result<PipelineResult, String> {
    // RV-5: Entry path sandbox validation
    for path in &request.reference_image_paths {
        validate_pipeline_read_path(path)?;
    }
    if let Some(ref mask) = request.mask_image_path {
        validate_pipeline_read_path(mask)?;
    }
    if let Some(ref dir) = request.output_directory {
        validate_pipeline_write_dir(dir, &deps.data_dir)?;
    }

    let mut ctx = PipelineContext::new(request);

    let emit_progress = |step: &str, pct: u8| {
        deps.sink.emit_json("image-pipeline-progress", serde_json::json!({
            "step": step, "progress": pct
        }));
    };

    // Step 0: Prompt Optimization (optional)
    if ctx.request.optimize_prompt {
        emit_progress("prompt_optimize", 5);
        step_prompt_optimize(deps, &mut ctx).await;
    }

    // Step 1: Route
    emit_progress("route", 15);
    step_route(&deps.endpoint, &mut ctx);
    ctx.steps_completed.push("route".into());

    // Step 2: Upload (only for Responses API + reference images)
    if ctx.strategy == ImageApiStrategy::ResponsesApi
        && (!ctx.request.reference_image_paths.is_empty() || ctx.request.mask_image_path.is_some())
    {
        emit_progress("upload", 30);
        step_upload(&deps.endpoint, &deps.file_id_cache, &mut ctx).await.map_err(|e| e.to_string())?;
        ctx.steps_completed.push("upload".into());
    }

    // Step 3: Build
    emit_progress("build", 45);
    step_build(&deps.endpoint, &mut ctx);
    ctx.steps_completed.push("build".into());

    // Step 4: Execute
    emit_progress("execute", 60);
    step_execute(&deps.endpoint, &mut ctx).await.map_err(|e| e.to_string())?;
    ctx.steps_completed.push("execute".into());

    // Step 5: Land
    emit_progress("land", 85);
    step_land(&deps.data_dir, &mut ctx).await.map_err(|e| e.to_string())?;
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

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  Base64 helpers
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

pub fn base64_encode(data: &[u8]) -> String {
    use base64::Engine;
    base64::engine::general_purpose::STANDARD.encode(data)
}

pub fn base64_decode(s: &str) -> Result<Vec<u8>, String> {
    use base64::Engine;
    base64::engine::general_purpose::STANDARD.decode(s).map_err(|e| e.to_string())
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  Step 0: Prompt Optimization
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

async fn step_prompt_optimize(deps: &PipelineDeps, ctx: &mut PipelineContext) {
    if let Some(ref ai) = deps.ai_provider {
        let model = if deps.quick_model.is_empty() { "gpt-4.1-mini".to_string() } else { deps.quick_model.clone() };

        let req = tfp_core::CompletionRequest {
            messages: vec![
                tfp_core::ChatMessage {
                    role: "system".into(),
                    content: serde_json::Value::String(
                        "You are an image prompt optimizer. Improve the user's image generation prompt to be more descriptive and effective. Return ONLY the optimized prompt, nothing else.".into(),
                    ),
                },
                tfp_core::ChatMessage {
                    role: "user".into(),
                    content: serde_json::Value::String(ctx.request.prompt.clone()),
                },
            ],
            model,
            temperature: Some(0.7),
            max_tokens: Some(500),
            endpoint_id: deps.endpoint.id.clone(),
        };
        if let Ok(resp) = ai.complete(&req).await {
            ctx.optimized_prompt = Some(resp.content.clone());
            ctx.steps_completed.push("prompt_optimization".into());
        }
    }
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  Step 1: Route
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

fn step_route(ep: &AiEndpoint, ctx: &mut PipelineContext) {
    ctx.strategy = if ep.endpoint_type == EndpointType::ApiManagementGateway
        && ctx.request.text_model.is_some()
        && ctx.request.image_model.is_some()
    {
        ImageApiStrategy::ResponsesApi
    } else if !ctx.request.reference_image_paths.is_empty()
        && ctx.request.text_model.is_some()
    {
        ImageApiStrategy::ResponsesApi
    } else {
        ImageApiStrategy::ImagesApi
    };

    tracing::info!("Pipeline Route: strategy={:?}", ctx.strategy);
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  Step 2: Upload
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

async fn step_upload(
    ep: &AiEndpoint,
    cache: &FileIdCache,
    ctx: &mut PipelineContext,
) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    let client = reqwest::Client::new();
    let base = ep.url.trim_end_matches('/');

    for path_str in &ctx.request.reference_image_paths {
        let file_bytes = tokio::fs::read(path_str).await?;
        if let Some(cached_id) = cache.try_get(&ep.id, &file_bytes) {
            tracing::info!("Pipeline Upload: cache hit for {path_str} → {cached_id}");
            ctx.uploaded_file_ids.push(cached_id);
            continue;
        }
        let file_id = upload_file_bytes(&client, ep, base, path_str, file_bytes.clone()).await?;
        cache.set(&ep.id, &file_bytes, file_id.clone());
        ctx.uploaded_file_ids.push(file_id);
    }

    if let Some(mask_path) = &ctx.request.mask_image_path {
        let file_bytes = tokio::fs::read(mask_path).await?;
        if let Some(cached_id) = cache.try_get(&ep.id, &file_bytes) {
            tracing::info!("Pipeline Upload: cache hit for mask {mask_path} → {cached_id}");
            ctx.mask_file_id = Some(cached_id);
        } else {
            let file_id = upload_file_bytes(&client, ep, base, mask_path, file_bytes.clone()).await?;
            cache.set(&ep.id, &file_bytes, file_id.clone());
            ctx.mask_file_id = Some(file_id);
        }
    }

    Ok(())
}

async fn upload_file_bytes(
    client: &reqwest::Client,
    ep: &AiEndpoint,
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

pub fn apply_auth_to_request(
    req: reqwest::RequestBuilder,
    ep: &AiEndpoint,
) -> reqwest::RequestBuilder {
    use tfp_core::ApiKeyHeaderMode;
    let key = &ep.api_key;
    match &ep.auth_header_mode {
        ApiKeyHeaderMode::Bearer => req.header("Authorization", format!("Bearer {key}")),
        ApiKeyHeaderMode::ApiKeyHeader => req.header("api-key", key),
        ApiKeyHeaderMode::Auto => {
            if ep.endpoint_type == EndpointType::AzureOpenAi
                || ep.endpoint_type == EndpointType::AzureSpeech
            {
                req.header("api-key", key)
            } else {
                req.header("Authorization", format!("Bearer {key}"))
            }
        }
    }
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  Step 3: Build
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

fn step_build(ep: &AiEndpoint, ctx: &mut PipelineContext) {
    let base = ep.url.trim_end_matches('/');
    let prompt = ctx.optimized_prompt.as_deref().unwrap_or(&ctx.request.prompt);

    match ctx.strategy {
        ImageApiStrategy::ResponsesApi => {
            let text_model = ctx.request.text_model.as_deref().unwrap_or("gpt-4.1");
            let image_model = ctx.request.image_model.as_deref().unwrap_or(&ctx.request.model);
            let api_ver = ep.api_version.as_deref().unwrap_or("2025-03-01-preview");

            ctx.request_url = Some(format!("{base}/responses?api-version={api_ver}"));

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

            let api_ver = ep.api_version.as_deref().unwrap_or("2025-04-01-preview");
            let model = &ctx.request.model;
            let candidate_urls = [
                format!("{base}/openai/deployments/{model}/images/generations?api-version={api_ver}"),
                format!("{base}/v1/images/generations"),
                format!("{base}/images/generations"),
            ];
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
//  Step 4: Execute
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

async fn step_execute(
    ep: &AiEndpoint,
    ctx: &mut PipelineContext,
) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    let url = ctx.request_url.as_ref().ok_or("No URL built by Build step")?;
    let body = ctx.request_json_body.as_ref().ok_or("No body built by Build step")?;

    let client = reqwest::Client::new();
    let mut req = client.post(url).json(body);
    req = apply_auth_to_request(req, ep);

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

    ctx.response_id = json_resp["id"].as_str().map(|s| s.to_string());

    // Responses API format: output[].image_generation_call.result
    if let Some(output) = json_resp["output"].as_array() {
        for item in output {
            if item["type"].as_str() == Some("image_generation_call") {
                if let Some(b64) = item["result"].as_str() {
                    ctx.decoded_images.push(base64_decode(b64)?);
                }
            }
        }
    }

    // Images API format: data[].b64_json
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
//  Step 5: Land
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

async fn step_land(
    data_dir: &std::path::Path,
    ctx: &mut PipelineContext,
) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    let out_dir = if let Some(ref dir) = ctx.request.output_directory {
        PathBuf::from(dir)
    } else {
        data_dir.join("generated_images")
    };

    tokio::fs::create_dir_all(&out_dir).await?;

    let format = ctx.request.output_format.as_deref().unwrap_or("png");
    let now = chrono::Utc::now().format("%Y%m%d_%H%M%S");

    for (idx, image_data) in ctx.decoded_images.iter().enumerate() {
        let file_name = format!("img_{now}_{idx}.{format}");
        let final_path = out_dir.join(&file_name);
        let tmp_path = out_dir.join(format!("{file_name}.tmp"));

        tokio::fs::write(&tmp_path, image_data).await?;
        tokio::fs::rename(&tmp_path, &final_path).await?;

        tracing::info!("Pipeline Land: wrote {}", final_path.display());
        ctx.result_file_paths.push(final_path);
    }

    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_pipeline_request_json_fields() {
        let req = PipelineRequest {
            prompt: "a sunset".into(),
            model: "gpt-image-2".into(),
            width: 1024,
            height: 1024,
            quality: Some("auto".into()),
            output_format: Some("png".into()),
            background: Some("auto".into()),
            endpoint_id: "ep1".into(),
            optimize_prompt: false,
            reference_image_paths: vec!["/tmp/ref.png".into()],
            mask_image_path: None,
            previous_response_id: None,
            output_directory: None,
            text_model: Some("gpt-4.1".into()),
            image_model: Some("gpt-image-2".into()),
        };
        let json: serde_json::Value = serde_json::to_value(&req).unwrap();
        let obj = json.as_object().unwrap();

        for key in &[
            "prompt", "model", "width", "height", "quality", "output_format",
            "background", "endpoint_id", "optimize_prompt", "reference_image_paths",
            "mask_image_path", "previous_response_id", "output_directory",
            "text_model", "image_model",
        ] {
            assert!(obj.contains_key(*key), "Missing key: {key}");
        }
    }

    #[test]
    fn test_pipeline_result_json_fields() {
        let result = PipelineResult {
            original_prompt: "a sunset".into(),
            optimized_prompt: Some("A beautiful golden sunset...".into()),
            image_base64: Some("iVBOR".into()),
            image_url: None,
            revised_prompt: None,
            steps_completed: vec!["route".into(), "build".into()],
            response_id: Some("resp-123".into()),
            result_file_paths: vec!["/tmp/out.png".into()],
            error_message: None,
        };
        let json: serde_json::Value = serde_json::to_value(&result).unwrap();
        let obj = json.as_object().unwrap();

        for key in &[
            "original_prompt", "optimized_prompt", "image_base64", "image_url",
            "revised_prompt", "steps_completed", "response_id", "result_file_paths",
            "error_message",
        ] {
            assert!(obj.contains_key(*key), "Missing key: {key}");
        }
    }

    #[test]
    fn test_image_api_strategy_serde() {
        assert_eq!(serde_json::to_value(ImageApiStrategy::ImagesApi).unwrap(), serde_json::json!("ImagesApi"));
        assert_eq!(serde_json::to_value(ImageApiStrategy::ResponsesApi).unwrap(), serde_json::json!("ResponsesApi"));
    }

    #[test]
    fn test_base64_roundtrip() {
        let original = b"hello world";
        let encoded = base64_encode(original);
        let decoded = base64_decode(&encoded).unwrap();
        assert_eq!(decoded, original);
    }

    #[test]
    fn test_base64_decode_invalid() {
        assert!(base64_decode("!!!invalid!!!").is_err());
    }

    #[test]
    fn test_validate_read_path_nonexistent() {
        let result = validate_pipeline_read_path("/nonexistent/path/to/file.png");
        assert!(result.is_err());
        assert!(result.unwrap_err().contains("Path resolution failed"));
    }

    #[test]
    fn test_validate_read_path_directory() {
        let dir = std::env::temp_dir();
        let result = validate_pipeline_read_path(dir.to_str().unwrap());
        assert!(result.is_err());
        assert!(result.unwrap_err().contains("not a regular file"));
    }

    #[test]
    fn test_validate_read_path_valid_file() {
        let dir = std::env::temp_dir();
        let path = dir.join("test_pipeline_read_42.txt");
        std::fs::write(&path, b"test").unwrap();
        let result = validate_pipeline_read_path(path.to_str().unwrap());
        assert!(result.is_ok());
        std::fs::remove_file(&path).ok();
    }

    #[test]
    fn test_route_apim_responses_api() {
        let ep = AiEndpoint {
            endpoint_type: EndpointType::ApiManagementGateway,
            ..Default::default()
        };
        let mut ctx = PipelineContext::new(PipelineRequest {
            prompt: "test".into(),
            model: "gpt-image-2".into(),
            width: 1024, height: 1024,
            quality: None, output_format: None, background: None,
            endpoint_id: "ep1".into(),
            optimize_prompt: false,
            reference_image_paths: vec![],
            mask_image_path: None,
            previous_response_id: None,
            output_directory: None,
            text_model: Some("gpt-4.1".into()),
            image_model: Some("gpt-image-2".into()),
        });
        step_route(&ep, &mut ctx);
        assert_eq!(ctx.strategy, ImageApiStrategy::ResponsesApi);
    }

    #[test]
    fn test_route_default_images_api() {
        let ep = AiEndpoint {
            endpoint_type: EndpointType::AzureOpenAi,
            ..Default::default()
        };
        let mut ctx = PipelineContext::new(PipelineRequest {
            prompt: "test".into(),
            model: "gpt-image-2".into(),
            width: 1024, height: 1024,
            quality: None, output_format: None, background: None,
            endpoint_id: "ep1".into(),
            optimize_prompt: false,
            reference_image_paths: vec![],
            mask_image_path: None,
            previous_response_id: None,
            output_directory: None,
            text_model: None,
            image_model: None,
        });
        step_route(&ep, &mut ctx);
        assert_eq!(ctx.strategy, ImageApiStrategy::ImagesApi);
    }
}