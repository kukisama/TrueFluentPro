use std::sync::Arc;
use tauri::{Manager, State};

use crate::state::AppState;
use tfp_core::{
    ImageGenRequest,
    StudioMessage, StudioReferenceImage, StudioSession,
    StudioSessionBundle, StudioTask, VideoGenRequest,
};
use tfp_providers::OpenAiVideoProvider;

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  Studio commands
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

/// Determine video API mode — delegates to tfp_media::video_util
#[cfg(test)]
pub(crate) fn determine_video_api_mode(model: &str) -> &'static str {
    tfp_media::video_util::determine_video_api_mode(model)
}

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

#[allow(clippy::too_many_arguments)]
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
    enable_image_gen: Option<bool>,
    image_model_deployment: Option<String>,
    max_turns: Option<u32>,
) -> Result<String, String> {
    // Resolve provider before spawn (RwLock guard cannot cross spawn boundary)
    let providers = state.providers.read().await;
    let provider = providers
        .get_ai_completion(&endpoint_id)
        .ok_or_else(|| format!("AI Provider not found: {}", endpoint_id))?;
    drop(providers);

    let db = state.db.clone();
    let sink: Arc<dyn tfp_core::EventSink> = Arc::new(crate::tauri_event_sink::TauriEventSink::new(app));
    let stream_id = uuid::Uuid::new_v4().to_string();
    let sid = session_id.clone();
    let t = text.clone();
    let m = model.clone();
    let eid = endpoint_id.clone();

    let options = tfp_chat::streaming::ChatStreamOptions {
        enable_image_generation: enable_image_gen.unwrap_or(false),
        image_model_deployment,
        image_size: None,
        image_quality: None,
        max_turns: max_turns.map(|v| v as usize),
    };

    tokio::spawn(async move {
        let _ = tfp_chat::streaming::run_studio_chat_stream(
            &db, sink.as_ref(), provider, &sid, &t, m, &eid, options,
        ).await;
    });

    // Return a stream_id-bearing response that matches the old contract
    let user_msg_id = uuid::Uuid::new_v4().to_string();
    Ok(serde_json::json!({
        "stream_id": stream_id,
        "user_message": { "id": user_msg_id, "session_id": session_id, "text": text },
        "assistant_message_id": uuid::Uuid::new_v4().to_string(),
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

    if let Ok(Some(session)) = state.db.studio_get_session(&session_id).await {
        let _ = state.db.studio_update_counts(
            &session_id, session.message_count, session.task_count + 1, session.asset_count,
        ).await;
    }

    let endpoint_id = params.get("endpoint_id").and_then(|v| v.as_str()).unwrap_or("").to_string();
    let provider = {
        let reg = state.providers.read().await;
        reg.get_image_gen(&endpoint_id)
    };
    let provider = match provider {
        Some(p) => p,
        None => return Err(format!("Image generation provider not found: {}", endpoint_id)),
    };

    let width = params.get("width").and_then(|v| v.as_u64()).unwrap_or(1024) as u32;
    let height = params.get("height").and_then(|v| v.as_u64()).unwrap_or(1024) as u32;
    let quality = params.get("quality").and_then(|v| v.as_str()).unwrap_or("auto").to_string();
    let format = params.get("format").and_then(|v| v.as_str()).unwrap_or("png").to_string();
    let background = params.get("background").and_then(|v| v.as_str()).unwrap_or("auto").to_string();
    let n = params.get("n").and_then(|v| v.as_u64()).unwrap_or(1) as u32;
    let model = params.get("model").and_then(|v| v.as_str()).unwrap_or("gpt-image-1").to_string();

    let request = ImageGenRequest {
        prompt: prompt.clone(),
        width, height,
        model,
        quality: Some(quality),
        output_format: Some(format.clone()),
        background: Some(background),
        n: Some(n),
        endpoint_id: endpoint_id.clone(),
        text_model: None, image_model: None, previous_response_id: None,
        reference_image_path: None, image_edit_mode: None,
        uploaded_file_ids: vec![],
    };

    let db = state.db.clone();
    let data_dir = app.path().app_data_dir().map_err(|e| e.to_string())?;
    let sink: Arc<dyn tfp_core::EventSink> = Arc::new(crate::tauri_event_sink::TauriEventSink::new(app));
    let tid = task_id.clone();
    let sid = session_id.clone();
    let fmt = format.clone();

    tokio::spawn(async move {
        tfp_media::studio_service::run_studio_image_task(
            &db, sink.as_ref(), provider, &tid, &sid, &prompt, request, &fmt, &data_dir,
        ).await;
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

    let endpoint_id = params.get("endpoint_id").and_then(|v| v.as_str()).unwrap_or("").to_string();
    let ep = {
        let config = state.config.read().await;
        config.endpoints.iter().find(|e| e.id == endpoint_id).cloned()
    }.ok_or("Endpoint not found")?;

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

    let db = state.db.clone();
    let data_dir = app.path().app_data_dir().map_err(|e| e.to_string())?;
    let sink: Arc<dyn tfp_core::EventSink> = Arc::new(crate::tauri_event_sink::TauriEventSink::new(app));
    let tid = task_id.clone();
    let sid = session_id.clone();
    let task_created_at = task.created_at.clone();

    tokio::spawn(async move {
        let provider = OpenAiVideoProvider::new(ep);
        tfp_media::studio_service::run_studio_video_task(
            &db, sink.as_ref(), &provider,
            &tid, &sid, &prompt, request, &model, &endpoint_id, &task_created_at, &data_dir,
        ).await;
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
    let data_dir = app_handle.path().app_data_dir().unwrap_or_default();
    let sink: Arc<dyn tfp_core::EventSink> = Arc::new(crate::tauri_event_sink::TauriEventSink::new(app_handle));
    tfp_media::studio_service::resume_interrupted_video_tasks(db, sink, &data_dir).await;
}

// ── Helper: download video from URL ──

// ── Helper: download video from URL — delegates to tfp_media::video_util ──

#[cfg(test)]
pub(crate) async fn studio_download_video(url: &str, output_path: &std::path::Path) -> Result<String, String> {
    tfp_media::video_util::download_video(url, output_path).await
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  Message editing / branching
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

#[tauri::command]
pub async fn studio_edit_message(
    state: State<'_, AppState>,
    message_id: String,
    new_text: String,
) -> Result<(), String> {
    let msg = state.db.studio_get_message(&message_id).await.map_err(|e| e.to_string())?
        .ok_or_else(|| format!("Message not found: {message_id}"))?;
    let updated = tfp_core::StudioMessage {
        text: new_text,
        timestamp: chrono::Utc::now().to_rfc3339(),
        ..msg
    };
    state.db.studio_update_message(&updated).await.map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn studio_delete_message(
    state: State<'_, AppState>,
    message_id: String,
) -> Result<(), String> {
    let msg = state.db.studio_get_message(&message_id).await.map_err(|e| e.to_string())?
        .ok_or_else(|| format!("Message not found: {message_id}"))?;
    let updated = tfp_core::StudioMessage {
        is_deleted: true,
        timestamp: chrono::Utc::now().to_rfc3339(),
        ..msg
    };
    state.db.studio_update_message(&updated).await.map_err(|e| e.to_string())
}

#[allow(clippy::too_many_arguments)]
#[tauri::command]
pub async fn studio_send_edit(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    session_id: String,
    message_id: String,
    new_text: String,
    endpoint_id: String,
    model: String,
    enable_image_gen: Option<bool>,
    image_model_deployment: Option<String>,
) -> Result<String, String> {
    // 1. Get the message's sequence_no
    let msg = state.db.studio_get_message(&message_id).await.map_err(|e| e.to_string())?
        .ok_or_else(|| format!("Message not found: {message_id}"))?;
    let seq = msg.sequence_no;

    // 2. Update message text
    let updated = tfp_core::StudioMessage {
        text: new_text.clone(),
        timestamp: chrono::Utc::now().to_rfc3339(),
        ..msg
    };
    state.db.studio_update_message(&updated).await.map_err(|e| e.to_string())?;

    // 3. Delete all messages after this one
    state.db.studio_delete_messages_after(&session_id, seq).await.map_err(|e| e.to_string())?;

    // 4. Re-generate via streaming
    let providers = state.providers.read().await;
    let provider = providers
        .get_ai_completion(&endpoint_id)
        .ok_or_else(|| format!("AI Provider not found: {endpoint_id}"))?;
    drop(providers);

    let db = state.db.clone();
    let sink: Arc<dyn tfp_core::EventSink> = Arc::new(crate::tauri_event_sink::TauriEventSink::new(app));
    let sid = session_id.clone();
    let eid = endpoint_id.clone();
    let m = model.clone();

    let options = tfp_chat::streaming::ChatStreamOptions {
        enable_image_generation: enable_image_gen.unwrap_or(false),
        image_model_deployment,
        image_size: None,
        image_quality: None,
        max_turns: Some(20),
    };

    // Note: we delete the original user message and let run_studio_chat_stream
    // re-create it with the updated text, avoiding sequence_no conflicts.
    state.db.studio_hard_delete_message(&message_id).await.map_err(|e| e.to_string())?;

    let stream_id = uuid::Uuid::new_v4().to_string();
    tokio::spawn(async move {
        let _ = tfp_chat::streaming::run_studio_chat_stream(
            &db, sink.as_ref(), provider, &sid, &new_text, m, &eid, options,
        ).await;
    });

    Ok(serde_json::json!({ "stream_id": stream_id }).to_string())
}

#[tauri::command]
pub async fn studio_fork_from_message(
    state: State<'_, AppState>,
    session_id: String,
    message_id: String,
) -> Result<StudioSession, String> {
    let msg = state.db.studio_get_message(&message_id).await.map_err(|e| e.to_string())?
        .ok_or_else(|| format!("Message not found: {message_id}"))?;
    let source_name = state.db.studio_get_session(&session_id).await
        .map_err(|e| e.to_string())?
        .map(|s| s.name)
        .unwrap_or_else(|| "Session".into());
    let fork_name = format!("{source_name} (fork)");
    state.db.studio_fork_session(&session_id, msg.sequence_no, &fork_name)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn studio_count_messages(
    state: State<'_, AppState>,
    session_id: String,
) -> Result<i64, String> {
    state.db.studio_count_messages(&session_id)
        .await
        .map_err(|e| e.to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_api_mode_sora() {
        assert_eq!(determine_video_api_mode("sora"), "videos");
    }

    #[test]
    fn test_api_mode_sora2_variants() {
        assert_eq!(determine_video_api_mode("sora-2"), "videos");
        assert_eq!(determine_video_api_mode("sora-2-turbo"), "videos");
    }

    #[test]
    fn test_api_mode_other_models() {
        assert_eq!(determine_video_api_mode("other-model"), "sora_jobs");
        assert_eq!(determine_video_api_mode(""), "sora_jobs");
        // Note: with domain crate lowercase, "Sora" now matches as "videos"
        assert_eq!(determine_video_api_mode("Sora"), "videos");
    }
}
