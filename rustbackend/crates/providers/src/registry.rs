//! Provider registry — dynamic dispatch for capability → provider routing.

use crate::{ChatProvider, ImageProvider, TtsProvider, TextTranslator, LiveSpeechTranslator, SttProvider, ProviderError};
use crate::azure::chat::AzureOpenAiChat;
use crate::azure::image::AzureOpenAiImage;
use crate::azure::tts::AzureSpeechTts;
use crate::azure::translate::AzureTranslator;
use crate::azure::speech_translate::AzureSpeechTranslation;
use crate::azure::stt::AzureSpeechStt;
use crate::generic::openai_chat::GenericOpenAiChat;
use credential_broker::CredentialBroker;
use domain::models::ProviderInfo;
use std::sync::Arc;
use tracing::info;

/// Registry that holds all instantiated provider adapters.
pub struct ProviderRegistry {
    chat_providers: Vec<(String, Arc<dyn ChatProvider>)>,
    image_providers: Vec<(String, Arc<dyn ImageProvider>)>,
    tts_providers: Vec<(String, Arc<dyn TtsProvider>)>,
    translate_providers: Vec<(String, Arc<dyn TextTranslator>)>,
    live_translate_providers: Vec<(String, Arc<dyn LiveSpeechTranslator>)>,
    stt_providers: Vec<(String, Arc<dyn SttProvider>)>,
}

impl ProviderRegistry {
    /// Build registry from DB provider list + credential broker.
    pub fn build(
        providers: &[ProviderInfo],
        credentials: Arc<CredentialBroker>,
    ) -> Self {
        let mut chat_providers: Vec<(String, Arc<dyn ChatProvider>)> = Vec::new();
        let mut image_providers: Vec<(String, Arc<dyn ImageProvider>)> = Vec::new();
        let mut tts_providers: Vec<(String, Arc<dyn TtsProvider>)> = Vec::new();
        let mut translate_providers: Vec<(String, Arc<dyn TextTranslator>)> = Vec::new();
        let mut live_translate_providers: Vec<(String, Arc<dyn LiveSpeechTranslator>)> = Vec::new();
        let mut stt_providers: Vec<(String, Arc<dyn SttProvider>)> = Vec::new();

        for p in providers {
            if !p.is_enabled {
                continue;
            }

            match p.vendor.as_str() {
                "azure_openai" | "azure" => {
                    info!(provider = %p.id, vendor = %p.vendor, "Registering Azure OpenAI adapters");
                    let chat = Arc::new(AzureOpenAiChat::new(credentials.clone(), &p.id));
                    let image = Arc::new(AzureOpenAiImage::new(credentials.clone(), &p.id));
                    chat_providers.push((p.id.clone(), chat));
                    image_providers.push((p.id.clone(), image));
                }
                "azure_speech" => {
                    info!(provider = %p.id, vendor = %p.vendor, "Registering Azure Speech adapters (TTS + STT + live translation)");
                    let tts = Arc::new(AzureSpeechTts::new(credentials.clone(), &p.id));
                    let live = Arc::new(AzureSpeechTranslation::new(credentials.clone(), &p.id));
                    let stt = Arc::new(AzureSpeechStt::new(credentials.clone(), &p.id));
                    tts_providers.push((p.id.clone(), tts));
                    live_translate_providers.push((p.id.clone(), live));
                    stt_providers.push((p.id.clone(), stt));
                }
                "azure_translator" => {
                    info!(provider = %p.id, vendor = %p.vendor, "Registering Azure Translator adapter");
                    let translator = Arc::new(AzureTranslator::new(credentials.clone(), &p.id));
                    translate_providers.push((p.id.clone(), translator));
                }
                "generic_openai" | "openai" | "ollama" | "vllm" | "deepseek" | "lm_studio" => {
                    info!(provider = %p.id, vendor = %p.vendor, "Registering generic OpenAI-compatible chat adapter");
                    let chat = Arc::new(GenericOpenAiChat::new(credentials.clone(), &p.id));
                    chat_providers.push((p.id.clone(), chat));
                }
                other => {
                    info!(provider = %p.id, vendor = %other, "Unknown vendor — skipping");
                }
            }
        }

        Self { chat_providers, image_providers, tts_providers, translate_providers, live_translate_providers, stt_providers }
    }

    /// Rebuild registry (called when admin changes providers).
    pub fn rebuild(
        &mut self,
        providers: &[ProviderInfo],
        credentials: Arc<CredentialBroker>,
    ) {
        let new = Self::build(providers, credentials);
        self.chat_providers = new.chat_providers;
        self.image_providers = new.image_providers;
        self.tts_providers = new.tts_providers;
        self.translate_providers = new.translate_providers;
        self.live_translate_providers = new.live_translate_providers;
        self.stt_providers = new.stt_providers;
    }

    /// Get the first enabled ChatProvider (or specific by provider_id).
    pub fn get_chat(&self, provider_id: Option<&str>) -> Result<Arc<dyn ChatProvider>, ProviderError> {
        if let Some(id) = provider_id {
            self.chat_providers.iter()
                .find(|(pid, _)| pid == id)
                .map(|(_, p)| p.clone())
                .ok_or_else(|| ProviderError::ProviderNotFound(id.to_string()))
        } else {
            self.chat_providers.first()
                .map(|(_, p)| p.clone())
                .ok_or(ProviderError::UnsupportedCapability)
        }
    }

    /// Get the first enabled ImageProvider.
    pub fn get_image(&self, provider_id: Option<&str>) -> Result<Arc<dyn ImageProvider>, ProviderError> {
        if let Some(id) = provider_id {
            self.image_providers.iter()
                .find(|(pid, _)| pid == id)
                .map(|(_, p)| p.clone())
                .ok_or_else(|| ProviderError::ProviderNotFound(id.to_string()))
        } else {
            self.image_providers.first()
                .map(|(_, p)| p.clone())
                .ok_or(ProviderError::UnsupportedCapability)
        }
    }

    /// Get the first enabled TtsProvider.
    pub fn get_tts(&self, provider_id: Option<&str>) -> Result<Arc<dyn TtsProvider>, ProviderError> {
        if let Some(id) = provider_id {
            self.tts_providers.iter()
                .find(|(pid, _)| pid == id)
                .map(|(_, p)| p.clone())
                .ok_or_else(|| ProviderError::ProviderNotFound(id.to_string()))
        } else {
            self.tts_providers.first()
                .map(|(_, p)| p.clone())
                .ok_or(ProviderError::UnsupportedCapability)
        }
    }

    /// Get the first enabled TextTranslator.
    pub fn get_translator(&self, provider_id: Option<&str>) -> Result<Arc<dyn TextTranslator>, ProviderError> {
        if let Some(id) = provider_id {
            self.translate_providers.iter()
                .find(|(pid, _)| pid == id)
                .map(|(_, p)| p.clone())
                .ok_or_else(|| ProviderError::ProviderNotFound(id.to_string()))
        } else {
            self.translate_providers.first()
                .map(|(_, p)| p.clone())
                .ok_or(ProviderError::UnsupportedCapability)
        }
    }

    /// Get the first enabled LiveSpeechTranslator.
    pub fn get_live_translator(&self, provider_id: Option<&str>) -> Result<Arc<dyn LiveSpeechTranslator>, ProviderError> {
        if let Some(id) = provider_id {
            self.live_translate_providers.iter()
                .find(|(pid, _)| pid == id)
                .map(|(_, p)| p.clone())
                .ok_or_else(|| ProviderError::ProviderNotFound(id.to_string()))
        } else {
            self.live_translate_providers.first()
                .map(|(_, p)| p.clone())
                .ok_or(ProviderError::UnsupportedCapability)
        }
    }

    pub fn chat_count(&self) -> usize { self.chat_providers.len() }
    pub fn image_count(&self) -> usize { self.image_providers.len() }
    pub fn tts_count(&self) -> usize { self.tts_providers.len() }
    pub fn translate_count(&self) -> usize { self.translate_providers.len() }
    pub fn live_translate_count(&self) -> usize { self.live_translate_providers.len() }
    pub fn stt_count(&self) -> usize { self.stt_providers.len() }

    /// Get the first enabled SttProvider.
    pub fn get_stt(&self, provider_id: Option<&str>) -> Result<Arc<dyn SttProvider>, ProviderError> {
        if let Some(id) = provider_id {
            self.stt_providers.iter()
                .find(|(pid, _)| pid == id)
                .map(|(_, p)| p.clone())
                .ok_or_else(|| ProviderError::ProviderNotFound(id.to_string()))
        } else {
            self.stt_providers.first()
                .map(|(_, p)| p.clone())
                .ok_or(ProviderError::UnsupportedCapability)
        }
    }
}
