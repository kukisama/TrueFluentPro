//! Tencent Cloud TTS adapter (TextToVoice API).
//!
//! API reference: https://www.tencentcloud.com/document/product/1073/37995
//!
//! Authentication: TC3-HMAC-SHA256 signature (same as Tencent Translator).
//!
//! Credentials:
//!   - `secret_id`: Tencent Cloud SecretId
//!   - `secret_key`: Tencent Cloud SecretKey
//!   - `region`: Region (default: "ap-guangzhou")

use crate::{TtsProvider, TtsRequest, ProviderError};
use async_trait::async_trait;
use credential_broker::CredentialBroker;
use reqwest::Client;
use secrecy::ExposeSecret;
use serde::Deserialize;
use std::sync::Arc;
use tracing::{debug, error};

pub struct TencentTts {
    client: Client,
    credentials: Arc<CredentialBroker>,
    provider_id: String,
}

impl TencentTts {
    pub fn new(credentials: Arc<CredentialBroker>, provider_id: &str) -> Self {
        Self {
            client: Client::new(),
            credentials,
            provider_id: provider_id.to_string(),
        }
    }
}

#[async_trait]
impl TtsProvider for TencentTts {
    fn id(&self) -> &'static str {
        "tencent_tts"
    }

    async fn synthesize(&self, req: TtsRequest) -> Result<Vec<u8>, ProviderError> {
        let secret_id = self.credentials.get(&self.provider_id, "secret_id").await
            .map_err(|e| ProviderError::Upstream(e.to_string()))?
            .ok_or(ProviderError::BadCredential)?;
        let secret_key = self.credentials.get(&self.provider_id, "secret_key").await
            .map_err(|e| ProviderError::Upstream(e.to_string()))?
            .ok_or(ProviderError::BadCredential)?;
        let region = self.credentials.get(&self.provider_id, "region").await
            .map_err(|e| ProviderError::Upstream(e.to_string()))?
            .map(|s| s.expose_secret().to_string())
            .unwrap_or_else(|| "ap-guangzhou".to_string());

        let host = "tts.tencentcloudapi.com";
        let service = "tts";
        let action = "TextToVoice";
        let version = "2019-08-23";

        // Map voice_id to Tencent VoiceType integer; default to 101001 (generic Chinese female)
        let voice_type: i64 = req.voice_id.parse().unwrap_or(101001);

        // Map speed: Tencent accepts -2 to 6, default 0. Our speed is a float multiplier.
        let speed = req.speed.map(|s| ((s - 1.0) * 5.0).round() as i32).unwrap_or(0);

        // Map volume: Tencent accepts 0–10, default 5
        let volume = 5;

        // Codec: 1=WAV, 0=PCM
        let codec = match req.output_format.as_deref() {
            Some(f) if f.contains("wav") => "wav",
            Some(f) if f.contains("mp3") => "mp3",
            _ => "mp3",
        };

        let body = serde_json::json!({
            "Text": req.text,
            "SessionId": uuid::Uuid::new_v4().to_string(),
            "VoiceType": voice_type,
            "Speed": speed,
            "Volume": volume,
            "Codec": codec,
        });
        let payload = body.to_string();

        let timestamp = chrono::Utc::now().timestamp();
        let date = chrono::Utc::now().format("%Y-%m-%d").to_string();

        let auth_header = crate::tencent::translate::build_tc3_auth(
            secret_id.expose_secret(),
            secret_key.expose_secret(),
            host, service, &payload, timestamp, &date,
        );

        debug!(voice_type, codec, text_len = req.text.len(), "Tencent TTS request");

        let resp = self.client.post(format!("https://{host}"))
            .header("Authorization", &auth_header)
            .header("Content-Type", "application/json; charset=utf-8")
            .header("Host", host)
            .header("X-TC-Action", action)
            .header("X-TC-Version", version)
            .header("X-TC-Timestamp", timestamp.to_string())
            .header("X-TC-Region", &region)
            .body(payload)
            .send()
            .await
            .map_err(|e| ProviderError::Network(e.to_string()))?;

        if !resp.status().is_success() {
            let status = resp.status().as_u16();
            let text = resp.text().await.unwrap_or_default();
            error!(status, body = %text, "Tencent TTS HTTP error");
            return Err(ProviderError::Upstream(format!("HTTP {status}: {text}")));
        }

        let api_resp: TtsResponse = resp.json().await
            .map_err(|e| ProviderError::Upstream(format!("response parse error: {e}")))?;

        if let Some(err) = api_resp.response.error {
            return Err(ProviderError::Upstream(format!("{}: {}", err.code, err.message)));
        }

        // Tencent TTS returns base64-encoded audio in the Audio field
        let audio_b64 = api_resp.response.audio
            .ok_or_else(|| ProviderError::Upstream("no audio in response".into()))?;

        use base64::Engine;
        let audio_bytes = base64::engine::general_purpose::STANDARD
            .decode(&audio_b64)
            .map_err(|e| ProviderError::Upstream(format!("audio base64 decode error: {e}")))?;

        Ok(audio_bytes)
    }
}

// ═══ Wire types ═══

#[derive(Deserialize)]
struct TtsResponse {
    #[serde(rename = "Response")]
    response: TtsResponseInner,
}

#[derive(Deserialize)]
struct TtsResponseInner {
    #[serde(rename = "Audio")]
    audio: Option<String>,
    #[serde(rename = "Error")]
    error: Option<TtsError>,
}

#[derive(Deserialize)]
struct TtsError {
    #[serde(rename = "Code")]
    code: String,
    #[serde(rename = "Message")]
    message: String,
}
