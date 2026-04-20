//! Alibaba Cloud Machine Translation adapter (alimt TranslateGeneral API).
//!
//! API reference: https://next.api.aliyun.com/api/alimt/2018-10-12/TranslateGeneral
//!
//! Authentication: Alibaba Cloud v1 signature (HMAC-SHA1).
//!
//! Credentials:
//!   - `access_key_id`: Alibaba Cloud AccessKeyId
//!   - `access_key_secret`: Alibaba Cloud AccessKeySecret
//!   - `region`: Region (default: "cn-hangzhou")

use crate::{TextTranslator, TextTranslateRequest, TextTranslateResponse, ProviderError};
use async_trait::async_trait;
use credential_broker::CredentialBroker;
use reqwest::Client;
use secrecy::ExposeSecret;
use serde::Deserialize;
use std::sync::Arc;
use tracing::{debug, error};

pub struct AliTranslator {
    client: Client,
    credentials: Arc<CredentialBroker>,
    provider_id: String,
}

impl AliTranslator {
    pub fn new(credentials: Arc<CredentialBroker>, provider_id: &str) -> Self {
        Self {
            client: Client::new(),
            credentials,
            provider_id: provider_id.to_string(),
        }
    }
}

#[async_trait]
impl TextTranslator for AliTranslator {
    fn id(&self) -> &'static str {
        "ali_translator"
    }

    async fn translate(
        &self,
        req: TextTranslateRequest,
    ) -> Result<TextTranslateResponse, ProviderError> {
        let access_key_id = self.credentials.get(&self.provider_id, "access_key_id").await
            .map_err(|e| ProviderError::Upstream(e.to_string()))?
            .ok_or(ProviderError::BadCredential)?;
        let access_key_secret = self.credentials.get(&self.provider_id, "access_key_secret").await
            .map_err(|e| ProviderError::Upstream(e.to_string()))?
            .ok_or(ProviderError::BadCredential)?;

        let source_lang = if req.source_lang.is_empty() { "auto" } else { &req.source_lang };

        // Use the OpenAPI POP style: POST with form-encoded params
        let timestamp = chrono::Utc::now().format("%Y-%m-%dT%H:%M:%SZ").to_string();
        let nonce = uuid::Uuid::new_v4().to_string();

        let mut params = vec![
            ("Action", "TranslateGeneral".to_string()),
            ("Format", "JSON".to_string()),
            ("Version", "2018-10-12".to_string()),
            ("AccessKeyId", access_key_id.expose_secret().to_string()),
            ("SignatureMethod", "HMAC-SHA1".to_string()),
            ("SignatureVersion", "1.0".to_string()),
            ("SignatureNonce", nonce),
            ("Timestamp", timestamp),
            ("FormatType", "text".to_string()),
            ("SourceLanguage", source_lang.to_string()),
            ("TargetLanguage", req.target_lang.clone()),
            ("SourceText", req.text.clone()),
            ("Scene", "general".to_string()),
        ];

        // Sort params alphabetically for signature
        params.sort_by(|a, b| a.0.cmp(&b.0));

        // Build canonical query string
        let query_string: String = params.iter()
            .map(|(k, v)| format!("{}={}", percent_encode(k), percent_encode(v)))
            .collect::<Vec<_>>()
            .join("&");

        // String to sign
        let string_to_sign = format!("POST&{}&{}", percent_encode("/"), percent_encode(&query_string));

        // HMAC-SHA1 signature
        let signing_key = format!("{}&", access_key_secret.expose_secret());
        let signature = hmac_sha1(signing_key.as_bytes(), string_to_sign.as_bytes());

        let mut params_with_sig = params.clone();
        params_with_sig.push(("Signature", signature));

        debug!(source = %source_lang, target = %req.target_lang, "Alibaba Translator request");

        let resp = self.client.post("https://mt.aliyuncs.com/")
            .form(&params_with_sig)
            .send()
            .await
            .map_err(|e| ProviderError::Network(e.to_string()))?;

        if !resp.status().is_success() {
            let status = resp.status().as_u16();
            let text = resp.text().await.unwrap_or_default();
            error!(status, body = %text, "Alibaba Translator HTTP error");
            return Err(ProviderError::Upstream(format!("HTTP {status}: {text}")));
        }

        let api_resp: AlimtResponse = resp.json().await
            .map_err(|e| ProviderError::Upstream(format!("response parse error: {e}")))?;

        if api_resp.code != Some(200) && api_resp.code != Some(0) {
            let msg = api_resp.message.unwrap_or_else(|| "unknown error".into());
            return Err(ProviderError::Upstream(msg));
        }

        let data = api_resp.data
            .ok_or_else(|| ProviderError::Upstream("no data in response".into()))?;

        Ok(TextTranslateResponse {
            translation: data.translated,
            detected_source_lang: data.detected_language,
        })
    }
}

/// RFC 3986 percent-encoding.
fn percent_encode(s: &str) -> String {
    urlencoding::encode(s)
        .replace('+', "%20")
        .replace('*', "%2A")
        .replace("%7E", "~")
}

/// HMAC-SHA1 → base64.
fn hmac_sha1(key: &[u8], data: &[u8]) -> String {
    use hmac::{Hmac, Mac};
    use sha1::Sha1;
    type HmacSha1 = Hmac<Sha1>;

    let mut mac = HmacSha1::new_from_slice(key).expect("HMAC key length");
    mac.update(data);
    let result = mac.finalize().into_bytes();
    use base64::Engine;
    base64::engine::general_purpose::STANDARD.encode(result)
}

// ═══ Wire types ═══

#[derive(Deserialize)]
struct AlimtResponse {
    #[serde(rename = "Code")]
    code: Option<i64>,
    #[serde(rename = "Message")]
    message: Option<String>,
    #[serde(rename = "Data")]
    data: Option<AlimtData>,
}

#[derive(Deserialize)]
struct AlimtData {
    #[serde(rename = "Translated")]
    translated: String,
    #[serde(rename = "DetectedLanguage")]
    detected_language: Option<String>,
}
