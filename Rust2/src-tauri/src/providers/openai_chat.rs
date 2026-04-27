use async_trait::async_trait;
use reqwest::Client;
use serde_json::json;
use tokio::sync::mpsc;
use futures_util::StreamExt;

use crate::models::*;
use super::registry::*;

/// OpenAI 兼容的 Chat Completion Provider
///
/// 支持: Azure OpenAI、OpenAI、DeepSeek、Ollama、vLLM 等所有兼容 /v1/chat/completions 的服务。
/// 认证方式: api-key header（Azure）或 Bearer token（OpenAI 兼容）。
pub struct OpenAiChatProvider {
    client: Client,
    endpoint: AiEndpoint,
}

impl OpenAiChatProvider {
    pub fn new(endpoint: AiEndpoint) -> Self {
        Self {
            client: Client::new(),
            endpoint,
        }
    }

    fn build_url(&self) -> String {
        let base = self.endpoint.url.trim_end_matches('/');

        match self.endpoint.endpoint_type {
            EndpointType::AzureOpenAi => {
                // Azure: /openai/deployments/{deployment}/chat/completions?api-version=...
                let api_ver = self.endpoint.api_version.as_deref().unwrap_or("2024-08-01-preview");
                if let Some(model) = self.endpoint.first_model_with_capability(ModelCapability::Text) {
                    let deploy = model.effective_deployment();
                    format!("{base}/openai/deployments/{deploy}/chat/completions?api-version={api_ver}")
                } else {
                    format!("{base}/openai/v1/chat/completions")
                }
            }
            EndpointType::ApiManagementGateway => {
                // APIM: 优先 /responses（对齐 C# apim-gateway.json text_url_candidates 第 1 条）
                let api_ver = self.endpoint.api_version.as_deref().unwrap_or("2025-03-01-preview");
                format!("{base}/responses?api-version={api_ver}")
            }
            _ => {
                if base.ends_with("/v1") || base.ends_with("/v1/chat/completions") {
                    if base.ends_with("/v1/chat/completions") {
                        base.to_string()
                    } else {
                        format!("{base}/chat/completions")
                    }
                } else {
                    format!("{base}/v1/chat/completions")
                }
            }
        }
    }

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
    /// 是否走 Responses API（APIM 用 /responses 而非 /chat/completions）
    fn is_responses_api(&self) -> bool {
        self.endpoint.endpoint_type == EndpointType::ApiManagementGateway
    }
}

impl ProviderMeta for OpenAiChatProvider {
    fn id(&self) -> &str {
        &self.endpoint.id
    }

    fn display_name(&self) -> &str {
        &self.endpoint.name
    }

    fn capabilities(&self) -> Vec<ProviderCapability> {
        vec![ProviderCapability::AiCompletion]
    }
}

#[async_trait]
impl AiCompletionSlot for OpenAiChatProvider {
    async fn complete(
        &self,
        request: &CompletionRequest,
    ) -> Result<CompletionResponse, ProviderError> {
        let url = self.build_url();
        let is_responses = self.is_responses_api();

        let body = if is_responses {
            // Responses API 格式：input 是纯文本或 messages 数组
            let last_msg = request.messages.last()
                .map(|m| m.content.as_str())
                .unwrap_or("");
            let mut b = json!({
                "model": &request.model,
                "input": last_msg,
                "stream": false,
            });
            if let Some(temp) = request.temperature {
                b["temperature"] = json!(temp);
            }
            b
        } else {
            // ChatCompletions 格式
            let messages: Vec<serde_json::Value> = request
                .messages
                .iter()
                .map(|m| json!({ "role": &m.role, "content": &m.content }))
                .collect();

            let mut b = json!({
                "messages": messages,
                "stream": false,
            });

            // model 字段：纯 Azure 模式下可不传（由 deployment 决定），APIM 和其他必传
            if !self.endpoint.is_azure()
                || self.endpoint.endpoint_type == EndpointType::ApiManagementGateway
            {
                b["model"] = json!(&request.model);
            }

            if let Some(temp) = request.temperature {
                b["temperature"] = json!(temp);
            }
            if let Some(max) = request.max_tokens {
                b["max_tokens"] = json!(max);
            }
            b
        };

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
                return Err(ProviderError::RateLimited { retry_after_ms: 5000 });
            }
            return Err(ProviderError::Network(format!("{status}: {text}")));
        }

        let json_val: serde_json::Value = resp
            .json()
            .await
            .map_err(|e| ProviderError::Internal(format!("JSON 解析失败: {e}")))?;

        if is_responses {
            // Responses API 响应: output[].content[].text
            let content = json_val["output"]
                .as_array()
                .and_then(|arr| arr.iter().find(|o| o["type"].as_str() == Some("message")))
                .and_then(|msg| msg["content"].as_array())
                .and_then(|c| c.first())
                .and_then(|t| t["text"].as_str())
                .unwrap_or("")
                .to_string();

            let model_name = json_val["model"].as_str().unwrap_or(&request.model).to_string();

            let usage = json_val.get("usage").map(|u| TokenUsage {
                prompt_tokens: u["input_tokens"].as_u64().unwrap_or(0) as u32,
                completion_tokens: u["output_tokens"].as_u64().unwrap_or(0) as u32,
                total_tokens: u["total_tokens"].as_u64().unwrap_or(0) as u32,
            });

            Ok(CompletionResponse { content, model: model_name, usage })
        } else {
            // ChatCompletions 响应
            let content = json_val["choices"][0]["message"]["content"]
                .as_str()
                .unwrap_or("")
                .to_string();

            let model_name = json_val["model"].as_str().unwrap_or(&request.model).to_string();

            let usage = json_val.get("usage").map(|u| TokenUsage {
                prompt_tokens: u["prompt_tokens"].as_u64().unwrap_or(0) as u32,
                completion_tokens: u["completion_tokens"].as_u64().unwrap_or(0) as u32,
                total_tokens: u["total_tokens"].as_u64().unwrap_or(0) as u32,
            });

            Ok(CompletionResponse { content, model: model_name, usage })
        }
    }

    async fn complete_stream(
        &self,
        request: &CompletionRequest,
    ) -> Result<mpsc::UnboundedReceiver<Result<String, ProviderError>>, ProviderError> {
        let url = self.build_url();
        let is_responses = self.is_responses_api();

        let body = if is_responses {
            // Responses API 流式请求
            let last_msg = request.messages.last()
                .map(|m| m.content.as_str())
                .unwrap_or("");
            let mut b = json!({
                "model": &request.model,
                "input": last_msg,
                "stream": true,
            });
            if let Some(temp) = request.temperature {
                b["temperature"] = json!(temp);
            }
            b
        } else {
            // ChatCompletions 流式请求
            let messages: Vec<serde_json::Value> = request
                .messages
                .iter()
                .map(|m| json!({ "role": &m.role, "content": &m.content }))
                .collect();

            let mut b = json!({
                "messages": messages,
                "stream": true,
                "stream_options": { "include_usage": true },
            });

            if !self.endpoint.is_azure()
                || self.endpoint.endpoint_type == EndpointType::ApiManagementGateway
            {
                b["model"] = json!(&request.model);
            }
            if let Some(temp) = request.temperature {
                b["temperature"] = json!(temp);
            }
            if let Some(max) = request.max_tokens {
                b["max_tokens"] = json!(max);
            }
            b
        };

        let resp = self
            .apply_auth(self.client.post(&url))
            .json(&body)
            .send()
            .await
            .map_err(|e| ProviderError::Network(e.to_string()))?;

        if !resp.status().is_success() {
            let status = resp.status();
            let text = resp.text().await.unwrap_or_default();
            return Err(ProviderError::Network(format!("{status}: {text}")));
        }

        let (tx, rx) = mpsc::unbounded_channel();

        tokio::spawn(async move {
            let mut byte_stream = resp.bytes_stream();
            let mut buffer = String::new();

            while let Some(chunk_result) = byte_stream.next().await {
                let chunk = match chunk_result {
                    Ok(bytes) => String::from_utf8_lossy(&bytes).to_string(),
                    Err(e) => {
                        let _ = tx.send(Err(ProviderError::Network(e.to_string())));
                        return;
                    }
                };

                buffer.push_str(&chunk);

                while let Some(pos) = buffer.find('\n') {
                    let line = buffer[..pos].trim().to_string();
                    buffer = buffer[pos + 1..].to_string();

                    if line.is_empty() || line.starts_with(':') {
                        continue;
                    }
                    if line == "data: [DONE]" {
                        return;
                    }
                    if let Some(json_str) = line.strip_prefix("data: ") {
                        if let Ok(v) = serde_json::from_str::<serde_json::Value>(json_str) {
                            if is_responses {
                                // Responses API SSE: type=response.output_text.delta → delta 字段
                                if v["type"].as_str() == Some("response.output_text.delta") {
                                    if let Some(delta) = v["delta"].as_str() {
                                        if !delta.is_empty() {
                                            let _ = tx.send(Ok(delta.to_string()));
                                        }
                                    }
                                }
                            } else {
                                // ChatCompletions SSE: choices[0].delta.content
                                if let Some(delta) = v["choices"][0]["delta"]["content"].as_str() {
                                    if !delta.is_empty() {
                                        let _ = tx.send(Ok(delta.to_string()));
                                    }
                                }
                            }
                        }
                    }
                }
            }
        });

        Ok(rx)
    }
}
