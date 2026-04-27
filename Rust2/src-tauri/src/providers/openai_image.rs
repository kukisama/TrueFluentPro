use async_trait::async_trait;
use reqwest::Client;
use serde_json::json;

use crate::models::*;
use super::registry::*;

/// OpenAI 兼容的图片生成 Provider
///
/// 对齐 C# AiImageGenService.SendImageGenerateRequestAsync
/// 路径 A: `/images/generations` (JSON, 无参考图)
pub struct OpenAiImageProvider {
    client: Client,
    endpoint: AiEndpoint,
}

impl OpenAiImageProvider {
    pub fn new(endpoint: AiEndpoint) -> Self {
        Self {
            client: Client::builder()
                .timeout(std::time::Duration::from_secs(300))
                .build()
                .unwrap_or_default(),
            endpoint,
        }
    }

    /// 构建 URL 候选列表 —— 对齐 C# EndpointProfileUrlBuilder.BuildImageGenerateUrlCandidates
    fn build_url_candidates(&self) -> Vec<String> {
        let base = self.endpoint.url.trim_end_matches('/');

        match self.endpoint.endpoint_type {
            // C# azure-openai.json: generatePrimaryUrl = "{baseUrl}/openai/v1/images/generations"
            // deploymentGeneratePrimaryUrl = "" (空，不用 deployment URL)
            EndpointType::AzureOpenAi => {
                vec![
                    format!("{base}/openai/v1/images/generations"),
                ]
            }
            // C# apim-gateway.json: generatePrimaryUrl = "{baseUrl}/v1/images/generations"
            // fallbacks: /images/generations?api-version=..., /v1/images/generations, /images/generations
            EndpointType::ApiManagementGateway => {
                let api_ver = self.endpoint.api_version.as_deref().unwrap_or("2025-04-01-preview");
                vec![
                    format!("{base}/v1/images/generations"),
                    format!("{base}/images/generations?api-version={api_ver}"),
                    format!("{base}/images/generations"),
                ]
            }
            // C# openai-compatible.json: generatePrimaryUrl = "{baseUrl}/v1/images/generations"
            _ => {
                if base.ends_with("/v1") {
                    vec![format!("{base}/images/generations")]
                } else {
                    vec![format!("{base}/v1/images/generations")]
                }
            }
        }
    }

    /// 认证 —— 对齐 C# AiMediaServiceBase.ApplyApiKeyHeader
    /// 解析链: endpoint 显式值 → profile 默认值 → 平台默认值
    fn apply_auth(&self, req: reqwest::RequestBuilder) -> reqwest::RequestBuilder {
        let mode = self.endpoint.auth_header_mode.as_str();
        match mode {
            "bearer" => req.header("Authorization", format!("Bearer {}", self.endpoint.api_key)),
            "api_key" | "api_key_header" => req.header("api-key", &self.endpoint.api_key),
            _ => {
                // auto: 所有 Azure 系（含 APIM）用 api-key，对齐 C# defaults.apiKeyHeaderMode = "ApiKeyHeader"
                // 非 Azure 用 Bearer
                if self.endpoint.is_azure() {
                    req.header("api-key", &self.endpoint.api_key)
                } else {
                    req.header("Authorization", format!("Bearer {}", self.endpoint.api_key))
                }
            }
        }
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
    /// 图片生成：优先尝试 Responses API V2（APIM + text_model + image_model），
    /// fallback 到 Images API 候选 URL 遍历。
    async fn generate(
        &self,
        request: &ImageGenRequest,
    ) -> Result<Vec<ImageGenResult>, ProviderError> {
        // P3-3: Responses API V2 路径（APIM 端点 + 有 text_model/image_model 时启用）
        if self.endpoint.endpoint_type == EndpointType::ApiManagementGateway {
            if let Some(ref text_model) = request.text_model {
                if let Some(ref image_model) = request.image_model {
                    return self.generate_via_responses_v2(request, text_model, image_model).await;
                }
            }
        }

        // Images API 常规路径
        self.generate_via_images_api(request).await
    }
}

impl OpenAiImageProvider {
    /// P3-3: Responses API V2 — 使用 x-ms-oai-image-generation-deployment 头
    /// 对齐 C# AiImageGenService.SendImageEditV2RequestAsync
    async fn generate_via_responses_v2(
        &self,
        request: &ImageGenRequest,
        text_model: &str,
        image_model: &str,
    ) -> Result<Vec<ImageGenResult>, ProviderError> {
        let base = self.endpoint.url.trim_end_matches('/');
        let api_ver = self.endpoint.api_version.as_deref().unwrap_or("2025-03-01-preview");
        let url = format!("{base}/responses?api-version={api_ver}");

        let mut body = json!({
            "model": text_model,
            "input": &request.prompt,
            "tools": [{ "type": "image_generation" }],
        });

        if let Some(ref prev_id) = request.previous_response_id {
            body["previous_response_id"] = json!(prev_id);
        }

        tracing::info!(
            "Responses API V2 图片生成: text_model={text_model}, image_model={image_model}, url={url}"
        );

        let resp = self
            .apply_auth(self.client.post(&url))
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
                return Err(ProviderError::RateLimited { retry_after_ms: 10000 });
            }
            return Err(ProviderError::Network(format!("{status}: {text}")));
        }

        let json_resp: serde_json::Value = resp
            .json()
            .await
            .map_err(|e| ProviderError::Internal(format!("JSON 解析失败: {e}")))?;

        // 提取 response_id 用于多轮改图
        let response_id = json_resp["id"].as_str().map(|s| s.to_string());

        // Responses API V2 响应: output[] 中 type=image_generation_call 的 result
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
                tracing::info!("Responses API V2 图片生成成功: {} 张", results.len());
                return Ok(results);
            }
        }

        Err(ProviderError::Internal(format!(
            "Responses API V2 响应解析失败: {}",
            serde_json::to_string_pretty(&json_resp).unwrap_or_default()
        )))
    }

    /// Images API 常规路径（候选 URL 遍历）
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

        tracing::info!("图片生成请求: model={}, size={size}, format={output_format}", request.model);

        let mut last_error = String::new();

        for (idx, url) in candidates.iter().enumerate() {
            tracing::info!("图片生成候选 {}/{}: POST {url}", idx + 1, candidates.len());

            let resp = self
                .apply_auth(self.client.post(url))
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
                        return Err(ProviderError::RateLimited { retry_after_ms: 10000 });
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
            "所有 {} 条候选 URL 均失败: {last_error}",
            candidates.len()
        )))
    }
    /// 解析响应 —— 对齐 C# 两种格式
    async fn parse_response(
        &self,
        resp: reqwest::Response,
    ) -> Result<Vec<ImageGenResult>, ProviderError> {
        let json_resp: serde_json::Value = resp
            .json()
            .await
            .map_err(|e| ProviderError::Internal(format!("JSON 解析失败: {e}")))?;

        // 格式 1: Images API → data[].b64_json 或 data[].url
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
                return Err(ProviderError::Internal("API 返回空 data 数组".into()));
            }

            tracing::info!("图片生成成功 (Images API): {} 张", results.len());
            return Ok(results);
        }

        // 格式 2: Responses API → output[] 中 type=image_generation_call 的 result
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
                tracing::info!("图片生成成功 (Responses API): {} 张", results.len());
                return Ok(results);
            }
        }

        Err(ProviderError::Internal(format!(
            "无法解析图片响应: {}",
            serde_json::to_string_pretty(&json_resp).unwrap_or_default()
        )))
    }
}
