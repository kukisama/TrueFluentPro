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
    /// Speech style (e.g. "cheerful", "sad", "angry", "excited").
    #[serde(default)]
    style: Option<String>,
    /// Style intensity (0.01–2.0).
    #[serde(default)]
    style_degree: Option<f32>,
    /// Role play (e.g. "Girl", "Boy", "YoungAdultFemale").
    #[serde(default)]
    role: Option<String>,
    /// Pitch (e.g. "+5%", "-10%", "high", "low").
    #[serde(default)]
    pitch: Option<String>,
    /// Volume (e.g. "+10%", "loud", "soft").
    #[serde(default)]
    volume: Option<String>,
    /// Language override for multilingual voices (e.g. "en-US").
    #[serde(default)]
    language: Option<String>,
    /// Voice effect (e.g. "eq_car").
    #[serde(default)]
    effect: Option<String>,
    /// Prosody range.
    #[serde(default)]
    range: Option<String>,
    /// Prosody contour.
    #[serde(default)]
    contour: Option<String>,
    /// If true, `text` is treated as raw SSML.
    #[serde(default)]
    raw_ssml: Option<bool>,
    /// Break strength before text.
    #[serde(default)]
    break_strength: Option<String>,
    /// Break time before text.
    #[serde(default)]
    break_time: Option<String>,
    /// Silence tag type.
    #[serde(default)]
    silence_type: Option<String>,
    /// Silence duration.
    #[serde(default)]
    silence_value: Option<String>,
    /// Emphasis level.
    #[serde(default)]
    emphasis: Option<String>,
    /// Phoneme alphabet.
    #[serde(default)]
    phoneme_alphabet: Option<String>,
    /// Phoneme value.
    #[serde(default)]
    phoneme_value: Option<String>,
    /// Say-as interpret-as.
    #[serde(default)]
    say_as_interpret_as: Option<String>,
    /// Say-as format.
    #[serde(default)]
    say_as_format: Option<String>,
    /// Say-as detail.
    #[serde(default)]
    say_as_detail: Option<String>,
    /// Sub alias.
    #[serde(default)]
    sub_alias: Option<String>,
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
        style: req.style,
        style_degree: req.style_degree,
        role: req.role,
        pitch: req.pitch,
        volume: req.volume,
        language: req.language,
        effect: req.effect,
        range: req.range,
        contour: req.contour,
        raw_ssml: req.raw_ssml,
        break_strength: req.break_strength,
        break_time: req.break_time,
        silence_type: req.silence_type,
        silence_value: req.silence_value,
        emphasis: req.emphasis,
        phoneme_alphabet: req.phoneme_alphabet,
        phoneme_value: req.phoneme_value,
        say_as_interpret_as: req.say_as_interpret_as,
        say_as_format: req.say_as_format,
        say_as_detail: req.say_as_detail,
        sub_alias: req.sub_alias,
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
