//! Image generation API endpoint — proxies to configured ImageProvider.

use crate::state::AppState;
use crate::error::ApiError;
use domain::auth::UserContext;
use providers::ImageGenRequest;
use std::sync::Arc;
use axum::{Router, routing::post, extract::State, Extension, Json};
use serde::Deserialize;
use serde_json::{json, Value};
use tracing::info;
use billing;

pub fn routes() -> Router<Arc<AppState>> {
    Router::new()
        .route("/v1/images/generations", post(generate_image))
}

#[derive(Debug, Deserialize)]
struct ImageGenerationRequest {
    prompt: String,
    #[serde(default)]
    size: Option<String>,
    #[serde(default)]
    n: Option<u32>,
    #[serde(default)]
    quality: Option<String>,
    /// Optional: specify which provider to use.
    #[serde(default)]
    provider_id: Option<String>,
}

async fn generate_image(
    State(state): State<Arc<AppState>>,
    Extension(ctx): Extension<UserContext>,
    Json(req): Json<ImageGenerationRequest>,
) -> Result<Json<Value>, ApiError> {
    let registry = state.providers.read().await;
    let provider = registry.get_image(req.provider_id.as_deref())
        .map_err(|_| ApiError::BadRequest("No image provider configured. Ask admin to set up a provider.".into()))?;

    // Check billing quota
    if state.billing.is_enabled() {
        match state.billing.check_quota(&ctx.user_id, "image").await {
            Ok(billing::QuotaStatus::Exceeded { used, limit }) => {
                return Err(ApiError::TooManyRequests(format!("Image quota exceeded: used {used}/{limit} images this month")));
            }
            Err(e) => { tracing::warn!(error = %e, "Billing check failed, allowing request"); }
            _ => {}
        }
    }

    let req_n = req.n.unwrap_or(1);

    info!(user = %ctx.user_id, prompt_len = req.prompt.len(), "Image generation request");

    let gen_req = ImageGenRequest {
        prompt: req.prompt,
        size: req.size,
        n: req.n,
        quality: req.quality,
    };

    let result = provider.generate(gen_req).await
        .map_err(|e| map_provider_error(e))?;

    // Record usage and audit log after successful generation
    let _ = state.billing.record_usage(&ctx.user_id, "image.generate", "image", req_n as i64).await;
    let _ = state.storage.write_audit_log(&ctx.user_id, "image.generate", None, None).await;

    let images: Vec<Value> = result.images.into_iter().map(|img| {
        let mut obj = json!({});
        if let Some(url) = img.url {
            obj["url"] = json!(url);
        }
        if let Some(b64) = img.b64_json {
            obj["b64_json"] = json!(b64);
        }
        obj
    }).collect();

    Ok(Json(json!({
        "data": images,
    })))
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
