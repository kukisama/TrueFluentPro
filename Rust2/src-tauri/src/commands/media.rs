use tauri::{Emitter, Manager, State};

use crate::models::*;
use crate::state::AppState;

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

/// 保存图片到本地文件 + 记录到数据库
#[tauri::command]
pub async fn save_image(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    request: SaveImageRequest,
) -> Result<SavedImage, String> {
    use base64::Engine;
    use std::io::Write;

    let bytes = base64::engine::general_purpose::STANDARD
        .decode(&request.base64)
        .map_err(|e| format!("base64 解码失败: {e}"))?;

    let data_dir = app.path().app_data_dir()
        .map_err(|e| format!("获取数据目录失败: {e}"))?;
    let img_dir = data_dir.join("images");
    std::fs::create_dir_all(&img_dir).map_err(|e| format!("创建目录失败: {e}"))?;

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
    state.db.add_saved_image(&record).await.map_err(|e| e.to_string())?;

    tracing::info!("✓ 图片已保存: {} ({} bytes)", file_name, file_size);
    Ok(record)
}

#[tauri::command]
pub async fn list_saved_images(
    state: State<'_, AppState>,
    limit: Option<u32>,
) -> Result<Vec<SavedImage>, String> {
    state.db.list_saved_images(limit.unwrap_or(50)).await.map_err(|e| e.to_string())
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
//  流式 AI 补全
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
                Ok(chunk) => {
                    use crate::providers::StreamChunk;
                    let payload = match chunk {
                        StreamChunk::Token(token) => serde_json::json!({
                            "stream_id": &sid,
                            "token": token,
                        }),
                        StreamChunk::Reasoning(text) => serde_json::json!({
                            "stream_id": &sid,
                            "reasoning": text,
                        }),
                        StreamChunk::Usage { prompt_tokens, completion_tokens } => serde_json::json!({
                            "stream_id": &sid,
                            "usage": { "prompt_tokens": prompt_tokens, "completion_tokens": completion_tokens },
                        }),
                    };
                    let _ = app.emit("ai-stream-token", payload);
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

/// O-06: 提示词优化
#[tauri::command]
pub async fn optimize_prompt(
    _app_handle: tauri::AppHandle,
    state: State<'_, AppState>,
    prompt: String,
    endpoint_id: Option<String>,
) -> Result<String, String> {
    let providers = state.providers.read().await;
    let ep_id = endpoint_id.unwrap_or_default();
    let completion = if ep_id.is_empty() {
        providers.list_providers().iter()
            .find(|p| p.capabilities.contains(&crate::providers::ProviderCapability::AiCompletion))
            .and_then(|p| providers.get_ai_completion(&p.id))
    } else {
        providers.get_ai_completion(&ep_id)
    }.ok_or("未找到 AI Completion Provider，请先配置 AI 端点")?;
    drop(providers);

    let system = "你是一个提示词优化专家。用户会给你一段提示词（可能是对话问题或图片描述），请优化它使其更精确、更有效。仅返回优化后的提示词文本，不要任何解释。".to_string();
    let messages = vec![
        ChatMessage { role: "system".into(), content: serde_json::Value::String(system) },
        ChatMessage { role: "user".into(), content: serde_json::Value::String(prompt) },
    ];
    let req = CompletionRequest {
        messages,
        model: String::new(),
        temperature: Some(0.7),
        max_tokens: Some(1024),
        endpoint_id: ep_id,
    };
    let resp = completion.complete(&req).await.map_err(|e| e.to_string())?;
    Ok(resp.content)
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
pub async fn get_image_model_catalog(app: tauri::AppHandle) -> Result<Vec<crate::image_pipeline::catalog::ModelCapabilityEntry>, String> {
    let resource_path = app.path().resolve("assets/image-models.json", tauri::path::BaseDirectory::Resource);
    if let Ok(path) = resource_path {
        if path.exists() {
            return Ok(crate::image_pipeline::catalog::load_image_models_from_file(&path));
        }
    }
    if let Ok(data_dir) = app.path().app_data_dir() {
        let fallback = data_dir.join("image-models.json");
        if fallback.exists() {
            return Ok(crate::image_pipeline::catalog::load_image_models_from_file(&fallback));
        }
    }
    Ok(crate::image_pipeline::catalog::builtin_image_models())
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  视频生成（完整 create → poll → download 循环）
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

#[tauri::command]
pub async fn generate_video(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    request: crate::models::VideoGenRequest,
) -> Result<String, String> {
    use crate::providers::openai_video::OpenAiVideoProvider;

    // 读取端点配置
    let endpoint = {
        let config = state.config.read().await;
        config.endpoints.iter().find(|e| e.id == request.endpoint_id).cloned()
            .ok_or_else(|| format!("未找到端点: {}", request.endpoint_id))?
    };

    let provider = OpenAiVideoProvider::new(endpoint);
    let task_id = uuid::Uuid::new_v4().to_string();
    let tid = task_id.clone();

    let app_clone = app.clone();
    let data_dir = app.path().app_data_dir()
        .map_err(|e| format!("获取数据目录失败: {e}"))?;

    tokio::spawn(async move {
        // Phase 1: 创建任务
        let _ = app_clone.emit("video-progress", serde_json::json!({
            "task_id": &tid,
            "status": "creating",
            "message": "正在提交视频生成任务...",
        }));

        let video_id = match provider.create_video(&request).await {
            Ok(id) => id,
            Err(e) => {
                let _ = app_clone.emit("video-progress", serde_json::json!({
                    "task_id": &tid,
                    "status": "failed",
                    "error": e.to_string(),
                }));
                return;
            }
        };

        let _ = app_clone.emit("video-progress", serde_json::json!({
            "task_id": &tid,
            "status": "polling",
            "video_id": &video_id,
            "message": "任务已创建，等待生成中...",
        }));

        // Phase 2: 轮询（最多 10 分钟）
        let start_time = std::time::Instant::now();
        let max_duration = std::time::Duration::from_secs(600);
        let poll_interval = std::time::Duration::from_secs(5);
        let mut retry_count = 0;

        loop {
            if start_time.elapsed() > max_duration {
                let _ = app_clone.emit("video-progress", serde_json::json!({
                    "task_id": &tid,
                    "status": "failed",
                    "error": "视频生成超时（10分钟）",
                }));
                return;
            }

            tokio::time::sleep(poll_interval).await;

            match provider.poll_video(&video_id, &request).await {
                Ok(result) => {
                    retry_count = 0;
                    let status_lower = result.status.to_lowercase();

                    let _ = app_clone.emit("video-progress", serde_json::json!({
                        "task_id": &tid,
                        "status": "polling",
                        "video_status": &result.status,
                        "message": format!("状态: {}，已等待 {}s", result.status, start_time.elapsed().as_secs()),
                    }));

                    if status_lower == "succeeded" || status_lower == "completed" || status_lower == "success" {
                        // Phase 3: 下载
                        let download_url = match result.download_url {
                            Some(url) => url,
                            None => {
                                // 尝试构建下载 URL
                                let base = request.endpoint_id.clone();
                                let _ = app_clone.emit("video-progress", serde_json::json!({
                                    "task_id": &tid,
                                    "status": "failed",
                                    "error": format!("视频生成成功但无下载 URL: {base}"),
                                }));
                                return;
                            }
                        };

                        let _ = app_clone.emit("video-progress", serde_json::json!({
                            "task_id": &tid,
                            "status": "downloading",
                            "message": "正在下载视频...",
                        }));

                        let video_dir = data_dir.join("videos");
                        let _ = std::fs::create_dir_all(&video_dir);
                        let file_name = format!(
                            "vid_{}_{}.mp4",
                            chrono::Utc::now().format("%Y%m%d_%H%M%S"),
                            &uuid::Uuid::new_v4().to_string()[..8]
                        );
                        let output_path = video_dir.join(&file_name);

                        match provider.download_video(&download_url, &output_path).await {
                            Ok(path) => {
                                let elapsed = start_time.elapsed().as_secs_f64();
                                let _ = app_clone.emit("video-progress", serde_json::json!({
                                    "task_id": &tid,
                                    "status": "completed",
                                    "file_path": path,
                                    "elapsed_seconds": elapsed,
                                    "message": format!("视频已生成，耗时 {:.1}s", elapsed),
                                }));
                            }
                            Err(e) => {
                                let _ = app_clone.emit("video-progress", serde_json::json!({
                                    "task_id": &tid,
                                    "status": "failed",
                                    "error": format!("下载失败: {e}"),
                                }));
                            }
                        }
                        return;
                    }

                    if status_lower == "failed" || status_lower == "error" || status_lower == "cancelled" {
                        let _ = app_clone.emit("video-progress", serde_json::json!({
                            "task_id": &tid,
                            "status": "failed",
                            "error": format!("视频生成失败: {}", result.status),
                        }));
                        return;
                    }

                    // 其他状态 (pending, running, in_progress) → 继续轮询
                }
                Err(e) => {
                    retry_count += 1;
                    if retry_count >= 3 {
                        let _ = app_clone.emit("video-progress", serde_json::json!({
                            "task_id": &tid,
                            "status": "failed",
                            "error": format!("轮询连续失败 3 次: {e}"),
                        }));
                        return;
                    }
                    tracing::warn!("视频轮询失败 (重试 {retry_count}/3): {e}");
                }
            }
        }
    });

    Ok(task_id)
}
