use std::time::SystemTime;
use std::sync::Arc;

use tauri::{Manager, State};
use tfp_core::{
    CompletionRequest, CompletionResponse, ChatMessage, ImageGenRequest, ImageGenResult,
    SaveImageRequest,
};
use tfp_storage::SavedImage;

use crate::state::AppState;

// ── Image generation ──

#[tauri::command]
pub async fn upload_image_file(
    state: State<'_, AppState>,
    endpoint_id: String,
    file_path: String,
) -> Result<String, String> {
    // Read file bytes
    let file_bytes = tokio::fs::read(&file_path)
        .await
        .map_err(|e| format!("Failed to read file '{file_path}': {e}"))?;

    // Check FileIdCache first
    if let Some(cached_id) = state.file_id_cache.try_get(&endpoint_id, &file_bytes) {
        return Ok(cached_id);
    }

    // Upload via provider
    let providers = state.providers.read().await;
    let provider = providers
        .get_image_gen(&endpoint_id)
        .ok_or_else(|| format!("Image generation provider not found: {endpoint_id}"))?;
    let file_id = provider
        .upload_file(&file_path, &file_bytes)
        .await
        .map_err(|e| e.to_string())?;

    // Cache the result
    state.file_id_cache.set(&endpoint_id, &file_bytes, file_id.clone());

    Ok(file_id)
}

#[tauri::command]
pub async fn generate_image(
    state: State<'_, AppState>,
    request: ImageGenRequest,
) -> Result<Vec<ImageGenResult>, String> {
    let providers = state.providers.read().await;
    let provider = providers
        .get_image_gen(&request.endpoint_id)
        .ok_or_else(|| format!("Image generation provider not found: {}", request.endpoint_id))?;
    provider.generate(&request).await.map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn save_image(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    request: SaveImageRequest,
) -> Result<SavedImage, String> {
    use base64::Engine;

    let data_dir = app
        .path()
        .app_data_dir()
        .map_err(|e| e.to_string())?;
    let images_dir = data_dir.join("images");
    tokio::fs::create_dir_all(&images_dir)
        .await
        .map_err(|e| format!("Cannot create images dir: {e}"))?;

    // Decode base64
    let bytes = base64::engine::general_purpose::STANDARD
        .decode(&request.base64)
        .map_err(|e| format!("Base64 decode error: {e}"))?;

    // Determine extension
    let ext = match request.format.to_lowercase().as_str() {
        "jpeg" | "jpg" => "jpg",
        "webp" => "webp",
        _ => "png",
    };

    // Build filename: img_{timestamp}_{uuid8}.{ext}
    let timestamp = format_timestamp_for_filename();
    let uuid8 = &uuid::Uuid::new_v4().to_string()[..8];
    let filename = format!("img_{timestamp}_{uuid8}.{ext}");
    let final_path = images_dir.join(&filename);
    let tmp_path = images_dir.join(format!("{filename}.tmp"));

    // Atomic write: write to .tmp then rename
    tokio::fs::write(&tmp_path, &bytes)
        .await
        .map_err(|e| format!("Write tmp file error: {e}"))?;
    tokio::fs::rename(&tmp_path, &final_path)
        .await
        .map_err(|e| format!("Rename error: {e}"))?;

    let now = super::session::now_utc_string();
    let saved = SavedImage {
        id: uuid::Uuid::new_v4().to_string(),
        prompt: request.prompt,
        revised_prompt: request.revised_prompt,
        file_path: final_path.to_string_lossy().to_string(),
        file_size: bytes.len() as i64,
        width: request.width,
        height: request.height,
        model_id: request.model_id,
        endpoint_id: request.endpoint_id,
        generate_seconds: request.generate_seconds,
        source: request.source,
        created_at: now,
    };

    state
        .db
        .add_saved_image(&saved)
        .await
        .map_err(|e| e.to_string())?;

    Ok(saved)
}

#[tauri::command]
pub async fn list_saved_images(
    state: State<'_, AppState>,
    limit: Option<u32>,
) -> Result<Vec<SavedImage>, String> {
    state
        .db
        .list_saved_images(limit.unwrap_or(50))
        .await
        .map_err(|e| e.to_string())
}

// ── Prompt optimization ──

#[tauri::command]
pub async fn optimize_prompt(
    state: State<'_, AppState>,
    prompt: String,
    endpoint_id: Option<String>,
) -> Result<String, String> {
    let providers = state.providers.read().await;

    let provider = match endpoint_id.as_deref() {
        Some(id) if !id.is_empty() => providers
            .get_ai_completion(id)
            .ok_or_else(|| format!("AI completion provider not found: {id}"))?,
        _ => {
            let list = providers.list_providers();
            let id = list
                .iter()
                .find(|p| {
                    p.capabilities
                        .contains(&tfp_providers::ProviderCapability::AiCompletion)
                })
                .map(|p| p.id.clone())
                .ok_or_else(|| "No AI completion provider available".to_string())?;
            providers
                .get_ai_completion(&id)
                .ok_or_else(|| format!("AI completion provider not found: {id}"))?
        }
    };

    let request = CompletionRequest {
        messages: vec![
            ChatMessage {
                role: "system".into(),
                content: serde_json::Value::String(
                    "你是一个提示词优化专家。用户会给你一段提示词（可能是对话问题或图片描述），\
                     请优化它使其更精确、更有效。仅返回优化后的提示词文本，不要任何解释。"
                        .into(),
                ),
            },
            ChatMessage {
                role: "user".into(),
                content: serde_json::Value::String(prompt),
            },
        ],
        model: String::new(), // provider will use its own model
        temperature: Some(0.7),
        max_tokens: Some(1000),
        endpoint_id: provider.id().to_string(),
    };

    let resp = provider
        .complete(&request)
        .await
        .map_err(|e| e.to_string())?;

    Ok(resp.content)
}

// ── AI completion (existing) ──

#[tauri::command]
pub async fn ai_complete(
    state: State<'_, AppState>,
    request: CompletionRequest,
) -> Result<CompletionResponse, String> {
    let providers = state.providers.read().await;
    let provider = providers
        .get_ai_completion(&request.endpoint_id)
        .ok_or_else(|| {
            format!(
                "AI completion provider not found: {}",
                request.endpoint_id
            )
        })?;

    provider
        .complete(&request)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn ai_complete_stream(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    request: CompletionRequest,
) -> Result<String, String> {
    let providers = state.providers.read().await;
    let provider = providers
        .get_ai_completion(&request.endpoint_id)
        .ok_or_else(|| {
            format!(
                "AI completion provider not found: {}",
                request.endpoint_id
            )
        })?;

    let stream_id = uuid::Uuid::new_v4().to_string();
    let sid = stream_id.clone();
    let sink: Arc<dyn tfp_core::EventSink> = Arc::new(crate::tauri_event_sink::TauriEventSink::new(app));

    tauri::async_runtime::spawn(async move {
        let _ = tfp_chat::streaming::run_ai_stream(sink.as_ref(), provider, &request, &sid).await;
    });

    Ok(stream_id)
}

/// Format current time as YYYYMMDD_HHMMSS for filenames (no chrono)
pub(crate) fn format_timestamp_for_filename() -> String {
    let dur = SystemTime::now()
        .duration_since(SystemTime::UNIX_EPOCH)
        .unwrap_or_default();
    let secs = dur.as_secs();
    let days = secs / 86400;
    let time_of_day = secs % 86400;
    let hours = time_of_day / 3600;
    let minutes = (time_of_day % 3600) / 60;
    let seconds = time_of_day % 60;
    let (year, month, day) = super::session::days_to_ymd(days);
    format!("{year:04}{month:02}{day:02}_{hours:02}{minutes:02}{seconds:02}")
}

// ── Image pipeline commands ──

#[tauri::command]
pub async fn run_image_pipeline(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    request: tfp_media::image_pipeline::PipelineRequest,
) -> Result<tfp_media::image_pipeline::PipelineResult, String> {
    let data_dir = app.path().app_data_dir().map_err(|e| e.to_string())?;
    let config = state.config.read().await;
    let endpoint = config
        .endpoints
        .iter()
        .find(|ep| ep.id == request.endpoint_id)
        .cloned()
        .ok_or_else(|| format!("Endpoint not found: {}", request.endpoint_id))?;
    let quick_model = config.ai.quick_model.model_id.clone();
    drop(config);

    let providers = state.providers.read().await;
    let ai_provider = providers.get_ai_completion(&request.endpoint_id);
    drop(providers);

    let sink: Arc<dyn tfp_core::EventSink> = Arc::new(crate::tauri_event_sink::TauriEventSink::new(app));
    let deps = tfp_media::image_pipeline::PipelineDeps {
        endpoint,
        file_id_cache: state.file_id_cache.clone(),
        sink,
        data_dir,
        ai_provider,
        quick_model,
    };

    tfp_media::image_pipeline::run_pipeline(&deps, request).await
}

#[tauri::command]
pub async fn get_image_model_catalog(
    app: tauri::AppHandle,
) -> Result<Vec<tfp_media::catalog::ModelCapabilityEntry>, String> {
    let resource_path = app.path().resolve("assets/image-models.json", tauri::path::BaseDirectory::Resource);
    if let Ok(path) = resource_path {
        if path.exists() {
            return Ok(tfp_media::catalog::load_image_models_from_file(&path));
        }
    }
    if let Ok(data_dir) = app.path().app_data_dir() {
        let fallback = data_dir.join("image-models.json");
        if fallback.exists() {
            return Ok(tfp_media::catalog::load_image_models_from_file(&fallback));
        }
    }
    Ok(tfp_media::catalog::builtin_image_models())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_format_timestamp_format() {
        let ts = format_timestamp_for_filename();
        // Must be exactly 15 chars: YYYYMMDD_HHMMSS
        assert_eq!(ts.len(), 15);
        // Verify underscore at position 8
        assert_eq!(ts.as_bytes()[8], b'_');
        // All other chars must be ASCII digits
        for (i, ch) in ts.chars().enumerate() {
            if i == 8 {
                assert_eq!(ch, '_');
            } else {
                assert!(ch.is_ascii_digit(), "char at {i} is not digit: {ch}");
            }
        }
    }

    #[test]
    fn test_format_timestamp_reasonable_year() {
        let ts = format_timestamp_for_filename();
        let year: u32 = ts[..4].parse().unwrap();
        assert!(year >= 2020 && year <= 2099, "year out of range: {year}");
    }

    #[test]
    fn test_format_timestamp_deterministic_within_second() {
        let ts1 = format_timestamp_for_filename();
        let ts2 = format_timestamp_for_filename();
        // Either identical or seconds differ by at most 1
        if ts1 != ts2 {
            // Parse seconds from both (positions 13..15)
            let s1: u32 = ts1[13..15].parse().unwrap();
            let s2: u32 = ts2[13..15].parse().unwrap();
            let diff = if s2 >= s1 { s2 - s1 } else { s1 - s2 };
            assert!(diff <= 1, "timestamps differ by more than 1 second: {ts1} vs {ts2}");
        }
    }
}

