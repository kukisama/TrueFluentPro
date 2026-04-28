use tauri::State;
use tfp_core::{AiEndpoint, AppConfig};

use crate::state::AppState;

#[tauri::command]
pub async fn get_config(state: State<'_, AppState>) -> Result<AppConfig, String> {
    let config = state.config.read().await;
    Ok(config.clone())
}

#[tauri::command]
pub async fn update_config(
    state: State<'_, AppState>,
    config: AppConfig,
) -> Result<(), String> {
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
