use async_trait::async_trait;
use reqwest::Client;
use serde_json::json;

use crate::models::*;
use super::registry::*;

/// OpenAI 兼容的图片生成 Provider
///
/// 支持: Azure OpenAI (gpt-image-2, dall-e-3)、OpenAI API
/// 接口: POST /v1/images/generations
/// 返回: base64 编码的图片数据
pub struct OpenAiImageProvider {
    client: Client,
    endpoint: AiEndpoint,
}

impl OpenAiImageProvider {
    pub fn new(endpoint: AiEndpoint) -> Self {
        Self {
            client: Client::builder()
                .timeout(std::time::Duration::from_secs(120))
                .build()
                .unwrap_or_default(),
            endpoint,
        }
    }

    fn build_url(&self) -> String {
        let base = self.endpoint.url.trim_end_matches('/');

        match self.endpoint.endpoint_type {
            EndpointType::AzureOpenAi => {
                let api_ver = self.endpoint.api_version.as_deref().unwrap_or("2025-03-01-preview");
                if let Some(model) = self.endpoint.first_model_with_capability(ModelCapability::Image) {
                    let deploy = model.effective_deployment();
                    format!("{base}/openai/deployments/{deploy}/images/generations?api-version={api_ver}")
                } else {
                    format!("{base}/openai/v1/images/generations")
                }
            }
            _ => {
                if base.ends_with("/v1") {
                    format!("{base}/images/generations")
                } else {
                    format!("{base}/v1/images/generations")
                }
            }
        }
    }

    fn apply_auth(&self, req: reqwest::RequestBuilder) -> reqwest::RequestBuilder {
        if self.endpoint.is_azure() {
            req.header("api-key", &self.endpoint.api_key)
        } else {
            req.header("Authorization", format!("Bearer {}", self.endpoint.api_key))
        }
    }

    fn format_size(width: u32, height: u32) -> String {
        format!("{width}x{height}")
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
        let url = self.build_url();
        let size = Self::format_size(request.width, request.height);

        let mut body = json!({
            "prompt": &request.prompt,
            "model": &request.model,
            "size": size,
            "n": 1,
            "response_format": "b64_json",
        });

        if let Some(ref quality) = request.quality {
            body["quality"] = json!(quality);
        }
        if let Some(ref style) = request.style {
            body["style"] = json!(style);
        }

        // gpt-image-2 支持 output_format
        if request.model.contains("gpt-image") {
            body["output_format"] = json!("png");
        }

        tracing::info!("图片生成请求: model={}, size={size}", request.model);

        let resp = self
            .apply_auth(self.client.post(&url))
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
                return Err(ProviderError::RateLimited { retry_after_ms: 10000 });
            }
            return Err(ProviderError::Network(format!("{status}: {text}")));
        }

        let json_resp: serde_json::Value = resp
            .json()
            .await
            .map_err(|e| ProviderError::Internal(format!("JSON 解析失败: {e}")))?;

        // 解析 images/generations 响应
        let data = json_resp["data"]
            .as_array()
            .ok_or_else(|| ProviderError::Internal("响应缺少 data 字段".into()))?;

        let results: Vec<ImageGenResult> = data
            .iter()
            .map(|item| {
                ImageGenResult {
                    url: item["url"].as_str().map(|s| s.to_string()),
                    base64: item["b64_json"].as_str().map(|s| s.to_string()),
                    revised_prompt: item["revised_prompt"].as_str().map(|s| s.to_string()),
                }
            })
            .collect();

        if results.is_empty() {
            return Err(ProviderError::Internal("API 返回空结果".into()));
        }

        tracing::info!("图片生成成功: {} 张", results.len());
        Ok(results)
    }
}
