//! Azure Cognitive Services TTS adapter — calls Azure Speech REST API.

use crate::{TtsProvider, TtsRequest, ProviderError};
use async_trait::async_trait;
use credential_broker::CredentialBroker;
use reqwest::Client;
use secrecy::ExposeSecret;
use std::sync::Arc;
use tracing::{debug, error};

/// Azure Cognitive Services Text-to-Speech adapter.
pub struct AzureSpeechTts {
    client: Client,
    credentials: Arc<CredentialBroker>,
    provider_id: String,
}

impl AzureSpeechTts {
    pub fn new(credentials: Arc<CredentialBroker>, provider_id: &str) -> Self {
        Self {
            client: Client::new(),
            credentials,
            provider_id: provider_id.to_string(),
        }
    }
}

#[async_trait]
impl TtsProvider for AzureSpeechTts {
    fn id(&self) -> &'static str {
        "azure_speech"
    }

    async fn synthesize(&self, req: TtsRequest) -> Result<Vec<u8>, ProviderError> {
        // Resolve credentials: speech_endpoint + speech_key
        let endpoint = self.credentials.get(&self.provider_id, "speech_endpoint").await
            .map_err(|e| ProviderError::Upstream(e.to_string()))?
            .ok_or(ProviderError::BadCredential)?;
        let api_key = self.credentials.get(&self.provider_id, "speech_key").await
            .map_err(|e| ProviderError::Upstream(e.to_string()))?
            .ok_or(ProviderError::BadCredential)?;

        let base = endpoint.expose_secret().trim_end_matches('/').to_string();
        let url = format!("{base}/cognitiveservices/v1");

        // Build SSML
        let voice = &req.voice_id;
        let rate = req.speed.map(|s| format!("{:.0}%", (s - 1.0) * 100.0))
            .unwrap_or_else(|| "0%".to_string());
        let text = escape_xml(&req.text);

        // Detect language from voice name (e.g. "en-US-AriaNeural" → "en-US")
        let lang = voice.split('-').take(2).collect::<Vec<_>>().join("-");

        let ssml = format!(
            r#"<speak version="1.0" xmlns="http://www.w3.org/2001/10/synthesis" xml:lang="{lang}"><voice name="{voice}"><prosody rate="{rate}">{text}</prosody></voice></speak>"#,
        );

        let output_format = req.output_format
            .unwrap_or_else(|| "audio-24khz-96kbitrate-mono-mp3".to_string());

        debug!(url = %url, voice = %voice, format = %output_format, "Azure TTS request");

        let resp = self.client.post(&url)
            .header("Ocp-Apim-Subscription-Key", api_key.expose_secret())
            .header("Content-Type", "application/ssml+xml")
            .header("X-Microsoft-OutputFormat", &output_format)
            .header("User-Agent", "TrueFluentPro-Gateway")
            .body(ssml)
            .send()
            .await
            .map_err(|e| ProviderError::Network(e.to_string()))?;

        if !resp.status().is_success() {
            let status = resp.status().as_u16();
            let text = resp.text().await.unwrap_or_default();
            if status == 429 {
                return Err(ProviderError::RateLimited);
            }
            error!(status, body = %text, "Azure TTS error");
            return Err(ProviderError::Upstream(format!("HTTP {status}: {text}")));
        }

        let audio_bytes = resp.bytes().await
            .map_err(|e| ProviderError::Network(format!("failed to read audio: {e}")))?;

        Ok(audio_bytes.to_vec())
    }
}

/// Escape XML special characters in text content.
fn escape_xml(s: &str) -> String {
    s.replace('&', "&amp;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
        .replace('"', "&quot;")
        .replace('\'', "&apos;")
}
