//! OpenAI Whisper STT Provider — transcribes audio via /v1/audio/transcriptions endpoint.
//!
//! Supports OpenAI API, Azure OpenAI API, and compatible APIs.
//! Uses candidate URL pattern for multi-endpoint resolution.

use async_trait::async_trait;
use serde::Deserialize;

use tfp_core::{AiEndpoint, EndpointType, ProviderError, TranscriptSegment};

use crate::traits::{ProviderCapability, ProviderMeta, SpeechToTextSlot};

/// OpenAI Whisper STT provider.
pub struct OpenAiSttProvider {
    client: reqwest::Client,
    endpoint: AiEndpoint,
}

impl OpenAiSttProvider {
    pub fn new(endpoint: AiEndpoint) -> Self {
        Self {
            client: reqwest::Client::builder()
                .timeout(std::time::Duration::from_secs(300))
                .build()
                .unwrap_or_default(),
            endpoint,
        }
    }

    /// Build candidate URLs in priority order.
    fn build_candidate_urls(&self) -> Vec<String> {
        let base = self.endpoint.url.trim_end_matches('/');
        let model = self.endpoint.models.first()
            .map(|m| m.model_id.as_str())
            .unwrap_or("whisper-1");

        match self.endpoint.endpoint_type {
            EndpointType::AzureOpenAi | EndpointType::ApiManagementGateway => {
                let api_version = self.endpoint.api_version.as_deref().unwrap_or("2024-06-01");
                vec![
                    format!("{base}/openai/deployments/{model}/audio/transcriptions?api-version={api_version}"),
                    format!("{base}/openai/audio/transcriptions?api-version={api_version}"),
                ]
            }
            _ => {
                vec![
                    format!("{base}/v1/audio/transcriptions"),
                    format!("{base}/audio/transcriptions"),
                ]
            }
        }
    }

    /// Build auth headers.
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
}

impl ProviderMeta for OpenAiSttProvider {
    fn id(&self) -> &str {
        &self.endpoint.id
    }
    fn display_name(&self) -> &str {
        &self.endpoint.name
    }
    fn capabilities(&self) -> Vec<ProviderCapability> {
        vec![ProviderCapability::SpeechToText]
    }
}

#[derive(Debug, Deserialize)]
struct WhisperSegment {
    #[serde(default)]
    text: String,
    #[serde(default)]
    start: f64,
    #[serde(default)]
    end: f64,
}

#[derive(Debug, Deserialize)]
struct WhisperVerboseResponse {
    #[serde(default)]
    text: String,
    #[serde(default)]
    segments: Vec<WhisperSegment>,
    #[serde(default)]
    duration: f64,
}

#[async_trait]
impl SpeechToTextSlot for OpenAiSttProvider {
    async fn transcribe(
        &self,
        audio_data: &[u8],
        lang: &str,
    ) -> Result<Vec<TranscriptSegment>, ProviderError> {
        let api_key = &self.endpoint.api_key;
        if api_key.is_empty() {
            return Err(ProviderError::NotConfigured("API key is empty".into()));
        }

        let urls = self.build_candidate_urls();
        let model = self.endpoint.models.first()
            .map(|m| m.model_id.clone())
            .unwrap_or_else(|| "whisper-1".to_string());

        let mut last_error = ProviderError::NotConfigured("No candidate URLs".into());

        for url in &urls {
            let audio_part = reqwest::multipart::Part::bytes(audio_data.to_vec())
                .file_name("audio.wav")
                .mime_str("audio/wav")
                .map_err(|e| ProviderError::Internal(e.to_string()))?;

            let mut form = reqwest::multipart::Form::new()
                .part("file", audio_part)
                .text("model", model.clone())
                .text("response_format", "verbose_json")
                .text("timestamp_granularities[]", "segment");

            if !lang.is_empty() {
                form = form.text("language", lang.to_string());
            }

            let mut req = self.client.post(url).multipart(form);
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

            let result: WhisperVerboseResponse = resp
                .json()
                .await
                .map_err(|e| ProviderError::Internal(format!("Parse error: {e}")))?;

            return Ok(parse_whisper_response(result));
        }

        Err(last_error)
    }
}

fn parse_whisper_response(resp: WhisperVerboseResponse) -> Vec<TranscriptSegment> {
    if resp.segments.is_empty() {
        // Fallback: single segment from full text
        if resp.text.trim().is_empty() {
            return vec![];
        }
        return vec![TranscriptSegment {
            text: resp.text,
            start_ms: 0,
            end_ms: (resp.duration * 1000.0) as u64,
            confidence: 0.9,
            speaker: None,
        }];
    }

    resp.segments
        .into_iter()
        .filter(|s| !s.text.trim().is_empty())
        .map(|s| TranscriptSegment {
            text: s.text.trim().to_string(),
            start_ms: (s.start * 1000.0) as u64,
            end_ms: (s.end * 1000.0) as u64,
            confidence: 0.9,
            speaker: None,
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::test_helpers::factories;

    #[test]
    fn test_provider_meta() {
        let ep = factories::openai_compatible_endpoint("stt-ep", "STT Provider");
        let p = OpenAiSttProvider::new(ep);
        assert_eq!(p.id(), "stt-ep");
        assert_eq!(p.capabilities(), vec![ProviderCapability::SpeechToText]);
    }

    #[test]
    fn test_candidate_urls_openai() {
        let ep = factories::openai_compatible_endpoint("stt-ep", "STT");
        let p = OpenAiSttProvider::new(ep);
        let urls = p.build_candidate_urls();
        assert_eq!(urls.len(), 2);
        assert!(urls[0].contains("/v1/audio/transcriptions"));
        assert!(urls[1].contains("/audio/transcriptions"));
    }

    #[test]
    fn test_candidate_urls_azure() {
        let ep = factories::azure_openai_endpoint("az-stt", "Azure STT");
        let p = OpenAiSttProvider::new(ep);
        let urls = p.build_candidate_urls();
        assert_eq!(urls.len(), 2);
        assert!(urls[0].contains("/openai/deployments/"));
        assert!(urls[0].contains("audio/transcriptions"));
        assert!(urls[0].contains("api-version="));
    }

    #[test]
    fn test_parse_whisper_verbose_response() {
        let resp = WhisperVerboseResponse {
            text: "Hello world".into(),
            segments: vec![
                WhisperSegment { text: "Hello".into(), start: 0.0, end: 1.5 },
                WhisperSegment { text: "world".into(), start: 1.5, end: 3.0 },
            ],
            duration: 3.0,
        };
        let segments = parse_whisper_response(resp);
        assert_eq!(segments.len(), 2);
        assert_eq!(segments[0].text, "Hello");
        assert_eq!(segments[0].start_ms, 0);
        assert_eq!(segments[0].end_ms, 1500);
        assert_eq!(segments[1].text, "world");
        assert_eq!(segments[1].start_ms, 1500);
        assert_eq!(segments[1].end_ms, 3000);
    }

    #[test]
    fn test_parse_whisper_no_segments_fallback() {
        let resp = WhisperVerboseResponse {
            text: "Single line".into(),
            segments: vec![],
            duration: 5.0,
        };
        let segments = parse_whisper_response(resp);
        assert_eq!(segments.len(), 1);
        assert_eq!(segments[0].text, "Single line");
        assert_eq!(segments[0].end_ms, 5000);
    }
}
