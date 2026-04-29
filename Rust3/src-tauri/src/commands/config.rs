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

/// Sanitize config for export: remove AAD credentials.
pub(crate) fn sanitize_config(config: &mut AppConfig) {
    for ep in &mut config.endpoints {
        ep.azure_tenant_id = String::new();
        ep.azure_client_id = String::new();
    }
}

/// Validate that all ModelReferences point to existing endpoints and models.
pub(crate) fn validate_model_references(config: &mut AppConfig) {
    let endpoint_ids: std::collections::HashSet<String> = config.endpoints.iter().map(|e| e.id.clone()).collect();
    let refs = [
        &mut config.ai.insight_model,
        &mut config.ai.summary_model,
        &mut config.ai.quick_model,
        &mut config.ai.review_model,
        &mut config.ai.conversation_model,
        &mut config.ai.intent_model,
    ];
    for r in refs {
        if !r.endpoint_id.is_empty() && !endpoint_ids.contains(&r.endpoint_id) {
            *r = tfp_core::ModelReference::default();
        }
    }
}

/// Deduplicate endpoint IDs: if an imported endpoint has the same ID as an existing one, regenerate.
pub(crate) fn dedup_endpoint_ids(config: &mut AppConfig) {
    let mut seen = std::collections::HashSet::new();
    for ep in &mut config.endpoints {
        if !seen.insert(ep.id.clone()) {
            ep.id = uuid::Uuid::new_v4().to_string();
        }
    }
}

#[tauri::command]
pub async fn export_config(state: State<'_, AppState>) -> Result<String, String> {
    let config = state.config.read().await;
    let mut export = config.clone();
    sanitize_config(&mut export);
    serde_json::to_string_pretty(&export).map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn import_config(
    state: State<'_, AppState>,
    json: String,
) -> Result<(), String> {
    let mut new_config: AppConfig =
        serde_json::from_str(&json).map_err(|e| format!("Invalid config JSON: {e}"))?;
    sanitize_config(&mut new_config);
    dedup_endpoint_ids(&mut new_config);
    for ep in &mut new_config.endpoints {
        ep.migrate_auth_header_mode();
    }
    validate_model_references(&mut new_config);
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

/// Health check against a cloud backend URL (GET {url}/healthz with 5s timeout)
#[tauri::command]
pub async fn cloud_health_check(url: String) -> Result<String, String> {
    let check_url = format!("{}/healthz", url.trim_end_matches('/'));
    let client = reqwest::Client::builder()
        .timeout(Duration::from_secs(5))
        .build()
        .map_err(|e| format!("HTTP client error: {e}"))?;

    let start = std::time::Instant::now();
    let resp = client.get(&check_url).send().await.map_err(|e| {
        if e.is_timeout() {
            "连接超时 (5s)".to_string()
        } else if e.is_connect() {
            format!("无法连接: {e}")
        } else {
            format!("请求失败: {e}")
        }
    })?;

    let elapsed_ms = start.elapsed().as_millis();
    let status = resp.status();
    if status.is_success() {
        Ok(format!("✓ OK ({elapsed_ms}ms)"))
    } else {
        let body = resp.text().await.unwrap_or_default();
        Err(format!("{status} — {body}"))
    }
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

    #[test]
    fn test_sanitize_clears_aad_credentials() {
        let mut config = AppConfig::default();
        let mut ep = tfp_core::AiEndpoint::default();
        ep.id = "ep-1".into();
        ep.azure_tenant_id = "secret-tenant".into();
        ep.azure_client_id = "secret-client".into();
        config.endpoints.push(ep);

        sanitize_config(&mut config);

        assert!(config.endpoints[0].azure_tenant_id.is_empty());
        assert!(config.endpoints[0].azure_client_id.is_empty());
    }

    #[test]
    fn test_dedup_endpoint_ids() {
        let mut config = AppConfig::default();
        let mut ep1 = tfp_core::AiEndpoint::default();
        ep1.id = "dup-id".into();
        ep1.name = "First".into();
        let mut ep2 = tfp_core::AiEndpoint::default();
        ep2.id = "dup-id".into();
        ep2.name = "Second".into();
        config.endpoints.push(ep1);
        config.endpoints.push(ep2);

        dedup_endpoint_ids(&mut config);

        assert_eq!(config.endpoints[0].id, "dup-id");
        assert_ne!(config.endpoints[1].id, "dup-id");
        assert_eq!(config.endpoints[1].name, "Second");
    }

    #[test]
    fn test_validate_model_references_clears_invalid() {
        let mut config = AppConfig::default();
        let mut ep = tfp_core::AiEndpoint::default();
        ep.id = "ep-1".into();
        config.endpoints.push(ep);

        config.ai.insight_model = tfp_core::ModelReference {
            endpoint_id: "ep-1".into(),
            model_id: "gpt-4o".into(),
        };
        config.ai.summary_model = tfp_core::ModelReference {
            endpoint_id: "nonexistent".into(),
            model_id: "gpt-4o".into(),
        };

        validate_model_references(&mut config);

        // Valid reference remains
        assert_eq!(config.ai.insight_model.endpoint_id, "ep-1");
        // Invalid reference cleared
        assert!(config.ai.summary_model.endpoint_id.is_empty());
    }

    #[tokio::test]
    async fn test_cloud_health_check_invalid_url() {
        let result = cloud_health_check("http://127.0.0.1:1".into()).await;
        assert!(result.is_err());
    }

    #[tokio::test]
    async fn test_cloud_health_check_trailing_slash() {
        // Should strip trailing slash before appending /healthz
        let result = cloud_health_check("http://127.0.0.1:1/".into()).await;
        assert!(result.is_err());
    }
}
