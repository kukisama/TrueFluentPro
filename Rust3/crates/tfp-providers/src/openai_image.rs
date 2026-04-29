use async_trait::async_trait;
use reqwest::multipart;
use serde_json::json;
use std::time::Instant;

use tfp_core::{
    AiEndpoint, EndpointType, ImageEditMode, ImageGenRequest, ImageGenResult, ProviderError,
};

use crate::auth::apply_auth;
use crate::traits::{ImageGenSlot, ProviderCapability, ProviderMeta};

pub struct OpenAiImageProvider {
    client: reqwest::Client,
    endpoint: AiEndpoint,
}

impl OpenAiImageProvider {
    pub fn new(endpoint: AiEndpoint) -> Self {
        Self {
            client: reqwest::Client::builder()
                .timeout(std::time::Duration::from_secs(300))
                .build()
                .unwrap_or_default(),
            endpoint,
        }
    }

    /// Determine whether to use Responses API based on edit mode and text_model presence.
    fn should_use_responses_api(&self, request: &ImageGenRequest) -> bool {
        // 1. User explicitly chose V1Multipart → never use Responses API
        if request.image_edit_mode == Some(ImageEditMode::V1Multipart) {
            return false;
        }
        // 2. text_model is set → use Responses API
        if request.text_model.is_some() {
            return true;
        }
        // 3. Otherwise → traditional Images API
        false
    }

    /// Build candidate URLs for /images/generations.
    pub(crate) fn build_generate_urls(&self) -> Vec<String> {
        let base = self.endpoint.url.trim_end_matches('/');
        match self.endpoint.endpoint_type {
            EndpointType::AzureOpenAi => {
                vec![format!("{base}/openai/v1/images/generations")]
            }
            EndpointType::ApiManagementGateway => {
                let api_ver = self
                    .endpoint
                    .api_version
                    .as_deref()
                    .unwrap_or("2025-04-01-preview");
                vec![
                    format!("{base}/v1/images/generations"),
                    format!("{base}/images/generations?api-version={api_ver}"),
                    format!("{base}/images/generations"),
                ]
            }
            _ => {
                if base.ends_with("/v1") {
                    vec![format!("{base}/images/generations")]
                } else {
                    vec![format!("{base}/v1/images/generations")]
                }
            }
        }
    }

    /// Build candidate URLs for /images/edits (multipart upload).
    pub(crate) fn build_edit_urls(&self) -> Vec<String> {
        let base = self.endpoint.url.trim_end_matches('/');
        match self.endpoint.endpoint_type {
            EndpointType::AzureOpenAi => {
                vec![format!("{base}/openai/v1/images/edits")]
            }
            EndpointType::ApiManagementGateway => {
                let api_ver = self
                    .endpoint
                    .api_version
                    .as_deref()
                    .unwrap_or("2025-04-01-preview");
                vec![
                    format!("{base}/v1/images/edits"),
                    format!("{base}/images/edits?api-version={api_ver}"),
                    format!("{base}/images/edits"),
                ]
            }
            _ => {
                if base.ends_with("/v1") {
                    vec![format!("{base}/images/edits")]
                } else {
                    vec![format!("{base}/v1/images/edits")]
                }
            }
        }
    }

    /// Build candidate URLs for Responses API (/responses).
    pub(crate) fn build_responses_urls(&self) -> Vec<String> {
        let base = self.endpoint.url.trim_end_matches('/');
        match self.endpoint.endpoint_type {
            EndpointType::AzureOpenAi => {
                vec![format!("{base}/openai/v1/responses")]
            }
            EndpointType::ApiManagementGateway => {
                let api_ver = self
                    .endpoint
                    .api_version
                    .as_deref()
                    .unwrap_or("2025-03-01-preview");
                vec![
                    format!("{base}/v1/responses"),
                    format!("{base}/responses?api-version={api_ver}"),
                    format!("{base}/responses"),
                ]
            }
            _ => {
                if base.ends_with("/v1") {
                    vec![format!("{base}/responses")]
                } else {
                    vec![format!("{base}/v1/responses")]
                }
            }
        }
    }

    /// Build candidate URLs for /v1/files upload.
    pub(crate) fn build_file_upload_urls(&self) -> Vec<String> {
        let base = self.endpoint.url.trim_end_matches('/');
        match self.endpoint.endpoint_type {
            EndpointType::AzureOpenAi => {
                vec![format!("{base}/openai/v1/files")]
            }
            EndpointType::ApiManagementGateway => {
                vec![
                    format!("{base}/v1/files"),
                    format!("{base}/openai/v1/files"),
                ]
            }
            _ => {
                if base.ends_with("/v1") {
                    vec![format!("{base}/files")]
                } else {
                    vec![format!("{base}/v1/files")]
                }
            }
        }
    }

    /// Legacy alias for backward compat (delegates to build_generate_urls).
    #[allow(dead_code)]
    pub(crate) fn build_url_candidates(&self) -> Vec<String> {
        self.build_generate_urls()
    }

    /// Try candidate URLs in order. Returns (Response, success_url, attempted_urls) on 2xx.
    /// 404/405 → try next candidate. Other errors → return immediately.
    async fn try_candidates(
        &self,
        urls: &[String],
        build_request: impl Fn(&str) -> reqwest::RequestBuilder,
    ) -> Result<(reqwest::Response, String, Vec<String>), ProviderError> {
        let mut attempted: Vec<String> = Vec::new();
        let mut last_error = String::new();

        for url in urls {
            attempted.push(url.clone());
            let resp = build_request(url).send().await;

            match resp {
                Ok(response) => {
                    let status = response.status();
                    if status.is_success() {
                        return Ok((response, url.clone(), attempted));
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

    /// Upload a file to the Files API and return its file_id.
    ///
    /// Uses multipart/form-data with purpose=assistants (the only value APIM accepts).
    /// Tries candidate URLs in order with inline retry (multipart is not Clone).
    pub async fn upload_file(
        &self,
        file_path: &str,
        file_bytes: &[u8],
    ) -> Result<String, ProviderError> {
        let file_name = std::path::Path::new(file_path)
            .file_name()
            .unwrap_or_default()
            .to_string_lossy()
            .to_string();
        let mime_type = mime_from_extension(file_path);
        let candidates = self.build_file_upload_urls();

        let mut attempted: Vec<String> = Vec::new();
        let mut last_error = String::new();

        for url in &candidates {
            attempted.push(url.clone());

            let file_part = multipart::Part::bytes(file_bytes.to_vec())
                .file_name(file_name.clone())
                .mime_str(&mime_type)
                .map_err(|e| ProviderError::Internal(format!("MIME error: {e}")))?;

            let form = multipart::Form::new()
                .part("file", file_part)
                .text("purpose", "assistants");

            let resp = apply_auth(&self.endpoint, self.client.post(url))
                .multipart(form)
                .send()
                .await;

            match resp {
                Ok(response) => {
                    let status = response.status();
                    if status.is_success() {
                        let json_resp: serde_json::Value = response
                            .json()
                            .await
                            .map_err(|e| ProviderError::Internal(format!("JSON parse error: {e}")))?;
                        let file_id = json_resp["id"]
                            .as_str()
                            .ok_or_else(|| {
                                ProviderError::Internal(
                                    "File upload response missing 'id' field".into(),
                                )
                            })?
                            .to_string();
                        return Ok(file_id);
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
            "All {} file upload candidate URLs failed: {last_error}",
            candidates.len()
        )))
    }

    /// Generate via traditional /images/generations endpoint with candidate URL fallback.
    async fn generate_via_images_api(
        &self,
        request: &ImageGenRequest,
    ) -> Result<Vec<ImageGenResult>, ProviderError> {
        let candidates = self.build_generate_urls();
        let size = format!("{}x{}", request.width, request.height);
        let output_format = request.output_format.as_deref().unwrap_or("png");

        let mut body = json!({
            "prompt": &request.prompt,
            "model": &request.model,
            "size": size,
            "quality": request.quality.as_deref().unwrap_or("auto"),
            "output_format": output_format,
        });

        if let Some(ref bg) = request.background {
            if bg != "auto" && !bg.is_empty() {
                body["background"] = json!(bg);
            }
        }
        if let Some(n) = request.n {
            if n > 1 {
                body["n"] = json!(n);
            }
        }

        let body_bytes = serde_json::to_vec(&body)
            .map_err(|e| ProviderError::Internal(format!("JSON serialize error: {e}")))?;

        let start = Instant::now();
        let (resp, success_url, attempted_urls) = self
            .try_candidates(&candidates, |url| {
                apply_auth(&self.endpoint, self.client.post(url))
                    .header("Content-Type", "application/json")
                    .body(body_bytes.clone())
            })
            .await?;
        let generate_seconds = start.elapsed().as_secs_f64();

        let download_start = Instant::now();
        let mut results =
            parse_image_response_from_reqwest(resp, &success_url, attempted_urls).await?;
        let download_seconds = download_start.elapsed().as_secs_f64();

        for r in &mut results {
            r.generate_seconds = generate_seconds;
            r.download_seconds = download_seconds;
        }
        Ok(results)
    }

    /// Generate via Responses API with candidate URL fallback.
    async fn generate_via_responses(
        &self,
        request: &ImageGenRequest,
    ) -> Result<Vec<ImageGenResult>, ProviderError> {
        let text_model = request
            .text_model
            .as_deref()
            .unwrap_or(&request.model);
        let image_model = request
            .image_model
            .as_deref()
            .unwrap_or(&request.model);

        let candidates = self.build_responses_urls();

        let mut body = json!({
            "model": text_model,
            "input": &request.prompt,
            "tools": [{ "type": "image_generation" }],
        });

        if let Some(ref prev_id) = request.previous_response_id {
            body["previous_response_id"] = json!(prev_id);
        }

        let body_bytes = serde_json::to_vec(&body)
            .map_err(|e| ProviderError::Internal(format!("JSON serialize error: {e}")))?;
        let image_model_owned = image_model.to_string();

        let start = Instant::now();
        let (resp, success_url, attempted_urls) = self
            .try_candidates(&candidates, |url| {
                apply_auth(&self.endpoint, self.client.post(url))
                    .header("Content-Type", "application/json")
                    .header(
                        "x-ms-oai-image-generation-deployment",
                        &image_model_owned,
                    )
                    .body(body_bytes.clone())
            })
            .await?;
        let generate_seconds = start.elapsed().as_secs_f64();

        let download_start = Instant::now();
        let mut results =
            parse_image_response_from_reqwest(resp, &success_url, attempted_urls).await?;
        let download_seconds = download_start.elapsed().as_secs_f64();

        for r in &mut results {
            r.generate_seconds = generate_seconds;
            r.download_seconds = download_seconds;
        }
        Ok(results)
    }

    /// Edit an image via Responses API using file_id references.
    ///
    /// Builds input array with input_text + input_image (file_id) entries,
    /// sends to /responses with x-ms-oai-image-generation-deployment header.
    async fn edit_via_responses_api(
        &self,
        request: &ImageGenRequest,
    ) -> Result<Vec<ImageGenResult>, ProviderError> {
        let text_model = request
            .text_model
            .as_deref()
            .unwrap_or(&request.model);
        let image_model = request
            .image_model
            .as_deref()
            .unwrap_or(&request.model);

        let candidates = self.build_responses_urls();

        // Build input array: text prompt + file_id references
        let mut input_items: Vec<serde_json::Value> = Vec::new();
        input_items.push(json!({
            "type": "input_text",
            "text": &request.prompt,
        }));
        for fid in &request.uploaded_file_ids {
            input_items.push(json!({
                "type": "input_image",
                "file_id": fid,
            }));
        }

        let mut body = json!({
            "model": text_model,
            "input": input_items,
            "tools": [{ "type": "image_generation" }],
        });

        if let Some(ref prev_id) = request.previous_response_id {
            body["previous_response_id"] = json!(prev_id);
        }

        let body_bytes = serde_json::to_vec(&body)
            .map_err(|e| ProviderError::Internal(format!("JSON serialize error: {e}")))?;
        let image_model_owned = image_model.to_string();

        let start = Instant::now();
        let (resp, success_url, attempted_urls) = self
            .try_candidates(&candidates, |url| {
                apply_auth(&self.endpoint, self.client.post(url))
                    .header("Content-Type", "application/json")
                    .header(
                        "x-ms-oai-image-generation-deployment",
                        &image_model_owned,
                    )
                    .body(body_bytes.clone())
            })
            .await?;
        let generate_seconds = start.elapsed().as_secs_f64();

        let download_start = Instant::now();
        let mut results =
            parse_image_response_from_reqwest(resp, &success_url, attempted_urls).await?;
        let download_seconds = download_start.elapsed().as_secs_f64();

        for r in &mut results {
            r.generate_seconds = generate_seconds;
            r.download_seconds = download_seconds;
        }
        Ok(results)
    }

    /// Edit an image via multipart POST to /images/edits.
    pub async fn edit_via_multipart(
        &self,
        request: &ImageGenRequest,
        reference_image_path: &str,
    ) -> Result<Vec<ImageGenResult>, ProviderError> {
        let image_bytes = tokio::fs::read(reference_image_path)
            .await
            .map_err(|e| {
                ProviderError::Internal(format!(
                    "Failed to read reference image '{reference_image_path}': {e}"
                ))
            })?;

        let mime_type = mime_from_extension(reference_image_path);
        let file_name = std::path::Path::new(reference_image_path)
            .file_name()
            .unwrap_or_default()
            .to_string_lossy()
            .to_string();

        let candidates = self.build_edit_urls();
        let size = format!("{}x{}", request.width, request.height);

        // We need to rebuild multipart for each candidate attempt
        let start = Instant::now();
        let mut attempted: Vec<String> = Vec::new();
        let mut last_error = String::new();
        let mut success_url = String::new();
        let mut success_resp: Option<reqwest::Response> = None;

        for url in &candidates {
            attempted.push(url.clone());

            let image_part = multipart::Part::bytes(image_bytes.clone())
                .file_name(file_name.clone())
                .mime_str(&mime_type)
                .map_err(|e| ProviderError::Internal(format!("MIME error: {e}")))?;

            let mut form = multipart::Form::new()
                .part("image", image_part)
                .text("prompt", request.prompt.clone())
                .text("model", request.model.clone())
                .text("size", size.clone());

            if let Some(ref quality) = request.quality {
                form = form.text("quality", quality.clone());
            }
            if let Some(ref bg) = request.background {
                if bg != "auto" && !bg.is_empty() {
                    form = form.text("background", bg.clone());
                }
            }

            let resp = apply_auth(&self.endpoint, self.client.post(url))
                .multipart(form)
                .send()
                .await;

            match resp {
                Ok(response) => {
                    let status = response.status();
                    if status.is_success() {
                        success_url = url.clone();
                        success_resp = Some(response);
                        break;
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

        let generate_seconds = start.elapsed().as_secs_f64();

        let resp = success_resp.ok_or_else(|| {
            ProviderError::Network(format!(
                "All {} edit candidate URLs failed: {last_error}",
                candidates.len()
            ))
        })?;

        let download_start = Instant::now();
        let mut results =
            parse_image_response_from_reqwest(resp, &success_url, attempted).await?;
        let download_seconds = download_start.elapsed().as_secs_f64();

        for r in &mut results {
            r.generate_seconds = generate_seconds;
            r.download_seconds = download_seconds;
        }
        Ok(results)
    }
}

/// Parse an image API response JSON body into ImageGenResult list.
///
/// Supports 3 formats:
/// - `data[].b64_json` — Images API base64
/// - `data[].url` — Images API URL reference
/// - `output[].type=="image_generation_call"` — Responses API format
pub fn parse_image_response(
    body: &serde_json::Value,
    request_url: &str,
    attempted_urls: Vec<String>,
) -> Result<Vec<ImageGenResult>, ProviderError> {
    let response_id = body["id"].as_str().map(|s| s.to_string());
    let input_tokens = body["usage"]["input_tokens"].as_u64().map(|v| v as u32);
    let output_tokens = body["usage"]["output_tokens"].as_u64().map(|v| v as u32);

    // Format 1: Images API → data[].b64_json or data[].url
    if let Some(data) = body["data"].as_array() {
        let results: Vec<ImageGenResult> = data
            .iter()
            .map(|item| ImageGenResult {
                url: item["url"].as_str().map(|s| s.to_string()),
                base64: item["b64_json"].as_str().map(|s| s.to_string()),
                revised_prompt: item["revised_prompt"].as_str().map(|s| s.to_string()),
                response_id: response_id.clone(),
                request_url: request_url.to_string(),
                attempted_urls: attempted_urls.clone(),
                actual_input_tokens: input_tokens,
                actual_output_tokens: output_tokens,
                ..Default::default()
            })
            .collect();
        if results.is_empty() {
            return Err(ProviderError::Internal(
                "API returned empty data array".into(),
            ));
        }
        return Ok(results);
    }

    // Format 2: Responses API → output[] with image_generation_call
    if let Some(output) = body["output"].as_array() {
        let results: Vec<ImageGenResult> = output
            .iter()
            .filter(|item| item["type"].as_str() == Some("image_generation_call"))
            .map(|item| ImageGenResult {
                url: None,
                base64: item["result"].as_str().map(|s| s.to_string()),
                revised_prompt: None,
                response_id: response_id.clone(),
                request_url: request_url.to_string(),
                attempted_urls: attempted_urls.clone(),
                actual_input_tokens: input_tokens,
                actual_output_tokens: output_tokens,
                ..Default::default()
            })
            .collect();
        if !results.is_empty() {
            return Ok(results);
        }
    }

    Err(ProviderError::Internal(
        "Could not parse image response".into(),
    ))
}

/// Helper: read a reqwest Response as JSON, then delegate to parse_image_response.
async fn parse_image_response_from_reqwest(
    resp: reqwest::Response,
    request_url: &str,
    attempted_urls: Vec<String>,
) -> Result<Vec<ImageGenResult>, ProviderError> {
    let json_resp: serde_json::Value = resp
        .json()
        .await
        .map_err(|e| ProviderError::Internal(format!("JSON parse error: {e}")))?;
    parse_image_response(&json_resp, request_url, attempted_urls)
}

/// Determine MIME type from file extension.
fn mime_from_extension(path: &str) -> String {
    let ext = std::path::Path::new(path)
        .extension()
        .and_then(|e| e.to_str())
        .unwrap_or("")
        .to_lowercase();
    match ext.as_str() {
        "png" => "image/png".to_string(),
        "jpg" | "jpeg" => "image/jpeg".to_string(),
        "webp" => "image/webp".to_string(),
        "gif" => "image/gif".to_string(),
        "bmp" => "image/bmp".to_string(),
        _ => "application/octet-stream".to_string(),
    }
}

impl ProviderMeta for OpenAiImageProvider {
    fn id(&self) -> &str {
        &self.endpoint.id
    }
    fn display_name(&self) -> &str {
        &self.endpoint.name
    }
    fn capabilities(&self) -> Vec<ProviderCapability> {
        vec![ProviderCapability::ImageGeneration]
    }
}

#[async_trait]
impl ImageGenSlot for OpenAiImageProvider {
    async fn generate(
        &self,
        request: &ImageGenRequest,
    ) -> Result<Vec<ImageGenResult>, ProviderError> {
        let has_reference = request.reference_image_path.is_some()
            || !request.uploaded_file_ids.is_empty();

        if has_reference {
            if self.should_use_responses_api(request) {
                self.edit_via_responses_api(request).await
            } else {
                let path = request.reference_image_path.as_deref().ok_or(
                    ProviderError::Internal(
                        "reference_image_path required for V1 multipart edit".into(),
                    ),
                )?;
                self.edit_via_multipart(request, path).await
            }
        } else if self.should_use_responses_api(request) {
            self.generate_via_responses(request).await
        } else {
            self.generate_via_images_api(request).await
        }
    }

    async fn upload_file(
        &self,
        file_path: &str,
        file_bytes: &[u8],
    ) -> Result<String, ProviderError> {
        self.upload_file(file_path, file_bytes).await
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use tfp_core::ApiKeyHeaderMode;

    fn azure_endpoint() -> AiEndpoint {
        AiEndpoint {
            id: "img-azure".into(),
            name: "Azure Image".into(),
            endpoint_type: EndpointType::AzureOpenAi,
            url: "https://myresource.openai.azure.com".into(),
            api_key: "key".into(),
            api_version: Some("2025-04-01-preview".into()),
            enabled: true,
            auth_header_mode: ApiKeyHeaderMode::ApiKeyHeader,
            ..AiEndpoint::default()
        }
    }

    fn apim_endpoint() -> AiEndpoint {
        AiEndpoint {
            id: "img-apim".into(),
            name: "APIM Image".into(),
            endpoint_type: EndpointType::ApiManagementGateway,
            url: "https://myapim.azure-api.net/ai".into(),
            api_key: "sub-key".into(),
            api_version: Some("2025-04-01-preview".into()),
            enabled: true,
            auth_header_mode: ApiKeyHeaderMode::ApiKeyHeader,
            ..AiEndpoint::default()
        }
    }

    fn openai_endpoint() -> AiEndpoint {
        AiEndpoint {
            id: "img-oai".into(),
            name: "OpenAI Image".into(),
            endpoint_type: EndpointType::OpenAiCompatible,
            url: "https://api.openai.com".into(),
            api_key: "sk-xxx".into(),
            enabled: true,
            auth_header_mode: ApiKeyHeaderMode::Bearer,
            ..AiEndpoint::default()
        }
    }

    fn openai_endpoint_with_v1() -> AiEndpoint {
        AiEndpoint {
            id: "img-oai-v1".into(),
            name: "OpenAI v1".into(),
            endpoint_type: EndpointType::OpenAiCompatible,
            url: "https://api.openai.com/v1".into(),
            api_key: "sk-xxx".into(),
            enabled: true,
            auth_header_mode: ApiKeyHeaderMode::Bearer,
            ..AiEndpoint::default()
        }
    }

    fn sample_request() -> ImageGenRequest {
        ImageGenRequest {
            prompt: "A cat sitting on a windowsill".into(),
            width: 1024,
            height: 1024,
            model: "gpt-image-1".into(),
            quality: Some("auto".into()),
            output_format: Some("png".into()),
            background: None,
            n: None,
            endpoint_id: "img-azure".into(),
            text_model: None,
            image_model: None,
            previous_response_id: None,
            reference_image_path: None,
            image_edit_mode: None,
            uploaded_file_ids: vec![],
        }
    }

    // ── T-002: should_use_responses_api tests ──

    #[test]
    fn test_should_use_responses_api_v1_multipart_returns_false() {
        let p = OpenAiImageProvider::new(azure_endpoint());
        let mut req = sample_request();
        req.image_edit_mode = Some(ImageEditMode::V1Multipart);
        req.text_model = Some("gpt-4o".into());
        assert!(!p.should_use_responses_api(&req));
    }

    #[test]
    fn test_should_use_responses_api_text_model_set_returns_true() {
        let p = OpenAiImageProvider::new(azure_endpoint());
        let mut req = sample_request();
        req.text_model = Some("gpt-4o".into());
        assert!(p.should_use_responses_api(&req));
    }

    #[test]
    fn test_should_use_responses_api_default_returns_false() {
        let p = OpenAiImageProvider::new(azure_endpoint());
        let req = sample_request();
        assert!(!p.should_use_responses_api(&req));
    }

    // ── T-003: URL building tests ──

    // Generate URLs
    #[test]
    fn test_build_generate_urls_azure() {
        let p = OpenAiImageProvider::new(azure_endpoint());
        let urls = p.build_generate_urls();
        assert_eq!(urls.len(), 1);
        assert_eq!(
            urls[0],
            "https://myresource.openai.azure.com/openai/v1/images/generations"
        );
    }

    #[test]
    fn test_build_generate_urls_apim() {
        let p = OpenAiImageProvider::new(apim_endpoint());
        let urls = p.build_generate_urls();
        assert_eq!(urls.len(), 3);
        assert_eq!(
            urls[0],
            "https://myapim.azure-api.net/ai/v1/images/generations"
        );
        assert!(urls[1].contains("api-version=2025-04-01-preview"));
        assert_eq!(
            urls[2],
            "https://myapim.azure-api.net/ai/images/generations"
        );
    }

    #[test]
    fn test_build_generate_urls_openai() {
        let p = OpenAiImageProvider::new(openai_endpoint());
        let urls = p.build_generate_urls();
        assert_eq!(urls.len(), 1);
        assert_eq!(urls[0], "https://api.openai.com/v1/images/generations");
    }

    // Edit URLs
    #[test]
    fn test_build_edit_urls_azure() {
        let p = OpenAiImageProvider::new(azure_endpoint());
        let urls = p.build_edit_urls();
        assert_eq!(urls.len(), 1);
        assert_eq!(
            urls[0],
            "https://myresource.openai.azure.com/openai/v1/images/edits"
        );
    }

    #[test]
    fn test_build_edit_urls_apim() {
        let p = OpenAiImageProvider::new(apim_endpoint());
        let urls = p.build_edit_urls();
        assert_eq!(urls.len(), 3);
        assert_eq!(urls[0], "https://myapim.azure-api.net/ai/v1/images/edits");
        assert!(urls[1].contains("api-version=2025-04-01-preview"));
        assert_eq!(urls[2], "https://myapim.azure-api.net/ai/images/edits");
    }

    #[test]
    fn test_build_edit_urls_openai() {
        let p = OpenAiImageProvider::new(openai_endpoint());
        let urls = p.build_edit_urls();
        assert_eq!(urls.len(), 1);
        assert_eq!(urls[0], "https://api.openai.com/v1/images/edits");
    }

    // Responses URLs
    #[test]
    fn test_build_responses_urls_azure() {
        let p = OpenAiImageProvider::new(azure_endpoint());
        let urls = p.build_responses_urls();
        assert_eq!(urls.len(), 1);
        assert_eq!(
            urls[0],
            "https://myresource.openai.azure.com/openai/v1/responses"
        );
    }

    #[test]
    fn test_build_responses_urls_apim() {
        let p = OpenAiImageProvider::new(apim_endpoint());
        let urls = p.build_responses_urls();
        assert_eq!(urls.len(), 3);
        assert_eq!(urls[0], "https://myapim.azure-api.net/ai/v1/responses");
        // Uses the endpoint's configured api_version for Responses API too
        assert!(urls[1].contains("api-version=2025-04-01-preview"));
        assert_eq!(urls[2], "https://myapim.azure-api.net/ai/responses");
    }

    #[test]
    fn test_build_responses_urls_openai() {
        let p = OpenAiImageProvider::new(openai_endpoint());
        let urls = p.build_responses_urls();
        assert_eq!(urls.len(), 1);
        assert_eq!(urls[0], "https://api.openai.com/v1/responses");
    }

    #[test]
    fn test_build_responses_urls_apim_default_version() {
        let mut ep = apim_endpoint();
        ep.api_version = None;
        let p = OpenAiImageProvider::new(ep);
        let urls = p.build_responses_urls();
        assert!(urls[1].contains("api-version=2025-03-01-preview"));
    }

    // URL with trailing /v1 in base
    #[test]
    fn test_build_urls_with_v1_base() {
        let p = OpenAiImageProvider::new(openai_endpoint_with_v1());
        assert_eq!(
            p.build_generate_urls(),
            vec!["https://api.openai.com/v1/images/generations"]
        );
        assert_eq!(
            p.build_edit_urls(),
            vec!["https://api.openai.com/v1/images/edits"]
        );
        assert_eq!(
            p.build_responses_urls(),
            vec!["https://api.openai.com/v1/responses"]
        );
    }

    // Legacy alias
    #[test]
    fn test_build_url_candidates_alias() {
        let p = OpenAiImageProvider::new(azure_endpoint());
        assert_eq!(p.build_url_candidates(), p.build_generate_urls());
    }

    // ── T-007: parse_image_response tests ──

    #[test]
    fn test_parse_image_response_b64_json() {
        let body = json!({
            "data": [
                {
                    "b64_json": "aW1hZ2VfZGF0YQ==",
                    "revised_prompt": "A happy cat on a windowsill"
                }
            ]
        });
        let results =
            parse_image_response(&body, "https://example.com/api", vec!["https://example.com/api".into()])
                .unwrap();
        assert_eq!(results.len(), 1);
        assert_eq!(results[0].base64.as_deref(), Some("aW1hZ2VfZGF0YQ=="));
        assert_eq!(
            results[0].revised_prompt.as_deref(),
            Some("A happy cat on a windowsill")
        );
        assert_eq!(results[0].request_url, "https://example.com/api");
        assert_eq!(results[0].attempted_urls.len(), 1);
    }

    #[test]
    fn test_parse_image_response_url_format() {
        let body = json!({
            "data": [
                {
                    "url": "https://cdn.example.com/img.png",
                    "revised_prompt": null
                }
            ]
        });
        let results = parse_image_response(
            &body,
            "https://api.openai.com/v1/images/generations",
            vec!["https://api.openai.com/v1/images/generations".into()],
        )
        .unwrap();
        assert_eq!(results.len(), 1);
        assert_eq!(
            results[0].url.as_deref(),
            Some("https://cdn.example.com/img.png")
        );
        assert!(results[0].base64.is_none());
    }

    #[test]
    fn test_parse_image_response_responses_api_format() {
        let body = json!({
            "id": "resp_abc123",
            "output": [
                {
                    "type": "image_generation_call",
                    "result": "cGljdHVyZV9kYXRh"
                }
            ],
            "usage": {
                "input_tokens": 50,
                "output_tokens": 4096
            }
        });
        let results = parse_image_response(
            &body,
            "https://api.example.com/v1/responses",
            vec![
                "https://api.example.com/v1/responses".into(),
                "https://api.example.com/responses".into(),
            ],
        )
        .unwrap();
        assert_eq!(results.len(), 1);
        assert_eq!(results[0].base64.as_deref(), Some("cGljdHVyZV9kYXRh"));
        assert_eq!(results[0].response_id.as_deref(), Some("resp_abc123"));
        assert_eq!(results[0].actual_input_tokens, Some(50));
        assert_eq!(results[0].actual_output_tokens, Some(4096));
    }

    #[test]
    fn test_parse_image_response_with_usage() {
        let body = json!({
            "data": [
                { "b64_json": "AAAA" }
            ],
            "usage": {
                "input_tokens": 100,
                "output_tokens": 2048
            }
        });
        let results = parse_image_response(&body, "https://x.com/api", vec![]).unwrap();
        assert_eq!(results[0].actual_input_tokens, Some(100));
        assert_eq!(results[0].actual_output_tokens, Some(2048));
    }

    #[test]
    fn test_parse_image_response_empty_data_returns_error() {
        let body = json!({ "data": [] });
        let result = parse_image_response(&body, "https://x.com/api", vec![]);
        assert!(result.is_err());
    }

    #[test]
    fn test_parse_image_response_no_recognized_format_returns_error() {
        let body = json!({ "foo": "bar" });
        let result = parse_image_response(&body, "https://x.com/api", vec![]);
        assert!(result.is_err());
    }

    // ── T-006: MIME detection ──

    #[test]
    fn test_mime_from_extension() {
        assert_eq!(mime_from_extension("photo.png"), "image/png");
        assert_eq!(mime_from_extension("photo.jpg"), "image/jpeg");
        assert_eq!(mime_from_extension("photo.jpeg"), "image/jpeg");
        assert_eq!(mime_from_extension("photo.webp"), "image/webp");
        assert_eq!(mime_from_extension("photo.gif"), "image/gif");
        assert_eq!(mime_from_extension("photo.bmp"), "image/bmp");
        assert_eq!(mime_from_extension("photo.tiff"), "application/octet-stream");
        assert_eq!(mime_from_extension("noext"), "application/octet-stream");
    }

    // ── Provider meta ──

    #[test]
    fn test_provider_meta() {
        let p = OpenAiImageProvider::new(azure_endpoint());
        assert_eq!(p.id(), "img-azure");
        assert_eq!(p.capabilities(), vec![ProviderCapability::ImageGeneration]);
    }

    // ── ImageGenResult Default ──

    #[test]
    fn test_image_gen_result_default() {
        let r = ImageGenResult::default();
        assert!(r.url.is_none());
        assert!(r.base64.is_none());
        assert!(r.response_id.is_none());
        assert_eq!(r.request_url, "");
        assert!(r.attempted_urls.is_empty());
        assert_eq!(r.generate_seconds, 0.0);
        assert_eq!(r.download_seconds, 0.0);
        assert!(r.actual_input_tokens.is_none());
        assert!(r.actual_output_tokens.is_none());
    }

    #[test]
    fn test_image_gen_result_serde_roundtrip() {
        let r = ImageGenResult {
            url: None,
            base64: Some("YWJj".into()),
            revised_prompt: Some("test prompt".into()),
            response_id: Some("resp_123".into()),
            request_url: "https://api.example.com".into(),
            attempted_urls: vec!["https://api.example.com".into()],
            generate_seconds: 3.5,
            download_seconds: 0.2,
            actual_input_tokens: Some(100),
            actual_output_tokens: Some(4096),
        };
        let json = serde_json::to_string(&r).unwrap();
        let restored: ImageGenResult = serde_json::from_str(&json).unwrap();
        assert_eq!(restored.response_id.as_deref(), Some("resp_123"));
        assert_eq!(restored.generate_seconds, 3.5);
        assert_eq!(restored.actual_output_tokens, Some(4096));
    }

    // ── batch-2 T-002: build_file_upload_urls tests ──

    #[test]
    fn test_build_file_upload_urls_azure() {
        let p = OpenAiImageProvider::new(azure_endpoint());
        let urls = p.build_file_upload_urls();
        assert_eq!(urls.len(), 1);
        assert_eq!(
            urls[0],
            "https://myresource.openai.azure.com/openai/v1/files"
        );
    }

    #[test]
    fn test_build_file_upload_urls_apim() {
        let p = OpenAiImageProvider::new(apim_endpoint());
        let urls = p.build_file_upload_urls();
        assert_eq!(urls.len(), 2);
        assert_eq!(urls[0], "https://myapim.azure-api.net/ai/v1/files");
        assert_eq!(urls[1], "https://myapim.azure-api.net/ai/openai/v1/files");
    }

    #[test]
    fn test_build_file_upload_urls_openai() {
        let p = OpenAiImageProvider::new(openai_endpoint());
        let urls = p.build_file_upload_urls();
        assert_eq!(urls.len(), 1);
        assert_eq!(urls[0], "https://api.openai.com/v1/files");
    }

    #[test]
    fn test_build_file_upload_urls_openai_with_v1_base() {
        let p = OpenAiImageProvider::new(openai_endpoint_with_v1());
        let urls = p.build_file_upload_urls();
        assert_eq!(urls.len(), 1);
        assert_eq!(urls[0], "https://api.openai.com/v1/files");
    }

    // ── batch-2 T-004: edit_via_responses_api body construction ──

    #[test]
    fn test_edit_via_responses_api_body_construction() {
        // Verify the JSON body that edit_via_responses_api would build
        let request = ImageGenRequest {
            prompt: "Make the cat wear a hat".into(),
            width: 1024,
            height: 1024,
            model: "gpt-image-1".into(),
            quality: Some("auto".into()),
            output_format: Some("png".into()),
            background: None,
            n: None,
            endpoint_id: "img-azure".into(),
            text_model: Some("gpt-4o".into()),
            image_model: Some("gpt-image-1".into()),
            previous_response_id: Some("resp_prev_123".into()),
            reference_image_path: None,
            image_edit_mode: None,
            uploaded_file_ids: vec!["file-abc123".into(), "file-def456".into()],
        };

        // Simulate the body construction logic from edit_via_responses_api
        let text_model = request.text_model.as_deref().unwrap_or(&request.model);
        let image_model = request.image_model.as_deref().unwrap_or(&request.model);

        let mut input_items: Vec<serde_json::Value> = Vec::new();
        input_items.push(json!({
            "type": "input_text",
            "text": &request.prompt,
        }));
        for fid in &request.uploaded_file_ids {
            input_items.push(json!({
                "type": "input_image",
                "file_id": fid,
            }));
        }

        let mut body = json!({
            "model": text_model,
            "input": input_items,
            "tools": [{ "type": "image_generation" }],
        });
        if let Some(ref prev_id) = request.previous_response_id {
            body["previous_response_id"] = json!(prev_id);
        }

        assert_eq!(body["model"], "gpt-4o");
        assert_eq!(body["input"][0]["type"], "input_text");
        assert_eq!(body["input"][0]["text"], "Make the cat wear a hat");
        assert_eq!(body["input"][1]["type"], "input_image");
        assert_eq!(body["input"][1]["file_id"], "file-abc123");
        assert_eq!(body["input"][2]["type"], "input_image");
        assert_eq!(body["input"][2]["file_id"], "file-def456");
        assert_eq!(body["tools"][0]["type"], "image_generation");
        assert_eq!(body["previous_response_id"], "resp_prev_123");
        // image_model is used as header, not in body
        assert_eq!(image_model, "gpt-image-1");
    }

    // ── batch-2 T-005: generate() routing tests ──

    #[test]
    fn test_generate_routing_no_ref_no_responses() {
        // No reference + no text_model → generate_via_images_api
        let p = OpenAiImageProvider::new(azure_endpoint());
        let req = sample_request();
        assert!(!req.reference_image_path.is_some());
        assert!(req.uploaded_file_ids.is_empty());
        assert!(!p.should_use_responses_api(&req));
        // Route: generate_via_images_api
    }

    #[test]
    fn test_generate_routing_no_ref_with_responses() {
        // No reference + text_model → generate_via_responses
        let p = OpenAiImageProvider::new(azure_endpoint());
        let mut req = sample_request();
        req.text_model = Some("gpt-4o".into());
        assert!(!req.reference_image_path.is_some());
        assert!(req.uploaded_file_ids.is_empty());
        assert!(p.should_use_responses_api(&req));
        // Route: generate_via_responses
    }

    #[test]
    fn test_generate_routing_with_ref_path_no_responses() {
        // Has reference_image_path + no text_model → edit_via_multipart
        let p = OpenAiImageProvider::new(azure_endpoint());
        let mut req = sample_request();
        req.reference_image_path = Some("/tmp/photo.png".into());
        let has_ref = req.reference_image_path.is_some() || !req.uploaded_file_ids.is_empty();
        assert!(has_ref);
        assert!(!p.should_use_responses_api(&req));
        // Route: edit_via_multipart
    }

    #[test]
    fn test_generate_routing_with_file_ids_with_responses() {
        // Has uploaded_file_ids + text_model → edit_via_responses_api
        let p = OpenAiImageProvider::new(azure_endpoint());
        let mut req = sample_request();
        req.uploaded_file_ids = vec!["file-abc".into()];
        req.text_model = Some("gpt-4o".into());
        let has_ref = req.reference_image_path.is_some() || !req.uploaded_file_ids.is_empty();
        assert!(has_ref);
        assert!(p.should_use_responses_api(&req));
        // Route: edit_via_responses_api
    }

    #[test]
    fn test_generate_routing_with_file_ids_v1_multipart_forced() {
        // Has uploaded_file_ids + V1Multipart forced → edit_via_multipart (needs path)
        let p = OpenAiImageProvider::new(azure_endpoint());
        let mut req = sample_request();
        req.uploaded_file_ids = vec!["file-abc".into()];
        req.image_edit_mode = Some(ImageEditMode::V1Multipart);
        req.text_model = Some("gpt-4o".into()); // would enable responses, but V1Multipart overrides
        let has_ref = req.reference_image_path.is_some() || !req.uploaded_file_ids.is_empty();
        assert!(has_ref);
        assert!(!p.should_use_responses_api(&req)); // V1Multipart overrides
    }

    // ── batch-2 T-001: uploaded_file_ids serde ──

    #[test]
    fn test_image_gen_request_missing_uploaded_file_ids_defaults_empty() {
        let json = r#"{
            "prompt": "hello",
            "width": 512,
            "height": 512,
            "model": "dall-e-3",
            "endpoint_id": "ep1"
        }"#;
        let req: ImageGenRequest = serde_json::from_str(json).unwrap();
        assert!(req.uploaded_file_ids.is_empty());
    }

    #[test]
    fn test_image_gen_request_with_uploaded_file_ids() {
        let json = r#"{
            "prompt": "edit my photo",
            "width": 1024,
            "height": 1024,
            "model": "gpt-image-1",
            "endpoint_id": "ep1",
            "uploaded_file_ids": ["file-123", "file-456"]
        }"#;
        let req: ImageGenRequest = serde_json::from_str(json).unwrap();
        assert_eq!(req.uploaded_file_ids, vec!["file-123", "file-456"]);
    }
}