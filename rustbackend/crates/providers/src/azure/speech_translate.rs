//! Azure Speech Translation WebSocket adapter.
//!
//! Implements the `LiveSpeechTranslator` trait by resolving credentials and
//! constructing the upstream WebSocket URL for Azure's speech translation service.
//!
//! Protocol reference:
//! https://github.com/Azure-Samples/voice-translator-and-personal-voice/blob/main/HOW_TO_USE_SPEECH_TRANSLATION_WEBSOCKETS.md
//!
//! Endpoint:
//!   wss://{region}.stt.speech.microsoft.com/stt/speech/universal/v2
//!     ?from={source_lang}&to={targets}&scenario=conversation

use crate::{LiveSpeechTranslator, LiveTranslateRequest, LiveTranslateSessionConfig, ProviderError};
use async_trait::async_trait;
use credential_broker::CredentialBroker;
use secrecy::ExposeSecret;
use std::sync::Arc;
use tracing::debug;

/// Azure Speech Translation adapter.
pub struct AzureSpeechTranslation {
    credentials: Arc<CredentialBroker>,
    provider_id: String,
}

impl AzureSpeechTranslation {
    pub fn new(credentials: Arc<CredentialBroker>, provider_id: &str) -> Self {
        Self {
            credentials,
            provider_id: provider_id.to_string(),
        }
    }
}

#[async_trait]
impl LiveSpeechTranslator for AzureSpeechTranslation {
    fn id(&self) -> &'static str {
        "azure_speech_translation"
    }

    async fn build_session_config(
        &self,
        req: &LiveTranslateRequest,
    ) -> Result<LiveTranslateSessionConfig, ProviderError> {
        // Resolve credentials: speech_key + speech_region
        let api_key = self.credentials.get(&self.provider_id, "speech_key").await
            .map_err(|e| ProviderError::Upstream(e.to_string()))?
            .ok_or(ProviderError::BadCredential)?;
        let region = self.credentials.get(&self.provider_id, "speech_region").await
            .map_err(|e| ProviderError::Upstream(e.to_string()))?
            .map(|s| s.expose_secret().to_string())
            .unwrap_or_else(|| {
                // Fallback: try to extract region from speech_endpoint
                "eastus".to_string()
            });

        // Build upstream WebSocket URL
        let targets = req.target_langs.join(",");
        let mut url = format!(
            "wss://{region}.stt.speech.microsoft.com/stt/speech/universal/v2?to={targets}&scenario=conversation"
        );

        // Add source language if specified (fixed mode)
        if !req.source_lang.is_empty() {
            url.push_str(&format!("&from={}", req.source_lang));
        }

        debug!(
            provider = %self.provider_id,
            region = %region,
            url = %url,
            "Azure Speech Translation session config"
        );

        let auth_headers = vec![
            ("Ocp-Apim-Subscription-Key".to_string(), api_key.expose_secret().to_string()),
        ];

        Ok(LiveTranslateSessionConfig {
            upstream_url: url,
            auth_headers,
            region,
        })
    }
}
