use std::time::Instant;

use tauri::{Emitter, State};
use tfp_core::{
    DiscoveredModel, EndpointTestItem, EndpointTestProgress, EndpointTestReport, EndpointType,
    TestStatus, VendorProfile,
};

use crate::state::AppState;

use super::test_runner;

fn find_profile<'a>(
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

    // Execute tests concurrently
    let mut handles = Vec::new();
    for (model_id, deployment, cap) in plan {
        let c = client.clone();
        let ep = endpoint.clone();
        let prof = profile.cloned();
        handles.push(tokio::spawn(async move {
            test_runner::test_single_capability(&c, &ep, prof.as_ref(), &model_id, &deployment, &cap)
                .await
        }));
    }

    for handle in handles {
        match handle.await {
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

    if endpoint.endpoint_type == EndpointType::AzureOpenAi {
        return Err(
            "Azure OpenAI does not support model discovery via /models API. \
             Use the Azure Portal to view available deployments."
                .into(),
        );
    }

    let base = endpoint.url.trim_end_matches('/');

    // Determine URL attempt order
    let urls = if endpoint.endpoint_type == EndpointType::ApiManagementGateway {
        vec![
            format!("{base}/models"),
            format!("{base}/v1/models"),
        ]
    } else {
        vec![
            format!("{base}/v1/models"),
            format!("{base}/models"),
        ]
    };

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
                    let models = test_runner::parse_model_list(&json);
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
