//! Tencent Cloud Machine Translation adapter (TMT TextTranslate API v3).
//!
//! API reference: https://www.tencentcloud.com/document/api/551/15619
//!
//! Authentication: TC3-HMAC-SHA256 signature.
//!
//! Credentials:
//!   - `secret_id`: Tencent Cloud SecretId
//!   - `secret_key`: Tencent Cloud SecretKey
//!   - `region`: Region (default: "ap-guangzhou")

use crate::{TextTranslator, TextTranslateRequest, TextTranslateResponse, ProviderError};
use async_trait::async_trait;
use credential_broker::CredentialBroker;
use reqwest::Client;
use secrecy::ExposeSecret;
use serde::Deserialize;
use std::sync::Arc;
use tracing::{debug, error};

pub struct TencentTranslator {
    client: Client,
    credentials: Arc<CredentialBroker>,
    provider_id: String,
}

impl TencentTranslator {
    pub fn new(credentials: Arc<CredentialBroker>, provider_id: &str) -> Self {
        Self {
            client: Client::new(),
            credentials,
            provider_id: provider_id.to_string(),
        }
    }
}

#[async_trait]
impl TextTranslator for TencentTranslator {
    fn id(&self) -> &'static str {
        "tencent_translator"
    }

    async fn translate(
        &self,
        req: TextTranslateRequest,
    ) -> Result<TextTranslateResponse, ProviderError> {
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

        let host = "tmt.tencentcloudapi.com";
        let service = "tmt";
        let action = "TextTranslate";
        let version = "2018-03-21";

        let source_lang = if req.source_lang.is_empty() { "auto" } else { &req.source_lang };

        let body = serde_json::json!({
            "SourceText": req.text,
            "Source": source_lang,
            "Target": req.target_lang,
            "ProjectId": 0,
        });
        let payload = body.to_string();

        let timestamp = chrono::Utc::now().timestamp();
        let date = chrono::Utc::now().format("%Y-%m-%d").to_string();

        let auth_header = build_tc3_auth(
            secret_id.expose_secret(),
            secret_key.expose_secret(),
            host, service, &payload, timestamp, &date,
        );

        debug!(source = %source_lang, target = %req.target_lang, "Tencent Translator request");

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
            error!(status, body = %text, "Tencent Translator HTTP error");
            return Err(ProviderError::Upstream(format!("HTTP {status}: {text}")));
        }

        let api_resp: TmtResponse = resp.json().await
            .map_err(|e| ProviderError::Upstream(format!("response parse error: {e}")))?;

        if let Some(err) = api_resp.response.error {
            return Err(ProviderError::Upstream(format!("{}: {}", err.code, err.message)));
        }

        Ok(TextTranslateResponse {
            translation: api_resp.response.target_text.unwrap_or_default(),
            detected_source_lang: api_resp.response.source.map(|s| s.to_string()),
        })
    }
}

/// Build Tencent Cloud TC3-HMAC-SHA256 authorization header.
pub(crate) fn build_tc3_auth(
    secret_id: &str,
    secret_key: &str,
    host: &str,
    service: &str,
    payload: &str,
    timestamp: i64,
    date: &str,
) -> String {
    use hmac::{Hmac, Mac};
    use sha2::{Sha256, Digest};

    // Step 1: Canonical request
    let hashed_payload = hex::encode(Sha256::digest(payload.as_bytes()));
    let canonical_request = format!(
        "POST\n/\n\ncontent-type:application/json; charset=utf-8\nhost:{host}\n\ncontent-type;host\n{hashed_payload}"
    );

    // Step 2: String to sign
    let credential_scope = format!("{date}/{service}/tc3_request");
    let hashed_canonical = hex::encode(Sha256::digest(canonical_request.as_bytes()));
    let string_to_sign = format!(
        "TC3-HMAC-SHA256\n{timestamp}\n{credential_scope}\n{hashed_canonical}"
    );

    // Step 3: Signing key
    let secret_date = hmac_sha256(format!("TC3{secret_key}").as_bytes(), date.as_bytes());
    let secret_service = hmac_sha256(&secret_date, service.as_bytes());
    let secret_signing = hmac_sha256(&secret_service, b"tc3_request");

    // Step 4: Signature
    let signature = hex::encode(hmac_sha256(&secret_signing, string_to_sign.as_bytes()));

    format!(
        "TC3-HMAC-SHA256 Credential={secret_id}/{credential_scope}, SignedHeaders=content-type;host, Signature={signature}"
    )
}

fn hmac_sha256(key: &[u8], data: &[u8]) -> Vec<u8> {
    use hmac::{Hmac, Mac};
    use sha2::Sha256;
    type HmacSha256 = Hmac<Sha256>;

    let mut mac = HmacSha256::new_from_slice(key).expect("HMAC key length");
    mac.update(data);
    mac.finalize().into_bytes().to_vec()
}

// ═══ Wire types ═══

#[derive(Deserialize)]
struct TmtResponse {
    #[serde(rename = "Response")]
    response: TmtResponseInner,
}

#[derive(Deserialize)]
struct TmtResponseInner {
    #[serde(rename = "TargetText")]
    target_text: Option<String>,
    #[serde(rename = "Source")]
    source: Option<String>,
    #[serde(rename = "Error")]
    error: Option<TmtError>,
}

#[derive(Deserialize)]
struct TmtError {
    #[serde(rename = "Code")]
    code: String,
    #[serde(rename = "Message")]
    message: String,
}
