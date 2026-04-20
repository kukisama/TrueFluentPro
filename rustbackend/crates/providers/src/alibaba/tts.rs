//! Alibaba Cloud DashScope TTS adapter (CosyVoice).
//!
//! API reference: https://help.aliyun.com/document_detail/2635056.html
//!
//! Endpoint: POST https://dashscope.aliyuncs.com/api/v1/services/aigc/text2audio/generation
//! Authentication: Bearer token (DashScope API key).
//!
//! Credentials:
//!   - `api_key`: DashScope API key
//!   - `default_model`: (optional) Model name, default "cosyvoice-v1"

use crate::{TtsProvider, TtsRequest, ProviderError};
use async_trait::async_trait;
use credential_broker::CredentialBroker;
use reqwest::Client;
use secrecy::ExposeSecret;
use std::sync::Arc;
use tracing::{debug, error};

pub struct AliTts {
    client: Client,
    credentials: Arc<CredentialBroker>,
    provider_id: String,
}

impl AliTts {
    pub fn new(credentials: Arc<CredentialBroker>, provider_id: &str) -> Self {
        Self {
            client: Client::new(),
            credentials,
            provider_id: provider_id.to_string(),
        }
    }
}

#[async_trait]
impl TtsProvider for AliTts {
    fn id(&self) -> &'static str {
        "ali_tts"
    }

    async fn synthesize(&self, req: TtsRequest) -> Result<Vec<u8>, ProviderError> {
        let api_key = self.credentials.get(&self.provider_id, "api_key").await
            .map_err(|e| ProviderError::Upstream(e.to_string()))?
            .ok_or(ProviderError::BadCredential)?;

        let default_model = self.credentials.get(&self.provider_id, "default_model").await
            .map_err(|e| ProviderError::Upstream(e.to_string()))?
            .map(|s| s.expose_secret().to_string())
            .unwrap_or_else(|| "cosyvoice-v1".to_string());

        let url = "https://dashscope.aliyuncs.com/api/v1/services/aigc/text2audio/generation";

        // Map voice_id (e.g. "longxiaochun") to DashScope voice
        let voice = if req.voice_id.is_empty() {
            "longxiaochun".to_string()
        } else {
            req.voice_id.clone()
        };

        let format = match req.output_format.as_deref() {
            Some(f) if f.contains("wav") => "wav",
            Some(f) if f.contains("pcm") => "pcm",
            _ => "mp3",
        };

        let body = serde_json::json!({
            "model": default_model,
            "input": {
                "text": req.text,
            },
            "parameters": {
                "voice": voice,
                "format": format,
                "sample_rate": 24000,
                "volume": 50,
                "rate": req.speed.map(|s| ((s - 1.0) * 200.0) as i32).unwrap_or(0),
            }
        });

        debug!(model = %default_model, voice = %voice, text_len = req.text.len(), "Alibaba DashScope TTS request");

        let resp = self.client.post(url)
            .header("Authorization", format!("Bearer {}", api_key.expose_secret()))
            .header("Content-Type", "application/json")
            .header("X-DashScope-Async", "disable")
            .json(&body)
            .send()
            .await
            .map_err(|e| ProviderError::Network(e.to_string()))?;

        // Check if the response is audio (binary) or JSON (error)
        let content_type = resp.headers()
            .get("content-type")
            .and_then(|v| v.to_str().ok())
            .unwrap_or("")
            .to_string();

        if content_type.contains("audio/") || content_type.contains("application/octet-stream") {
            // Direct binary audio response
            let bytes = resp.bytes().await
                .map_err(|e| ProviderError::Network(e.to_string()))?;
            return Ok(bytes.to_vec());
        }

        // JSON response — may contain base64 audio or an error
        if !resp.status().is_success() {
            let status = resp.status().as_u16();
            let text = resp.text().await.unwrap_or_default();
            error!(status, body = %text, "Alibaba TTS HTTP error");
            return Err(ProviderError::Upstream(format!("HTTP {status}: {text}")));
        }

        let api_resp: serde_json::Value = resp.json().await
            .map_err(|e| ProviderError::Upstream(format!("response parse error: {e}")))?;

        // Check for error
        if let Some(code) = api_resp.get("code").and_then(|v| v.as_str()) {
            if code != "200" && code != "0" {
                let msg = api_resp.get("message").and_then(|v| v.as_str()).unwrap_or("unknown error");
                return Err(ProviderError::Upstream(format!("{code}: {msg}")));
            }
        }

        // Try to extract audio URL and download
        if let Some(url) = api_resp["output"]["audio_url"].as_str() {
            let audio_resp = self.client.get(url).send().await
                .map_err(|e| ProviderError::Network(e.to_string()))?;
            let bytes = audio_resp.bytes().await
                .map_err(|e| ProviderError::Network(e.to_string()))?;
            return Ok(bytes.to_vec());
        }

        // Try base64 audio
        if let Some(b64) = api_resp["output"]["audio"].as_str() {
            use base64::Engine;
            let bytes = base64::engine::general_purpose::STANDARD
                .decode(b64)
                .map_err(|e| ProviderError::Upstream(format!("audio base64 decode error: {e}")))?;
            return Ok(bytes);
        }

        Err(ProviderError::Upstream("no audio in DashScope response".into()))
    }
}
