use async_trait::async_trait;

use tfp_core::{
    AiEndpoint, ChatMessage, CompletionRequest, LanguageInfo, ModelCapability, ProviderError,
    TranslateRequest, TranslateResponse,
};

use crate::openai_chat::OpenAiChatProvider;
use crate::traits::{AiCompletionSlot, ProviderCapability, ProviderMeta, TextTranslationSlot};

/// AI-based text translation via Chat Completion API.
///
/// Uses system prompts to guide an LLM for translation, giving any
/// OpenAI-compatible endpoint TextTranslation capability.
pub struct OpenAiTranslationProvider {
    inner: OpenAiChatProvider,
    endpoint: AiEndpoint,
}

impl OpenAiTranslationProvider {
    pub fn new(endpoint: AiEndpoint) -> Self {
        Self {
            inner: OpenAiChatProvider::new(endpoint.clone()),
            endpoint,
        }
    }

    fn pick_model(&self) -> String {
        self.endpoint
            .models
            .iter()
            .find(|m| m.capabilities.contains(&ModelCapability::Text))
            .map(|m| m.model_id.clone())
            .unwrap_or_else(|| "gpt-4.1-mini".to_string())
    }
}

impl ProviderMeta for OpenAiTranslationProvider {
    fn id(&self) -> &str {
        &self.endpoint.id
    }
    fn display_name(&self) -> &str {
        &self.endpoint.name
    }
    fn capabilities(&self) -> Vec<ProviderCapability> {
        vec![ProviderCapability::TextTranslation]
    }
}

#[async_trait]
impl TextTranslationSlot for OpenAiTranslationProvider {
    async fn translate(
        &self,
        request: &TranslateRequest,
    ) -> Result<TranslateResponse, ProviderError> {
        let system_prompt = format!(
            "You are a professional translator. Translate the following text from {} to {}. \
             Return ONLY the translated text, nothing else. Do not add explanations or notes.",
            request.source_lang, request.target_lang,
        );

        let comp_request = CompletionRequest {
            messages: vec![
                ChatMessage {
                    role: "system".into(),
                    content: serde_json::Value::String(system_prompt),
                },
                ChatMessage {
                    role: "user".into(),
                    content: serde_json::Value::String(request.text.clone()),
                },
            ],
            model: self.pick_model(),
            temperature: Some(0.3),
            max_tokens: Some(4096),
            endpoint_id: self.endpoint.id.clone(),
        };

        let resp = self.inner.complete(&comp_request).await?;

        Ok(TranslateResponse {
            translated_text: resp.content,
            source_lang: request.source_lang.clone(),
            target_lang: request.target_lang.clone(),
            confidence: None,
            provider: self.endpoint.name.clone(),
        })
    }

    async fn detect_language(&self, text: &str) -> Result<String, ProviderError> {
        let comp_request = CompletionRequest {
            messages: vec![
                ChatMessage {
                    role: "system".into(),
                    content: serde_json::Value::String(
                        "Detect the language of the following text. Return ONLY the ISO 639-1 \
                         language code (e.g., 'en', 'zh', 'ja', 'ko'). Nothing else."
                            .into(),
                    ),
                },
                ChatMessage {
                    role: "user".into(),
                    content: serde_json::Value::String(text.to_string()),
                },
            ],
            model: self.pick_model(),
            temperature: Some(0.0),
            max_tokens: Some(10),
            endpoint_id: self.endpoint.id.clone(),
        };

        let resp = self.inner.complete(&comp_request).await?;
        Ok(resp.content.trim().to_lowercase())
    }

    fn supported_languages(&self) -> Vec<LanguageInfo> {
        // AI translation is not limited to a fixed set of languages
        vec![]
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use tfp_core::EndpointType;

    fn test_endpoint() -> AiEndpoint {
        AiEndpoint {
            id: "trans-ep".into(),
            name: "Translation EP".into(),
            endpoint_type: EndpointType::OpenAiCompatible,
            url: "https://api.openai.com".into(),
            api_key: "sk-xxx".into(),
            api_version: None,
            region: None,
            models: vec![],
            enabled: true,
            auth_header_mode: "bearer".into(),
            auth_mode: "api_key".into(),
            azure_tenant_id: String::new(),
            azure_client_id: String::new(),
            speech_subscription_key: String::new(),
            speech_region: String::new(),
            speech_endpoint: String::new(),
        }
    }

    #[test]
    fn test_provider_meta() {
        let p = OpenAiTranslationProvider::new(test_endpoint());
        assert_eq!(p.id(), "trans-ep");
        assert_eq!(p.display_name(), "Translation EP");
        assert_eq!(
            p.capabilities(),
            vec![ProviderCapability::TextTranslation]
        );
    }

    #[test]
    fn test_supported_languages_empty() {
        let p = OpenAiTranslationProvider::new(test_endpoint());
        assert!(p.supported_languages().is_empty());
    }
}
