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
        if self.endpoint.is_azure() {
            req.header("api-key", &self.endpoint.api_key)
        } else {
            req.header("Authorization", format!("Bearer {}", self.endpoint.api_key))
        }
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

        let messages: Vec<serde_json::Value> = request
            .messages
            .iter()
            .map(|m| json!({ "role": &m.role, "content": &m.content }))
            .collect();

        let mut body = json!({
            "messages": messages,
            "stream": false,
        });

        // model 字段：Azure 模式下可不传（由 deployment 决定），其他必传
        if !self.endpoint.is_azure() {
            body["model"] = json!(&request.model);
        }

        if let Some(temp) = request.temperature {
            body["temperature"] = json!(temp);
        }
        if let Some(max) = request.max_tokens {
            body["max_tokens"] = json!(max);
        }

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

        let json: serde_json::Value = resp
            .json()
            .await
            .map_err(|e| ProviderError::Internal(format!("JSON 解析失败: {e}")))?;

        let content = json["choices"][0]["message"]["content"]
            .as_str()
            .unwrap_or("")
            .to_string();

        let model_name = json["model"].as_str().unwrap_or(&request.model).to_string();

        let usage = json.get("usage").map(|u| TokenUsage {
            prompt_tokens: u["prompt_tokens"].as_u64().unwrap_or(0) as u32,
            completion_tokens: u["completion_tokens"].as_u64().unwrap_or(0) as u32,
            total_tokens: u["total_tokens"].as_u64().unwrap_or(0) as u32,
        });

        Ok(CompletionResponse {
            content,
            model: model_name,
            usage,
        })
    }

    async fn complete_stream(
        &self,
        request: &CompletionRequest,
    ) -> Result<mpsc::UnboundedReceiver<Result<String, ProviderError>>, ProviderError> {
        let url = self.build_url();

        let messages: Vec<serde_json::Value> = request
            .messages
            .iter()
            .map(|m| json!({ "role": &m.role, "content": &m.content }))
            .collect();

        let mut body = json!({
            "messages": messages,
            "stream": true,
            "stream_options": { "include_usage": true },
        });

        if !self.endpoint.is_azure() {
            body["model"] = json!(&request.model);
        }
        if let Some(temp) = request.temperature {
            body["temperature"] = json!(temp);
        }
        if let Some(max) = request.max_tokens {
            body["max_tokens"] = json!(max);
        }

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

        // 简洁的 SSE 解析：直接消费 bytes_stream
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

                // 按行解析 SSE
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
                            if let Some(delta) = v["choices"][0]["delta"]["content"].as_str() {
                                if !delta.is_empty() {
                                    let _ = tx.send(Ok(delta.to_string()));
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
