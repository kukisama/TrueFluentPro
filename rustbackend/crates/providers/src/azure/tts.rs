//! Azure Cognitive Services TTS adapter — calls Azure Speech REST API.
//!
//! Supports the full range of SSML elements:
//! - express-as (style, role, styleDegree)
//! - prosody (rate, pitch, volume, range, contour)
//! - lang tag (multilingual voices)
//! - audio effects
//! - break, silence, emphasis, phoneme, say-as, sub tags
//!
//! SSML reference: https://learn.microsoft.com/en-us/azure/ai-services/speech-service/speech-synthesis-markup-voice

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
        let ssml = if req.raw_ssml.unwrap_or(false) {
            // User provides raw SSML body — wrap minimally
            req.text.clone()
        } else {
            build_ssml(&req)
        };

        let output_format = req.output_format
            .unwrap_or_else(|| "audio-24khz-96kbitrate-mono-mp3".to_string());

        debug!(url = %url, voice = %req.voice_id, format = %output_format, "Azure TTS request");

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

/// Build full SSML from a TtsRequest, supporting all advanced options.
///
/// Produces SSML like:
/// ```xml
/// <speak version="1.0" xmlns="http://www.w3.org/2001/10/synthesis"
///        xmlns:mstts="http://www.w3.org/2001/mstts" xml:lang="en-US">
///   <voice name="en-US-AriaNeural" effect="eq_car">
///     <mstts:express-as style="cheerful" styledegree="1.5" role="Girl">
///       <lang xml:lang="zh-CN">
///         <mstts:silence type="Leading-exact" value="200ms"/>
///         <break strength="strong"/>
///         <prosody rate="+20%" pitch="+5%" volume="loud" range="+10%"
///                  contour="(0%,+20Hz)(100%,+5Hz)">
///           <emphasis level="strong">
///             <phoneme alphabet="ipa" ph="...">
///               <say-as interpret-as="date" format="mdy">
///                 <sub alias="World Wide Web">WWW</sub>
///                 Hello world
///               </say-as>
///             </phoneme>
///           </emphasis>
///         </prosody>
///       </lang>
///     </mstts:express-as>
///   </voice>
/// </speak>
/// ```
fn build_ssml(req: &TtsRequest) -> String {
    let voice = &req.voice_id;
    let text = escape_xml(&req.text);

    // Detect language from voice name (e.g. "en-US-AriaNeural" → "en-US")
    let lang = voice.split('-').take(2).collect::<Vec<_>>().join("-");

    // ── Build from innermost to outermost ──

    // Start with the text content
    let mut content = text;

    // Wrap in <sub> if sub_alias is set
    if let Some(ref alias) = req.sub_alias {
        content = format!(r#"<sub alias="{}">{}</sub>"#, escape_xml(alias), content);
    }

    // Wrap in <say-as> if say_as_interpret_as is set
    if let Some(ref interpret_as) = req.say_as_interpret_as {
        let mut attrs = format!(r#"interpret-as="{}""#, escape_xml(interpret_as));
        if let Some(ref fmt) = req.say_as_format {
            attrs.push_str(&format!(r#" format="{}""#, escape_xml(fmt)));
        }
        if let Some(ref detail) = req.say_as_detail {
            attrs.push_str(&format!(r#" detail="{}""#, escape_xml(detail)));
        }
        content = format!("<say-as {attrs}>{content}</say-as>");
    }

    // Wrap in <phoneme> if phoneme_alphabet and phoneme_value are set
    if let (Some(alphabet), Some(ph)) = (&req.phoneme_alphabet, &req.phoneme_value) {
        content = format!(
            r#"<phoneme alphabet="{}" ph="{}">{}</phoneme>"#,
            escape_xml(alphabet), escape_xml(ph), content
        );
    }

    // Wrap in <emphasis> if emphasis level is set
    if let Some(ref level) = req.emphasis {
        content = format!(r#"<emphasis level="{}">{}</emphasis>"#, escape_xml(level), content);
    }

    // Wrap in <prosody> if any prosody attributes are set
    let rate_str = req.speed.map(|s| {
        if (s - 1.0).abs() < 0.001 { "0%".to_string() }
        else { format!("{:+.0}%", (s - 1.0) * 100.0) }
    });
    let has_prosody = rate_str.is_some()
        || req.pitch.is_some()
        || req.volume.is_some()
        || req.range.is_some()
        || req.contour.is_some();

    if has_prosody {
        let mut attrs = Vec::new();
        if let Some(ref rate) = rate_str {
            attrs.push(format!(r#"rate="{}""#, rate));
        }
        if let Some(ref pitch) = req.pitch {
            attrs.push(format!(r#"pitch="{}""#, escape_xml(pitch)));
        }
        if let Some(ref vol) = req.volume {
            attrs.push(format!(r#"volume="{}""#, escape_xml(vol)));
        }
        if let Some(ref range) = req.range {
            attrs.push(format!(r#"range="{}""#, escape_xml(range)));
        }
        if let Some(ref contour) = req.contour {
            attrs.push(format!(r#"contour="{}""#, escape_xml(contour)));
        }
        content = format!("<prosody {}>{}</prosody>", attrs.join(" "), content);
    }

    // Prepend <break> if break_strength or break_time is set
    if let Some(ref strength) = req.break_strength {
        content = format!(r#"<break strength="{}"/>{}"#, escape_xml(strength), content);
    } else if let Some(ref time) = req.break_time {
        content = format!(r#"<break time="{}"/>{}"#, escape_xml(time), content);
    }

    // Wrap in <lang> if language override is set (for multilingual voices)
    if let Some(ref language) = req.language {
        content = format!(r#"<lang xml:lang="{}">{}</lang>"#, escape_xml(language), content);
    }

    // Prepend <mstts:silence> if silence_type + silence_value are set
    if let (Some(stype), Some(svalue)) = (&req.silence_type, &req.silence_value) {
        content = format!(
            r#"<mstts:silence type="{}" value="{}"/>{}"#,
            escape_xml(stype), escape_xml(svalue), content
        );
    }

    // Wrap in <mstts:express-as> if style or role is set
    let has_express_as = req.style.is_some() || req.role.is_some();
    if has_express_as {
        let mut attrs = Vec::new();
        if let Some(ref style) = req.style {
            attrs.push(format!(r#"style="{}""#, escape_xml(style)));
        }
        if let Some(ref degree) = req.style_degree {
            attrs.push(format!(r#"styledegree="{:.2}""#, degree));
        }
        if let Some(ref role) = req.role {
            attrs.push(format!(r#"role="{}""#, escape_xml(role)));
        }
        content = format!(
            "<mstts:express-as {}>{}</mstts:express-as>",
            attrs.join(" "), content
        );
    }

    // Build <voice> element with optional effect attribute
    let effect_attr = req.effect.as_ref()
        .map(|e| format!(r#" effect="{}""#, escape_xml(e)))
        .unwrap_or_default();

    format!(
        r#"<speak version="1.0" xmlns="http://www.w3.org/2001/10/synthesis" xmlns:mstts="http://www.w3.org/2001/mstts" xml:lang="{lang}"><voice name="{voice}"{effect_attr}>{content}</voice></speak>"#,
    )
}

/// Escape XML special characters in text content.
fn escape_xml(s: &str) -> String {
    s.replace('&', "&amp;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
        .replace('"', "&quot;")
        .replace('\'', "&apos;")
}
