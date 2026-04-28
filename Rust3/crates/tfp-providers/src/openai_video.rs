use async_trait::async_trait;
use serde_json::json;

use tfp_core::{AiEndpoint, ProviderError, VideoApiMode, VideoGenRequest, VideoGenResult};

use crate::auth::{apply_auth, append_api_version};
use crate::traits::{ProviderCapability, ProviderMeta, VideoGenSlot};

pub struct OpenAiVideoProvider {
    client: reqwest::Client,
    endpoint: AiEndpoint,
}

impl OpenAiVideoProvider {
    pub fn new(endpoint: AiEndpoint) -> Self {
        Self {
            client: reqwest::Client::builder()
                .timeout(std::time::Duration::from_secs(120))
                .build()
                .unwrap_or_default(),
            endpoint,
        }
    }

    fn detect_api_mode(&self, request: &VideoGenRequest) -> VideoApiMode {
        if let Some(ref mode) = request.api_mode {
            return mode.clone();
        }
        if request.model.contains("sora-2") || request.model == "sora" {
            VideoApiMode::Videos
        } else {
            VideoApiMode::SoraJobs
        }
    }

    async fn create_sora_jobs(&self, request: &VideoGenRequest) -> Result<String, ProviderError> {
        let base = self.endpoint.url.trim_end_matches('/');
        let api_ver = self
            .endpoint
            .api_version
            .as_deref()
            .unwrap_or("2025-03-01-preview");
        let url = append_api_version(
            &format!("{base}/openai/v1/video/generations/jobs"),
            api_ver,
        );

        let body = json!({
            "model": &request.model,
            "prompt": &request.prompt,
            "size": &request.size,
            "n": request.n.unwrap_or(1),
        });

        let resp = apply_auth(&self.endpoint, self.client.post(&url))
            .json(&body)
            .send()
            .await
            .map_err(|e| ProviderError::Network(e.to_string()))?;

        let status = resp.status();
        if !status.is_success() {
            let text = resp.text().await.unwrap_or_default();
            return Err(ProviderError::Network(format!(
                "Create video failed {status}: {text}"
            )));
        }

        let json_val: serde_json::Value = resp
            .json()
            .await
            .map_err(|e| ProviderError::Internal(e.to_string()))?;

        json_val["id"]
            .as_str()
            .map(|s| s.to_string())
            .ok_or_else(|| ProviderError::Internal("No 'id' in create response".into()))
    }

    async fn create_videos(&self, request: &VideoGenRequest) -> Result<String, ProviderError> {
        let base = self.endpoint.url.trim_end_matches('/');
        let url = format!("{base}/v1/videos");

        let mut form = reqwest::multipart::Form::new()
            .text("model", request.model.clone())
            .text("prompt", request.prompt.clone())
            .text("size", request.size.clone())
            .text("duration", request.duration_seconds.to_string())
            .text("n", request.n.unwrap_or(1).to_string());

        if let Some(ref ref_path) = request.reference_image_path {
            let data = tokio::fs::read(ref_path)
                .await
                .map_err(|e| ProviderError::Internal(format!("Read ref image: {e}")))?;
            let file_name = std::path::Path::new(ref_path)
                .file_name()
                .unwrap_or_default()
                .to_string_lossy()
                .to_string();
            let part = reqwest::multipart::Part::bytes(data)
                .file_name(file_name)
                .mime_str("application/octet-stream")
                .map_err(|e| ProviderError::Internal(e.to_string()))?;
            form = form.part("input_reference", part);
        }

        let resp = apply_auth(&self.endpoint, self.client.post(&url))
            .multipart(form)
            .send()
            .await
            .map_err(|e| ProviderError::Network(e.to_string()))?;

        let status = resp.status();
        if !status.is_success() {
            let text = resp.text().await.unwrap_or_default();
            return Err(ProviderError::Network(format!(
                "Create video failed {status}: {text}"
            )));
        }

        let json_val: serde_json::Value = resp
            .json()
            .await
            .map_err(|e| ProviderError::Internal(e.to_string()))?;

        json_val["id"]
            .as_str()
            .map(|s| s.to_string())
            .ok_or_else(|| ProviderError::Internal("No 'id' in create response".into()))
    }
}

impl ProviderMeta for OpenAiVideoProvider {
    fn id(&self) -> &str {
        &self.endpoint.id
    }
    fn display_name(&self) -> &str {
        &self.endpoint.name
    }
    fn capabilities(&self) -> Vec<ProviderCapability> {
        vec![ProviderCapability::VideoGeneration]
    }
}

#[async_trait]
impl VideoGenSlot for OpenAiVideoProvider {
    async fn generate(
        &self,
        request: &VideoGenRequest,
    ) -> Result<VideoGenResult, ProviderError> {
        let api_mode = self.detect_api_mode(request);
        let video_id = match api_mode {
            VideoApiMode::SoraJobs => self.create_sora_jobs(request).await?,
            VideoApiMode::Videos => self.create_videos(request).await?,
        };

        Ok(VideoGenResult {
            video_id,
            status: "pending".into(),
            download_url: None,
            file_path: None,
            generate_seconds: None,
        })
    }

    async fn poll_status(
        &self,
        video_id: &str,
        _endpoint_id: &str,
    ) -> Result<VideoGenResult, ProviderError> {
        let base = self.endpoint.url.trim_end_matches('/');

        // Try SoraJobs poll first, then Videos poll
        let api_ver = self
            .endpoint
            .api_version
            .as_deref()
            .unwrap_or("2025-03-01-preview");

        // Detect mode from video_id pattern or try both
        let sora_url = append_api_version(
            &format!("{base}/openai/v1/video/generations/jobs/{video_id}"),
            api_ver,
        );
        let videos_url = format!("{base}/v1/videos/{video_id}");

        // Try SoraJobs first
        for url in [&sora_url, &videos_url] {
            let resp = apply_auth(&self.endpoint, self.client.get(url))
                .send()
                .await
                .map_err(|e| ProviderError::Network(e.to_string()))?;

            let status_code = resp.status();
            if status_code.as_u16() == 404 {
                continue;
            }
            if !status_code.is_success() {
                let text = resp.text().await.unwrap_or_default();
                return Err(ProviderError::Network(format!(
                    "Poll video failed {status_code}: {text}"
                )));
            }

            let json_val: serde_json::Value = resp
                .json()
                .await
                .map_err(|e| ProviderError::Internal(e.to_string()))?;

            let job_status = json_val["status"]
                .as_str()
                .unwrap_or("unknown")
                .to_string();

            let download_url = json_val["generations"]
                .as_array()
                .and_then(|g| g.first())
                .and_then(|g| g["url"].as_str())
                .or_else(|| {
                    json_val["output"]["url"]
                        .as_str()
                        .or_else(|| json_val["video"]["url"].as_str())
                })
                .map(|s| s.to_string());

            return Ok(VideoGenResult {
                video_id: video_id.to_string(),
                status: job_status,
                download_url,
                file_path: None,
                generate_seconds: None,
            });
        }

        Err(ProviderError::Network(format!(
            "Could not poll video status for {video_id}"
        )))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use tfp_core::EndpointType;

    fn video_endpoint() -> AiEndpoint {
        AiEndpoint {
            id: "vid-ep".into(),
            name: "Video EP".into(),
            endpoint_type: EndpointType::AzureOpenAi,
            url: "https://myresource.openai.azure.com".into(),
            api_key: "key".into(),
            api_version: Some("2025-03-01-preview".into()),
            region: None,
            models: vec![],
            enabled: true,
            auth_header_mode: "api_key".into(),
            auth_mode: "api_key".into(),
            azure_tenant_id: String::new(),
            azure_client_id: String::new(),
            speech_subscription_key: String::new(),
            speech_region: String::new(),
            speech_endpoint: String::new(),
        }
    }

    #[test]
    fn test_provider_meta() {
        let p = OpenAiVideoProvider::new(video_endpoint());
        assert_eq!(p.id(), "vid-ep");
        assert_eq!(p.display_name(), "Video EP");
        assert_eq!(p.capabilities(), vec![ProviderCapability::VideoGeneration]);
    }
}
