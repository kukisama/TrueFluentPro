use async_trait::async_trait;
use serde_json::json;

use tfp_core::{AiEndpoint, EndpointType, ProviderError, VideoApiMode, VideoGenRequest, VideoGenResult};

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

    fn api_version(&self) -> &str {
        self.endpoint
            .api_version
            .as_deref()
            .unwrap_or("2025-03-01-preview")
    }

    /// Build candidate URLs for video creation based on endpoint_type and api_mode.
    pub(crate) fn build_video_create_urls(&self, mode: &VideoApiMode) -> Vec<String> {
        let base = self.endpoint.url.trim_end_matches('/');
        let api_ver = self.api_version();

        match mode {
            VideoApiMode::SoraJobs => match self.endpoint.endpoint_type {
                EndpointType::AzureOpenAi => {
                    vec![append_api_version(
                        &format!("{base}/openai/v1/video/generations/jobs"),
                        api_ver,
                    )]
                }
                EndpointType::ApiManagementGateway => {
                    vec![
                        format!("{base}/v1/video/generations/jobs"),
                        append_api_version(
                            &format!("{base}/openai/v1/video/generations/jobs"),
                            api_ver,
                        ),
                        append_api_version(
                            &format!("{base}/video/generations/jobs"),
                            api_ver,
                        ),
                    ]
                }
                _ => {
                    if base.ends_with("/v1") {
                        vec![format!("{base}/video/generations/jobs")]
                    } else {
                        vec![format!("{base}/v1/video/generations/jobs")]
                    }
                }
            },
            VideoApiMode::Videos => match self.endpoint.endpoint_type {
                EndpointType::AzureOpenAi => {
                    vec![format!("{base}/openai/v1/videos")]
                }
                EndpointType::ApiManagementGateway => {
                    vec![
                        format!("{base}/v1/videos"),
                        format!("{base}/openai/v1/videos"),
                    ]
                }
                _ => {
                    if base.ends_with("/v1") {
                        vec![format!("{base}/videos")]
                    } else {
                        vec![format!("{base}/v1/videos")]
                    }
                }
            },
        }
    }

    /// Build candidate URLs for polling video status.
    pub(crate) fn build_video_poll_urls(&self, video_id: &str, mode: &VideoApiMode) -> Vec<String> {
        self.build_video_create_urls(mode)
            .into_iter()
            .map(|url| {
                // Strip query string before appending /{video_id}, then re-add query
                if let Some(idx) = url.find('?') {
                    let (path, query) = url.split_at(idx);
                    format!("{path}/{video_id}{query}")
                } else {
                    format!("{url}/{video_id}")
                }
            })
            .collect()
    }

    /// Build candidate URLs for downloading the generated video.
    /// Used by tests and will be used by higher-level orchestration (e.g., endpoint testing).
    #[allow(dead_code)]
    pub(crate) fn build_video_download_urls(
        &self,
        video_id: &str,
        generation_id: Option<&str>,
        mode: &VideoApiMode,
    ) -> Vec<String> {
        let base = self.endpoint.url.trim_end_matches('/');
        let api_ver = self.api_version();
        let mut urls = Vec::new();

        match mode {
            VideoApiMode::SoraJobs => {
                // Primary: .../jobs/{video_id}/content/video
                match self.endpoint.endpoint_type {
                    EndpointType::AzureOpenAi => {
                        urls.push(append_api_version(
                            &format!("{base}/openai/v1/video/generations/jobs/{video_id}/content/video"),
                            api_ver,
                        ));
                        if let Some(gen_id) = generation_id {
                            urls.push(append_api_version(
                                &format!("{base}/openai/v1/video/generations/{gen_id}/content/video"),
                                api_ver,
                            ));
                        }
                    }
                    EndpointType::ApiManagementGateway => {
                        urls.push(format!(
                            "{base}/v1/video/generations/jobs/{video_id}/content/video"
                        ));
                        urls.push(append_api_version(
                            &format!("{base}/openai/v1/video/generations/jobs/{video_id}/content/video"),
                            api_ver,
                        ));
                        if let Some(gen_id) = generation_id {
                            urls.push(format!(
                                "{base}/v1/video/generations/{gen_id}/content/video"
                            ));
                            urls.push(append_api_version(
                                &format!("{base}/openai/v1/video/generations/{gen_id}/content/video"),
                                api_ver,
                            ));
                        }
                    }
                    _ => {
                        if base.ends_with("/v1") {
                            urls.push(format!(
                                "{base}/video/generations/jobs/{video_id}/content/video"
                            ));
                        } else {
                            urls.push(format!(
                                "{base}/v1/video/generations/jobs/{video_id}/content/video"
                            ));
                        }
                        if let Some(gen_id) = generation_id {
                            if base.ends_with("/v1") {
                                urls.push(format!(
                                    "{base}/video/generations/{gen_id}/content/video"
                                ));
                            } else {
                                urls.push(format!(
                                    "{base}/v1/video/generations/{gen_id}/content/video"
                                ));
                            }
                        }
                    }
                }
            }
            VideoApiMode::Videos => {
                match self.endpoint.endpoint_type {
                    EndpointType::AzureOpenAi => {
                        urls.push(format!("{base}/openai/v1/videos/{video_id}/content"));
                    }
                    EndpointType::ApiManagementGateway => {
                        urls.push(format!("{base}/v1/videos/{video_id}/content"));
                        urls.push(format!("{base}/openai/v1/videos/{video_id}/content"));
                    }
                    _ => {
                        if base.ends_with("/v1") {
                            urls.push(format!("{base}/videos/{video_id}/content"));
                        } else {
                            urls.push(format!("{base}/v1/videos/{video_id}/content"));
                        }
                    }
                }
            }
        }

        urls
    }

    /// Try candidate URLs in order. Returns (Response, success_url) on first 2xx.
    /// 404/405 → try next. 401/403 → Auth error. 429 → RateLimited. Other → Network error.
    async fn try_candidates(
        &self,
        urls: &[String],
        build_request: impl Fn(&str) -> reqwest::RequestBuilder,
    ) -> Result<(reqwest::Response, String), ProviderError> {
        let mut last_error = String::new();

        for url in urls {
            let resp = build_request(url).send().await;
            match resp {
                Ok(response) => {
                    let status = response.status();
                    if status.is_success() {
                        return Ok((response, url.clone()));
                    }
                    let text = response.text().await.unwrap_or_default();
                    if status.as_u16() == 401 || status.as_u16() == 403 {
                        return Err(ProviderError::Auth(format!("{status}: {text}")));
                    }
                    if status.as_u16() == 429 {
                        return Err(ProviderError::RateLimited {
                            retry_after_ms: 10000,
                        });
                    }
                    if status.as_u16() == 404 || status.as_u16() == 405 {
                        last_error = format!("{status}: {text}");
                        continue;
                    }
                    return Err(ProviderError::Network(format!("{status}: {text}")));
                }
                Err(e) => {
                    last_error = e.to_string();
                    continue;
                }
            }
        }

        Err(ProviderError::Network(format!(
            "All {} candidate URLs failed: {last_error}",
            urls.len()
        )))
    }

    /// Extract generation_id from API response JSON.
    fn extract_generation_id(json_val: &serde_json::Value) -> Option<String> {
        json_val["generations"]
            .as_array()
            .and_then(|g| g.first())
            .and_then(|g| g["id"].as_str())
            .map(|s| s.to_string())
    }

    /// Extract download URL from poll response JSON (multiple possible locations).
    fn extract_download_url(json_val: &serde_json::Value) -> Option<String> {
        // Try generations[0].url first, then output.url, then video.url
        json_val["generations"]
            .as_array()
            .and_then(|g| g.first())
            .and_then(|g| g["url"].as_str())
            .or_else(|| {
                json_val["output"]["url"]
                    .as_str()
                    .or_else(|| json_val["video"]["url"].as_str())
            })
            .map(|s| s.to_string())
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
        let candidates = self.build_video_create_urls(&api_mode);

        let body = match api_mode {
            VideoApiMode::SoraJobs => json!({
                "model": &request.model,
                "prompt": &request.prompt,
                "size": &request.size,
                "n": request.n.unwrap_or(1),
            }),
            VideoApiMode::Videos => json!({
                "model": &request.model,
                "prompt": &request.prompt,
                "size": &request.size,
                "duration": request.duration_seconds,
                "n": request.n.unwrap_or(1),
                // TODO: reference_image via multipart when API confirms JSON support
            }),
        };

        let (resp, _url) = self
            .try_candidates(&candidates, |url| {
                apply_auth(&self.endpoint, self.client.post(url)).json(&body)
            })
            .await?;

        let json_val: serde_json::Value = resp
            .json()
            .await
            .map_err(|e| ProviderError::Internal(e.to_string()))?;

        let video_id = json_val["id"]
            .as_str()
            .map(|s| s.to_string())
            .ok_or_else(|| ProviderError::Internal("No 'id' in create response".into()))?;

        let generation_id = Self::extract_generation_id(&json_val);

        Ok(VideoGenResult {
            video_id,
            status: "pending".into(),
            generation_id,
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
        // Try SoraJobs poll URLs first, then Videos poll URLs
        let modes = [VideoApiMode::SoraJobs, VideoApiMode::Videos];
        let mut last_error = String::new();

        for mode in &modes {
            let poll_urls = self.build_video_poll_urls(video_id, mode);

            for url in &poll_urls {
                let resp = apply_auth(&self.endpoint, self.client.get(url))
                    .send()
                    .await;

                match resp {
                    Ok(response) => {
                        let status_code = response.status();
                        if status_code.as_u16() == 404 {
                            last_error = format!("404 from {url}");
                            continue;
                        }
                        if status_code.as_u16() == 401 || status_code.as_u16() == 403 {
                            let text = response.text().await.unwrap_or_default();
                            return Err(ProviderError::Auth(format!("{status_code}: {text}")));
                        }
                        if status_code.as_u16() == 429 {
                            return Err(ProviderError::RateLimited {
                                retry_after_ms: 10000,
                            });
                        }
                        if !status_code.is_success() {
                            let text = response.text().await.unwrap_or_default();
                            return Err(ProviderError::Network(format!(
                                "Poll video failed {status_code}: {text}"
                            )));
                        }

                        let json_val: serde_json::Value = response
                            .json()
                            .await
                            .map_err(|e| ProviderError::Internal(e.to_string()))?;

                        let job_status = json_val["status"]
                            .as_str()
                            .unwrap_or("unknown")
                            .to_string();

                        let download_url = Self::extract_download_url(&json_val);
                        let generation_id = Self::extract_generation_id(&json_val);

                        return Ok(VideoGenResult {
                            video_id: video_id.to_string(),
                            status: job_status,
                            generation_id,
                            download_url,
                            file_path: None,
                            generate_seconds: None,
                        });
                    }
                    Err(e) => {
                        last_error = e.to_string();
                        continue;
                    }
                }
            }
        }

        Err(ProviderError::Network(format!(
            "Could not poll video status for {video_id}: {last_error}"
        )))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use tfp_core::ApiKeyHeaderMode;

    fn azure_endpoint() -> AiEndpoint {
        AiEndpoint {
            id: "vid-ep".into(),
            name: "Video EP".into(),
            endpoint_type: EndpointType::AzureOpenAi,
            url: "https://myresource.openai.azure.com".into(),
            api_key: "key".into(),
            api_version: Some("2025-03-01-preview".into()),
            enabled: true,
            auth_header_mode: ApiKeyHeaderMode::ApiKeyHeader,
            ..AiEndpoint::default()
        }
    }

    fn apim_endpoint() -> AiEndpoint {
        AiEndpoint {
            id: "apim-ep".into(),
            name: "APIM EP".into(),
            endpoint_type: EndpointType::ApiManagementGateway,
            url: "https://apim.example.com/ai01/openai".into(),
            api_key: "key".into(),
            api_version: Some("2025-03-01-preview".into()),
            enabled: true,
            auth_header_mode: ApiKeyHeaderMode::Bearer,
            ..AiEndpoint::default()
        }
    }

    fn compat_endpoint() -> AiEndpoint {
        AiEndpoint {
            id: "compat-ep".into(),
            name: "Compat EP".into(),
            endpoint_type: EndpointType::OpenAiCompatible,
            url: "https://api.openai.com/v1".into(),
            api_key: "key".into(),
            api_version: None,
            enabled: true,
            auth_header_mode: ApiKeyHeaderMode::Bearer,
            ..AiEndpoint::default()
        }
    }

    fn compat_endpoint_no_v1() -> AiEndpoint {
        AiEndpoint {
            id: "compat-ep2".into(),
            name: "Compat EP2".into(),
            endpoint_type: EndpointType::OpenAiCompatible,
            url: "https://my-server.example.com".into(),
            api_key: "key".into(),
            api_version: None,
            enabled: true,
            auth_header_mode: ApiKeyHeaderMode::Bearer,
            ..AiEndpoint::default()
        }
    }

    #[test]
    fn test_provider_meta() {
        let p = OpenAiVideoProvider::new(azure_endpoint());
        assert_eq!(p.id(), "vid-ep");
        assert_eq!(p.display_name(), "Video EP");
        assert_eq!(p.capabilities(), vec![ProviderCapability::VideoGeneration]);
    }

    #[test]
    fn test_detect_api_mode() {
        let p = OpenAiVideoProvider::new(azure_endpoint());
        let mut req = VideoGenRequest {
            prompt: "test".into(),
            model: "custom".into(),
            endpoint_id: "vid-ep".into(),
            size: "1080x1920".into(),
            duration_seconds: 10,
            api_mode: Some(VideoApiMode::SoraJobs),
            reference_image_path: None,
            n: None,
        };
        assert_eq!(p.detect_api_mode(&req), VideoApiMode::SoraJobs);

        req.api_mode = None;
        req.model = "sora".into();
        assert_eq!(p.detect_api_mode(&req), VideoApiMode::Videos);

        req.model = "sora-2-turbo".into();
        assert_eq!(p.detect_api_mode(&req), VideoApiMode::Videos);

        req.model = "custom-model".into();
        assert_eq!(p.detect_api_mode(&req), VideoApiMode::SoraJobs);
    }

    // --- T-002: build_video_create_urls ---

    #[test]
    fn test_create_urls_azure_sora_jobs() {
        let p = OpenAiVideoProvider::new(azure_endpoint());
        let urls = p.build_video_create_urls(&VideoApiMode::SoraJobs);
        assert_eq!(urls.len(), 1);
        assert_eq!(
            urls[0],
            "https://myresource.openai.azure.com/openai/v1/video/generations/jobs?api-version=2025-03-01-preview"
        );
    }

    #[test]
    fn test_create_urls_azure_videos() {
        let p = OpenAiVideoProvider::new(azure_endpoint());
        let urls = p.build_video_create_urls(&VideoApiMode::Videos);
        assert_eq!(urls.len(), 1);
        assert_eq!(
            urls[0],
            "https://myresource.openai.azure.com/openai/v1/videos"
        );
    }

    #[test]
    fn test_create_urls_apim_sora_jobs() {
        let p = OpenAiVideoProvider::new(apim_endpoint());
        let urls = p.build_video_create_urls(&VideoApiMode::SoraJobs);
        assert_eq!(urls.len(), 3);
        assert_eq!(urls[0], "https://apim.example.com/ai01/openai/v1/video/generations/jobs");
        assert!(urls[1].contains("/openai/v1/video/generations/jobs?api-version="));
        assert!(urls[2].contains("/video/generations/jobs?api-version="));
    }

    #[test]
    fn test_create_urls_apim_videos() {
        let p = OpenAiVideoProvider::new(apim_endpoint());
        let urls = p.build_video_create_urls(&VideoApiMode::Videos);
        assert_eq!(urls.len(), 2);
        assert_eq!(urls[0], "https://apim.example.com/ai01/openai/v1/videos");
        assert_eq!(urls[1], "https://apim.example.com/ai01/openai/openai/v1/videos");
    }

    #[test]
    fn test_create_urls_compat_sora_jobs_with_v1() {
        let p = OpenAiVideoProvider::new(compat_endpoint());
        let urls = p.build_video_create_urls(&VideoApiMode::SoraJobs);
        assert_eq!(urls.len(), 1);
        assert_eq!(urls[0], "https://api.openai.com/v1/video/generations/jobs");
    }

    #[test]
    fn test_create_urls_compat_sora_jobs_no_v1() {
        let p = OpenAiVideoProvider::new(compat_endpoint_no_v1());
        let urls = p.build_video_create_urls(&VideoApiMode::SoraJobs);
        assert_eq!(urls.len(), 1);
        assert_eq!(urls[0], "https://my-server.example.com/v1/video/generations/jobs");
    }

    #[test]
    fn test_create_urls_compat_videos_with_v1() {
        let p = OpenAiVideoProvider::new(compat_endpoint());
        let urls = p.build_video_create_urls(&VideoApiMode::Videos);
        assert_eq!(urls.len(), 1);
        assert_eq!(urls[0], "https://api.openai.com/v1/videos");
    }

    #[test]
    fn test_create_urls_compat_videos_no_v1() {
        let p = OpenAiVideoProvider::new(compat_endpoint_no_v1());
        let urls = p.build_video_create_urls(&VideoApiMode::Videos);
        assert_eq!(urls.len(), 1);
        assert_eq!(urls[0], "https://my-server.example.com/v1/videos");
    }

    // --- T-003: build_video_poll_urls ---

    #[test]
    fn test_poll_urls_azure_sora_jobs() {
        let p = OpenAiVideoProvider::new(azure_endpoint());
        let urls = p.build_video_poll_urls("job-123", &VideoApiMode::SoraJobs);
        assert_eq!(urls.len(), 1);
        assert_eq!(
            urls[0],
            "https://myresource.openai.azure.com/openai/v1/video/generations/jobs/job-123?api-version=2025-03-01-preview"
        );
    }

    #[test]
    fn test_poll_urls_apim_sora_jobs() {
        let p = OpenAiVideoProvider::new(apim_endpoint());
        let urls = p.build_video_poll_urls("job-456", &VideoApiMode::SoraJobs);
        assert_eq!(urls.len(), 3);
        assert_eq!(
            urls[0],
            "https://apim.example.com/ai01/openai/v1/video/generations/jobs/job-456"
        );
        assert!(urls[1].contains("/jobs/job-456?api-version="));
    }

    #[test]
    fn test_poll_urls_azure_videos() {
        let p = OpenAiVideoProvider::new(azure_endpoint());
        let urls = p.build_video_poll_urls("vid-789", &VideoApiMode::Videos);
        assert_eq!(urls.len(), 1);
        assert_eq!(
            urls[0],
            "https://myresource.openai.azure.com/openai/v1/videos/vid-789"
        );
    }

    #[test]
    fn test_poll_urls_compat_videos() {
        let p = OpenAiVideoProvider::new(compat_endpoint_no_v1());
        let urls = p.build_video_poll_urls("vid-abc", &VideoApiMode::Videos);
        assert_eq!(urls.len(), 1);
        assert_eq!(
            urls[0],
            "https://my-server.example.com/v1/videos/vid-abc"
        );
    }

    // --- T-004: build_video_download_urls ---

    #[test]
    fn test_download_urls_azure_sora_jobs_no_gen_id() {
        let p = OpenAiVideoProvider::new(azure_endpoint());
        let urls = p.build_video_download_urls("job-1", None, &VideoApiMode::SoraJobs);
        assert_eq!(urls.len(), 1);
        assert!(urls[0].contains("/jobs/job-1/content/video?api-version="));
    }

    #[test]
    fn test_download_urls_azure_sora_jobs_with_gen_id() {
        let p = OpenAiVideoProvider::new(azure_endpoint());
        let urls = p.build_video_download_urls("job-1", Some("gen-42"), &VideoApiMode::SoraJobs);
        assert_eq!(urls.len(), 2);
        assert!(urls[0].contains("/jobs/job-1/content/video"));
        assert!(urls[1].contains("/generations/gen-42/content/video"));
    }

    #[test]
    fn test_download_urls_apim_sora_jobs_with_gen_id() {
        let p = OpenAiVideoProvider::new(apim_endpoint());
        let urls = p.build_video_download_urls("job-1", Some("gen-42"), &VideoApiMode::SoraJobs);
        // APIM: primary (no openai prefix), openai prefix, + generation_id variants
        assert!(urls.len() >= 3);
        assert!(urls[0].contains("/v1/video/generations/jobs/job-1/content/video"));
        // Should have gen-42 variants too
        assert!(urls.iter().any(|u| u.contains("gen-42")));
    }

    #[test]
    fn test_download_urls_videos_mode() {
        let p = OpenAiVideoProvider::new(azure_endpoint());
        let urls = p.build_video_download_urls("vid-1", None, &VideoApiMode::Videos);
        assert_eq!(urls.len(), 1);
        assert_eq!(
            urls[0],
            "https://myresource.openai.azure.com/openai/v1/videos/vid-1/content"
        );
    }

    #[test]
    fn test_download_urls_compat_videos() {
        let p = OpenAiVideoProvider::new(compat_endpoint_no_v1());
        let urls = p.build_video_download_urls("vid-1", None, &VideoApiMode::Videos);
        assert_eq!(urls.len(), 1);
        assert_eq!(
            urls[0],
            "https://my-server.example.com/v1/videos/vid-1/content"
        );
    }

    // --- Helper extraction tests ---

    #[test]
    fn test_extract_generation_id_present() {
        let json = serde_json::json!({
            "id": "job-1",
            "status": "completed",
            "generations": [{"id": "gen-42", "url": "https://example.com/video.mp4"}]
        });
        assert_eq!(
            OpenAiVideoProvider::extract_generation_id(&json),
            Some("gen-42".to_string())
        );
    }

    #[test]
    fn test_extract_generation_id_absent() {
        let json = serde_json::json!({"id": "job-1", "status": "pending"});
        assert_eq!(OpenAiVideoProvider::extract_generation_id(&json), None);
    }

    #[test]
    fn test_extract_download_url_generations() {
        let json = serde_json::json!({
            "generations": [{"id": "g1", "url": "https://cdn.example.com/v.mp4"}]
        });
        assert_eq!(
            OpenAiVideoProvider::extract_download_url(&json),
            Some("https://cdn.example.com/v.mp4".to_string())
        );
    }

    #[test]
    fn test_extract_download_url_output() {
        let json = serde_json::json!({"output": {"url": "https://cdn.example.com/out.mp4"}});
        assert_eq!(
            OpenAiVideoProvider::extract_download_url(&json),
            Some("https://cdn.example.com/out.mp4".to_string())
        );
    }

    #[test]
    fn test_extract_download_url_video() {
        let json = serde_json::json!({"video": {"url": "https://cdn.example.com/vid.mp4"}});
        assert_eq!(
            OpenAiVideoProvider::extract_download_url(&json),
            Some("https://cdn.example.com/vid.mp4".to_string())
        );
    }

    #[test]
    fn test_extract_download_url_none() {
        let json = serde_json::json!({"status": "pending"});
        assert_eq!(OpenAiVideoProvider::extract_download_url(&json), None);
    }

    // --- VideoGenResult generation_id serde ---

    #[test]
    fn test_video_gen_result_generation_id_serde() {
        let result = VideoGenResult {
            video_id: "v1".into(),
            status: "completed".into(),
            generation_id: Some("gen-1".into()),
            download_url: Some("https://example.com/v.mp4".into()),
            file_path: None,
            generate_seconds: None,
        };
        let json = serde_json::to_string(&result).unwrap();
        assert!(json.contains("generation_id"));

        let deserialized: VideoGenResult = serde_json::from_str(&json).unwrap();
        assert_eq!(deserialized.generation_id, Some("gen-1".into()));
    }

    #[test]
    fn test_video_gen_result_without_generation_id() {
        // Ensure backward compat: JSON without generation_id deserializes fine
        let json = r#"{"video_id":"v1","status":"pending","download_url":null,"file_path":null,"generate_seconds":null}"#;
        let result: VideoGenResult = serde_json::from_str(json).unwrap();
        assert_eq!(result.generation_id, None);
        assert_eq!(result.video_id, "v1");
    }
}