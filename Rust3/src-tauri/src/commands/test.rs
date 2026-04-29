use std::time::Instant;

use tauri::{Emitter, State};
use tfp_core::{
    DiscoveredModel, EndpointTestItem, EndpointTestProgress, EndpointTestReport, EndpointType,
    TestStatus, VendorProfile,
};

use crate::state::AppState;

use super::test_runner;

pub(crate) fn find_profile<'a>(
    profiles: &'a [VendorProfile],
    ep_type: &EndpointType,
) -> Option<&'a VendorProfile> {
    profiles.iter().find(|p| &p.endpoint_type == ep_type)
}

#[tauri::command]
pub async fn test_endpoint(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    endpoint_id: String,
) -> Result<EndpointTestReport, String> {
    let config = state.config.read().await;
    let endpoint = config
        .endpoints
        .iter()
        .find(|e| e.id == endpoint_id)
        .ok_or_else(|| format!("Endpoint not found: {endpoint_id}"))?
        .clone();
    drop(config);

    let profiles = tfp_providers::load_profiles();
    let profile = find_profile(&profiles, &endpoint.endpoint_type);

    // Speech endpoints: skip
    if endpoint.is_speech() {
        let report = EndpointTestReport {
            endpoint_id: endpoint.id.clone(),
            endpoint_name: endpoint.name.clone(),
            endpoint_type_name: format!("{:?}", endpoint.endpoint_type),
            items: vec![EndpointTestItem {
                model_id: String::new(),
                capability: "speech".into(),
                status: TestStatus::Skipped,
                summary: "Speech endpoint testing not implemented yet".into(),
                detail: None,
                request_url: None,
                request_summary: None,
                duration_ms: 0,
                test_branch: Some("speech_skip".into()),
                urls_tried: Vec::new(),
            }],
            duration_ms: 0,
            total_count: 1,
            success_count: 0,
            failed_count: 0,
            skipped_count: 1,
        };
        return Ok(report);
    }

    // Pre-validate
    if endpoint.url.is_empty() {
        return Err("Endpoint URL is empty".into());
    }
    if endpoint.api_key.is_empty() {
        return Err("Endpoint API key is empty".into());
    }
    if endpoint.models.is_empty() {
        return Err("Endpoint has no models configured".into());
    }

    // Build test plan: (model_id, deployment, capability)
    let mut plan: Vec<(String, String, tfp_core::ModelCapability)> = Vec::new();
    for model in &endpoint.models {
        for cap in &model.capabilities {
            plan.push((
                model.model_id.clone(),
                model.effective_deployment().to_string(),
                cap.clone(),
            ));
        }
    }

    let total_count = plan.len();
    let started_at = super::session::now_utc_string();

    let client = reqwest::Client::builder()
        .timeout(std::time::Duration::from_secs(30))
        .build()
        .map_err(|e| e.to_string())?;

    let start = Instant::now();
    let mut items: Vec<EndpointTestItem> = Vec::new();

    // Execute tests concurrently with JoinSet — results arrive as they finish
    let mut join_set = tokio::task::JoinSet::new();
    for (model_id, deployment, cap) in plan {
        let c = client.clone();
        let ep = endpoint.clone();
        let prof = profile.cloned();
        join_set.spawn(async move {
            test_runner::test_single_capability(&c, &ep, prof.as_ref(), &model_id, &deployment, &cap)
                .await
        });
    }

    while let Some(result) = join_set.join_next().await {
        match result {
            Ok(item) => {
                items.push(item);
                // Emit progress
                let success = items.iter().filter(|i| i.status == TestStatus::Success).count();
                let failed = items.iter().filter(|i| i.status == TestStatus::Failed).count();
                let skipped = items.iter().filter(|i| i.status == TestStatus::Skipped).count();
                let pending = total_count - items.len();

                let progress = EndpointTestProgress {
                    endpoint_id: endpoint.id.clone(),
                    endpoint_name: endpoint.name.clone(),
                    total_count,
                    pending_count: pending,
                    running_count: 0,
                    success_count: success,
                    failed_count: failed,
                    skipped_count: skipped,
                    items: items.clone(),
                    is_completed: items.len() == total_count,
                    started_at: started_at.clone(),
                };
                let _ = app.emit("endpoint-test-progress", &progress);
            }
            Err(e) => {
                tracing::error!("Test task panicked: {e}");
            }
        }
    }

    let duration_ms = start.elapsed().as_millis() as u64;
    let success_count = items.iter().filter(|i| i.status == TestStatus::Success).count();
    let failed_count = items.iter().filter(|i| i.status == TestStatus::Failed).count();
    let skipped_count = items.iter().filter(|i| i.status == TestStatus::Skipped).count();

    Ok(EndpointTestReport {
        endpoint_id: endpoint.id.clone(),
        endpoint_name: endpoint.name.clone(),
        endpoint_type_name: format!("{:?}", endpoint.endpoint_type),
        items,
        duration_ms,
        total_count,
        success_count,
        failed_count,
        skipped_count,
    })
}

#[tauri::command]
pub async fn discover_models(
    state: State<'_, AppState>,
    endpoint_id: String,
) -> Result<Vec<DiscoveredModel>, String> {
    let config = state.config.read().await;
    let endpoint = config
        .endpoints
        .iter()
        .find(|e| e.id == endpoint_id)
        .ok_or_else(|| format!("Endpoint not found: {endpoint_id}"))?
        .clone();
    drop(config);

    let profiles = tfp_providers::load_profiles();
    let profile = find_profile(&profiles, &endpoint.endpoint_type);

    let base = endpoint.url.trim_end_matches('/');

    // Build candidate URLs from profile + fallback
    let mut urls: Vec<String> = Vec::new();

    // Try profile-defined model_discovery_urls first
    if let Some(prof) = profile {
        for tpl in &prof.model_discovery_urls {
            let url = tpl.replace("{endpoint}", base);
            // For Azure, append api-version
            if endpoint.is_azure() {
                let ver = endpoint.api_version.as_deref().unwrap_or("2025-03-01-preview");
                if !url.contains("api-version") {
                    urls.push(format!("{url}?api-version={ver}"));
                } else {
                    urls.push(url);
                }
            } else {
                urls.push(url);
            }
        }
    }

    // Fallback: standard /models paths
    if urls.is_empty() {
        if endpoint.endpoint_type == EndpointType::AzureOpenAi {
            // Azure: try /openai/deployments with api-version
            let ver = endpoint.api_version.as_deref().unwrap_or("2025-03-01-preview");
            urls.push(format!("{base}/openai/deployments?api-version={ver}"));
            urls.push(format!("{base}/openai/models?api-version={ver}"));
        } else if endpoint.endpoint_type == EndpointType::ApiManagementGateway {
            urls.push(format!("{base}/models"));
            urls.push(format!("{base}/v1/models"));
        } else {
            urls.push(format!("{base}/v1/models"));
            urls.push(format!("{base}/models"));
        }
    }

    let client = reqwest::Client::builder()
        .timeout(std::time::Duration::from_secs(15))
        .build()
        .map_err(|e| e.to_string())?;

    let mut last_error = String::new();

    for url in &urls {
        let req = test_runner::build_authed_request(
            &client,
            reqwest::Method::GET,
            url,
            &endpoint,
            None,
        );
        match req.send().await {
            Ok(resp) => {
                let status = resp.status();
                if status.is_success() {
                    let json: serde_json::Value =
                        resp.json().await.map_err(|e| e.to_string())?;
                    // Try standard /models format first
                    let mut models = test_runner::parse_model_list(&json);
                    // Try Azure /deployments format
                    if models.is_empty() {
                        models = parse_azure_deployments(&json);
                    }
                    if !models.is_empty() {
                        return Ok(models);
                    }
                    last_error = "Response parsed but no models found".into();
                    continue;
                }
                if status.as_u16() == 404 {
                    last_error = format!("GET {url} → 404");
                    continue;
                }
                let text = resp.text().await.unwrap_or_default();
                last_error = format!("GET {url} → {status}: {text}");
            }
            Err(e) => {
                last_error = format!("GET {url} → {e}");
                continue;
            }
        }
    }

    Err(format!("Model discovery failed: {last_error}"))
}

/// Parse Azure deployments list response into DiscoveredModel vec.
fn parse_azure_deployments(json: &serde_json::Value) -> Vec<DiscoveredModel> {
    let data = json.get("data").and_then(|d| d.as_array());
    if data.is_none() {
        return Vec::new();
    }
    data.unwrap()
        .iter()
        .filter_map(|item| {
            let id = item.get("id").and_then(|v| v.as_str()).unwrap_or("");
            let model = item.get("model").and_then(|v| v.as_str());
            let status = item.get("status").and_then(|v| v.as_str());
            if id.is_empty() {
                return None;
            }
            // Skip non-succeeded deployments
            if let Some(s) = status {
                if s != "succeeded" {
                    return None;
                }
            }
            Some(DiscoveredModel {
                id: id.to_string(),
                display_name: model.map(|m| format!("{id} ({m})")),
                owned_by: Some("azure".to_string()),
            })
        })
        .collect()
}


#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::HashMap;
    use tfp_core::{EndpointType, VendorProfile};

    fn make_profile(ep_type: EndpointType, label: &str) -> VendorProfile {
        VendorProfile {
            endpoint_type: ep_type,
            label: label.into(),
            badge: String::new(),
            subtitle: String::new(),
            glyph: String::new(),
            default_auth_header: String::new(),
            default_api_version: String::new(),
            supports_aad: false,
            supports_model_discovery: false,
            model_discovery_urls: vec![],
            test_url_templates: HashMap::new(),
            text_url_candidates: vec![],
            image_url_candidates: vec![],
            video_url_candidates: vec![],
            audio_url_candidates: vec![],
            speech_url_candidates: vec![],
            text_protocol: String::new(),
            supported_auth_modes: vec![],
            raw_json: None,
        }
    }

    #[test]
    fn test_find_profile_match() {
        let profiles = vec![
            make_profile(EndpointType::AzureOpenAi, "Azure OpenAI"),
            make_profile(EndpointType::OpenAiCompatible, "OpenAI Compatible"),
        ];
        let found = find_profile(&profiles, &EndpointType::AzureOpenAi);
        assert!(found.is_some());
        assert_eq!(found.unwrap().endpoint_type, EndpointType::AzureOpenAi);
    }

    #[test]
    fn test_find_profile_no_match() {
        let profiles = vec![
            make_profile(EndpointType::AzureOpenAi, "Azure OpenAI"),
        ];
        let found = find_profile(&profiles, &EndpointType::AzureSpeech);
        assert!(found.is_none());
    }

    #[test]
    fn test_parse_azure_deployments() {
        let json = serde_json::json!({
            "data": [
                { "id": "gpt-4o", "model": "gpt-4o-2024-08-06", "status": "succeeded" },
                { "id": "gpt-35-turbo", "model": "gpt-35-turbo", "status": "succeeded" },
                { "id": "failed-one", "model": "test", "status": "failed" }
            ]
        });
        let models = parse_azure_deployments(&json);
        assert_eq!(models.len(), 2);
        assert_eq!(models[0].id, "gpt-4o");
        assert!(models[0].display_name.as_ref().unwrap().contains("gpt-4o-2024-08-06"));
        assert_eq!(models[0].owned_by.as_deref(), Some("azure"));
    }

    #[test]
    fn test_parse_azure_deployments_empty() {
        let json = serde_json::json!({});
        let models = parse_azure_deployments(&json);
        assert!(models.is_empty());
    }

}
