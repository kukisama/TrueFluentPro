//! Azure Translator adapter — calls Azure Translator REST API v3.0.
//!
//! API reference: https://learn.microsoft.com/en-us/azure/ai-services/translator/reference/v3-0-translate

use crate::{TextTranslator, TextTranslateRequest, TextTranslateResponse, ProviderError};
use async_trait::async_trait;
use credential_broker::CredentialBroker;
use reqwest::Client;
use secrecy::ExposeSecret;
use serde::{Deserialize, Serialize};
use std::sync::Arc;
use tracing::{debug, error};

/// Azure Translator adapter.
pub struct AzureTranslator {
    client: Client,
    credentials: Arc<CredentialBroker>,
    provider_id: String,
}

impl AzureTranslator {
    pub fn new(credentials: Arc<CredentialBroker>, provider_id: &str) -> Self {
        Self {
            client: Client::new(),
            credentials,
            provider_id: provider_id.to_string(),
        }
    }
}

#[async_trait]
impl TextTranslator for AzureTranslator {
    fn id(&self) -> &'static str {
        "azure_translator"
    }

    async fn translate(
        &self,
        req: TextTranslateRequest,
    ) -> Result<TextTranslateResponse, ProviderError> {
        // Resolve credentials: translator_key + translator_region
        // Endpoint defaults to the global endpoint if not specified.
        let api_key = self.credentials.get(&self.provider_id, "translator_key").await
            .map_err(|e| ProviderError::Upstream(e.to_string()))?
            .ok_or(ProviderError::BadCredential)?;
        let region = self.credentials.get(&self.provider_id, "translator_region").await
            .map_err(|e| ProviderError::Upstream(e.to_string()))?
            .map(|s| s.expose_secret().to_string())
            .unwrap_or_else(|| "global".to_string());
        let endpoint = self.credentials.get(&self.provider_id, "translator_endpoint").await
            .map_err(|e| ProviderError::Upstream(e.to_string()))?
            .map(|s| s.expose_secret().trim_end_matches('/').to_string())
            .unwrap_or_else(|| "https://api.cognitive.microsofttranslator.com".to_string());

        // Build URL with query params
        let mut url = format!("{endpoint}/translate?api-version=3.0&to={}", req.target_lang);
        if !req.source_lang.is_empty() {
            url.push_str(&format!("&from={}", req.source_lang));
        }

        debug!(url = %url, source = %req.source_lang, target = %req.target_lang, "Azure Translator request");

        // Azure Translator expects a JSON array of objects with "Text" field
        let body = vec![TranslatorRequestBody { text: req.text }];

        let mut request_builder = self.client.post(&url)
            .header("Ocp-Apim-Subscription-Key", api_key.expose_secret())
            .header("Content-Type", "application/json");

        // Add region header if not "global"
        if region != "global" {
            request_builder = request_builder.header("Ocp-Apim-Subscription-Region", &region);
        }

        let resp = request_builder
            .json(&body)
            .send()
            .await
            .map_err(|e| ProviderError::Network(e.to_string()))?;

        if !resp.status().is_success() {
            let status = resp.status().as_u16();
            let text = resp.text().await.unwrap_or_default();
            if status == 429 {
                return Err(ProviderError::RateLimited);
            }
            error!(status, body = %text, "Azure Translator error");
            return Err(ProviderError::Upstream(format!("HTTP {status}: {text}")));
        }

        // Parse response: array of translation results
        let results: Vec<TranslatorResponse> = resp.json().await
            .map_err(|e| ProviderError::Upstream(format!("failed to parse response: {e}")))?;

        let first = results.into_iter().next()
            .ok_or_else(|| ProviderError::Upstream("empty response from translator".to_string()))?;

        let translation = first.translations.into_iter().next()
            .ok_or_else(|| ProviderError::Upstream("no translations in response".to_string()))?;

        let detected_lang = first.detected_language.map(|d| d.language);

        Ok(TextTranslateResponse {
            translation: translation.text,
            detected_source_lang: detected_lang,
        })
    }
}

// ═══ Azure Translator wire types ═══

#[derive(Serialize)]
struct TranslatorRequestBody {
    #[serde(rename = "Text")]
    text: String,
}

#[derive(Deserialize)]
struct TranslatorResponse {
    translations: Vec<TranslationItem>,
    #[serde(rename = "detectedLanguage")]
    detected_language: Option<DetectedLanguage>,
}

#[derive(Deserialize)]
struct TranslationItem {
    text: String,
}

#[derive(Deserialize)]
struct DetectedLanguage {
    language: String,
}
