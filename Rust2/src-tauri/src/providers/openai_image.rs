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
    /// 对齐 C# AiImageGenService.SendImageGenerateRequestAsync (路径 A)
    ///
    /// Body 字段:
    ///   prompt, model, size, quality, output_format
    ///   background（仅非 "auto" 时）
    ///   n（仅 > 1 时）
    async fn generate(
        &self,
        request: &ImageGenRequest,
    ) -> Result<Vec<ImageGenResult>, ProviderError> {
        let candidates = self.build_url_candidates();
        let size = format!("{}x{}", request.width, request.height);
        let output_format = request.output_format.as_deref().unwrap_or("png");

        // ── 构建 body（对齐 C# SendImageGenerateRequestAsync） ──
        let mut body = json!({
            "prompt": &request.prompt,
            "model": &request.model,
            "size": size,
            "quality": request.quality.as_deref().unwrap_or("auto"),
            "output_format": output_format,
        });

        // background: 仅非 "auto" 时发送（对齐 C#）
        if let Some(ref bg) = request.background {
            if bg != "auto" && !bg.is_empty() {
                body["background"] = json!(bg);
            }
        }

        // n: 仅 > 1 时发送（对齐 C#）
        if let Some(n) = request.n {
            if n > 1 {
                body["n"] = json!(n);
            }
        }

        tracing::info!("图片生成请求: model={}, size={size}, format={output_format}", request.model);

        // ── URL 候选遍历（对齐 C# foreach candidateUrls + ShouldTryNextImageCandidate） ──
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

                    // 401/403 → 认证错误，不再尝试其他候选
                    if status.as_u16() == 401 || status.as_u16() == 403 {
                        return Err(ProviderError::Auth(format!("{status}: {text}")));
                    }
                    // 429 → 限流
                    if status.as_u16() == 429 {
                        return Err(ProviderError::RateLimited { retry_after_ms: 10000 });
                    }
                    // 404/405 → 尝试下一个候选 URL（对齐 C# ShouldTryNextImageCandidate）
                    if status.as_u16() == 404 || status.as_u16() == 405 {
                        last_error = format!("{status}: {text}");
                        continue;
                    }
                    // 其他错误 → 返回
                    return Err(ProviderError::Network(format!("{status}: {text}")));
                }
                Err(e) => {
                    last_error = e.to_string();
                    // 网络错误也尝试下一个候选
                    continue;
                }
            }
        }

        Err(ProviderError::Network(format!(
            "所有 {} 条候选 URL 均失败: {last_error}",
            candidates.len()
        )))
    }
}

impl OpenAiImageProvider {
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
            let results: Vec<ImageGenResult> = output
                .iter()
                .filter(|item| item["type"].as_str() == Some("image_generation_call"))
                .map(|item| ImageGenResult {
                    url: None,
                    base64: item["result"].as_str().map(|s| s.to_string()),
                    revised_prompt: None,
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
