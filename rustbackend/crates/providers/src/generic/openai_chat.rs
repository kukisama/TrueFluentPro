//! Generic OpenAI-compatible chat adapter.
//!
//! Works with any API that follows the OpenAI Chat Completions format:
//! ollama, vLLM, LM Studio, self-hosted models, DeepSeek, OpenAI direct, etc.
//!
//! Credentials:
//!   - `endpoint`: Base URL (e.g. "http://localhost:11434/v1" or "https://api.openai.com/v1")
//!   - `api_key`: API key (optional for local models, required for cloud)
//!   - `default_model`: Default model name (e.g. "llama3", "gpt-4o")

use crate::{ChatChunk, ChatProvider, ChatRequest, ProviderError, TokenUsage};
use async_trait::async_trait;
use credential_broker::CredentialBroker;
use futures::stream::BoxStream;
use futures::StreamExt;
use reqwest::Client;
use serde_json::json;
use std::sync::Arc;
use tracing::debug;

pub struct GenericOpenAiChat {
    credentials: Arc<CredentialBroker>,
    provider_id: String,
    http: Client,
}

impl GenericOpenAiChat {
    pub fn new(credentials: Arc<CredentialBroker>, provider_id: &str) -> Self {
        Self {
            credentials,
            provider_id: provider_id.to_string(),
            http: Client::new(),
        }
    }
}

#[async_trait]
impl ChatProvider for GenericOpenAiChat {
    fn id(&self) -> &'static str {
        "generic_openai_chat"
    }

    async fn chat_stream(
        &self,
        req: ChatRequest,
    ) -> Result<BoxStream<'static, Result<ChatChunk, ProviderError>>, ProviderError> {
        use secrecy::ExposeSecret;

        // Resolve credentials
        let endpoint = self
            .credentials
            .get(&self.provider_id, "endpoint")
            .await
            .map_err(|_| ProviderError::BadCredential)?
            .ok_or(ProviderError::BadCredential)?;

        let api_key = self
            .credentials
            .get(&self.provider_id, "api_key")
            .await
            .map_err(|_| ProviderError::BadCredential)?;

        let default_model = self
            .credentials
            .get(&self.provider_id, "default_model")
            .await
            .map_err(|_| ProviderError::BadCredential)?;

        let base_url = endpoint.expose_secret().trim_end_matches('/').to_string();
        let url = format!("{}/chat/completions", base_url);

        let model = if req.model.is_empty() || req.model == "gpt-4o" {
            // Use default model if not specified or if it's the gateway default
            default_model
                .map(|s| s.expose_secret().to_string())
                .unwrap_or_else(|| req.model.clone())
        } else {
            req.model.clone()
        };

        let messages: Vec<serde_json::Value> = req
            .messages
            .iter()
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

        let mut request = self
            .http
            .post(&url)
            .header("Content-Type", "application/json");

        // Add API key if available (optional for local models)
        if let Some(key) = &api_key {
            request = request.header("Authorization", format!("Bearer {}", key.expose_secret()));
        }

        debug!(url = %url, model = %model, stream = req.stream, "Generic OpenAI chat request");

        let response = request
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
            // Non-streaming: parse full response and return single chunk
            let text = response
                .text()
                .await
                .map_err(|e| ProviderError::Network(e.to_string()))?;
            let parsed: serde_json::Value = serde_json::from_str(&text)
                .map_err(|e| ProviderError::Upstream(format!("JSON parse error: {e}")))?;

            let content = parsed["choices"][0]["message"]["content"]
                .as_str()
                .unwrap_or("")
                .to_string();

            let usage = parsed["usage"].as_object().map(|u| TokenUsage {
                prompt: u
                    .get("prompt_tokens")
                    .and_then(|v| v.as_u64())
                    .unwrap_or(0) as u32,
                completion: u
                    .get("completion_tokens")
                    .and_then(|v| v.as_u64())
                    .unwrap_or(0) as u32,
            });

            let chunk = ChatChunk {
                delta: content,
                finish_reason: Some("stop".to_string()),
                usage,
            };
            return Ok(Box::pin(futures::stream::once(async move { Ok(chunk) })));
        }

        // Streaming: parse SSE
        let byte_stream = response.bytes_stream();

        let stream = byte_stream
            .map(move |chunk_result| match chunk_result {
                Err(e) => vec![Err(ProviderError::Network(e.to_string()))],
                Ok(bytes) => {
                    let text = String::from_utf8_lossy(&bytes);
                    let mut results = Vec::new();

                    for line in text.lines() {
                        let line = line.trim();
                        if line.is_empty() || line.starts_with(':') {
                            continue;
                        }
                        if let Some(data) = line.strip_prefix("data: ") {
                            let data = data.trim();
                            if data == "[DONE]" {
                                continue;
                            }
                            match serde_json::from_str::<serde_json::Value>(data) {
                                Ok(parsed) => {
                                    let delta = parsed["choices"][0]["delta"]["content"]
                                        .as_str()
                                        .unwrap_or("")
                                        .to_string();
                                    let finish = parsed["choices"][0]["finish_reason"]
                                        .as_str()
                                        .map(|s| s.to_string());
                                    let usage =
                                        parsed["usage"].as_object().map(|u| TokenUsage {
                                            prompt: u
                                                .get("prompt_tokens")
                                                .and_then(|v| v.as_u64())
                                                .unwrap_or(0)
                                                as u32,
                                            completion: u
                                                .get("completion_tokens")
                                                .and_then(|v| v.as_u64())
                                                .unwrap_or(0)
                                                as u32,
                                        });

                                    if !delta.is_empty() || finish.is_some() || usage.is_some() {
                                        results.push(Ok(ChatChunk {
                                            delta,
                                            finish_reason: finish,
                                            usage,
                                        }));
                                    }
                                }
                                Err(_) => {} // Skip unparseable lines
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
