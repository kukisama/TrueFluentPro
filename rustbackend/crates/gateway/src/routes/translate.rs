//! Text translation API endpoint — proxies to configured TextTranslator.

use crate::state::AppState;
use crate::error::ApiError;
use domain::auth::UserContext;
use providers::TextTranslateRequest;
use std::sync::Arc;
use axum::{Router, routing::post, extract::State, Extension, Json};
use serde::Deserialize;
use serde_json::{json, Value};
use tracing::info;

pub fn routes() -> Router<Arc<AppState>> {
    Router::new()
        .route("/v1/translate", post(translate))
}

#[derive(Debug, Deserialize)]
struct TranslateRequestBody {
    text: String,
    #[serde(default)]
    source_lang: Option<String>,
    target_lang: String,
    /// Text type: "plain" (default) or "html".
    #[serde(default)]
    text_type: Option<String>,
    /// Profanity handling: "NoAction", "Marked", or "Deleted".
    #[serde(default)]
    profanity_action: Option<String>,
    /// Profanity marker: "Asterisk" or "Tag".
    #[serde(default)]
    profanity_marker: Option<String>,
    /// Custom translation category.
    #[serde(default)]
    category: Option<String>,
    /// Include alignment info.
    #[serde(default)]
    include_alignment: Option<bool>,
    /// Include sentence length info.
    #[serde(default)]
    include_sentence_length: Option<bool>,
    /// Optional: specify which provider to use.
    #[serde(default)]
    provider_id: Option<String>,
}

async fn translate(
    State(state): State<Arc<AppState>>,
    Extension(ctx): Extension<UserContext>,
    Json(req): Json<TranslateRequestBody>,
) -> Result<Json<Value>, ApiError> {
    let registry = state.providers.read().await;
    let provider = registry.get_translator(req.provider_id.as_deref())
        .map_err(|_| ApiError::BadRequest("No translator provider configured. Ask admin to set up a provider.".into()))?;

    info!(
        user = %ctx.user_id,
        source = %req.source_lang.as_deref().unwrap_or("auto"),
        target = %req.target_lang,
        text_len = req.text.len(),
        "Translate request"
    );

    let translate_req = TextTranslateRequest {
        text: req.text,
        source_lang: req.source_lang.unwrap_or_default(),
        target_lang: req.target_lang,
        tenant_id: ctx.tenant_id.clone(),
        text_type: req.text_type,
        profanity_action: req.profanity_action,
        profanity_marker: req.profanity_marker,
        category: req.category,
        include_alignment: req.include_alignment,
        include_sentence_length: req.include_sentence_length,
    };

    let result = provider.translate(translate_req).await
        .map_err(|e| map_provider_error(e))?;

    Ok(Json(json!({
        "translation": result.translation,
        "detected_source_lang": result.detected_source_lang,
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
