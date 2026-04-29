use async_trait::async_trait;
use serde::Deserialize;

use tfp_core::{AiEndpoint, ProviderError, VoiceInfo};

use crate::traits::{ProviderCapability, ProviderMeta, TextToSpeechSlot};

/// Azure Speech TTS Provider — REST API + SSML
///
/// Endpoint: POST {region}.tts.speech.microsoft.com/cognitiveservices/v1
/// Auth: Ocp-Apim-Subscription-Key
pub struct AzureTtsProvider {
    client: reqwest::Client,
    endpoint: AiEndpoint,
}

impl AzureTtsProvider {
    pub fn new(endpoint: AiEndpoint) -> Self {
        Self {
            client: reqwest::Client::builder()
                .timeout(std::time::Duration::from_secs(120))
                .build()
                .unwrap_or_default(),
            endpoint,
        }
    }

    fn build_tts_url(&self) -> String {
        let region = &self.endpoint.speech_region;
        format!("https://{region}.tts.speech.microsoft.com/cognitiveservices/v1")
    }

    fn build_voices_url(&self) -> String {
        let region = &self.endpoint.speech_region;
        format!("https://{region}.tts.speech.microsoft.com/cognitiveservices/voices/list")
    }

    pub(crate) fn build_ssml(text: &str, voice: &str, format: &str) -> String {
        let rate = if format.contains("fast") {
            "+20%"
        } else {
            "+0%"
        };
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

    fn build_multi_speaker_ssml(text: &str, speakers: &[(String, String)]) -> String {
        let mut ssml = String::from(
            r#"<speak version="1.0" xmlns="http://www.w3.org/2001/10/synthesis" xml:lang="zh-CN">"#,
        );
        ssml.push('\n');

        let default_voice = speakers
            .first()
            .map(|(_, v)| v.as_str())
            .unwrap_or("zh-CN-XiaoxiaoMultilingualNeural");

        for line in text.lines() {
            let trimmed = line.trim();
            if trimmed.is_empty() {
                continue;
            }

            let mut matched_voice = default_voice;
            let mut content = trimmed;

            for (label, voice) in speakers {
                let prefix_colon = format!("{label}: ");
                let prefix_cn_colon = format!("{label}\u{ff1a}");
                if let Some(stripped) = trimmed.strip_prefix(&prefix_colon) {
                    matched_voice = voice;
                    content = stripped;
                    break;
                }
                if let Some(stripped) = trimmed.strip_prefix(&prefix_cn_colon) {
                    matched_voice = voice;
                    content = stripped;
                    break;
                }
            }

            ssml.push_str(&format!(
                "  <voice name=\"{voice}\">\n    <prosody rate=\"+0%\">{text}</prosody>\n  </voice>\n",
                voice = matched_voice,
                text = xml_escape(content),
            ));
        }

        ssml.push_str("</speak>");
        ssml
    }


    /// Build SSML with express-as style/role support (mstts namespace).
    pub(crate) fn build_styled_ssml(
        text: &str,
        voice: &str,
        style: Option<&str>,
        style_degree: Option<f32>,
        role: Option<&str>,
        rate: Option<&str>,
        pitch: Option<&str>,
    ) -> String {
        let rate_val = rate.unwrap_or("+0%");
        let mut ssml = format!(
            r#"<speak version="1.0" xmlns="http://www.w3.org/2001/10/synthesis" xmlns:mstts="http://www.w3.org/2001/mstts" xml:lang="en-US">
  <voice name="{voice}">"#
        );

        // Add express-as if style is provided
        if let Some(style_name) = style {
            ssml.push_str("\n    <mstts:express-as style=\"");
            ssml.push_str(style_name);
            ssml.push('"');
            if let Some(degree) = style_degree {
                ssml.push_str(&format!(" styledegree=\"{degree:.1}\""));
            }
            if let Some(role_name) = role {
                ssml.push_str(&format!(" role=\"{role_name}\""));
            }
            ssml.push('>');
            ssml.push_str(&format!("\n      <prosody rate=\"{rate_val}\""));
            if let Some(p) = pitch {
                ssml.push_str(&format!(" pitch=\"{p}\""));
            }
            ssml.push('>');
            ssml.push_str(&xml_escape(text));
            ssml.push_str("</prosody>\n    </mstts:express-as>");
        } else {
            ssml.push_str(&format!("\n    <prosody rate=\"{rate_val}\""));
            if let Some(p) = pitch {
                ssml.push_str(&format!(" pitch=\"{p}\""));
            }
            ssml.push('>');
            ssml.push_str(&xml_escape(text));
            ssml.push_str("</prosody>");
        }

        ssml.push_str("\n  </voice>\n</speak>");
        ssml
    }

    async fn send_ssml(
        &self,
        url: &str,
        key: &str,
        ssml: &str,
        format: &str,
    ) -> Result<Vec<u8>, ProviderError> {
        let output_format = match format {
            "mp3" => "audio-16khz-128kbitrate-mono-mp3",
            "ogg" => "ogg-16khz-16bit-mono-opus",
            _ => "riff-16khz-16bit-mono-pcm",
        };

        let resp = self
            .client
            .post(url)
            .header("Ocp-Apim-Subscription-Key", key)
            .header("Content-Type", "application/ssml+xml")
            .header("X-Microsoft-OutputFormat", output_format)
            .body(ssml.to_string())
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
}

fn xml_escape(s: &str) -> String {
    s.replace('&', "&amp;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
        .replace('"', "&quot;")
        .replace('\'', "&apos;")
}

impl ProviderMeta for AzureTtsProvider {
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
        self.send_ssml(&url, key, &ssml, format).await
    }

    async fn synthesize_multi_speaker(
        &self,
        text: &str,
        speakers: &[(String, String)],
        format: &str,
    ) -> Result<Vec<u8>, ProviderError> {
        let url = self.build_tts_url();
        let key = &self.endpoint.speech_subscription_key;

        if key.is_empty() {
            return Err(ProviderError::NotConfigured(
                "Speech subscription key is empty".into(),
            ));
        }

        let ssml = Self::build_multi_speaker_ssml(text, speakers);
        self.send_ssml(&url, key, &ssml, format).await
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

#[cfg(test)]
mod tests {
    use super::*;
    use crate::test_helpers::factories;

    #[test]
    fn test_build_ssml() {
        let ssml = AzureTtsProvider::build_ssml("Hello world", "en-US-JennyNeural", "wav");
        assert!(ssml.contains("en-US-JennyNeural"));
        assert!(ssml.contains("Hello world"));
        assert!(ssml.contains("<speak"));
        assert!(ssml.contains("</speak>"));
    }

    #[test]
    fn test_build_ssml_escapes_xml() {
        let ssml = AzureTtsProvider::build_ssml("A & B < C", "en-US-JennyNeural", "wav");
        assert!(ssml.contains("A &amp; B &lt; C"));
    }

    #[test]
    fn test_provider_meta() {
        let p = AzureTtsProvider::new(factories::speech_endpoint("tts-ep", "TTS EP"));
        assert_eq!(p.id(), "tts-ep");
        assert_eq!(p.capabilities(), vec![ProviderCapability::TextToSpeech]);
    }

    #[test]
    fn test_build_tts_and_voices_url() {
        let p = AzureTtsProvider::new(factories::speech_endpoint("tts-ep", "TTS EP"));
        let tts_url = p.build_tts_url();
        assert!(tts_url.contains("eastus"));
        assert_eq!(
            tts_url,
            "https://eastus.tts.speech.microsoft.com/cognitiveservices/v1"
        );

        let voices_url = p.build_voices_url();
        assert!(voices_url.contains("eastus"));
        assert_eq!(
            voices_url,
            "https://eastus.tts.speech.microsoft.com/cognitiveservices/voices/list"
        );
    }

    #[test]
    fn test_build_ssml_fast_rate() {
        // format contains "fast" → rate "+20%"
        let ssml = AzureTtsProvider::build_ssml("Hello", "en-US-JennyNeural", "fast-wav");
        assert!(ssml.contains("+20%"));
        assert!(!ssml.contains("+0%"));

        // format without "fast" → rate "+0%"
        let ssml = AzureTtsProvider::build_ssml("Hello", "en-US-JennyNeural", "mp3");
        assert!(ssml.contains("+0%"));
        assert!(!ssml.contains("+20%"));
    }

    #[test]
    fn test_build_multi_speaker_ssml() {
        let speakers = vec![
            ("Alice".to_string(), "voice-a".to_string()),
            ("Bob".to_string(), "voice-b".to_string()),
        ];
        let text = "Alice: Hello\nBob\u{ff1a}\u{4f60}\u{597d}\n\nUnknown line";
        let ssml = AzureTtsProvider::build_multi_speaker_ssml(text, &speakers);

        // Alice: Hello → voice-a
        assert!(ssml.contains(r#"<voice name="voice-a">"#));
        assert!(ssml.contains("Hello"));

        // Bob：你好 (Chinese colon) → voice-b
        assert!(ssml.contains(r#"<voice name="voice-b">"#));
        assert!(ssml.contains("\u{4f60}\u{597d}"));

        // Unknown line → default voice (voice-a)
        assert!(ssml.contains("Unknown line"));

        // empty lines are skipped
        let lines: Vec<&str> = ssml.lines().collect();
        assert!(!lines.iter().any(|l| l.trim().is_empty() && l.contains("voice")));

        // well-formed
        assert!(ssml.starts_with("<speak"));
        assert!(ssml.ends_with("</speak>"));
    }

    #[test]
    fn test_xml_escape_all_chars() {
        let input = r#"A & B < C > D "E" 'F'"#;
        let escaped = xml_escape(input);
        assert_eq!(escaped, "A &amp; B &lt; C &gt; D &quot;E&quot; &apos;F&apos;");

        // no-op for clean string
        assert_eq!(xml_escape("hello"), "hello");
    }

    #[test]
    fn test_build_styled_ssml_with_style() {
        let ssml = AzureTtsProvider::build_styled_ssml(
            "Hello",
            "en-US-JennyNeural",
            Some("cheerful"),
            Some(1.5),
            None,
            None,
            None,
        );
        assert!(ssml.contains("mstts:express-as"));
        assert!(ssml.contains(r#"style="cheerful""#));
        assert!(ssml.contains(r#"styledegree="1.5""#));
        assert!(ssml.contains("xmlns:mstts"));
    }

    #[test]
    fn test_build_styled_ssml_with_role() {
        let ssml = AzureTtsProvider::build_styled_ssml(
            "Hi there",
            "zh-CN-XiaoxiaoNeural",
            Some("chat"),
            None,
            Some("Girl"),
            Some("+10%"),
            Some("+5Hz"),
        );
        assert!(ssml.contains(r#"role="Girl""#));
        assert!(ssml.contains(r#"rate="+10%""#));
        assert!(ssml.contains(r#"pitch="+5Hz""#));
    }

    #[test]
    fn test_build_styled_ssml_no_style() {
        let ssml = AzureTtsProvider::build_styled_ssml(
            "Plain text",
            "en-US-GuyNeural",
            None,
            None,
            None,
            Some("+20%"),
            None,
        );
        assert!(!ssml.contains("mstts:express-as"));
        assert!(ssml.contains(r#"rate="+20%""#));
        assert!(ssml.contains("Plain text"));
    }
}
