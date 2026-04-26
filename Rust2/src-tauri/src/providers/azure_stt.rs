use async_trait::async_trait;
use reqwest::Client;
use serde::Deserialize;

use crate::models::*;
use super::registry::*;

/// Azure Speech STT Provider — Fast Transcription REST API
///
/// 端点: POST {region}.api.cognitive.microsoft.com/speechtotext/transcriptions:transcribe
/// 认证: Ocp-Apim-Subscription-Key
/// 输入: multipart/form-data (WAV/MP3)
/// 输出: TranscriptSegment[]
pub struct AzureSttProvider {
    client: Client,
    endpoint: AiEndpoint,
}

impl AzureSttProvider {
    pub fn new(endpoint: AiEndpoint) -> Self {
        Self {
            client: Client::builder()
                .timeout(std::time::Duration::from_secs(300))
                .build()
                .unwrap_or_default(),
            endpoint,
        }
    }

    fn build_url(&self) -> String {
        let region = &self.endpoint.speech_region;
        format!(
            "https://{region}.api.cognitive.microsoft.com/speechtotext/transcriptions:transcribe?api-version=2024-11-15"
        )
    }
}

impl ProviderMeta for AzureSttProvider {
    fn id(&self) -> &str { &self.endpoint.id }
    fn display_name(&self) -> &str { &self.endpoint.name }
    fn capabilities(&self) -> Vec<ProviderCapability> {
        vec![ProviderCapability::SpeechToText]
    }
}

#[derive(Debug, Deserialize)]
struct FastTranscriptionResponse {
    #[serde(default)]
    phrases: Vec<FastTranscriptionPhrase>,
}

#[derive(Debug, Deserialize)]
struct FastTranscriptionPhrase {
    #[serde(default)]
    text: String,
    #[serde(rename = "offsetMilliseconds", default)]
    offset_ms: u64,
    #[serde(rename = "durationMilliseconds", default)]
    duration_ms: u64,
    #[serde(default)]
    confidence: Option<f64>,
    #[serde(default)]
    speaker: Option<u32>,
}

#[async_trait]
impl SpeechToTextSlot for AzureSttProvider {
    async fn transcribe(
        &self,
        audio_data: &[u8],
        lang: &str,
    ) -> Result<Vec<TranscriptSegment>, ProviderError> {
        let url = self.build_url();
        let key = &self.endpoint.speech_subscription_key;

        if key.is_empty() {
            return Err(ProviderError::NotConfigured(
                "Speech subscription key is empty".into(),
            ));
        }

        // 构建 definition JSON
        let definition = serde_json::json!({
            "locales": [lang],
            "profanityFilterMode": "Masked",
            "channels": [0]
        });

        // multipart: audio + definition
        let audio_part = reqwest::multipart::Part::bytes(audio_data.to_vec())
            .file_name("audio.wav")
            .mime_str("audio/wav")
            .map_err(|e| ProviderError::Internal(e.to_string()))?;

        let def_part = reqwest::multipart::Part::text(definition.to_string())
            .mime_str("application/json")
            .map_err(|e| ProviderError::Internal(e.to_string()))?;

        let form = reqwest::multipart::Form::new()
            .part("audio", audio_part)
            .part("definition", def_part);

        let resp = self
            .client
            .post(&url)
            .header("Ocp-Apim-Subscription-Key", key)
            .multipart(form)
            .send()
            .await
            .map_err(|e| ProviderError::Network(e.to_string()))?;

        if !resp.status().is_success() {
            let status = resp.status();
            let body = resp.text().await.unwrap_or_default();
            return Err(ProviderError::Network(format!(
                "STT API returned {status}: {body}"
            )));
        }

        let result: FastTranscriptionResponse = resp
            .json()
            .await
            .map_err(|e| ProviderError::Internal(format!("Failed to parse STT response: {e}")))?;

        Ok(result
            .phrases
            .into_iter()
            .map(|p| TranscriptSegment {
                text: p.text,
                start_ms: p.offset_ms,
                end_ms: p.offset_ms + p.duration_ms,
                confidence: p.confidence.unwrap_or(0.95),
                speaker: p.speaker.map(|s| format!("Speaker {s}")),
            })
            .collect())
    }
}
