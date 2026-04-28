use reqwest::Client;
use serde_json::json;

use crate::models::*;
use super::registry::*;

/// P3-4: OpenAI 视频生成 Provider
///
/// 支持两种 API 模式:
/// - SoraJobs (sora-1): POST /openai/v1/video/generations/jobs → GET .../jobs/{id}
/// - Videos (sora-2): POST /v1/videos → GET /v1/videos/{id}
///
/// 异步轮询模式: create → poll → download
pub struct OpenAiVideoProvider {
    client: Client,
    endpoint: AiEndpoint,
}

impl OpenAiVideoProvider {
    pub fn new(endpoint: AiEndpoint) -> Self {
        Self {
            client: Client::builder()
                .timeout(std::time::Duration::from_secs(120))
                .build()
                .unwrap_or_default(),
            endpoint,
        }
    }

    fn apply_auth(&self, req: reqwest::RequestBuilder) -> reqwest::RequestBuilder {
        let key = &self.endpoint.api_key;
        let mode = self.endpoint.auth_header_mode.as_str();
        match mode {
            "bearer" => req.header("Authorization", format!("Bearer {key}")),
            "api_key" => req.header("api-key", key),
            _ => {
                if self.endpoint.endpoint_type == EndpointType::AzureOpenAi
                    || self.endpoint.endpoint_type == EndpointType::AzureSpeech
                {
                    req.header("api-key", key)
                } else {
                    req.header("Authorization", format!("Bearer {key}"))
                }
            }
        }
    }

    fn detect_api_mode(&self, request: &VideoGenRequest) -> VideoApiMode {
        if let Some(ref mode) = request.api_mode {
            return mode.clone();
        }
        // sora-2 默认走 Videos，sora-1 走 SoraJobs
        if request.model.contains("sora-2") || request.model == "sora" {
            VideoApiMode::Videos
        } else {
            VideoApiMode::SoraJobs
        }
    }

    /// 创建视频生成任务，返回 video_id
    pub async fn create_video(
        &self,
        request: &VideoGenRequest,
    ) -> Result<String, ProviderError> {
        let api_mode = self.detect_api_mode(request);
        match api_mode {
            VideoApiMode::SoraJobs => self.create_sora_jobs(request).await,
            VideoApiMode::Videos => self.create_videos(request).await,
        }
    }

    /// sora-1: POST /openai/v1/video/generations/jobs
    async fn create_sora_jobs(&self, request: &VideoGenRequest) -> Result<String, ProviderError> {
        let base = self.endpoint.url.trim_end_matches('/');
        let api_ver = self.endpoint.api_version.as_deref().unwrap_or("2025-03-01-preview");
        let url = format!("{base}/openai/v1/video/generations/jobs?api-version={api_ver}");

        let body = json!({
            "model": &request.model,
            "prompt": &request.prompt,
            "size": &request.size,
            "n": request.n.unwrap_or(1),
        });

        let resp = self
            .apply_auth(self.client.post(&url))
            .json(&body)
            .send()
            .await
            .map_err(|e| ProviderError::Network(e.to_string()))?;

        let status = resp.status();
        if !status.is_success() {
            let text = resp.text().await.unwrap_or_default();
            return Err(ProviderError::Network(format!("Create video failed {status}: {text}")));
        }

        let json: serde_json::Value = resp.json().await
            .map_err(|e| ProviderError::Internal(e.to_string()))?;

        json["id"]
            .as_str()
            .map(|s| s.to_string())
            .ok_or_else(|| ProviderError::Internal("No 'id' in create response".into()))
    }

    /// sora-2: POST /v1/videos (multipart)
    async fn create_videos(&self, request: &VideoGenRequest) -> Result<String, ProviderError> {
        let base = self.endpoint.url.trim_end_matches('/');
        let url = format!("{base}/v1/videos");

        let mut form = reqwest::multipart::Form::new()
            .text("model", request.model.clone())
            .text("prompt", request.prompt.clone())
            .text("size", request.size.clone())
            .text("duration", request.duration_seconds.to_string())
            .text("n", request.n.unwrap_or(1).to_string());

        // 可选参考图
        if let Some(ref ref_path) = request.reference_image_path {
            let data = tokio::fs::read(ref_path).await
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

        let resp = self
            .apply_auth(self.client.post(&url))
            .multipart(form)
            .send()
            .await
            .map_err(|e| ProviderError::Network(e.to_string()))?;

        let status = resp.status();
        if !status.is_success() {
            let text = resp.text().await.unwrap_or_default();
            return Err(ProviderError::Network(format!("Create video failed {status}: {text}")));
        }

        let json: serde_json::Value = resp.json().await
            .map_err(|e| ProviderError::Internal(e.to_string()))?;

        json["id"]
            .as_str()
            .map(|s| s.to_string())
            .ok_or_else(|| ProviderError::Internal("No 'id' in create response".into()))
    }

    /// 轮询视频生成状态
    pub async fn poll_video(
        &self,
        video_id: &str,
        request: &VideoGenRequest,
    ) -> Result<VideoGenResult, ProviderError> {
        let api_mode = self.detect_api_mode(request);
        let base = self.endpoint.url.trim_end_matches('/');

        let url = match api_mode {
            VideoApiMode::SoraJobs => {
                let api_ver = self.endpoint.api_version.as_deref().unwrap_or("2025-03-01-preview");
                format!("{base}/openai/v1/video/generations/jobs/{video_id}?api-version={api_ver}")
            }
            VideoApiMode::Videos => {
                format!("{base}/v1/videos/{video_id}")
            }
        };

        let resp = self
            .apply_auth(self.client.get(&url))
            .send()
            .await
            .map_err(|e| ProviderError::Network(e.to_string()))?;

        let status_code = resp.status();
        if !status_code.is_success() {
            let text = resp.text().await.unwrap_or_default();
            return Err(ProviderError::Network(format!("Poll video failed {status_code}: {text}")));
        }

        let json: serde_json::Value = resp.json().await
            .map_err(|e| ProviderError::Internal(e.to_string()))?;

        let job_status = json["status"].as_str().unwrap_or("unknown").to_string();

        // 提取下载 URL
        let download_url = match api_mode {
            VideoApiMode::SoraJobs => {
                // sora-1: generations[0].url
                json["generations"]
                    .as_array()
                    .and_then(|g| g.first())
                    .and_then(|g| g["url"].as_str())
                    .map(|s| s.to_string())
            }
            VideoApiMode::Videos => {
                // sora-2: output.url 或 video.url
                json["output"]["url"]
                    .as_str()
                    .or_else(|| json["video"]["url"].as_str())
                    .map(|s| s.to_string())
            }
        };

        Ok(VideoGenResult {
            video_id: video_id.to_string(),
            status: job_status,
            download_url,
            file_path: None,
            generate_seconds: None,
        })
    }

    /// 下载视频到本地路径
    pub async fn download_video(
        &self,
        download_url: &str,
        output_path: &std::path::Path,
    ) -> Result<String, ProviderError> {
        let resp = self
            .apply_auth(self.client.get(download_url))
            .send()
            .await
            .map_err(|e| ProviderError::Network(e.to_string()))?;

        if !resp.status().is_success() {
            let text = resp.text().await.unwrap_or_default();
            return Err(ProviderError::Network(format!("Download video failed: {text}")));
        }

        let bytes = resp.bytes().await
            .map_err(|e| ProviderError::Internal(e.to_string()))?;

        // 原子写入
        let tmp_path = output_path.with_extension("mp4.tmp");
        tokio::fs::write(&tmp_path, &bytes).await
            .map_err(|e| ProviderError::Internal(format!("Write tmp: {e}")))?;
        tokio::fs::rename(&tmp_path, output_path).await
            .map_err(|e| ProviderError::Internal(format!("Rename: {e}")))?;

        Ok(output_path.to_string_lossy().to_string())
    }
}

impl ProviderMeta for OpenAiVideoProvider {
    fn id(&self) -> &str { &self.endpoint.id }
    fn display_name(&self) -> &str { &self.endpoint.name }
    fn capabilities(&self) -> Vec<ProviderCapability> {
        vec![ProviderCapability::ImageGeneration] // 复用 — 视频也是媒体生成
    }
}
