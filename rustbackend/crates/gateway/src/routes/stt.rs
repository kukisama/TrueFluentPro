//! Speech-to-Text API endpoint — proxies to configured SttProvider.

use crate::error::ApiError;
use crate::state::AppState;
use axum::{
    body::Bytes, extract::State, routing::post, Extension, Router,
};
use domain::auth::UserContext;
use providers::SttRequest;
use serde::Deserialize;
use serde_json::{json, Value};
use std::sync::Arc;
use tracing::info;
use axum::response::Json;

pub fn routes() -> Router<Arc<AppState>> {
    Router::new().route("/v1/speech/transcribe", post(transcribe))
}

#[derive(Debug, Deserialize)]
struct TranscribeQuery {
    #[serde(default = "default_lang")]
    language: String,
    #[serde(default)]
    profanity_filter: Option<String>,
    #[serde(default)]
    provider_id: Option<String>,
}

fn default_lang() -> String {
    "en-US".to_string()
}

async fn transcribe(
    State(state): State<Arc<AppState>>,
    Extension(ctx): Extension<UserContext>,
    axum::extract::Query(params): axum::extract::Query<TranscribeQuery>,
    body: Bytes,
) -> Result<Json<Value>, ApiError> {
    let registry = state.providers.read().await;
    let provider = registry
        .get_stt(params.provider_id.as_deref())
        .map_err(|_| {
            ApiError::BadRequest(
                "No STT provider configured. Ask admin to set up a provider.".into(),
            )
        })?;

    info!(
        user = %ctx.user_id,
        audio_size = body.len(),
        language = %params.language,
        "STT request"
    );

    // Check billing quota
    if state.billing.is_enabled() {
        match state
            .billing
            .check_quota(&ctx.user_id, "speech_minute")
            .await
        {
            Ok(billing::QuotaStatus::Exceeded { used, limit }) => {
                return Err(ApiError::TooManyRequests(format!(
                    "Speech quota exceeded: used {used}/{limit} minutes this month"
                )));
            }
            Err(e) => {
                tracing::warn!(error = %e, "Billing check failed, allowing request");
            }
            _ => {}
        }
    }

    let stt_req = SttRequest {
        audio_data: body.to_vec(),
        language: params.language,
        profanity_filter: params.profanity_filter,
    };

    let result = provider
        .transcribe(stt_req)
        .await
        .map_err(|e| map_provider_error(e))?;

    // Record usage (1 minute per request as estimate)
    let _ = state
        .billing
        .record_usage(&ctx.user_id, "speech.stt", "speech_minute", 1)
        .await;
    let _ = state
        .storage
        .write_audit_log(&ctx.user_id, "speech.transcribe", None, None)
        .await;

    Ok(Json(json!({
        "text": result.text,
        "language": result.language,
        "duration_ms": result.duration_ms,
    })))
}

fn map_provider_error(e: providers::ProviderError) -> ApiError {
    match e {
        providers::ProviderError::RateLimited => {
            ApiError::TooManyRequests("Provider rate limited".into())
        }
        providers::ProviderError::BadCredential => {
            ApiError::Internal("Provider credentials not configured".into())
        }
        providers::ProviderError::UnsupportedCapability => {
            ApiError::BadRequest("No provider available for this capability".into())
        }
        providers::ProviderError::ProviderNotFound(id) => {
            ApiError::NotFound(format!("Provider '{id}' not found or not enabled"))
        }
        providers::ProviderError::Network(m) => {
            ApiError::Internal(format!("Network error: {m}"))
        }
        providers::ProviderError::Upstream(m) => {
            ApiError::Internal(format!("Upstream error: {m}"))
        }
    }
}
