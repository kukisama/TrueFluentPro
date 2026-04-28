use tauri::State;
use tfp_core::{AiEndpoint, AppConfig};

use crate::state::AppState;
use std::time::Duration;

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

// ── Config import / export ──

#[tauri::command]
pub async fn export_config(state: State<'_, AppState>) -> Result<String, String> {
    let config = state.config.read().await;
    serde_json::to_string_pretty(&*config).map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn import_config(
    state: State<'_, AppState>,
    json: String,
) -> Result<(), String> {
    let new_config: AppConfig =
        serde_json::from_str(&json).map_err(|e| format!("Invalid config JSON: {e}"))?;
    {
        let mut config = state.config.write().await;
        *config = new_config;
    }
    state.persist_config().await
}

// ── Azure storage connection validation ──

/// Parse account name and blob endpoint URL from an Azure Storage connection string.
pub(crate) fn parse_storage_connection_info(connection_string: &str) -> Result<(String, String), String> {
    if !connection_string.contains("AccountName=") || !connection_string.contains("AccountKey=") {
        return Err(
            "Invalid connection string: must contain AccountName and AccountKey".into(),
        );
    }
    let account_name = connection_string
        .split(';')
        .find_map(|p| p.strip_prefix("AccountName="))
        .ok_or("Cannot parse AccountName from connection string")?
        .to_string();
    let endpoint_suffix = connection_string
        .split(';')
        .find_map(|p| p.strip_prefix("EndpointSuffix="))
        .unwrap_or("core.windows.net");
    let url = format!("https://{account_name}.blob.{endpoint_suffix}");
    Ok((account_name, url))
}

#[tauri::command]
pub async fn validate_storage_connection(connection_string: String) -> Result<(), String> {
    let (_account_name, url) = parse_storage_connection_info(&connection_string)?;

    let client = reqwest::Client::builder()
        .timeout(Duration::from_secs(10))
        .build()
        .map_err(|e| e.to_string())?;

    let resp = client
        .get(&url)
        .send()
        .await
        .map_err(|e| format!("Cannot connect to storage endpoint: {e}"))?;
    if resp.status().is_server_error() {
        return Err(format!(
            "Storage endpoint returned server error: {}",
            resp.status()
        ));
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_parse_valid_connection_string() {
        let input = "DefaultEndpointsProtocol=https;AccountName=myaccount;AccountKey=abc123;EndpointSuffix=core.windows.net";
        let result = parse_storage_connection_info(input);
        assert!(result.is_ok());
        let (name, url) = result.unwrap();
        assert_eq!(name, "myaccount");
        assert_eq!(url, "https://myaccount.blob.core.windows.net");
    }

    #[test]
    fn test_parse_missing_account_name() {
        let input = "AccountKey=abc123";
        let result = parse_storage_connection_info(input);
        assert!(result.is_err());
        assert!(result.unwrap_err().contains("AccountName"));
    }

    #[test]
    fn test_parse_missing_account_key() {
        let input = "AccountName=myaccount";
        let result = parse_storage_connection_info(input);
        assert!(result.is_err());
        assert!(result.unwrap_err().contains("AccountKey"));
    }

    #[test]
    fn test_parse_custom_endpoint_suffix() {
        let input = "DefaultEndpointsProtocol=https;AccountName=myaccount;AccountKey=abc123;EndpointSuffix=core.chinacloudapi.cn";
        let result = parse_storage_connection_info(input);
        assert!(result.is_ok());
        let (name, url) = result.unwrap();
        assert_eq!(name, "myaccount");
        assert_eq!(url, "https://myaccount.blob.core.chinacloudapi.cn");
    }
}
