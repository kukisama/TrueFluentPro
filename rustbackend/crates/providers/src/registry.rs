//! Provider registry — dynamic dispatch for capability → provider routing.

use crate::{ChatProvider, ImageProvider, TtsProvider, TextTranslator, LiveSpeechTranslator, SttProvider, VideoProvider, ProviderError};
use crate::azure::chat::AzureOpenAiChat;
use crate::azure::image::AzureOpenAiImage;
use crate::azure::tts::AzureSpeechTts;
use crate::azure::translate::AzureTranslator;
use crate::azure::speech_translate::AzureSpeechTranslation;
use crate::azure::stt::AzureSpeechStt;
use crate::azure::video::AzureOpenAiVideo;
use crate::generic::openai_chat::GenericOpenAiChat;
use crate::tencent::translate::TencentTranslator;
use crate::tencent::tts::TencentTts;
use crate::tencent::chat::TencentHunyuanChat;
use crate::alibaba::translate::AliTranslator;
use crate::alibaba::tts::AliTts;
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
    video_providers: Vec<(String, Arc<dyn VideoProvider>)>,
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
        let mut video_providers: Vec<(String, Arc<dyn VideoProvider>)> = Vec::new();

        for p in providers {
            if !p.is_enabled {
                continue;
            }

            match p.vendor.as_str() {
                "azure_openai" | "azure" => {
                    info!(provider = %p.id, vendor = %p.vendor, "Registering Azure OpenAI adapters (chat + image + video)");
                    let chat = Arc::new(AzureOpenAiChat::new(credentials.clone(), &p.id));
                    let image = Arc::new(AzureOpenAiImage::new(credentials.clone(), &p.id));
                    let video = Arc::new(AzureOpenAiVideo::new(credentials.clone(), &p.id));
                    chat_providers.push((p.id.clone(), chat));
                    image_providers.push((p.id.clone(), image));
                    video_providers.push((p.id.clone(), video));
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
                "tencent" | "tencent_cloud" => {
                    info!(provider = %p.id, vendor = %p.vendor, "Registering Tencent Cloud adapters (translate + TTS)");
                    let translator = Arc::new(TencentTranslator::new(credentials.clone(), &p.id));
                    let tts = Arc::new(TencentTts::new(credentials.clone(), &p.id));
                    translate_providers.push((p.id.clone(), translator));
                    tts_providers.push((p.id.clone(), tts));
                }
                "tencent_hunyuan" | "hunyuan" => {
                    info!(provider = %p.id, vendor = %p.vendor, "Registering Tencent Hunyuan chat adapter");
                    let chat = Arc::new(TencentHunyuanChat::new(credentials.clone(), &p.id));
                    chat_providers.push((p.id.clone(), chat));
                }
                "alibaba" | "aliyun" | "alibaba_cloud" => {
                    info!(provider = %p.id, vendor = %p.vendor, "Registering Alibaba Cloud adapters (translate + TTS)");
                    let translator = Arc::new(AliTranslator::new(credentials.clone(), &p.id));
                    let tts = Arc::new(AliTts::new(credentials.clone(), &p.id));
                    translate_providers.push((p.id.clone(), translator));
                    tts_providers.push((p.id.clone(), tts));
                }
                "generic_openai" | "openai" | "ollama" | "vllm" | "deepseek" | "lm_studio"
                | "dashscope" | "qwen" => {
                    info!(provider = %p.id, vendor = %p.vendor, "Registering generic OpenAI-compatible chat adapter");
                    let chat = Arc::new(GenericOpenAiChat::new(credentials.clone(), &p.id));
                    chat_providers.push((p.id.clone(), chat));
                }
                other => {
                    info!(provider = %p.id, vendor = %other, "Unknown vendor — skipping");
                }
            }
        }

        Self { chat_providers, image_providers, tts_providers, translate_providers, live_translate_providers, stt_providers, video_providers }
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
        self.video_providers = new.video_providers;
    }

    /// Get the first enabled ChatProvider (or specific by provider_id).
    pub fn get_chat(&self, provider_id: Option<&str>) -> Result<Arc<dyn ChatProvider>, ProviderError> {
        get_provider(&self.chat_providers, provider_id)
    }

    /// Get the first enabled ImageProvider.
    pub fn get_image(&self, provider_id: Option<&str>) -> Result<Arc<dyn ImageProvider>, ProviderError> {
        get_provider(&self.image_providers, provider_id)
    }

    /// Get the first enabled TtsProvider.
    pub fn get_tts(&self, provider_id: Option<&str>) -> Result<Arc<dyn TtsProvider>, ProviderError> {
        get_provider(&self.tts_providers, provider_id)
    }

    /// Get the first enabled TextTranslator.
    pub fn get_translator(&self, provider_id: Option<&str>) -> Result<Arc<dyn TextTranslator>, ProviderError> {
        get_provider(&self.translate_providers, provider_id)
    }

    /// Get the first enabled LiveSpeechTranslator.
    pub fn get_live_translator(&self, provider_id: Option<&str>) -> Result<Arc<dyn LiveSpeechTranslator>, ProviderError> {
        get_provider(&self.live_translate_providers, provider_id)
    }

    /// Get the first enabled SttProvider.
    pub fn get_stt(&self, provider_id: Option<&str>) -> Result<Arc<dyn SttProvider>, ProviderError> {
        get_provider(&self.stt_providers, provider_id)
    }

    /// Get the first enabled VideoProvider.
    pub fn get_video(&self, provider_id: Option<&str>) -> Result<Arc<dyn VideoProvider>, ProviderError> {
        get_provider(&self.video_providers, provider_id)
    }

    pub fn chat_count(&self) -> usize { self.chat_providers.len() }
    pub fn image_count(&self) -> usize { self.image_providers.len() }
    pub fn tts_count(&self) -> usize { self.tts_providers.len() }
    pub fn translate_count(&self) -> usize { self.translate_providers.len() }
    pub fn live_translate_count(&self) -> usize { self.live_translate_providers.len() }
    pub fn stt_count(&self) -> usize { self.stt_providers.len() }
    pub fn video_count(&self) -> usize { self.video_providers.len() }
}

/// Generic provider lookup — by id or first available.
fn get_provider<T: ?Sized>(
    providers: &[(String, Arc<T>)],
    provider_id: Option<&str>,
) -> Result<Arc<T>, ProviderError> {
    if let Some(id) = provider_id {
        providers.iter()
            .find(|(pid, _)| pid == id)
            .map(|(_, p)| p.clone())
            .ok_or_else(|| ProviderError::ProviderNotFound(id.to_string()))
    } else {
        providers.first()
            .map(|(_, p)| p.clone())
            .ok_or(ProviderError::UnsupportedCapability)
    }
}
