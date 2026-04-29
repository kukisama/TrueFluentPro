use tauri::State;
use tfp_core::{BatchBucketNav, BatchPackage, BatchSubtaskView, ReviewSheetPreset};
use tfp_engine::BatchCoordinator;
use tfp_providers::blob_storage::BlobStorageService;
use tfp_speech::speech_batch_client::SpeechBatchClient;
use tfp_speech::subtitle::{build_vtt, SubtitleEntry};

use crate::state::AppState;

#[tauri::command]
pub async fn batch_create_package(
    state: State<'_, AppState>,
    session_id: String,
    audio_file_id: String,
    display_name: String,
    include_subtitle: bool,
) -> Result<BatchPackage, String> {
    let config = state.config.read().await;
    let sheets: Vec<ReviewSheetPreset> = config
        .batch
        .review_sheets
        .clone();
    drop(config);

    BatchCoordinator::create_package(
        &state.db,
        &session_id,
        &audio_file_id,
        &display_name,
        &sheets,
        include_subtitle,
    )
    .await
}

#[tauri::command]
pub async fn batch_start(
    state: State<'_, AppState>,
    package_ids: Vec<String>,
    include_subtitle: bool,
) -> Result<u32, String> {
    let engine_guard = state.task_engine.read().await;
    let engine = engine_guard
        .as_ref()
        .ok_or_else(|| "Task engine not started".to_string())?;

    let config = state.config.read().await;
    let sheets: Vec<ReviewSheetPreset> = config
        .batch
        .review_sheets
        .clone();
    drop(config);

    BatchCoordinator::start_batch(&state.db, engine, &package_ids, &sheets, include_subtitle).await
}

#[tauri::command]
pub async fn batch_stop(
    state: State<'_, AppState>,
    package_ids: Vec<String>,
) -> Result<(), String> {
    for pkg_id in &package_ids {
        BatchCoordinator::pause_package(&state.db, pkg_id).await?;
    }
    Ok(())
}

#[tauri::command]
pub async fn batch_pause_package(
    state: State<'_, AppState>,
    package_id: String,
) -> Result<(), String> {
    BatchCoordinator::pause_package(&state.db, &package_id).await
}

#[tauri::command]
pub async fn batch_resume_package(
    state: State<'_, AppState>,
    package_id: String,
) -> Result<(), String> {
    let engine_guard = state.task_engine.read().await;
    let engine = engine_guard
        .as_ref()
        .ok_or_else(|| "Task engine not started".to_string())?;
    BatchCoordinator::resume_package(&state.db, engine, &package_id).await
}

#[tauri::command]
pub async fn batch_remove_package(
    state: State<'_, AppState>,
    package_id: String,
) -> Result<(), String> {
    BatchCoordinator::remove_package(&state.db, &package_id).await
}

#[tauri::command]
pub async fn batch_restore_package(
    state: State<'_, AppState>,
    package_id: String,
) -> Result<(), String> {
    BatchCoordinator::restore_package(&state.db, &package_id).await
}

#[tauri::command]
pub async fn batch_get_bucket_nav(
    state: State<'_, AppState>,
) -> Result<Vec<BatchBucketNav>, String> {
    BatchCoordinator::get_bucket_nav(&state.db).await
}

#[tauri::command]
pub async fn batch_get_packages(
    state: State<'_, AppState>,
    bucket_key: String,
) -> Result<Vec<BatchPackage>, String> {
    BatchCoordinator::get_packages_for_bucket(&state.db, &bucket_key).await
}

#[tauri::command]
pub async fn batch_get_subtasks(
    state: State<'_, AppState>,
    package_id: String,
) -> Result<Vec<BatchSubtaskView>, String> {
    BatchCoordinator::get_subtasks_for_package(&state.db, &package_id).await
}

#[tauri::command]
pub async fn batch_regenerate_package(
    state: State<'_, AppState>,
    package_id: String,
) -> Result<(), String> {
    let engine_guard = state.task_engine.read().await;
    let engine = engine_guard
        .as_ref()
        .ok_or_else(|| "Task engine not started".to_string())?;
    BatchCoordinator::regenerate_package(&state.db, engine, &package_id).await
}

#[tauri::command]
pub async fn batch_regenerate_subtask(
    state: State<'_, AppState>,
    queue_item_id: String,
) -> Result<(), String> {
    let engine_guard = state.task_engine.read().await;
    let engine = engine_guard
        .as_ref()
        .ok_or_else(|| "Task engine not started".to_string())?;
    BatchCoordinator::regenerate_subtask(&state.db, engine, &queue_item_id).await
}

#[tauri::command]
pub async fn validate_blob_connection(
    state: State<'_, AppState>,
    connection_string: String,
) -> Result<bool, String> {
    let config = state.config.read().await;
    let container = config.storage.batch_audio_container_name.clone();
    drop(config);

    match BlobStorageService::get_or_create_container(&connection_string, &container).await {
        Ok(()) => Ok(true),
        Err(e) => {
            tracing::warn!("Blob connection validation failed: {}", e);
            Ok(false)
        }
    }
}

#[tauri::command]
pub async fn batch_speech_transcribe(
    state: State<'_, AppState>,
    audio_file_path: String,
    locale: String,
) -> Result<String, String> {
    let config = state.config.read().await;
    let conn_str = config.storage.batch_storage_connection_string.clone();
    let audio_container = config.storage.batch_audio_container_name.clone();

    // Find a speech endpoint for the subscription key + region
    let speech_ep = config
        .endpoints
        .iter()
        .find(|ep| ep.endpoint_type == tfp_core::EndpointType::AzureSpeech && ep.enabled)
        .ok_or_else(|| "No enabled azure_speech endpoint configured".to_string())?;

    let subscription_key = speech_ep.speech_subscription_key.clone();
    let region = speech_ep.speech_region.clone();
    drop(config);

    if conn_str.is_empty() {
        return Err("Blob storage connection string not configured".to_string());
    }
    if subscription_key.is_empty() || region.is_empty() {
        return Err("Speech subscription key or region not configured".to_string());
    }

    // Step 1: Ensure container exists
    BlobStorageService::get_or_create_container(&conn_str, &audio_container)
        .await
        .map_err(|e| format!("Container creation failed: {}", e))?;

    // Step 2: Upload audio to blob
    let blob_name = BlobStorageService::upload_audio(&conn_str, &audio_container, &audio_file_path)
        .await
        .map_err(|e| format!("Audio upload failed: {}", e))?;

    // Step 3: Generate SAS URL (valid for 2 hours)
    let sas_url =
        BlobStorageService::create_blob_read_sas(&conn_str, &audio_container, &blob_name, 7200)
            .await
            .map_err(|e| format!("SAS generation failed: {}", e))?;

    // Step 4: Run batch transcription
    let display_name = std::path::Path::new(&audio_file_path)
        .file_name()
        .and_then(|n| n.to_str())
        .unwrap_or("transcription")
        .to_string();

    let cues = SpeechBatchClient::batch_transcribe(
        &region,
        &subscription_key,
        &sas_url,
        &locale,
        &display_name,
    )
    .await
    .map_err(|e| format!("Batch transcription failed: {}", e))?;

    // Step 5: Format as VTT
    let entries: Vec<SubtitleEntry> = cues
        .iter()
        .enumerate()
        .map(|(i, cue)| SubtitleEntry {
            index: (i + 1) as u32,
            start_ms: cue.start_ms.max(0) as u64,
            end_ms: cue.end_ms.max(0) as u64,
            text: cue.text.clone(),
        })
        .collect();

    Ok(build_vtt(&entries))
}
