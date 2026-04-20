//! Video generation API endpoints.
//!
//! POST /v1/videos/generations — submit a video generation task
//! GET  /v1/videos/generations/{task_id} — query task status

use crate::state::AppState;
use crate::error::ApiError;
use domain::auth::UserContext;
use providers::VideoGenRequest;
use std::sync::Arc;
use axum::{Router, routing::{post, get}, extract::{State, Path, Extension, Json}};
use serde::Deserialize;
use serde_json::{json, Value};
use tracing::info;
use billing;

pub fn routes() -> Router<Arc<AppState>> {
    Router::new()
        .route("/v1/videos/generations", post(generate_video))
        .route("/v1/videos/generations/{task_id}", get(query_video_task))
}

#[derive(Debug, Deserialize)]
struct VideoGenerationRequest {
    prompt: String,
    #[serde(default)]
    resolution: Option<String>,
    #[serde(default)]
    duration_seconds: Option<u32>,
    #[serde(default)]
    fps: Option<u32>,
    #[serde(default)]
    negative_prompt: Option<String>,
    #[serde(default)]
    source_image_url: Option<String>,
    #[serde(default)]
    provider_id: Option<String>,
}

async fn generate_video(
    State(state): State<Arc<AppState>>,
    Extension(ctx): Extension<UserContext>,
    Json(req): Json<VideoGenerationRequest>,
) -> Result<Json<Value>, ApiError> {
    let registry = state.providers.read().await;
    let provider = registry.get_video(req.provider_id.as_deref())
        .map_err(|_| ApiError::BadRequest("No video provider configured. Ask admin to set up a provider.".into()))?;

    // Check billing quota
    if state.billing.is_enabled() {
        match state.billing.check_quota(&ctx.user_id, "video").await {
            Ok(billing::QuotaStatus::Exceeded { used, limit }) => {
                return Err(ApiError::TooManyRequests(format!("Video quota exceeded: used {used}/{limit} videos this month")));
            }
            Err(e) => { tracing::warn!(error = %e, "Billing check failed, allowing request"); }
            _ => {}
        }
    }

    info!(user = %ctx.user_id, prompt_len = req.prompt.len(), "Video generation request");

    let gen_req = VideoGenRequest {
        prompt: req.prompt,
        resolution: req.resolution,
        duration_seconds: req.duration_seconds,
        fps: req.fps,
        negative_prompt: req.negative_prompt,
        source_image_url: req.source_image_url,
    };

    let result = provider.generate(gen_req).await
        .map_err(|e| map_provider_error(e))?;

    // Record usage and audit log
    let _ = state.billing.record_usage(&ctx.user_id, "video.generate", "video", 1).await;
    let _ = state.storage.write_audit_log(&ctx.user_id, "video.generate", None, None).await;

    Ok(Json(json!({
        "task_id": result.task_id,
        "status": result.status,
        "video_url": result.video_url,
    })))
}

async fn query_video_task(
    State(state): State<Arc<AppState>>,
    Extension(ctx): Extension<UserContext>,
    Path(task_id): Path<String>,
) -> Result<Json<Value>, ApiError> {
    // Use first available video provider for status check
    let registry = state.providers.read().await;
    let provider = registry.get_video(None)
        .map_err(|_| ApiError::BadRequest("No video provider configured.".into()))?;

    info!(user = %ctx.user_id, task_id = %task_id, "Video task status query");

    let result = provider.query_task(&task_id).await
        .map_err(|e| map_provider_error(e))?;

    Ok(Json(json!({
        "task_id": result.task_id,
        "status": result.status,
        "video_url": result.video_url,
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
