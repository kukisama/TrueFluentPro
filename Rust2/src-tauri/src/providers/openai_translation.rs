use async_trait::async_trait;

use crate::models::*;
use super::registry::*;

/// B-05 修复: 基于 OpenAI Chat Completion 的文本翻译 Provider
///
/// 通过 system prompt 指导 LLM 做翻译，让 OpenAI 兼容端点同时拥有 TextTranslationSlot 能力。
pub struct OpenAiTranslationProvider {
    inner: super::openai_chat::OpenAiChatProvider,
    endpoint: AiEndpoint,
}

impl OpenAiTranslationProvider {
    pub fn new(endpoint: AiEndpoint) -> Self {
        Self {
            inner: super::openai_chat::OpenAiChatProvider::new(endpoint.clone()),
            endpoint,
        }
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

        let model = self.endpoint.models.iter()
            .find(|m| m.capabilities.contains(&ModelCapability::Text))
            .map(|m| m.model_id.clone())
            .unwrap_or_else(|| "gpt-4.1-mini".to_string());

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
            model,
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
        let model = self.endpoint.models.iter()
            .find(|m| m.capabilities.contains(&ModelCapability::Text))
            .map(|m| m.model_id.clone())
            .unwrap_or_else(|| "gpt-4.1-mini".to_string());

        let comp_request = CompletionRequest {
            messages: vec![
                ChatMessage {
                    role: "system".into(),
                    content: serde_json::Value::String(
                        "Detect the language of the following text. Return ONLY the ISO 639-1 language code (e.g., 'en', 'zh', 'ja', 'ko'). Nothing else.".into()
                    ),
                },
                ChatMessage {
                    role: "user".into(),
                    content: serde_json::Value::String(text.to_string()),
                },
            ],
            model,
            temperature: Some(0.0),
            max_tokens: Some(10),
            endpoint_id: self.endpoint.id.clone(),
        };

        let resp = self.inner.complete(&comp_request).await?;
        Ok(resp.content.trim().to_lowercase())
    }

    fn supported_languages(&self) -> Vec<LanguageInfo> {
        vec![
            LanguageInfo { code: "en".into(), name: "English".into(), native_name: "English".into() },
            LanguageInfo { code: "zh".into(), name: "Chinese".into(), native_name: "中文".into() },
            LanguageInfo { code: "ja".into(), name: "Japanese".into(), native_name: "日本語".into() },
            LanguageInfo { code: "ko".into(), name: "Korean".into(), native_name: "한국어".into() },
            LanguageInfo { code: "fr".into(), name: "French".into(), native_name: "Français".into() },
            LanguageInfo { code: "de".into(), name: "German".into(), native_name: "Deutsch".into() },
            LanguageInfo { code: "es".into(), name: "Spanish".into(), native_name: "Español".into() },
            LanguageInfo { code: "auto".into(), name: "Auto Detect".into(), native_name: "Auto".into() },
        ]
    }
}
