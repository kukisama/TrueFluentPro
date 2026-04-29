use tauri::State;

use crate::state::AppState;
use tfp_core::{
    AudioDeviceInfo, AudioDeviceType, AudioLibraryItem, AudioLifecycleRow,
    AudioTaskRow, TaskExecutionRow, TaskEngineStats,
};

// ── Audio device enumeration (cpal) ──

#[tauri::command]
pub async fn list_audio_devices() -> Result<Vec<AudioDeviceInfo>, String> {
    use cpal::traits::{DeviceTrait, HostTrait};

    tokio::task::spawn_blocking(|| {
        let host = cpal::default_host();
        let mut devices = Vec::new();

        let default_input_name = host
            .default_input_device()
            .and_then(|d| d.name().ok());
        let default_output_name = host
            .default_output_device()
            .and_then(|d| d.name().ok());

        if let Ok(input_devices) = host.input_devices() {
            for device in input_devices {
                if let Ok(name) = device.name() {
                    let is_default = default_input_name.as_deref() == Some(&name);
                    devices.push(AudioDeviceInfo {
                        id: name.clone(),
                        name: name.clone(),
                        device_type: AudioDeviceType::Input,
                        is_default,
                    });
                }
            }
        }

        if let Ok(output_devices) = host.output_devices() {
            for device in output_devices {
                if let Ok(name) = device.name() {
                    let is_default = default_output_name.as_deref() == Some(&name);
                    devices.push(AudioDeviceInfo {
                        id: name.clone(),
                        name: name.clone(),
                        device_type: AudioDeviceType::Output,
                        is_default,
                    });
                }
            }
        }

        Ok(devices)
    })
    .await
    .map_err(|e| format!("Device enumeration thread failed: {e}"))?
}

// ── Audio library CRUD ──

#[tauri::command]
pub async fn list_audio_items(
    state: State<'_, AppState>,
) -> Result<Vec<AudioLibraryItem>, String> {
    state.db.list_audio_items().await.map_err(|e| e.to_string())
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
    state.db.add_audio_item(&item).await.map_err(|e| e.to_string())?;
    state.db.init_lifecycle_stages(&item.id).await.map_err(|e| e.to_string())?;
    Ok(item)
}

#[tauri::command]
pub async fn delete_audio_item(
    state: State<'_, AppState>,
    item_id: String,
) -> Result<(), String> {
    state.db.delete_audio_item(&item_id).await.map_err(|e| e.to_string())
}

// ── Lifecycle ──

#[tauri::command]
pub async fn get_audio_lifecycle(
    state: State<'_, AppState>,
    audio_item_id: String,
) -> Result<Vec<AudioLifecycleRow>, String> {
    state.db.get_audio_lifecycle(&audio_item_id).await.map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn update_lifecycle_stage(
    state: State<'_, AppState>,
    lifecycle: AudioLifecycleRow,
) -> Result<(), String> {
    state.db.upsert_lifecycle(&lifecycle).await.map_err(|e| e.to_string())
}

// ── Task engine ──

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
    state.db.submit_task(&task).await.map_err(|e| e.to_string())?;
    Ok(task)
}

#[tauri::command]
pub async fn cancel_task(
    state: State<'_, AppState>,
    task_id: String,
) -> Result<(), String> {
    state.db.update_task_status_new(&task_id, "Cancelled", None).await.map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn retry_task(
    state: State<'_, AppState>,
    task_id: String,
) -> Result<(), String> {
    state.db.update_task_status_new(&task_id, "Queued", None).await.map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn get_task_engine_stats(
    state: State<'_, AppState>,
) -> Result<TaskEngineStats, String> {
    state.db.get_task_stats().await.map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn update_task_engine_config(
    state: State<'_, AppState>,
    concurrency: u32,
    timeout_secs: u64,
) -> Result<(), String> {
    {
        let mut config = state.config.write().await;
        config.task_engine_concurrency = Some(concurrency);
        config.task_engine_timeout_secs = Some(timeout_secs);
    }
    state.persist_config().await
}

#[tauri::command]
pub async fn cleanup_expired_tasks(
    state: State<'_, AppState>,
    days: u32,
) -> Result<u32, String> {
    state.db.cleanup_expired_tasks(days).await.map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn list_tasks(
    state: State<'_, AppState>,
    status: Option<String>,
    limit: Option<u32>,
) -> Result<Vec<AudioTaskRow>, String> {
    state.db.list_tasks(status.as_deref(), limit.unwrap_or(100)).await.map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn get_task_executions(
    state: State<'_, AppState>,
    task_id: String,
) -> Result<Vec<TaskExecutionRow>, String> {
    state.db.get_task_executions(&task_id).await.map_err(|e| e.to_string())
}

// ── STT Transcription ──

#[tauri::command]
pub async fn transcribe_audio(
    state: State<'_, AppState>,
    endpoint_id: String,
    audio_path: String,
    lang: String,
) -> Result<Vec<tfp_core::TranscriptSegment>, String> {
    let audio_data = tokio::fs::read(&audio_path)
        .await
        .map_err(|e| format!("Failed to read audio file: {e}"))?;

    let providers = state.providers.read().await;
    let stt = providers
        .get_stt(&endpoint_id)
        .ok_or_else(|| format!("No STT provider for endpoint: {endpoint_id}"))?;

    stt.transcribe(&audio_data, &lang)
        .await
        .map_err(|e| format!("Transcription failed: {e}"))
}
