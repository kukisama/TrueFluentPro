//! Azure OpenAI Video generation adapter (Sora via Azure AI Foundry).
//!
//! API reference:
//!   - POST /openai/deployments/{deployment}/v1/videos — submit generation task
//!   - GET  /openai/deployments/{deployment}/v1/videos/{id} — query task status
//!   - GET  /openai/deployments/{deployment}/v1/videos/{id}/content — download result
//!
//! Credentials:
//!   - `endpoint`: Azure OpenAI endpoint
//!   - `api_key`: Azure OpenAI API key
//!   - `video_deployment`: Deployment name for video model (e.g. "sora-2")
//!   - `api_version`: API version (default: "preview")

use crate::{VideoProvider, VideoGenRequest, VideoGenResponse, ProviderError};
use async_trait::async_trait;
use credential_broker::CredentialBroker;
use reqwest::Client;
use secrecy::ExposeSecret;
use serde::Deserialize;
use std::sync::Arc;
use tracing::{debug, error};

pub struct AzureOpenAiVideo {
    client: Client,
    credentials: Arc<CredentialBroker>,
    provider_id: String,
}

impl AzureOpenAiVideo {
    pub fn new(credentials: Arc<CredentialBroker>, provider_id: &str) -> Self {
        Self {
            client: Client::new(),
            credentials,
            provider_id: provider_id.to_string(),
        }
    }

    async fn resolve_base_url(&self) -> Result<(String, String), ProviderError> {
        let endpoint = self.credentials.get(&self.provider_id, "endpoint").await
            .map_err(|e| ProviderError::Upstream(e.to_string()))?
            .ok_or(ProviderError::BadCredential)?;
        let deployment = self.credentials.get(&self.provider_id, "video_deployment").await
            .map_err(|e| ProviderError::Upstream(e.to_string()))?
            .map(|s| s.expose_secret().to_string())
            .unwrap_or_else(|| "sora-2".to_string());
        let api_version = self.credentials.get(&self.provider_id, "api_version").await
            .map_err(|e| ProviderError::Upstream(e.to_string()))?
            .map(|s| s.expose_secret().to_string())
            .unwrap_or_else(|| "preview".to_string());

        let base = endpoint.expose_secret().trim_end_matches('/').to_string();
        let url = format!("{base}/openai/deployments/{deployment}/v1/videos?api-version={api_version}");
        let api_key = self.credentials.get(&self.provider_id, "api_key").await
            .map_err(|e| ProviderError::Upstream(e.to_string()))?
            .ok_or(ProviderError::BadCredential)?;

        Ok((url, api_key.expose_secret().to_string()))
    }
}

#[async_trait]
impl VideoProvider for AzureOpenAiVideo {
    fn id(&self) -> &'static str {
        "azure_openai_video"
    }

    async fn generate(&self, req: VideoGenRequest) -> Result<VideoGenResponse, ProviderError> {
        let (base_url, api_key) = self.resolve_base_url().await?;

        let mut body = serde_json::json!({
            "prompt": req.prompt,
        });

        if let Some(ref resolution) = req.resolution {
            body["size"] = serde_json::json!(resolution);
        }
        if let Some(seconds) = req.duration_seconds {
            body["seconds"] = serde_json::json!(seconds.to_string());
        }
        if let Some(ref neg) = req.negative_prompt {
            body["negative_prompt"] = serde_json::json!(neg);
        }
        if let Some(ref img) = req.source_image_url {
            body["image_url"] = serde_json::json!(img);
        }

        debug!(prompt_len = req.prompt.len(), "Azure Video generation request");

        let resp = self.client.post(&base_url)
            .header("api-key", &api_key)
            .header("Content-Type", "application/json")
            .json(&body)
            .send()
            .await
            .map_err(|e| ProviderError::Network(e.to_string()))?;

        if !resp.status().is_success() {
            let status = resp.status().as_u16();
            let text = resp.text().await.unwrap_or_default();
            if status == 429 {
                return Err(ProviderError::RateLimited);
            }
            error!(status, body = %text, "Azure Video generation error");
            return Err(ProviderError::Upstream(format!("HTTP {status}: {text}")));
        }

        let api_resp: AzureVideoResponse = resp.json().await
            .map_err(|e| ProviderError::Upstream(format!("response parse error: {e}")))?;

        Ok(VideoGenResponse {
            task_id: Some(api_resp.id),
            video_url: None,
            status: api_resp.status.unwrap_or_else(|| "pending".into()),
        })
    }

    async fn query_task(&self, task_id: &str) -> Result<VideoGenResponse, ProviderError> {
        let (base_url, api_key) = self.resolve_base_url().await?;

        // Strip the query params and append the task_id
        let url = if let Some(idx) = base_url.find('?') {
            format!("{}/{}{}", &base_url[..idx], task_id, &base_url[idx..])
        } else {
            format!("{base_url}/{task_id}")
        };

        debug!(task_id, "Azure Video query task");

        let resp = self.client.get(&url)
            .header("api-key", &api_key)
            .send()
            .await
            .map_err(|e| ProviderError::Network(e.to_string()))?;

        if !resp.status().is_success() {
            let status = resp.status().as_u16();
            let text = resp.text().await.unwrap_or_default();
            return Err(ProviderError::Upstream(format!("HTTP {status}: {text}")));
        }

        let api_resp: AzureVideoResponse = resp.json().await
            .map_err(|e| ProviderError::Upstream(format!("response parse error: {e}")))?;

        // Build content URL if completed
        let video_url = if api_resp.status.as_deref() == Some("completed") {
            let content_url = if let Some(idx) = base_url.find('?') {
                format!("{}/{}/content{}", &base_url[..idx], task_id, &base_url[idx..])
            } else {
                format!("{base_url}/{task_id}/content")
            };
            Some(content_url)
        } else {
            api_resp.video_url
        };

        Ok(VideoGenResponse {
            task_id: Some(api_resp.id),
            video_url,
            status: api_resp.status.unwrap_or_else(|| "unknown".into()),
        })
    }
}

// ═══ Wire types ═══

#[derive(Deserialize)]
struct AzureVideoResponse {
    id: String,
    status: Option<String>,
    #[serde(default)]
    video_url: Option<String>,
}
