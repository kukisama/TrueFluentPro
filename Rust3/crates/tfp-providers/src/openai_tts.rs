//! OpenAI TTS Provider — synthesizes speech via /v1/audio/speech endpoint.
//!
//! Supports OpenAI API, Azure OpenAI, and compatible APIs.
//! Uses candidate URL pattern for endpoint resolution.

use async_trait::async_trait;
use serde::Deserialize;

use tfp_core::{AiEndpoint, EndpointType, ProviderError, VoiceInfo};

use crate::traits::{ProviderCapability, ProviderMeta, TextToSpeechSlot};

/// OpenAI TTS provider.
pub struct OpenAiTtsProvider {
    client: reqwest::Client,
    endpoint: AiEndpoint,
}

impl OpenAiTtsProvider {
    pub fn new(endpoint: AiEndpoint) -> Self {
        Self {
            client: reqwest::Client::builder()
                .timeout(std::time::Duration::from_secs(120))
                .build()
                .unwrap_or_default(),
            endpoint,
        }
    }

    fn build_candidate_urls(&self) -> Vec<String> {
        let base = self.endpoint.url.trim_end_matches('/');
        let model = self.tts_model();

        match self.endpoint.endpoint_type {
            EndpointType::AzureOpenAi | EndpointType::ApiManagementGateway => {
                let api_version = self.endpoint.api_version.as_deref().unwrap_or("2024-06-01");
                vec![
                    format!("{base}/openai/deployments/{model}/audio/speech?api-version={api_version}"),
                    format!("{base}/openai/audio/speech?api-version={api_version}"),
                ]
            }
            _ => {
                vec![
                    format!("{base}/v1/audio/speech"),
                    format!("{base}/audio/speech"),
                ]
            }
        }
    }

    fn auth_headers(&self) -> Vec<(String, String)> {
        let key = &self.endpoint.api_key;
        match self.endpoint.endpoint_type {
            EndpointType::AzureOpenAi | EndpointType::ApiManagementGateway => {
                vec![("api-key".into(), key.clone())]
            }
            _ => {
                vec![("Authorization".into(), format!("Bearer {key}"))]
            }
        }
    }

    fn tts_model(&self) -> String {
        // Use tts-specific model if configured, otherwise default
        self.endpoint.models.iter()
            .find(|m| m.model_id.contains("tts"))
            .map(|m| m.model_id.clone())
            .unwrap_or_else(|| "tts-1".to_string())
    }
}

impl ProviderMeta for OpenAiTtsProvider {
    fn id(&self) -> &str {
        &self.endpoint.id
    }
    fn display_name(&self) -> &str {
        &self.endpoint.name
    }
    fn capabilities(&self) -> Vec<ProviderCapability> {
        vec![ProviderCapability::TextToSpeech]
    }
}

/// Standard OpenAI voices.
const OPENAI_VOICES: &[(&str, &str)] = &[
    ("alloy", "Alloy"),
    ("ash", "Ash"),
    ("ballad", "Ballad"),
    ("coral", "Coral"),
    ("echo", "Echo"),
    ("fable", "Fable"),
    ("nova", "Nova"),
    ("onyx", "Onyx"),
    ("sage", "Sage"),
    ("shimmer", "Shimmer"),
];

#[async_trait]
impl TextToSpeechSlot for OpenAiTtsProvider {
    async fn synthesize(
        &self,
        text: &str,
        voice: &str,
        format: &str,
    ) -> Result<Vec<u8>, ProviderError> {
        let api_key = &self.endpoint.api_key;
        if api_key.is_empty() {
            return Err(ProviderError::NotConfigured("API key is empty".into()));
        }

        let urls = self.build_candidate_urls();
        let model = self.tts_model();

        let response_format = match format {
            "mp3" => "mp3",
            "ogg" | "opus" => "opus",
            "flac" => "flac",
            "aac" => "aac",
            _ => "wav",
        };

        let body = serde_json::json!({
            "model": model,
            "input": text,
            "voice": voice,
            "response_format": response_format,
        });

        let mut last_error = ProviderError::NotConfigured("No candidate URLs".into());

        for url in &urls {
            let mut req = self.client.post(url)
                .header("Content-Type", "application/json")
                .body(body.to_string());

            for (k, v) in self.auth_headers() {
                req = req.header(&k, &v);
            }

            let resp = match req.send().await {
                Ok(r) => r,
                Err(e) => {
                    last_error = ProviderError::Network(e.to_string());
                    continue;
                }
            };

            let status = resp.status();
            if status.as_u16() == 404 || status.as_u16() == 405 {
                last_error = ProviderError::Network(format!("URL not found: {url}"));
                continue;
            }
            if status.as_u16() == 401 || status.as_u16() == 403 {
                let body = resp.text().await.unwrap_or_default();
                return Err(ProviderError::Auth(format!("{status}: {body}")));
            }
            if status.as_u16() == 429 {
                return Err(ProviderError::RateLimited { retry_after_ms: 0 });
            }
            if !status.is_success() {
                let body = resp.text().await.unwrap_or_default();
                return Err(ProviderError::Network(format!("{status}: {body}")));
            }

            let bytes = resp.bytes().await
                .map_err(|e| ProviderError::Internal(e.to_string()))?;
            return Ok(bytes.to_vec());
        }

        Err(last_error)
    }

    async fn synthesize_multi_speaker(
        &self,
        text: &str,
        speakers: &[(String, String)],
        format: &str,
    ) -> Result<Vec<u8>, ProviderError> {
        // OpenAI TTS doesn't natively support multi-speaker SSML.
        // Use the first speaker's voice as default.
        let voice = speakers.first()
            .map(|(_, v)| v.as_str())
            .unwrap_or("alloy");
        self.synthesize(text, voice, format).await
    }

    async fn list_voices(&self, _locale: &str) -> Result<Vec<VoiceInfo>, ProviderError> {
        // OpenAI doesn't have a voices list API; return static list
        Ok(OPENAI_VOICES
            .iter()
            .map(|(id, name)| VoiceInfo {
                id: id.to_string(),
                name: name.to_string(),
                locale: "multilingual".to_string(),
                gender: "neutral".to_string(),
            })
            .collect())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::test_helpers::factories;

    #[test]
    fn test_provider_meta() {
        let ep = factories::openai_compatible_endpoint("tts-ep", "TTS Provider");
        let p = OpenAiTtsProvider::new(ep);
        assert_eq!(p.id(), "tts-ep");
        assert_eq!(p.capabilities(), vec![ProviderCapability::TextToSpeech]);
    }

    #[test]
    fn test_candidate_urls_openai() {
        let ep = factories::openai_compatible_endpoint("tts-ep", "TTS");
        let p = OpenAiTtsProvider::new(ep);
        let urls = p.build_candidate_urls();
        assert_eq!(urls.len(), 2);
        assert!(urls[0].contains("/v1/audio/speech"));
        assert!(urls[1].contains("/audio/speech"));
    }

    #[test]
    fn test_candidate_urls_azure() {
        let ep = factories::azure_openai_endpoint("az-tts", "Azure TTS");
        let p = OpenAiTtsProvider::new(ep);
        let urls = p.build_candidate_urls();
        assert_eq!(urls.len(), 2);
        assert!(urls[0].contains("/openai/deployments/"));
        assert!(urls[0].contains("audio/speech"));
    }

    #[test]
    fn test_list_voices_static() {
        let ep = factories::openai_compatible_endpoint("tts-ep", "TTS");
        let p = OpenAiTtsProvider::new(ep);
        let rt = tokio::runtime::Builder::new_current_thread().enable_all().build().unwrap();
        let voices = rt.block_on(p.list_voices("")).unwrap();
        assert_eq!(voices.len(), 10);
        assert!(voices.iter().any(|v| v.id == "alloy"));
        assert!(voices.iter().any(|v| v.id == "nova"));
    }

    #[test]
    fn test_tts_model_default() {
        let ep = factories::openai_compatible_endpoint("tts-ep", "TTS");
        let p = OpenAiTtsProvider::new(ep);
        assert_eq!(p.tts_model(), "tts-1");
    }
}
