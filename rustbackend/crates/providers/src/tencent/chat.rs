//! Tencent Cloud Hunyuan chat adapter (OpenAI-compatible endpoint).
//!
//! Hunyuan provides an OpenAI-compatible chat completions endpoint:
//!   https://hunyuan.cloud.tencent.com/openai/v1/chat/completions
//!
//! API reference: https://cloud.tencent.com/document/product/1729/97744
//!
//! This adapter reuses the generic OpenAI streaming/parsing logic and only
//! customizes the endpoint, auth header, and default model.
//!
//! Credentials:
//!   - `api_key`: Tencent Hunyuan API key
//!   - `endpoint`: (optional) Override endpoint, default "https://api.hunyuan.cloud.tencent.com/v1"
//!   - `default_model`: (optional) Default model, e.g. "hunyuan-lite"

use crate::{ChatChunk, ChatProvider, ChatRequest, ProviderError, TokenUsage};
use async_trait::async_trait;
use credential_broker::CredentialBroker;
use futures::stream::BoxStream;
use futures::StreamExt;
use reqwest::Client;
use serde_json::json;
use std::sync::Arc;
use tracing::debug;

pub struct TencentHunyuanChat {
    credentials: Arc<CredentialBroker>,
    provider_id: String,
    http: Client,
}

impl TencentHunyuanChat {
    pub fn new(credentials: Arc<CredentialBroker>, provider_id: &str) -> Self {
        Self {
            credentials,
            provider_id: provider_id.to_string(),
            http: Client::new(),
        }
    }
}

#[async_trait]
impl ChatProvider for TencentHunyuanChat {
    fn id(&self) -> &'static str {
        "tencent_hunyuan"
    }

    async fn chat_stream(
        &self,
        req: ChatRequest,
    ) -> Result<BoxStream<'static, Result<ChatChunk, ProviderError>>, ProviderError> {
        use secrecy::ExposeSecret;

        let api_key = self.credentials.get(&self.provider_id, "api_key").await
            .map_err(|_| ProviderError::BadCredential)?
            .ok_or(ProviderError::BadCredential)?;

        let endpoint = self.credentials.get(&self.provider_id, "endpoint").await
            .map_err(|_| ProviderError::BadCredential)?
            .map(|s| s.expose_secret().to_string())
            .unwrap_or_else(|| "https://api.hunyuan.cloud.tencent.com/v1".to_string());

        let default_model = self.credentials.get(&self.provider_id, "default_model").await
            .map_err(|_| ProviderError::BadCredential)?
            .map(|s| s.expose_secret().to_string())
            .unwrap_or_else(|| "hunyuan-lite".to_string());

        let base_url = endpoint.trim_end_matches('/').to_string();
        let url = format!("{base_url}/chat/completions");

        let model = if req.model.is_empty() || req.model == "gpt-4o" {
            default_model
        } else {
            req.model.clone()
        };

        let messages: Vec<serde_json::Value> = req.messages.iter()
            .map(|m| json!({ "role": &m.role, "content": &m.content }))
            .collect();

        let mut body = json!({
            "model": model,
            "messages": messages,
            "stream": req.stream,
        });

        if let Some(temp) = req.temperature {
            body["temperature"] = json!(temp);
        }
        if let Some(max) = req.max_tokens {
            body["max_tokens"] = json!(max);
        }

        debug!(url = %url, model = %model, stream = req.stream, "Tencent Hunyuan chat request");

        let response = self.http.post(&url)
            .header("Content-Type", "application/json")
            .header("Authorization", format!("Bearer {}", api_key.expose_secret()))
            .body(body.to_string())
            .send()
            .await
            .map_err(|e| ProviderError::Network(e.to_string()))?;

        if !response.status().is_success() {
            let status = response.status();
            let text = response.text().await.unwrap_or_default();
            return Err(ProviderError::Upstream(format!("HTTP {status}: {text}")));
        }

        if !req.stream {
            let text = response.text().await
                .map_err(|e| ProviderError::Network(e.to_string()))?;
            let parsed: serde_json::Value = serde_json::from_str(&text)
                .map_err(|e| ProviderError::Upstream(format!("JSON parse error: {e}")))?;

            let content = parsed["choices"][0]["message"]["content"]
                .as_str().unwrap_or("").to_string();
            let usage = parsed["usage"].as_object().map(|u| TokenUsage {
                prompt: u.get("prompt_tokens").and_then(|v| v.as_u64()).unwrap_or(0) as u32,
                completion: u.get("completion_tokens").and_then(|v| v.as_u64()).unwrap_or(0) as u32,
            });

            let chunk = ChatChunk { delta: content, finish_reason: Some("stop".into()), usage };
            return Ok(Box::pin(futures::stream::once(async move { Ok(chunk) })));
        }

        // SSE streaming — parse OpenAI-compatible format
        let byte_stream = response.bytes_stream();
        let stream = byte_stream
            .map(move |chunk_result| match chunk_result {
                Err(e) => vec![Err(ProviderError::Network(e.to_string()))],
                Ok(bytes) => {
                    let text = String::from_utf8_lossy(&bytes);
                    let mut results = Vec::new();
                    for line in text.lines() {
                        let line = line.trim();
                        if line.is_empty() || line.starts_with(':') { continue; }
                        if let Some(data) = line.strip_prefix("data: ") {
                            let data = data.trim();
                            if data == "[DONE]" { continue; }
                            if let Ok(parsed) = serde_json::from_str::<serde_json::Value>(data) {
                                let delta = parsed["choices"][0]["delta"]["content"]
                                    .as_str().unwrap_or("").to_string();
                                let finish = parsed["choices"][0]["finish_reason"]
                                    .as_str().map(|s| s.to_string());
                                let usage = parsed["usage"].as_object().map(|u| TokenUsage {
                                    prompt: u.get("prompt_tokens").and_then(|v| v.as_u64()).unwrap_or(0) as u32,
                                    completion: u.get("completion_tokens").and_then(|v| v.as_u64()).unwrap_or(0) as u32,
                                });
                                if !delta.is_empty() || finish.is_some() || usage.is_some() {
                                    results.push(Ok(ChatChunk { delta, finish_reason: finish, usage }));
                                }
                            }
                        }
                    }
                    results
                }
            })
            .flat_map(futures::stream::iter);

        Ok(Box::pin(stream))
    }
}
