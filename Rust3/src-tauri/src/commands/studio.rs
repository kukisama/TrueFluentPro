use tauri::{Emitter, Manager, State};

use crate::state::AppState;
use tfp_core::{
    AppConfig, ChatMessage, CompletionRequest, ImageGenRequest,
    StudioMessage, StudioMediaRef, StudioReferenceImage, StudioSession,
    StudioSessionBundle, StudioTask, VideoApiMode, VideoGenRequest,
};
use tfp_providers::{OpenAiVideoProvider, StreamChunk, VideoGenSlot};

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  Studio commands
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

#[tauri::command]
pub async fn studio_list_sessions(
    state: State<'_, AppState>,
    limit: Option<i64>,
    offset: Option<i64>,
) -> Result<Vec<StudioSession>, String> {
    state.db.studio_list_sessions(limit.unwrap_or(30), offset.unwrap_or(0))
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn studio_get_session(
    state: State<'_, AppState>,
    session_id: String,
) -> Result<Option<StudioSession>, String> {
    state.db.studio_get_session(&session_id)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn studio_create_session(
    state: State<'_, AppState>,
    session_type: String,
    name: String,
) -> Result<StudioSession, String> {
    let now = chrono::Utc::now().to_rfc3339();
    let id = uuid::Uuid::new_v4().to_string();
    let session = StudioSession {
        id: id.clone(),
        session_type,
        name,
        directory_path: String::new(),
        canvas_mode: String::new(),
        media_kind: String::new(),
        is_deleted: false,
        created_at: now.clone(),
        updated_at: now.clone(),
        last_accessed_at: Some(now),
        source_session_id: None,
        source_session_name: None,
        source_session_directory_name: None,
        source_asset_id: None,
        source_asset_kind: None,
        source_asset_file_name: None,
        source_asset_path: None,
        source_preview_path: None,
        source_reference_role: None,
        message_count: 0,
        task_count: 0,
        asset_count: 0,
        latest_message_preview: None,
        legacy_source_path: None,
        import_batch_id: None,
        imported_at: None,
        is_legacy_import: false,
    };
    state.db.studio_create_session(&session).await.map_err(|e| e.to_string())?;
    Ok(session)
}

#[tauri::command]
pub async fn studio_rename_session(
    state: State<'_, AppState>,
    session_id: String,
    new_name: String,
) -> Result<(), String> {
    state.db.studio_rename_session(&session_id, &new_name)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn studio_soft_delete_session(
    state: State<'_, AppState>,
    session_id: String,
) -> Result<(), String> {
    state.db.studio_soft_delete_session(&session_id)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn studio_get_session_bundle(
    state: State<'_, AppState>,
    session_id: String,
) -> Result<StudioSessionBundle, String> {
    let _ = state.db.studio_update_last_accessed(&session_id).await;
    state.db.studio_get_session_bundle(&session_id)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn studio_append_message(
    state: State<'_, AppState>,
    session_id: String,
    role: String,
    text: String,
    content_type: Option<String>,
    reasoning_text: Option<String>,
    prompt_tokens: Option<i64>,
    completion_tokens: Option<i64>,
    generate_seconds: Option<f64>,
    download_seconds: Option<f64>,
    search_summary: Option<String>,
) -> Result<StudioMessage, String> {
    let now = chrono::Utc::now().to_rfc3339();
    let id = uuid::Uuid::new_v4().to_string();
    let seq = state
        .db
        .studio_get_max_sequence(&session_id)
        .await
        .map_err(|e| e.to_string())?
        + 1;

    let msg = StudioMessage {
        id: id.clone(),
        session_id: session_id.clone(),
        sequence_no: seq,
        role,
        content_type: content_type.unwrap_or_else(|| "text".to_string()),
        text: text.clone(),
        reasoning_text: reasoning_text.unwrap_or_default(),
        prompt_tokens,
        completion_tokens,
        generate_seconds,
        download_seconds,
        search_summary,
        timestamp: now,
        is_deleted: false,
    };

    state
        .db
        .studio_append_message(&msg)
        .await
        .map_err(|e| e.to_string())?;

    // UTF-8 safe preview truncation
    let preview: String = text.chars().take(100).collect();
    let _ = state
        .db
        .studio_update_latest_preview(&session_id, &preview)
        .await;

    Ok(msg)
}

#[tauri::command]
pub async fn studio_get_messages_before(
    state: State<'_, AppState>,
    session_id: String,
    before_sequence: i64,
    limit: Option<i64>,
) -> Result<Vec<StudioMessage>, String> {
    state.db.studio_get_messages_before(&session_id, before_sequence, limit.unwrap_or(40))
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn studio_list_running_tasks(
    state: State<'_, AppState>,
    session_id: String,
) -> Result<Vec<StudioTask>, String> {
    state.db.studio_list_running_tasks(&session_id)
        .await
        .map_err(|e| e.to_string())
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  Chat streaming
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

#[tauri::command]
pub async fn studio_chat_stream(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    session_id: String,
    text: String,
    endpoint_id: String,
    model: String,
) -> Result<String, String> {
    // 1. Persist user message
    let now = chrono::Utc::now().to_rfc3339();
    let user_msg_id = uuid::Uuid::new_v4().to_string();
    let seq = state.db.studio_get_max_sequence(&session_id).await.map_err(|e| e.to_string())? + 1;
    let user_msg = StudioMessage {
        id: user_msg_id.clone(),
        session_id: session_id.clone(),
        sequence_no: seq,
        role: "user".to_string(),
        content_type: "text".to_string(),
        text: text.clone(),
        reasoning_text: String::new(),
        prompt_tokens: None,
        completion_tokens: None,
        generate_seconds: None,
        download_seconds: None,
        search_summary: None,
        timestamp: now.clone(),
        is_deleted: false,
    };
    state.db.studio_append_message(&user_msg).await.map_err(|e| e.to_string())?;

    // UTF-8 safe preview truncation
    let preview: String = text.chars().take(100).collect();
    let _ = state.db.studio_update_latest_preview(&session_id, &preview).await;

    // 2. Build chat history
    let bundle = state.db.studio_get_session_bundle(&session_id).await.map_err(|e| e.to_string())?;
    let mut chat_messages: Vec<ChatMessage> = Vec::new();
    for msg in &bundle.messages {
        if msg.role == "user" || msg.role == "assistant" {
            chat_messages.push(ChatMessage {
                role: msg.role.clone(),
                content: serde_json::Value::String(msg.text.clone()),
            });
        }
    }
    // Limit history (max 20 turns = 40 messages)
    if chat_messages.len() > 40 {
        chat_messages = chat_messages[chat_messages.len() - 40..].to_vec();
    }

    // 3. Get provider (before spawn — RwLock guard cannot cross spawn boundary)
    let providers = state.providers.read().await;
    let provider = providers
        .get_ai_completion(&endpoint_id)
        .ok_or_else(|| format!("AI Provider not found: {}", endpoint_id))?;

    let req = CompletionRequest {
        messages: chat_messages,
        model,
        temperature: Some(0.7),
        max_tokens: Some(4096),
        endpoint_id: endpoint_id.clone(),
    };

    let mut rx = provider.complete_stream(&req).await.map_err(|e| e.to_string())?;
    drop(providers);

    let stream_id = uuid::Uuid::new_v4().to_string();
    let assistant_msg_id = uuid::Uuid::new_v4().to_string();
    let db = state.db.clone();
    let sid = session_id.clone();
    let aid = assistant_msg_id.clone();

    // 4. Background task: stream emit + persist assistant message on completion
    tokio::spawn(async move {
        let start = std::time::Instant::now();
        let mut full_text = String::new();
        let mut reasoning = String::new();
        let mut p_tokens: Option<i64> = None;
        let mut c_tokens: Option<i64> = None;

        while let Some(result) = rx.recv().await {
            match result {
                Ok(chunk) => match chunk {
                    StreamChunk::Token(token) => {
                        full_text.push_str(&token);
                        let _ = app.emit("studio-message-delta", serde_json::json!({
                            "session_id": &sid,
                            "message_id": &aid,
                            "token": token,
                        }));
                    }
                    StreamChunk::Reasoning(text) => {
                        reasoning.push_str(&text);
                        let _ = app.emit("studio-message-delta", serde_json::json!({
                            "session_id": &sid,
                            "message_id": &aid,
                            "reasoning": text,
                        }));
                    }
                    StreamChunk::Usage { prompt_tokens, completion_tokens } => {
                        p_tokens = Some(prompt_tokens as i64);
                        c_tokens = Some(completion_tokens as i64);
                    }
                },
                Err(e) => {
                    let _ = app.emit("studio-message-delta", serde_json::json!({
                        "session_id": &sid,
                        "message_id": &aid,
                        "error": e.to_string(),
                        "done": true,
                    }));
                    return;
                }
            }
        }

        // 5. Stream ended — persist assistant message
        let gen_secs = start.elapsed().as_secs_f64();
        let seq2 = db.studio_get_max_sequence(&sid).await.unwrap_or(0) + 1;
        let msg = StudioMessage {
            id: aid.clone(),
            session_id: sid.clone(),
            sequence_no: seq2,
            role: "assistant".to_string(),
            content_type: "text".to_string(),
            text: full_text,
            reasoning_text: reasoning,
            prompt_tokens: p_tokens,
            completion_tokens: c_tokens,
            generate_seconds: Some(gen_secs),
            download_seconds: None,
            search_summary: None,
            timestamp: chrono::Utc::now().to_rfc3339(),
            is_deleted: false,
        };
        let _ = db.studio_append_message(&msg).await;

        let _ = app.emit("studio-message-delta", serde_json::json!({
            "session_id": &sid,
            "message_id": &aid,
            "done": true,
        }));
    });

    Ok(serde_json::json!({
        "stream_id": stream_id,
        "user_message": user_msg,
        "assistant_message_id": assistant_msg_id,
    }).to_string())
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  Image task
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

#[tauri::command]
pub async fn studio_start_image_task(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    session_id: String,
    prompt: String,
    params: serde_json::Value,
    reference_paths: Vec<String>,
) -> Result<String, String> {
    let now = chrono::Utc::now().to_rfc3339();
    let task_id = uuid::Uuid::new_v4().to_string();

    let task = StudioTask {
        id: task_id.clone(),
        session_id: session_id.clone(),
        task_type: "image".to_string(),
        status: "running".to_string(),
        prompt: prompt.clone(),
        progress: 0.0,
        result_file_path: None,
        error_message: None,
        has_reference_input: !reference_paths.is_empty(),
        remote_video_id: None,
        remote_video_api_mode: None,
        remote_generation_id: None,
        remote_download_url: None,
        generate_seconds: None,
        download_seconds: None,
        created_at: now.clone(),
        updated_at: now,
    };
    state.db.studio_upsert_task(&task).await.map_err(|e| e.to_string())?;

    // Update session task_count
    if let Ok(Some(session)) = state.db.studio_get_session(&session_id).await {
        let _ = state.db.studio_update_counts(
            &session_id, session.message_count, session.task_count + 1, session.asset_count,
        ).await;
    }

    // Get provider Arc before spawn (RwLock guard cannot cross spawn boundary)
    let endpoint_id = params.get("endpoint_id").and_then(|v| v.as_str()).unwrap_or("").to_string();
    let provider = {
        let reg = state.providers.read().await;
        reg.get_image_gen(&endpoint_id)
    };
    let provider = match provider {
        Some(p) => p,
        None => return Err(format!("Image generation provider not found: {}", endpoint_id)),
    };

    let db = state.db.clone();
    let data_dir = app.path().app_data_dir().map_err(|e| e.to_string())?;
    let app_handle = app.clone();
    let tid = task_id.clone();
    let sid = session_id.clone();

    tokio::spawn(async move {
        let start = std::time::Instant::now();

        let width = params.get("width").and_then(|v| v.as_u64()).unwrap_or(1024) as u32;
        let height = params.get("height").and_then(|v| v.as_u64()).unwrap_or(1024) as u32;
        let quality = params.get("quality").and_then(|v| v.as_str()).unwrap_or("auto").to_string();
        let format = params.get("format").and_then(|v| v.as_str()).unwrap_or("png").to_string();
        let background = params.get("background").and_then(|v| v.as_str()).unwrap_or("auto").to_string();
        let n = params.get("n").and_then(|v| v.as_u64()).unwrap_or(1) as u32;
        let model = params.get("model").and_then(|v| v.as_str()).unwrap_or("gpt-image-1").to_string();

        let request = ImageGenRequest {
            prompt: prompt.clone(),
            width,
            height,
            model: model.clone(),
            quality: Some(quality),
            output_format: Some(format.clone()),
            background: Some(background),
            n: Some(n),
            endpoint_id: endpoint_id.clone(),
            text_model: None,
            image_model: None,
            previous_response_id: None,
        };

        let _ = app_handle.emit("studio-task-update", serde_json::json!({
            "task_id": tid, "session_id": sid, "status": "running", "progress": 0.1,
        }));

        let result = provider.generate(&request).await;
        let gen_secs = start.elapsed().as_secs_f64();

        match result {
            Ok(images) => {
                let img_dir = data_dir.join("studio_images");
                let _ = std::fs::create_dir_all(&img_dir);

                let mut saved_paths = Vec::new();
                for (i, img) in images.iter().enumerate() {
                    if let Some(b64) = &img.base64 {
                        use base64::Engine;
                        if let Ok(bytes) = base64::engine::general_purpose::STANDARD.decode(b64) {
                            let ext = match format.as_str() {
                                "webp" => "webp",
                                "jpeg" | "jpg" => "jpg",
                                _ => "png",
                            };
                            let fname = format!("{}_{}.{}", &tid[..8], i, ext);
                            let path = img_dir.join(&fname);
                            if std::fs::write(&path, &bytes).is_ok() {
                                saved_paths.push(path.to_string_lossy().to_string());
                            }
                        }
                    }
                }

                let result_path = saved_paths.first().cloned().unwrap_or_default();
                let _ = db.studio_update_task_result(&tid, &result_path, Some(gen_secs), None).await;

                // Create assistant message + media refs
                let msg_id = uuid::Uuid::new_v4().to_string();
                let seq = db.studio_get_max_sequence(&sid).await.unwrap_or(0) + 1;
                let revised = images.first().and_then(|i| i.revised_prompt.clone()).unwrap_or_default();
                let msg_text = if revised.is_empty() {
                    format!("[Generated {} image(s)]", saved_paths.len())
                } else {
                    revised
                };
                let msg = StudioMessage {
                    id: msg_id.clone(),
                    session_id: sid.clone(),
                    sequence_no: seq,
                    role: "assistant".to_string(),
                    content_type: "image".to_string(),
                    text: msg_text,
                    reasoning_text: String::new(),
                    prompt_tokens: None,
                    completion_tokens: None,
                    generate_seconds: Some(gen_secs),
                    download_seconds: None,
                    search_summary: None,
                    timestamp: chrono::Utc::now().to_rfc3339(),
                    is_deleted: false,
                };
                let _ = db.studio_append_message(&msg).await;

                let media_refs: Vec<StudioMediaRef> = saved_paths.iter().enumerate().map(|(i, p)| {
                    StudioMediaRef {
                        id: 0,
                        message_id: msg_id.clone(),
                        media_path: p.clone(),
                        media_kind: "image".to_string(),
                        sort_order: i as i64,
                        preview_path: None,
                    }
                }).collect();
                let _ = db.studio_insert_media_refs(&msg_id, &media_refs).await;

                let _ = app_handle.emit("studio-task-update", serde_json::json!({
                    "task_id": tid, "session_id": sid, "status": "completed",
                    "progress": 1.0, "result_paths": saved_paths,
                }));

                let _ = app_handle.emit("studio-message-new", serde_json::json!({
                    "session_id": sid, "message": msg, "media_refs": media_refs,
                }));
            }
            Err(e) => {
                let err_msg = e.to_string();
                let _ = db.studio_update_task_status(&tid, "failed", Some(&err_msg)).await;
                let _ = app_handle.emit("studio-task-update", serde_json::json!({
                    "task_id": tid, "session_id": sid, "status": "failed", "error": err_msg,
                }));
            }
        }
    });

    Ok(task_id)
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  Video task
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

#[tauri::command]
pub async fn studio_start_video_task(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    session_id: String,
    prompt: String,
    params: serde_json::Value,
    reference_path: Option<String>,
) -> Result<String, String> {
    let now = chrono::Utc::now().to_rfc3339();
    let task_id = uuid::Uuid::new_v4().to_string();

    let task = StudioTask {
        id: task_id.clone(),
        session_id: session_id.clone(),
        task_type: "video".to_string(),
        status: "running".to_string(),
        prompt: prompt.clone(),
        progress: 0.0,
        result_file_path: None,
        error_message: None,
        has_reference_input: reference_path.is_some(),
        remote_video_id: None,
        remote_video_api_mode: None,
        remote_generation_id: None,
        remote_download_url: None,
        generate_seconds: None,
        download_seconds: None,
        created_at: now.clone(),
        updated_at: now.clone(),
    };
    state.db.studio_upsert_task(&task).await.map_err(|e| e.to_string())?;

    // Resolve endpoint config before spawn
    let endpoint_id = params.get("endpoint_id").and_then(|v| v.as_str()).unwrap_or("").to_string();
    let ep_info = {
        let config = state.config.read().await;
        config.endpoints.iter().find(|e| e.id == endpoint_id).cloned()
    };
    let ep = match ep_info {
        Some(e) => e,
        None => return Err("Endpoint not found".to_string()),
    };

    let db = state.db.clone();
    let data_dir = app.path().app_data_dir().map_err(|e| e.to_string())?;
    let app_handle = app.clone();
    let tid = task_id.clone();
    let sid = session_id.clone();
    let task_created_at = task.created_at.clone();

    tokio::spawn(async move {
        let start = std::time::Instant::now();
        let provider = OpenAiVideoProvider::new(ep);

        let model = params.get("model").and_then(|v| v.as_str()).unwrap_or("sora").to_string();
        let size = params.get("size").and_then(|v| v.as_str()).unwrap_or("1080x1920").to_string();
        let duration = params.get("duration_seconds").and_then(|v| v.as_u64()).unwrap_or(10) as u32;
        let n = params.get("n").and_then(|v| v.as_u64()).map(|v| v as u32);

        let request = VideoGenRequest {
            prompt: prompt.clone(),
            model: model.clone(),
            endpoint_id: endpoint_id.clone(),
            size,
            duration_seconds: duration,
            api_mode: None,
            reference_image_path: reference_path,
            n,
        };

        let _ = app_handle.emit("studio-task-update", serde_json::json!({
            "task_id": tid, "session_id": sid, "status": "running", "progress": 0.1,
        }));

        // Phase 1: Create video
        let gen_result = match provider.generate(&request).await {
            Ok(r) => r,
            Err(e) => {
                let err_msg = format!("create_video: {e}");
                let _ = db.studio_update_task_status(&tid, "failed", Some(&err_msg)).await;
                let _ = app_handle.emit("studio-task-update", serde_json::json!({
                    "task_id": tid, "session_id": sid, "status": "failed", "error": err_msg,
                }));
                return;
            }
        };

        let video_id = gen_result.video_id;

        // Persist remote_video_id for crash recovery
        let api_mode_str = if model.contains("sora-2") || model == "sora" {
            "videos"
        } else {
            "sora_jobs"
        };
        {
            let now2 = chrono::Utc::now().to_rfc3339();
            let _ = db.studio_upsert_task(&StudioTask {
                id: tid.clone(),
                session_id: sid.clone(),
                task_type: "video".to_string(),
                status: "running".to_string(),
                prompt: prompt.clone(),
                progress: 0.2,
                result_file_path: None,
                error_message: None,
                has_reference_input: false,
                remote_video_id: Some(video_id.clone()),
                remote_video_api_mode: Some(api_mode_str.to_string()),
                remote_generation_id: None,
                remote_download_url: None,
                generate_seconds: None,
                download_seconds: None,
                created_at: task_created_at,
                updated_at: now2,
            }).await;
        }

        // Phase 2: Poll (max 10 minutes)
        let poll_interval = std::time::Duration::from_secs(5);
        let max_polls = 120;
        let mut download_url = None;

        for poll_i in 0..max_polls {
            tokio::time::sleep(poll_interval).await;
            match provider.poll_status(&video_id, &endpoint_id).await {
                Ok(result) => {
                    let progress = 0.2 + (poll_i as f64 / max_polls as f64) * 0.6;
                    let _ = db.studio_update_task_progress(&tid, progress).await;
                    let _ = app_handle.emit("studio-task-update", serde_json::json!({
                        "task_id": tid, "session_id": sid, "status": "running", "progress": progress,
                    }));

                    let s = result.status.to_lowercase();
                    if s == "succeeded" || s == "completed" || s == "success" {
                        download_url = result.download_url;
                        break;
                    } else if s == "failed" {
                        let err_msg = "Video generation failed (remote returned failed)";
                        let _ = db.studio_update_task_status(&tid, "failed", Some(err_msg)).await;
                        let _ = app_handle.emit("studio-task-update", serde_json::json!({
                            "task_id": tid, "session_id": sid, "status": "failed", "error": err_msg,
                        }));
                        return;
                    }
                }
                Err(_) => continue,
            }
        }

        let dl_url = match download_url {
            Some(u) if !u.is_empty() => u,
            _ => {
                let _ = db.studio_update_task_status(&tid, "failed", Some("Video generation timed out or no download URL")).await;
                let _ = app_handle.emit("studio-task-update", serde_json::json!({
                    "task_id": tid, "session_id": sid, "status": "failed", "error": "Timeout",
                }));
                return;
            }
        };

        // Phase 3: Download
        let dl_start = std::time::Instant::now();
        let vid_dir = data_dir.join("studio_videos");
        let _ = std::fs::create_dir_all(&vid_dir);
        let fname = format!("{}.mp4", &tid[..8]);
        let output_path = vid_dir.join(&fname);

        match studio_download_video(&dl_url, &output_path).await {
            Ok(path_str) => {
                let gen_secs = start.elapsed().as_secs_f64();
                let dl_secs = dl_start.elapsed().as_secs_f64();
                let _ = db.studio_update_task_result(&tid, &path_str, Some(gen_secs), Some(dl_secs)).await;

                // Create assistant message
                let msg_id = uuid::Uuid::new_v4().to_string();
                let seq = db.studio_get_max_sequence(&sid).await.unwrap_or(0) + 1;
                let msg = StudioMessage {
                    id: msg_id.clone(),
                    session_id: sid.clone(),
                    sequence_no: seq,
                    role: "assistant".to_string(),
                    content_type: "video".to_string(),
                    text: "[Video generated]".to_string(),
                    reasoning_text: String::new(),
                    prompt_tokens: None,
                    completion_tokens: None,
                    generate_seconds: Some(gen_secs),
                    download_seconds: Some(dl_secs),
                    search_summary: None,
                    timestamp: chrono::Utc::now().to_rfc3339(),
                    is_deleted: false,
                };
                let _ = db.studio_append_message(&msg).await;
                let media_refs = vec![StudioMediaRef {
                    id: 0,
                    message_id: msg_id.clone(),
                    media_path: path_str.clone(),
                    media_kind: "video".to_string(),
                    sort_order: 0,
                    preview_path: None,
                }];
                let _ = db.studio_insert_media_refs(&msg_id, &media_refs).await;

                let _ = app_handle.emit("studio-task-update", serde_json::json!({
                    "task_id": tid, "session_id": sid, "status": "completed",
                    "progress": 1.0, "result_path": path_str,
                }));
                let _ = app_handle.emit("studio-message-new", serde_json::json!({
                    "session_id": sid, "message": msg, "media_refs": media_refs,
                }));
            }
            Err(e) => {
                let err_msg = format!("download: {e}");
                let _ = db.studio_update_task_status(&tid, "failed", Some(&err_msg)).await;
                let _ = app_handle.emit("studio-task-update", serde_json::json!({
                    "task_id": tid, "session_id": sid, "status": "failed", "error": err_msg,
                }));
            }
        }
    });

    Ok(task_id)
}

#[tauri::command]
pub async fn studio_cancel_task(
    state: State<'_, AppState>,
    task_id: String,
) -> Result<(), String> {
    state.db.studio_update_task_status(&task_id, "cancelled", None)
        .await
        .map_err(|e| e.to_string())
}

// ── Reference images ──

#[tauri::command]
pub async fn studio_add_reference_image(
    state: State<'_, AppState>,
    session_id: String,
    file_path: String,
    width: Option<i64>,
    height: Option<i64>,
) -> Result<StudioReferenceImage, String> {
    let now = chrono::Utc::now().to_rfc3339();
    let id = uuid::Uuid::new_v4().to_string();
    let refs = state.db.studio_list_reference_images(&session_id).await.map_err(|e| e.to_string())?;
    let sort_order = refs.len() as i64;

    let img = StudioReferenceImage {
        id: id.clone(),
        session_id,
        file_path,
        sort_order,
        width,
        height,
        created_at: now,
    };
    state.db.studio_add_reference_image(&img).await.map_err(|e| e.to_string())?;
    Ok(img)
}

#[tauri::command]
pub async fn studio_delete_reference_image(
    state: State<'_, AppState>,
    id: String,
) -> Result<(), String> {
    state.db.studio_delete_reference_image(&id).await.map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn studio_list_reference_images(
    state: State<'_, AppState>,
    session_id: String,
) -> Result<Vec<StudioReferenceImage>, String> {
    state.db.studio_list_reference_images(&session_id).await.map_err(|e| e.to_string())
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  Resume interrupted video tasks on startup
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

pub async fn studio_resume_interrupted_video_tasks(
    app_handle: tauri::AppHandle,
    db: std::sync::Arc<tfp_storage::Database>,
) {
    let tasks = match db.studio_get_interrupted_video_tasks().await {
        Ok(t) => t,
        Err(e) => {
            tracing::warn!("Failed to resume interrupted video tasks: {e}");
            return;
        }
    };

    if tasks.is_empty() { return; }
    tracing::info!("Found {} interrupted video task(s), resuming...", tasks.len());

    for task in tasks {
        let db2 = db.clone();
        let app2 = app_handle.clone();
        let tid = task.id.clone();
        let sid = task.session_id.clone();
        let video_id = match &task.remote_video_id {
            Some(id) => id.clone(),
            None => continue,
        };

        tokio::spawn(async move {
            // Recover endpoint info from persisted config
            let config = {
                let cfg = db2.kv_get("app_config").await.ok().flatten();
                cfg.and_then(|j| serde_json::from_str::<AppConfig>(&j).ok()).unwrap_or_default()
            };

            let Some(ep) = config.endpoints.first().cloned() else { return; };
            let provider = OpenAiVideoProvider::new(ep.clone());

            let api_mode = match task.remote_video_api_mode.as_deref() {
                Some("videos") => Some(VideoApiMode::Videos),
                Some("sora_jobs") => Some(VideoApiMode::SoraJobs),
                _ => None,
            };
            // api_mode is kept for reference; poll_status uses endpoint-level detection
            let _ = api_mode;

            tracing::info!("Resuming video poll: task={}, video_id={}", tid, video_id);

            let poll_interval = std::time::Duration::from_secs(3);
            for _ in 0..200 {
                tokio::time::sleep(poll_interval).await;

                match provider.poll_status(&video_id, &ep.id).await {
                    Ok(result) => {
                        let s = result.status.to_lowercase();
                        if s == "succeeded" || s == "completed" || s == "success" {
                            if let Some(dl_url) = result.download_url {
                                if !dl_url.is_empty() {
                                    let data_dir = app2.path().app_data_dir().unwrap_or_default();
                                    let vid_dir = data_dir.join("studio_videos");
                                    let _ = std::fs::create_dir_all(&vid_dir);
                                    let path = vid_dir.join(format!("{}.mp4", &tid[..8]));

                                    match studio_download_video(&dl_url, &path).await {
                                        Ok(path_str) => {
                                            let _ = db2.studio_update_task_result(&tid, &path_str, None, None).await;
                                            let _ = app2.emit("studio-task-update", serde_json::json!({
                                                "task_id": tid, "session_id": sid, "status": "completed", "progress": 1.0,
                                            }));
                                        }
                                        Err(e) => {
                                            let err_msg = format!("Resume download failed: {e}");
                                            let _ = db2.studio_update_task_status(&tid, "failed", Some(&err_msg)).await;
                                        }
                                    }
                                }
                            }
                            return;
                        } else if s == "failed" {
                            let _ = db2.studio_update_task_status(&tid, "failed", Some("Video generation failed (resume)")).await;
                            return;
                        }
                    }
                    Err(_) => continue,
                }
            }
            let _ = db2.studio_update_task_status(&tid, "failed", Some("Resume poll timed out")).await;
        });
    }
}

// ── Helper: download video from URL ──

pub(crate) async fn studio_download_video(url: &str, output_path: &std::path::Path) -> Result<String, String> {
    let client = reqwest::Client::builder()
        .timeout(std::time::Duration::from_secs(300))
        .build()
        .map_err(|e| e.to_string())?;

    let resp = client
        .get(url)
        .send()
        .await
        .map_err(|e| format!("Download request failed: {e}"))?;

    if !resp.status().is_success() {
        return Err(format!("Download HTTP {}", resp.status()));
    }

    let bytes = resp
        .bytes()
        .await
        .map_err(|e| format!("Read download bytes: {e}"))?;

    std::fs::write(output_path, &bytes)
        .map_err(|e| format!("Write video file: {e}"))?;

    Ok(output_path.to_string_lossy().to_string())
}
