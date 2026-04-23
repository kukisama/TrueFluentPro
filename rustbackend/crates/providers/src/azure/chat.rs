//! Azure OpenAI Chat adapter — streams SSE from Azure OpenAI Chat Completions API.

use crate::{ChatProvider, ChatRequest, ChatChunk, TokenUsage, ProviderError};
use async_trait::async_trait;
use credential_broker::CredentialBroker;
use futures::stream::{BoxStream, StreamExt};
use reqwest::Client;
use secrecy::ExposeSecret;
use serde::{Deserialize, Serialize};
use std::sync::Arc;
use tracing::{debug, error, warn};

/// Azure OpenAI Chat adapter.
pub struct AzureOpenAiChat {
    client: Client,
    credentials: Arc<CredentialBroker>,
    provider_id: String,
}

impl AzureOpenAiChat {
    pub fn new(credentials: Arc<CredentialBroker>, provider_id: &str) -> Self {
        Self {
            client: Client::new(),
            credentials,
            provider_id: provider_id.to_string(),
        }
    }
}

#[async_trait]
impl ChatProvider for AzureOpenAiChat {
    fn id(&self) -> &'static str {
        "azure_openai"
    }

    async fn chat_stream(
        &self,
        req: ChatRequest,
    ) -> Result<BoxStream<'static, Result<ChatChunk, ProviderError>>, ProviderError> {
        // Resolve credentials
        let endpoint = self.credentials.get(&self.provider_id, "endpoint").await
            .map_err(|e| ProviderError::Upstream(e.to_string()))?
            .ok_or(ProviderError::BadCredential)?;
        let api_key = self.credentials.get(&self.provider_id, "api_key").await
            .map_err(|e| ProviderError::Upstream(e.to_string()))?
            .ok_or(ProviderError::BadCredential)?;

        // Resolve deployment and API version (with defaults)
        let deployment = self.credentials.get(&self.provider_id, "deployment").await
            .map_err(|e| ProviderError::Upstream(e.to_string()))?
            .map(|s| s.expose_secret().to_string())
            .unwrap_or_else(|| req.model.clone());
        let api_version = self.credentials.get(&self.provider_id, "api_version").await
            .map_err(|e| ProviderError::Upstream(e.to_string()))?
            .map(|s| s.expose_secret().to_string())
            .unwrap_or_else(|| "2024-06-01".to_string());

        let base = endpoint.expose_secret().trim_end_matches('/').to_string();
        let url = format!("{base}/openai/deployments/{deployment}/chat/completions?api-version={api_version}");

        debug!(url = %url, model = %req.model, "Azure OpenAI chat request");

        // Build request body
        let body = AzureOpenAiChatBody {
            messages: req.messages.iter().map(|m| AoaiMessage {
                role: m.role.clone(),
                content: m.content.clone(),
            }).collect(),
            stream: true,
            stream_options: Some(StreamOptions { include_usage: true }),
            temperature: req.temperature,
            max_tokens: req.max_tokens,
        };

        let resp = self.client.post(&url)
            .header("api-key", api_key.expose_secret())
            .header("Content-Type", "application/json")
            .json(&body)
            .send()
            .await
            .map_err(|e| ProviderError::Network(e.to_string()))?;

        if !resp.status().is_success() {
            let status = resp.status().as_u16();
            let text = resp.text().await.unwrap_or_default();
            if status == 429 {
                return Err(ProviderError::RateLimited);
            }
            error!(status, body = %text, "Azure OpenAI error");
            return Err(ProviderError::Upstream(format!("HTTP {status}: {text}")));
        }

        // Parse SSE stream
        let byte_stream = resp.bytes_stream();
        let sse_stream = parse_sse_stream(byte_stream);

        Ok(sse_stream.boxed())
    }
}

/// Parse an SSE byte stream into ChatChunk items.
fn parse_sse_stream(
    byte_stream: impl futures::Stream<Item = Result<bytes::Bytes, reqwest::Error>> + Send + 'static,
) -> impl futures::Stream<Item = Result<ChatChunk, ProviderError>> + Send + 'static {
    let buffer = String::new();

    futures::stream::unfold(
        (Box::pin(byte_stream), buffer),
        |(mut stream, mut buf)| async move {
            loop {
                // Try to find a complete SSE event in the buffer
                while let Some(pos) = buf.find("\n\n") {
                    let event = buf[..pos].to_string();
                    buf = buf[pos + 2..].to_string();

                    for line in event.lines() {
                        if let Some(data) = line.strip_prefix("data: ") {
                            if data.trim() == "[DONE]" {
                                return None;
                            }
                            match serde_json::from_str::<AoaiStreamChunk>(data) {
                                Ok(chunk) => {
                                    if let Some(chat_chunk) = convert_chunk(chunk) {
                                        return Some((Ok(chat_chunk), (stream, buf)));
                                    }
                                }
                                Err(e) => {
                                    warn!(error = %e, data = %data, "failed to parse SSE chunk");
                                }
                            }
                        }
                    }
                }

                // Read more data
                match stream.next().await {
                    Some(Ok(bytes)) => {
                        buf.push_str(&String::from_utf8_lossy(&bytes));
                    }
                    Some(Err(e)) => {
                        return Some((Err(ProviderError::Network(e.to_string())), (stream, buf)));
                    }
                    None => {
                        // Stream ended — process remaining buffer
                        if !buf.trim().is_empty() {
                            for line in buf.lines() {
                                if let Some(data) = line.strip_prefix("data: ") {
                                    if data.trim() == "[DONE]" {
                                        return None;
                                    }
                                    if let Ok(chunk) = serde_json::from_str::<AoaiStreamChunk>(data) {
                                        if let Some(chat_chunk) = convert_chunk(chunk) {
                                            buf.clear();
                                            return Some((Ok(chat_chunk), (stream, buf)));
                                        }
                                    }
                                }
                            }
                        }
                        return None;
                    }
                }
            }
        },
    )
}

fn convert_chunk(chunk: AoaiStreamChunk) -> Option<ChatChunk> {
    let choice = chunk.choices.into_iter().next();
    let delta = choice.as_ref().and_then(|c| c.delta.content.as_deref()).unwrap_or("");
    let finish_reason = choice.as_ref().and_then(|c| c.finish_reason.clone());
    let usage = chunk.usage.map(|u| TokenUsage {
        prompt: u.prompt_tokens,
        completion: u.completion_tokens,
    });

    if delta.is_empty() && finish_reason.is_none() && usage.is_none() {
        return None;
    }

    Some(ChatChunk {
        delta: delta.to_string(),
        finish_reason,
        usage,
    })
}

// ═══ Azure OpenAI wire types ═══

#[derive(Serialize)]
struct AzureOpenAiChatBody {
    messages: Vec<AoaiMessage>,
    stream: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    stream_options: Option<StreamOptions>,
    #[serde(skip_serializing_if = "Option::is_none")]
    temperature: Option<f32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    max_tokens: Option<u32>,
}

#[derive(Serialize)]
struct AoaiMessage {
    role: String,
    content: String,
}

#[derive(Serialize)]
struct StreamOptions {
    include_usage: bool,
}

#[derive(Deserialize)]
struct AoaiStreamChunk {
    #[serde(default)]
    choices: Vec<AoaiChoice>,
    usage: Option<AoaiUsage>,
}

#[derive(Deserialize)]
struct AoaiChoice {
    delta: AoaiDelta,
    finish_reason: Option<String>,
}

#[derive(Deserialize)]
struct AoaiDelta {
    content: Option<String>,
}

#[derive(Deserialize)]
struct AoaiUsage {
    prompt_tokens: u32,
    completion_tokens: u32,
}
