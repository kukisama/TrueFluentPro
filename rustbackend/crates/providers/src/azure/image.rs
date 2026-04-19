//! Azure OpenAI Image generation adapter — calls DALL-E via Azure OpenAI API.

use crate::{ImageProvider, ImageGenRequest, ImageGenResponse, ImageData, ProviderError};
use async_trait::async_trait;
use credential_broker::CredentialBroker;
use reqwest::Client;
use secrecy::ExposeSecret;
use serde::{Deserialize, Serialize};
use std::sync::Arc;
use tracing::{debug, error};

/// Azure OpenAI Image generation adapter (DALL-E 3).
pub struct AzureOpenAiImage {
    client: Client,
    credentials: Arc<CredentialBroker>,
    provider_id: String,
}

impl AzureOpenAiImage {
    pub fn new(credentials: Arc<CredentialBroker>, provider_id: &str) -> Self {
        Self {
            client: Client::new(),
            credentials,
            provider_id: provider_id.to_string(),
        }
    }
}

#[async_trait]
impl ImageProvider for AzureOpenAiImage {
    fn id(&self) -> &'static str {
        "azure_openai"
    }

    async fn generate(&self, req: ImageGenRequest) -> Result<ImageGenResponse, ProviderError> {
        // Resolve credentials
        let endpoint = self.credentials.get(&self.provider_id, "endpoint").await
            .map_err(|e| ProviderError::Upstream(e.to_string()))?
            .ok_or(ProviderError::BadCredential)?;
        let api_key = self.credentials.get(&self.provider_id, "api_key").await
            .map_err(|e| ProviderError::Upstream(e.to_string()))?
            .ok_or(ProviderError::BadCredential)?;

        // Resolve deployment and API version
        let deployment = self.credentials.get(&self.provider_id, "image_deployment").await
            .map_err(|e| ProviderError::Upstream(e.to_string()))?
            .map(|s| s.expose_secret().to_string())
            .unwrap_or_else(|| "dall-e-3".to_string());
        let api_version = self.credentials.get(&self.provider_id, "api_version").await
            .map_err(|e| ProviderError::Upstream(e.to_string()))?
            .map(|s| s.expose_secret().to_string())
            .unwrap_or_else(|| "2024-06-01".to_string());

        let base = endpoint.expose_secret().trim_end_matches('/').to_string();
        let url = format!("{base}/openai/deployments/{deployment}/images/generations?api-version={api_version}");

        debug!(url = %url, prompt = %req.prompt, "Azure OpenAI image generation request");

        let body = AoaiImageGenBody {
            prompt: req.prompt,
            n: req.n.unwrap_or(1),
            size: req.size.unwrap_or_else(|| "1024x1024".to_string()),
            quality: req.quality.unwrap_or_else(|| "standard".to_string()),
            response_format: "b64_json".to_string(),
        };

        let resp = self.client.post(&url)
            .header("api-key", api_key.expose_secret())
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
            error!(status, body = %text, "Azure OpenAI image error");
            return Err(ProviderError::Upstream(format!("HTTP {status}: {text}")));
        }

        let api_resp: AoaiImageResponse = resp.json().await
            .map_err(|e| ProviderError::Upstream(format!("failed to parse response: {e}")))?;

        let images = api_resp.data.into_iter().map(|d| ImageData {
            url: d.url,
            b64_json: d.b64_json,
        }).collect();

        Ok(ImageGenResponse { images })
    }
}

// ═══ Azure OpenAI Image wire types ═══

#[derive(Serialize)]
struct AoaiImageGenBody {
    prompt: String,
    n: u32,
    size: String,
    quality: String,
    response_format: String,
}

#[derive(Deserialize)]
struct AoaiImageResponse {
    data: Vec<AoaiImageData>,
}

#[derive(Deserialize)]
struct AoaiImageData {
    url: Option<String>,
    b64_json: Option<String>,
}
