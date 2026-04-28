use serde_json::json;
use tfp_core::{
    AiEndpoint, EndpointTestItem, EndpointType, ModelCapability, TestStatus, VendorProfile,
};

/// Resolve auth: endpoint override → profile default → platform fallback
pub(crate) fn resolve_auth_header(endpoint: &AiEndpoint, profile: Option<&VendorProfile>) -> String {
    let mode = &endpoint.auth_header_mode;
    if mode != "auto" && !mode.is_empty() {
        return mode.clone();
    }
    if let Some(p) = profile {
        if !p.default_auth_header.is_empty() {
            return p.default_auth_header.clone();
        }
    }
    if endpoint.is_azure() { "api_key".into() } else { "bearer".into() }
}

/// Build an authenticated request builder
pub(crate) fn build_authed_request(
    client: &reqwest::Client,
    method: reqwest::Method,
    url: &str,
    endpoint: &AiEndpoint,
    profile: Option<&VendorProfile>,
) -> reqwest::RequestBuilder {
    let auth = resolve_auth_header(endpoint, profile);
    let builder = client.request(method, url);
    match auth.as_str() {
        "api_key" | "api_key_header" => builder.header("api-key", &endpoint.api_key),
        _ => builder.header("Authorization", format!("Bearer {}", endpoint.api_key)),
    }
}

/// Build candidate URLs for a capability using profile templates + fallbacks
pub(crate) fn build_url_candidates(
    endpoint: &AiEndpoint, profile: Option<&VendorProfile>,
    capability: &ModelCapability, deployment: &str,
) -> Vec<String> {
    let base = endpoint.url.trim_end_matches('/');
    let api_ver = endpoint.api_version.as_deref().unwrap_or("");
    if let Some(p) = profile {
        let templates = match capability {
            ModelCapability::Text => &p.text_url_candidates,
            ModelCapability::Image => &p.image_url_candidates,
            ModelCapability::Video => &p.video_url_candidates,
            ModelCapability::SpeechToText => &p.audio_url_candidates,
            ModelCapability::TextToSpeech => &p.speech_url_candidates,
        };
        if !templates.is_empty() {
            return templates.iter().map(|t| {
                t.replace("{baseUrl}", base).replace("{deployment}", deployment)
                    .replace("{apiVersion}", api_ver).replace("{model}", deployment)
            }).collect();
        }
    }
    match capability {
        ModelCapability::Text => match endpoint.endpoint_type {
            EndpointType::AzureOpenAi => vec![format!(
                "{base}/openai/deployments/{deployment}/chat/completions?api-version={api_ver}"
            )],
            EndpointType::ApiManagementGateway => vec![
                format!("{base}/v1/chat/completions"),
                format!("{base}/chat/completions?api-version={api_ver}"),
            ],
            _ => vec![format!("{base}/v1/chat/completions")],
        },
        ModelCapability::Image => match endpoint.endpoint_type {
            EndpointType::AzureOpenAi => vec![format!("{base}/openai/v1/images/generations")],
            EndpointType::ApiManagementGateway => vec![
                format!("{base}/v1/images/generations"),
                format!("{base}/images/generations?api-version={api_ver}"),
            ],
            _ => vec![format!("{base}/v1/images/generations")],
        },
        _ => Vec::new(),
    }
}

type TestResult = (bool, String, Option<String>);

async fn test_text(
    client: &reqwest::Client, url: &str, model: &str,
    endpoint: &AiEndpoint, profile: Option<&VendorProfile>,
) -> TestResult {
    let body = json!({"model": model, "messages": [{"role":"user","content":"Say hello in one word."}], "max_tokens": 10});
    match build_authed_request(client, reqwest::Method::POST, url, endpoint, profile).json(&body).send().await {
        Ok(r) => {
            let status = r.status();
            if status.is_success() {
                let text = r.text().await.unwrap_or_default();
                let content = serde_json::from_str::<serde_json::Value>(&text).ok()
                    .and_then(|v| v["choices"][0]["message"]["content"].as_str().map(|s| s.chars().take(100).collect::<String>()));
                (true, format!("OK: {}", content.as_deref().unwrap_or("(no content)")), None)
            } else {
                let text = r.text().await.unwrap_or_default();
                (false, format!("HTTP {status}"), Some(text.chars().take(300).collect()))
            }
        }
        Err(e) => (false, format!("Network error: {e}"), None),
    }
}

async fn test_image(
    client: &reqwest::Client, url: &str, model: &str,
    endpoint: &AiEndpoint, profile: Option<&VendorProfile>,
) -> TestResult {
    let body = json!({"model": model, "prompt": "A tiny red dot", "size": "256x256", "quality": "low", "n": 1});
    match build_authed_request(client, reqwest::Method::POST, url, endpoint, profile).json(&body).send().await {
        Ok(r) => {
            let status = r.status();
            if status.is_success() {
                (true, "Image generation OK".into(), None)
            } else {
                let text = r.text().await.unwrap_or_default();
                (false, format!("HTTP {status}"), Some(text.chars().take(300).collect()))
            }
        }
        Err(e) => (false, format!("Network error: {e}"), None),
    }
}

/// Test a single (model, capability) with URL candidate fallback
pub(crate) async fn test_single_capability(
    client: &reqwest::Client, endpoint: &AiEndpoint, profile: Option<&VendorProfile>,
    model_id: &str, deployment: &str, capability: &ModelCapability,
) -> EndpointTestItem {
    let start = std::time::Instant::now();
    let cap_str = match capability {
        ModelCapability::Text => "text", ModelCapability::Image => "image",
        ModelCapability::Video => "video", ModelCapability::SpeechToText => "stt",
        ModelCapability::TextToSpeech => "tts",
    };
    if matches!(capability, ModelCapability::Video | ModelCapability::SpeechToText | ModelCapability::TextToSpeech) {
        return EndpointTestItem {
            model_id: model_id.into(), capability: cap_str.into(),
            status: TestStatus::Skipped, summary: format!("{cap_str} test not implemented yet"),
            detail: None, request_url: None, request_summary: None,
            duration_ms: start.elapsed().as_millis() as u64,
            test_branch: Some("skipped".into()), urls_tried: Vec::new(),
        };
    }
    let candidates = build_url_candidates(endpoint, profile, capability, deployment);
    if candidates.is_empty() {
        return EndpointTestItem {
            model_id: model_id.into(), capability: cap_str.into(),
            status: TestStatus::Skipped, summary: "No URL candidates".into(),
            detail: None, request_url: None, request_summary: None,
            duration_ms: start.elapsed().as_millis() as u64,
            test_branch: Some("no_url".into()), urls_tried: Vec::new(),
        };
    }
    let mut urls_tried = Vec::new();
    let mut last_summary = String::new();
    let mut last_detail = None;
    for url in &candidates {
        urls_tried.push(url.clone());
        let (ok, summary, detail) = match capability {
            ModelCapability::Text => test_text(client, url, deployment, endpoint, profile).await,
            ModelCapability::Image => test_image(client, url, deployment, endpoint, profile).await,
            _ => unreachable!(),
        };
        if ok {
            return EndpointTestItem {
                model_id: model_id.into(), capability: cap_str.into(),
                status: TestStatus::Success, summary, detail,
                request_url: Some(url.clone()), request_summary: Some(format!("POST {url}")),
                duration_ms: start.elapsed().as_millis() as u64,
                test_branch: Some(cap_str.into()), urls_tried,
            };
        }
        last_summary = summary;
        last_detail = detail;
    }
    EndpointTestItem {
        model_id: model_id.into(), capability: cap_str.into(),
        status: TestStatus::Failed, summary: last_summary, detail: last_detail,
        request_url: candidates.last().cloned(), request_summary: candidates.last().map(|u| format!("POST {u}")),
        duration_ms: start.elapsed().as_millis() as u64,
        test_branch: Some(cap_str.into()), urls_tried,
    }
}

/// Parse model list from various response formats
pub(crate) fn parse_model_list(json: &serde_json::Value) -> Vec<tfp_core::DiscoveredModel> {
    let arrays = [json["data"].as_array(), json["models"].as_array(), json["value"].as_array(), json.as_array()];
    for arr in arrays.into_iter().flatten() {
        let models: Vec<_> = arr.iter().filter_map(|item| {
            if let Some(s) = item.as_str() {
                return Some(tfp_core::DiscoveredModel { id: s.into(), display_name: None, owned_by: None });
            }
            let id = item["id"].as_str().or_else(|| item["model"].as_str()).or_else(|| item["name"].as_str())?;
            Some(tfp_core::DiscoveredModel {
                id: id.into(),
                display_name: item["display_name"].as_str().or_else(|| item["displayName"].as_str()).map(Into::into),
                owned_by: item["owned_by"].as_str().or_else(|| item["ownedBy"].as_str()).map(Into::into),
            })
        }).collect();
        if !models.is_empty() { return models; }
    }
    Vec::new()
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn test_ep(auth: &str) -> AiEndpoint {
        AiEndpoint {
            id: "test".into(), name: "Test".into(),
            endpoint_type: EndpointType::OpenAiCompatible,
            url: "https://example.com".into(), api_key: "key".into(),
            api_version: None, region: None, models: vec![], enabled: true,
            auth_header_mode: auth.into(), auth_mode: "api_key".into(),
            azure_tenant_id: String::new(), azure_client_id: String::new(),
            speech_subscription_key: String::new(), speech_region: String::new(),
            speech_endpoint: String::new(),
        }
    }

    #[test]
    fn test_parse_model_list_data_format() {
        let j = json!({"data": [{"id": "gpt-4", "owned_by": "openai"}, {"id": "gpt-3.5-turbo"}]});
        let m = parse_model_list(&j);
        assert_eq!(m.len(), 2);
        assert_eq!(m[0].id, "gpt-4");
        assert_eq!(m[0].owned_by.as_deref(), Some("openai"));
    }

    #[test]
    fn test_parse_model_list_models_format() {
        let m = parse_model_list(&json!({"models": [{"id": "llama-3"}]}));
        assert_eq!(m.len(), 1);
        assert_eq!(m[0].id, "llama-3");
    }

    #[test]
    fn test_parse_model_list_value_format() {
        let m = parse_model_list(&json!({"value": [{"name": "davinci", "displayName": "Davinci"}]}));
        assert_eq!(m.len(), 1);
        assert_eq!(m[0].display_name.as_deref(), Some("Davinci"));
    }

    #[test]
    fn test_parse_model_list_top_level_array() {
        assert_eq!(parse_model_list(&json!([{"id": "m1"}, {"id": "m2"}])).len(), 2);
    }

    #[test]
    fn test_parse_model_list_string_elements() {
        let m = parse_model_list(&json!({"data": ["model-a", "model-b"]}));
        assert_eq!(m.len(), 2);
        assert_eq!(m[0].id, "model-a");
    }

    #[test]
    fn test_resolve_auth_header_explicit() {
        assert_eq!(resolve_auth_header(&test_ep("bearer"), None), "bearer");
    }

    #[test]
    fn test_resolve_auth_header_auto_azure() {
        let mut ep = test_ep("auto");
        ep.endpoint_type = EndpointType::AzureOpenAi;
        assert_eq!(resolve_auth_header(&ep, None), "api_key");
    }
}
