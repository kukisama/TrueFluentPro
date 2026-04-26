use async_trait::async_trait;
use reqwest::Client;
use serde::Deserialize;

use crate::models::*;
use super::registry::*;

/// Azure Speech TTS Provider — REST API + SSML
///
/// 端点: POST {region}.tts.speech.microsoft.com/cognitiveservices/v1
/// 认证: Ocp-Apim-Subscription-Key
/// 输入: SSML 文本
/// 输出: audio/wav bytes
pub struct AzureTtsProvider {
    client: Client,
    endpoint: AiEndpoint,
}

impl AzureTtsProvider {
    pub fn new(endpoint: AiEndpoint) -> Self {
        Self {
            client: Client::builder()
                .timeout(std::time::Duration::from_secs(120))
                .build()
                .unwrap_or_default(),
            endpoint,
        }
    }

    fn build_tts_url(&self) -> String {
        let region = &self.endpoint.speech_region;
        format!(
            "https://{region}.tts.speech.microsoft.com/cognitiveservices/v1"
        )
    }

    fn build_voices_url(&self) -> String {
        let region = &self.endpoint.speech_region;
        format!(
            "https://{region}.tts.speech.microsoft.com/cognitiveservices/voices/list"
        )
    }

    fn build_ssml(text: &str, voice: &str, format: &str) -> String {
        let rate = if format.contains("fast") { "+20%" } else { "+0%" };
        format!(
            r#"<speak version="1.0" xmlns="http://www.w3.org/2001/10/synthesis" xml:lang="en-US">
  <voice name="{voice}">
    <prosody rate="{rate}">{text}</prosody>
  </voice>
</speak>"#,
            voice = voice,
            rate = rate,
            text = xml_escape(text),
        )
    }
}

fn xml_escape(s: &str) -> String {
    s.replace('&', "&amp;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
        .replace('"', "&quot;")
        .replace('\'', "&apos;")
}

impl ProviderMeta for AzureTtsProvider {
    fn id(&self) -> &str { &self.endpoint.id }
    fn display_name(&self) -> &str { &self.endpoint.name }
    fn capabilities(&self) -> Vec<ProviderCapability> {
        vec![ProviderCapability::TextToSpeech]
    }
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "PascalCase")]
struct AzureVoice {
    short_name: String,
    display_name: String,
    locale: String,
    gender: String,
}

#[async_trait]
impl TextToSpeechSlot for AzureTtsProvider {
    async fn synthesize(
        &self,
        text: &str,
        voice: &str,
        format: &str,
    ) -> Result<Vec<u8>, ProviderError> {
        let url = self.build_tts_url();
        let key = &self.endpoint.speech_subscription_key;

        if key.is_empty() {
            return Err(ProviderError::NotConfigured(
                "Speech subscription key is empty".into(),
            ));
        }

        let ssml = Self::build_ssml(text, voice, format);

        // 默认输出 16kHz WAV
        let output_format = match format {
            "mp3" => "audio-16khz-128kbitrate-mono-mp3",
            "ogg" => "ogg-16khz-16bit-mono-opus",
            _ => "riff-16khz-16bit-mono-pcm",
        };

        let resp = self
            .client
            .post(&url)
            .header("Ocp-Apim-Subscription-Key", key)
            .header("Content-Type", "application/ssml+xml")
            .header("X-Microsoft-OutputFormat", output_format)
            .body(ssml)
            .send()
            .await
            .map_err(|e| ProviderError::Network(e.to_string()))?;

        if !resp.status().is_success() {
            let status = resp.status();
            let body = resp.text().await.unwrap_or_default();
            return Err(ProviderError::Network(format!(
                "TTS API returned {status}: {body}"
            )));
        }

        resp.bytes()
            .await
            .map(|b| b.to_vec())
            .map_err(|e| ProviderError::Internal(e.to_string()))
    }

    async fn list_voices(&self, locale: &str) -> Result<Vec<VoiceInfo>, ProviderError> {
        let url = self.build_voices_url();
        let key = &self.endpoint.speech_subscription_key;

        if key.is_empty() {
            return Err(ProviderError::NotConfigured(
                "Speech subscription key is empty".into(),
            ));
        }

        let resp = self
            .client
            .get(&url)
            .header("Ocp-Apim-Subscription-Key", key)
            .send()
            .await
            .map_err(|e| ProviderError::Network(e.to_string()))?;

        if !resp.status().is_success() {
            let status = resp.status();
            let body = resp.text().await.unwrap_or_default();
            return Err(ProviderError::Network(format!(
                "Voice list API returned {status}: {body}"
            )));
        }

        let voices: Vec<AzureVoice> = resp
            .json()
            .await
            .map_err(|e| ProviderError::Internal(e.to_string()))?;

        Ok(voices
            .into_iter()
            .filter(|v| locale.is_empty() || v.locale.starts_with(locale))
            .map(|v| VoiceInfo {
                id: v.short_name.clone(),
                name: v.display_name,
                locale: v.locale,
                gender: v.gender,
            })
            .collect())
    }
}
