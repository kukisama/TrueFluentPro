//! Text-to-Speech API endpoint — proxies to configured TtsProvider.

use crate::state::AppState;
use crate::error::ApiError;
use domain::auth::UserContext;
use providers::TtsRequest;
use std::sync::Arc;
use axum::{
    Router, routing::post,
    extract::State,
    Extension, Json,
    http::{header, StatusCode},
    response::Response,
    body::Body,
};
use serde::Deserialize;
use tracing::info;

pub fn routes() -> Router<Arc<AppState>> {
    Router::new()
        .route("/v1/speech/synthesize", post(synthesize))
}

#[derive(Debug, Deserialize)]
struct SynthesizeRequest {
    text: String,
    #[serde(default = "default_voice")]
    voice_id: String,
    #[serde(default)]
    output_format: Option<String>,
    #[serde(default)]
    speed: Option<f32>,
    /// Optional: specify which provider to use.
    #[serde(default)]
    provider_id: Option<String>,
}

fn default_voice() -> String {
    "en-US-AriaNeural".to_string()
}

async fn synthesize(
    State(state): State<Arc<AppState>>,
    Extension(ctx): Extension<UserContext>,
    Json(req): Json<SynthesizeRequest>,
) -> Result<Response, ApiError> {
    let registry = state.providers.read().await;
    let provider = registry.get_tts(req.provider_id.as_deref())
        .map_err(|_| ApiError::BadRequest("No TTS provider configured. Ask admin to set up a provider.".into()))?;

    info!(user = %ctx.user_id, voice = %req.voice_id, text_len = req.text.len(), "TTS request");

    let tts_req = TtsRequest {
        text: req.text,
        voice_id: req.voice_id,
        output_format: req.output_format.clone(),
        speed: req.speed,
    };

    let audio_bytes = provider.synthesize(tts_req).await
        .map_err(|e| map_provider_error(e))?;

    // Determine content type from format
    let content_type = match req.output_format.as_deref() {
        Some(f) if f.contains("mp3") => "audio/mpeg",
        Some(f) if f.contains("wav") || f.contains("pcm") || f.contains("riff") => "audio/wav",
        Some(f) if f.contains("opus") || f.contains("ogg") => "audio/ogg",
        _ => "audio/mpeg",
    };

    Ok(Response::builder()
        .status(StatusCode::OK)
        .header(header::CONTENT_TYPE, content_type)
        .header(header::CONTENT_LENGTH, audio_bytes.len().to_string())
        .body(Body::from(audio_bytes))
        .unwrap())
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
