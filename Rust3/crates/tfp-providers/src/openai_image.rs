use async_trait::async_trait;
use serde_json::json;

use tfp_core::{AiEndpoint, EndpointType, ImageGenRequest, ImageGenResult, ProviderError};

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

    pub(crate) fn build_url_candidates(&self) -> Vec<String> {
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

    async fn generate_via_responses_v2(
        &self,
        request: &ImageGenRequest,
        text_model: &str,
        image_model: &str,
    ) -> Result<Vec<ImageGenResult>, ProviderError> {
        let base = self.endpoint.url.trim_end_matches('/');
        let api_ver = self
            .endpoint
            .api_version
            .as_deref()
            .unwrap_or("2025-03-01-preview");
        let url = format!("{base}/responses?api-version={api_ver}");

        let mut body = json!({
            "model": text_model,
            "input": &request.prompt,
            "tools": [{ "type": "image_generation" }],
        });

        if let Some(ref prev_id) = request.previous_response_id {
            body["previous_response_id"] = json!(prev_id);
        }

        let resp = apply_auth(&self.endpoint, self.client.post(&url))
            .header("x-ms-oai-image-generation-deployment", image_model)
            .json(&body)
            .send()
            .await
            .map_err(|e| ProviderError::Network(e.to_string()))?;

        let status = resp.status();
        if !status.is_success() {
            let text = resp.text().await.unwrap_or_default();
            if status.as_u16() == 401 || status.as_u16() == 403 {
                return Err(ProviderError::Auth(format!("{status}: {text}")));
            }
            if status.as_u16() == 429 {
                return Err(ProviderError::RateLimited {
                    retry_after_ms: 10000,
                });
            }
            return Err(ProviderError::Network(format!("{status}: {text}")));
        }

        let json_resp: serde_json::Value = resp
            .json()
            .await
            .map_err(|e| ProviderError::Internal(format!("JSON parse error: {e}")))?;

        let response_id = json_resp["id"].as_str().map(|s| s.to_string());

        if let Some(output) = json_resp["output"].as_array() {
            let results: Vec<ImageGenResult> = output
                .iter()
                .filter(|item| item["type"].as_str() == Some("image_generation_call"))
                .map(|item| ImageGenResult {
                    url: None,
                    base64: item["result"].as_str().map(|s| s.to_string()),
                    revised_prompt: None,
                    response_id: response_id.clone(),
                })
                .collect();
            if !results.is_empty() {
                return Ok(results);
            }
        }

        Err(ProviderError::Internal(
            "Responses API V2 returned no image results".into(),
        ))
    }

    async fn generate_via_images_api(
        &self,
        request: &ImageGenRequest,
    ) -> Result<Vec<ImageGenResult>, ProviderError> {
        let candidates = self.build_url_candidates();
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

        let mut last_error = String::new();

        for url in &candidates {
            let resp = apply_auth(&self.endpoint, self.client.post(url))
                .json(&body)
                .send()
                .await;

            match resp {
                Ok(response) => {
                    let status = response.status();
                    if status.is_success() {
                        return self.parse_response(response).await;
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
            candidates.len()
        )))
    }

    async fn parse_response(
        &self,
        resp: reqwest::Response,
    ) -> Result<Vec<ImageGenResult>, ProviderError> {
        let json_resp: serde_json::Value = resp
            .json()
            .await
            .map_err(|e| ProviderError::Internal(format!("JSON parse error: {e}")))?;

        // Format 1: Images API → data[].b64_json or data[].url
        if let Some(data) = json_resp["data"].as_array() {
            let results: Vec<ImageGenResult> = data
                .iter()
                .map(|item| ImageGenResult {
                    url: item["url"].as_str().map(|s| s.to_string()),
                    base64: item["b64_json"].as_str().map(|s| s.to_string()),
                    revised_prompt: item["revised_prompt"].as_str().map(|s| s.to_string()),
                    response_id: None,
                })
                .collect();
            if results.is_empty() {
                return Err(ProviderError::Internal("API returned empty data array".into()));
            }
            return Ok(results);
        }

        // Format 2: Responses API → output[] with image_generation_call
        if let Some(output) = json_resp["output"].as_array() {
            let response_id = json_resp["id"].as_str().map(|s| s.to_string());
            let results: Vec<ImageGenResult> = output
                .iter()
                .filter(|item| item["type"].as_str() == Some("image_generation_call"))
                .map(|item| ImageGenResult {
                    url: None,
                    base64: item["result"].as_str().map(|s| s.to_string()),
                    revised_prompt: None,
                    response_id: response_id.clone(),
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
        if self.endpoint.endpoint_type == EndpointType::ApiManagementGateway {
            if let Some(ref text_model) = request.text_model {
                if let Some(ref image_model) = request.image_model {
                    return self
                        .generate_via_responses_v2(request, text_model, image_model)
                        .await;
                }
            }
        }
        self.generate_via_images_api(request).await
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn azure_endpoint() -> AiEndpoint {
        AiEndpoint {
            id: "img-azure".into(),
            name: "Azure Image".into(),
            endpoint_type: EndpointType::AzureOpenAi,
            url: "https://myresource.openai.azure.com".into(),
            api_key: "key".into(),
            api_version: Some("2025-04-01-preview".into()),
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

    fn openai_endpoint() -> AiEndpoint {
        AiEndpoint {
            id: "img-oai".into(),
            name: "OpenAI Image".into(),
            endpoint_type: EndpointType::OpenAiCompatible,
            url: "https://api.openai.com".into(),
            api_key: "sk-xxx".into(),
            api_version: None,
            region: None,
            models: vec![],
            enabled: true,
            auth_header_mode: "bearer".into(),
            auth_mode: "api_key".into(),
            azure_tenant_id: String::new(),
            azure_client_id: String::new(),
            speech_subscription_key: String::new(),
            speech_region: String::new(),
            speech_endpoint: String::new(),
        }
    }

    #[test]
    fn test_build_url_candidates_azure() {
        let p = OpenAiImageProvider::new(azure_endpoint());
        let urls = p.build_url_candidates();
        assert_eq!(urls.len(), 1);
        assert_eq!(
            urls[0],
            "https://myresource.openai.azure.com/openai/v1/images/generations"
        );
    }

    #[test]
    fn test_build_url_candidates_openai() {
        let p = OpenAiImageProvider::new(openai_endpoint());
        let urls = p.build_url_candidates();
        assert_eq!(urls.len(), 1);
        assert_eq!(urls[0], "https://api.openai.com/v1/images/generations");
    }

    #[test]
    fn test_build_url_candidates_apim() {
        let mut ep = azure_endpoint();
        ep.endpoint_type = EndpointType::ApiManagementGateway;
        let p = OpenAiImageProvider::new(ep);
        let urls = p.build_url_candidates();
        assert_eq!(urls.len(), 3);
        assert!(urls[0].contains("/v1/images/generations"));
    }

    #[test]
    fn test_provider_meta() {
        let p = OpenAiImageProvider::new(azure_endpoint());
        assert_eq!(p.id(), "img-azure");
        assert_eq!(p.capabilities(), vec![ProviderCapability::ImageGeneration]);
    }
}
