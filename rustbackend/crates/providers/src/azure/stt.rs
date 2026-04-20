//! Azure Cognitive Services Speech-to-Text adapter.
//!
//! Uses the Azure Speech REST API for batch/file transcription.
//! Endpoint: POST /speechtotext/v3.2-preview.2/transcriptions:transcribe
//!
//! Credentials:
//!   - `speech_endpoint`: Azure Speech endpoint (e.g. "https://eastus.api.cognitive.microsoft.com")
//!   - `speech_key`: Azure Speech subscription key

use crate::{ProviderError, SttProvider, SttRequest, SttResponse};
use async_trait::async_trait;
use credential_broker::CredentialBroker;
use reqwest::Client;
use std::sync::Arc;
use tracing::debug;

pub struct AzureSpeechStt {
    credentials: Arc<CredentialBroker>,
    provider_id: String,
    http: Client,
}

impl AzureSpeechStt {
    pub fn new(credentials: Arc<CredentialBroker>, provider_id: &str) -> Self {
        Self {
            credentials,
            provider_id: provider_id.to_string(),
            http: Client::new(),
        }
    }
}

#[async_trait]
impl SttProvider for AzureSpeechStt {
    fn id(&self) -> &'static str {
        "azure_speech_stt"
    }

    async fn transcribe(&self, req: SttRequest) -> Result<SttResponse, ProviderError> {
        use secrecy::ExposeSecret;

        let endpoint = self
            .credentials
            .get(&self.provider_id, "speech_endpoint")
            .await
            .map_err(|_| ProviderError::BadCredential)?
            .ok_or(ProviderError::BadCredential)?;

        let key = self
            .credentials
            .get(&self.provider_id, "speech_key")
            .await
            .map_err(|_| ProviderError::BadCredential)?
            .ok_or(ProviderError::BadCredential)?;

        let base_url = endpoint.expose_secret().trim_end_matches('/').to_string();

        // Use the simple recognition endpoint for short audio
        let lang = if req.language.is_empty() {
            "en-US"
        } else {
            &req.language
        };
        let url = format!(
            "{}/speechtotext/v3.2-preview.2/transcriptions:transcribe?api-version=2024-11-15",
            base_url
        );

        debug!(url = %url, language = %lang, audio_size = req.audio_data.len(), "Azure STT request");

        // Build multipart form
        let definition = serde_json::json!({
            "locales": [lang],
            "profanityFilterMode": req.profanity_filter.as_deref().unwrap_or("Masked"),
            "channels": [0],
        });

        let form = reqwest::multipart::Form::new()
            .text("definition", definition.to_string())
            .part(
                "audio",
                reqwest::multipart::Part::bytes(req.audio_data)
                    .file_name("audio.wav")
                    .mime_str("audio/wav")
                    .unwrap(),
            );

        let response = self
            .http
            .post(&url)
            .header("Ocp-Apim-Subscription-Key", key.expose_secret())
            .header("Accept", "application/json")
            .multipart(form)
            .send()
            .await
            .map_err(|e| ProviderError::Network(e.to_string()))?;

        if !response.status().is_success() {
            let status = response.status();
            let text = response.text().await.unwrap_or_default();
            return Err(ProviderError::Upstream(format!(
                "STT HTTP {status}: {text}"
            )));
        }

        let body: serde_json::Value = response
            .json()
            .await
            .map_err(|e| ProviderError::Upstream(format!("STT response parse error: {e}")))?;

        // Parse combined phrases
        let text = body["combinedPhrases"]
            .as_array()
            .and_then(|arr| arr.first())
            .and_then(|p| p["text"].as_str())
            .unwrap_or("")
            .to_string();

        let duration_ms = body["duration"]
            .as_str()
            .and_then(|d| parse_iso_duration_ms(d))
            .unwrap_or(0);

        Ok(SttResponse {
            text,
            language: lang.to_string(),
            duration_ms,
        })
    }
}

/// Parse ISO 8601 duration like "PT1.234S" to milliseconds.
fn parse_iso_duration_ms(duration: &str) -> Option<u64> {
    let s = duration.strip_prefix("PT")?.strip_suffix('S')?;
    let seconds: f64 = s.parse().ok()?;
    Some((seconds * 1000.0) as u64)
}
