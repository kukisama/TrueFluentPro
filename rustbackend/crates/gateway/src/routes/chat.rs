//! Chat completions API endpoint — proxies to configured ChatProvider.

use crate::state::AppState;
use crate::error::ApiError;
use domain::auth::UserContext;
use providers::{ChatRequest, ChatMessage};
use std::sync::Arc;
use axum::{
    Router, routing::post,
    extract::State,
    Extension, Json,
    response::{sse, Sse, IntoResponse},
};
use futures::StreamExt;
use serde::Deserialize;
use serde_json::json;
use std::convert::Infallible;
use tracing::info;

pub fn routes() -> Router<Arc<AppState>> {
    Router::new()
        .route("/v1/chat/completions", post(chat_completions))
}

#[derive(Debug, Deserialize)]
struct ChatCompletionRequest {
    messages: Vec<MessageInput>,
    #[serde(default = "default_model")]
    model: String,
    #[serde(default)]
    temperature: Option<f32>,
    #[serde(default)]
    max_tokens: Option<u32>,
    #[serde(default)]
    stream: Option<bool>,
    /// Optional: specify which provider to use (by provider ID).
    #[serde(default)]
    provider_id: Option<String>,
}

fn default_model() -> String {
    "gpt-4o".to_string()
}

#[derive(Debug, Deserialize)]
struct MessageInput {
    role: String,
    content: String,
}

async fn chat_completions(
    State(state): State<Arc<AppState>>,
    Extension(ctx): Extension<UserContext>,
    Json(req): Json<ChatCompletionRequest>,
) -> Result<axum::response::Response, ApiError> {
    let should_stream = req.stream.unwrap_or(true);

    // Resolve provider
    let registry = state.providers.read().await;
    let provider = registry.get_chat(req.provider_id.as_deref())
        .map_err(|_| ApiError::BadRequest("No chat provider configured. Ask admin to set up a provider.".into()))?;

    let chat_req = ChatRequest {
        messages: req.messages.into_iter().map(|m| ChatMessage {
            role: m.role,
            content: m.content,
        }).collect(),
        model: req.model,
        temperature: req.temperature,
        max_tokens: req.max_tokens,
        stream: should_stream,
        tenant_id: ctx.tenant_id.clone(),
    };

    info!(user = %ctx.user_id, model = %chat_req.model, stream = should_stream, "Chat request");

    if should_stream {
        // SSE streaming response
        let chunk_stream = provider.chat_stream(chat_req).await
            .map_err(|e| map_provider_error(e))?;

        let sse_stream = chunk_stream.map(|result| {
            match result {
                Ok(chunk) => {
                    let data = json!({
                        "choices": [{
                            "delta": { "content": chunk.delta },
                            "finish_reason": chunk.finish_reason,
                        }],
                        "usage": chunk.usage.map(|u| json!({
                            "prompt_tokens": u.prompt,
                            "completion_tokens": u.completion,
                        })),
                    });
                    Ok::<_, Infallible>(sse::Event::default().data(data.to_string()))
                }
                Err(e) => {
                    let err_data = json!({ "error": e.to_string() });
                    Ok(sse::Event::default().data(err_data.to_string()))
                }
            }
        });

        let sse_response = Sse::new(sse_stream);
        Ok(sse_response.into_response())
    } else {
        // Non-streaming: collect all chunks
        let chunk_stream = provider.chat_stream(chat_req).await
            .map_err(|e| map_provider_error(e))?;

        let mut full_content = String::new();
        let mut final_usage = None;

        tokio::pin!(chunk_stream);
        while let Some(result) = chunk_stream.next().await {
            match result {
                Ok(chunk) => {
                    full_content.push_str(&chunk.delta);
                    if chunk.usage.is_some() {
                        final_usage = chunk.usage;
                    }
                }
                Err(e) => return Err(map_provider_error(e)),
            }
        }

        let resp = json!({
            "choices": [{
                "message": { "role": "assistant", "content": full_content },
                "finish_reason": "stop",
            }],
            "usage": final_usage.map(|u| json!({
                "prompt_tokens": u.prompt,
                "completion_tokens": u.completion,
            })),
        });

        Ok(Json(resp).into_response())
    }
}

fn map_provider_error(e: providers::ProviderError) -> ApiError {
    match e {
        providers::ProviderError::RateLimited => ApiError::TooManyRequests("Provider rate limited".into()),
        providers::ProviderError::BadCredential => ApiError::Internal("Provider credentials not configured".into()),
        providers::ProviderError::UnsupportedCapability => ApiError::BadRequest("No provider available for this capability".into()),
        providers::ProviderError::ProviderNotFound(id) => ApiError::NotFound(format!("Provider '{id}' not found or not enabled")),
        providers::ProviderError::Network(m) => ApiError::Internal(format!("Network error: {m}")),
        providers::ProviderError::Upstream(m) => ApiError::Internal(format!("Upstream error: {m}")),
    }
}
