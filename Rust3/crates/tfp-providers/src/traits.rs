use async_trait::async_trait;
use serde::{Deserialize, Serialize};
use tokio::sync::mpsc;

use tfp_core::{
    CompletionRequest, CompletionResponse, ImageGenRequest, ImageGenResult, LanguageInfo,
    ProviderError, RealtimeEvent, RealtimeSessionConfig, TranscriptSegment, TranslateRequest,
    TranslateResponse, VideoGenRequest, VideoGenResult, VoiceInfo,
};

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ProviderCapability {
    TextTranslation,
    RealtimeSpeechTranslation,
    SpeechToText,
    TextToSpeech,
    AiCompletion,
    ImageGeneration,
    VideoGeneration,
}

pub trait ProviderMeta: Send + Sync {
    fn id(&self) -> &str;
    fn display_name(&self) -> &str;
    fn capabilities(&self) -> Vec<ProviderCapability>;
}

// ── Slot 1: Text Translation ──

#[async_trait]
pub trait TextTranslationSlot: ProviderMeta {
    async fn translate(
        &self,
        request: &TranslateRequest,
    ) -> Result<TranslateResponse, ProviderError>;

    async fn detect_language(&self, text: &str) -> Result<String, ProviderError>;

    fn supported_languages(&self) -> Vec<LanguageInfo>;
}

// ── Slot 2: Realtime Speech Translation ──

#[async_trait]
pub trait RealtimeSpeechSlot: ProviderMeta {
    async fn create_session(
        &self,
        config: &RealtimeSessionConfig,
    ) -> Result<
        (
            mpsc::UnboundedReceiver<RealtimeEvent>,
            Box<dyn RealtimeSessionHandle>,
        ),
        ProviderError,
    >;
}

#[async_trait]
pub trait RealtimeSessionHandle: Send + Sync {
    async fn push_audio(&self, pcm_data: &[u8]) -> Result<(), ProviderError>;
    async fn stop(&self) -> Result<(), ProviderError>;
}

// ── Slot 3: Speech-to-Text ──

#[async_trait]
pub trait SpeechToTextSlot: ProviderMeta {
    async fn transcribe(
        &self,
        audio_data: &[u8],
        lang: &str,
    ) -> Result<Vec<TranscriptSegment>, ProviderError>;
}

// ── Slot 4: Text-to-Speech ──

#[async_trait]
pub trait TextToSpeechSlot: ProviderMeta {
    async fn synthesize(
        &self,
        text: &str,
        voice: &str,
        format: &str,
    ) -> Result<Vec<u8>, ProviderError>;

    async fn synthesize_multi_speaker(
        &self,
        text: &str,
        speakers: &[(String, String)],
        format: &str,
    ) -> Result<Vec<u8>, ProviderError> {
        let voice = speakers
            .first()
            .map(|(_, v)| v.as_str())
            .unwrap_or("en-US-JennyNeural");
        self.synthesize(text, voice, format).await
    }

    async fn list_voices(&self, locale: &str) -> Result<Vec<VoiceInfo>, ProviderError>;
}

// ── Slot 5: AI Completion ──

#[derive(Debug, Clone)]
pub enum StreamChunk {
    Token(String),
    Reasoning(String),
    Usage {
        prompt_tokens: u32,
        completion_tokens: u32,
    },
}

#[async_trait]
pub trait AiCompletionSlot: ProviderMeta {
    async fn complete(
        &self,
        request: &CompletionRequest,
    ) -> Result<CompletionResponse, ProviderError>;

    async fn complete_stream(
        &self,
        request: &CompletionRequest,
    ) -> Result<mpsc::UnboundedReceiver<Result<StreamChunk, ProviderError>>, ProviderError>;
}

// ── Slot 6: Image Generation ──

#[async_trait]
pub trait ImageGenSlot: ProviderMeta {
    async fn generate(
        &self,
        request: &ImageGenRequest,
    ) -> Result<Vec<ImageGenResult>, ProviderError>;
}

// ── Slot 7: Video Generation ──

#[async_trait]
pub trait VideoGenSlot: ProviderMeta {
    async fn generate(
        &self,
        request: &VideoGenRequest,
    ) -> Result<VideoGenResult, ProviderError>;

    async fn poll_status(
        &self,
        video_id: &str,
        endpoint_id: &str,
    ) -> Result<VideoGenResult, ProviderError>;
}
