use std::sync::Arc;
use tauri::{Manager, State};
use tfp_core::VideoGenRequest;
use tfp_providers::OpenAiVideoProvider;

use crate::state::AppState;

#[tauri::command]
pub async fn generate_video(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    request: VideoGenRequest,
) -> Result<String, String> {
    let config = state.config.read().await;
    let endpoint = config
        .endpoints
        .iter()
        .find(|e| e.id == request.endpoint_id)
        .ok_or_else(|| format!("Endpoint not found: {}", request.endpoint_id))?
        .clone();
    drop(config);

    let task_id = uuid::Uuid::new_v4().to_string();
    let tid = task_id.clone();
    let endpoint_id = request.endpoint_id.clone();

    let data_dir = app.path().app_data_dir().map_err(|e| e.to_string())?;
    let sink: Arc<dyn tfp_core::EventSink> = Arc::new(crate::tauri_event_sink::TauriEventSink::new(app));

    tauri::async_runtime::spawn(async move {
        let provider = OpenAiVideoProvider::new(endpoint);
        tfp_media::video_service::run_video_generation(
            sink.as_ref(), &provider, &endpoint_id, request, &tid, &data_dir,
        ).await;
    });

    Ok(task_id)
}

#[cfg(test)]
mod tests {
    #[test]
    fn test_video_command_module_compiles() {
        assert!(true);
    }
}
