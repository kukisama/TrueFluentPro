//! Provider crate — trait definitions for all capability providers.
//!
//! Each provider trait defines the contract for a specific capability type.
//! Implementations (adapters) live in submodules per vendor.

pub mod azure;
pub mod registry;

use serde::{Deserialize, Serialize};
use futures::stream::BoxStream;

// ═══ Error ═══

#[derive(Debug, thiserror::Error)]
pub enum ProviderError {
    #[error("upstream error: {0}")]
    Upstream(String),
    #[error("rate limited")]
    RateLimited,
    #[error("invalid credential")]
    BadCredential,
    #[error("unsupported capability")]
    UnsupportedCapability,
    #[error("provider not found: {0}")]
    ProviderNotFound(String),
    #[error("network: {0}")]
    Network(String),
}

// ═══ Chat Provider ═══

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ChatRequest {
    pub messages: Vec<ChatMessage>,
    pub model: String,
    pub temperature: Option<f32>,
    pub max_tokens: Option<u32>,
    pub stream: bool,
    /// Injected by gateway, never trust client.
    #[serde(skip_deserializing)]
    pub tenant_id: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ChatMessage {
    pub role: String,
    pub content: String,
}

#[derive(Debug, Clone, Serialize)]
pub struct ChatChunk {
    pub delta: String,
    pub finish_reason: Option<String>,
    pub usage: Option<TokenUsage>,
}

#[derive(Debug, Clone, Copy, Serialize)]
pub struct TokenUsage {
    pub prompt: u32,
    pub completion: u32,
}

use async_trait::async_trait;

#[async_trait]
pub trait ChatProvider: Send + Sync {
    fn id(&self) -> &'static str;
    async fn chat_stream(
        &self,
        req: ChatRequest,
    ) -> Result<BoxStream<'static, Result<ChatChunk, ProviderError>>, ProviderError>;
}

// ═══ Text Translator ═══

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct TextTranslateRequest {
    pub text: String,
    pub source_lang: String,
    pub target_lang: String,
    pub tenant_id: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct TextTranslateResponse {
    pub translation: String,
    pub detected_source_lang: Option<String>,
}

#[async_trait]
pub trait TextTranslator: Send + Sync {
    fn id(&self) -> &'static str;
    async fn translate(
        &self,
        req: TextTranslateRequest,
    ) -> Result<TextTranslateResponse, ProviderError>;
}

// ═══ TTS Provider ═══

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct TtsRequest {
    pub text: String,
    pub voice_id: String,
    pub output_format: Option<String>,
    pub speed: Option<f32>,
}

#[async_trait]
pub trait TtsProvider: Send + Sync {
    fn id(&self) -> &'static str;
    async fn synthesize(&self, req: TtsRequest) -> Result<Vec<u8>, ProviderError>;
}

// ═══ Image Provider ═══

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ImageGenRequest {
    pub prompt: String,
    pub size: Option<String>,
    pub n: Option<u32>,
    pub quality: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
pub struct ImageGenResponse {
    pub images: Vec<ImageData>,
}

#[derive(Debug, Clone, Serialize)]
pub struct ImageData {
    pub url: Option<String>,
    pub b64_json: Option<String>,
}

#[async_trait]
pub trait ImageProvider: Send + Sync {
    fn id(&self) -> &'static str;
    async fn generate(&self, req: ImageGenRequest) -> Result<ImageGenResponse, ProviderError>;
}
