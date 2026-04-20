//! Provider crate — trait definitions for all capability providers.
//!
//! Each provider trait defines the contract for a specific capability type.
//! Implementations (adapters) live in submodules per vendor.

pub mod azure;
pub mod generic;
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
    /// Text type: "plain" (default) or "html".
    #[serde(default)]
    pub text_type: Option<String>,
    /// Profanity handling: "NoAction" (default), "Marked", or "Deleted".
    /// See: https://learn.microsoft.com/en-us/azure/ai-services/translator/reference/v3-0-translate
    #[serde(default)]
    pub profanity_action: Option<String>,
    /// Profanity marker type: "Asterisk" (default) or "Tag". Only used when profanity_action is "Marked".
    #[serde(default)]
    pub profanity_marker: Option<String>,
    /// Category (custom translation model). E.g. "general" or a custom category ID.
    #[serde(default)]
    pub category: Option<String>,
    /// Include alignment information in the response.
    #[serde(default)]
    pub include_alignment: Option<bool>,
    /// Include sentence boundaries in the response.
    #[serde(default)]
    pub include_sentence_length: Option<bool>,
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
    /// Speech style (e.g. "cheerful", "sad", "angry", "excited", "friendly", etc.)
    #[serde(default)]
    pub style: Option<String>,
    /// Style intensity degree (0.01–2.0, default 1.0).
    #[serde(default)]
    pub style_degree: Option<f32>,
    /// Voice role play (e.g. "Girl", "Boy", "YoungAdultFemale", etc.)
    #[serde(default)]
    pub role: Option<String>,
    /// Pitch adjustment (e.g. "+5%", "-10%", "high", "low", "+2st", "200Hz").
    #[serde(default)]
    pub pitch: Option<String>,
    /// Volume (e.g. "+10%", "loud", "soft", "x-loud", "50").
    #[serde(default)]
    pub volume: Option<String>,
    /// Language override for multilingual voices (e.g. "en-US", "zh-CN").
    #[serde(default)]
    pub language: Option<String>,
    /// Voice effect (e.g. "eq_car", "eq_telecomhp8k").
    #[serde(default)]
    pub effect: Option<String>,
    /// Prosody range (e.g. "+10%", "high").
    #[serde(default)]
    pub range: Option<String>,
    /// Prosody contour (e.g. "(0%,+20Hz)(10%,-10Hz)(100%,+5Hz)").
    #[serde(default)]
    pub contour: Option<String>,
    /// If true, the `text` field is treated as raw SSML body content (user builds own SSML).
    #[serde(default)]
    pub raw_ssml: Option<bool>,
    /// Break strength before text (e.g. "strong", "medium", "x-weak").
    #[serde(default)]
    pub break_strength: Option<String>,
    /// Break time before text (e.g. "500ms", "1s").
    #[serde(default)]
    pub break_time: Option<String>,
    /// Silence tag type ("Sentenceboundary", "Tailing", "Leading-exact", "Tailing-exact")
    #[serde(default)]
    pub silence_type: Option<String>,
    /// Silence duration (e.g. "200ms", "1s").
    #[serde(default)]
    pub silence_value: Option<String>,
    /// Emphasis level ("strong", "moderate", "reduced", "none").
    #[serde(default)]
    pub emphasis: Option<String>,
    /// Phoneme alphabet ("ipa" or "sapi").
    #[serde(default)]
    pub phoneme_alphabet: Option<String>,
    /// Phoneme value.
    #[serde(default)]
    pub phoneme_value: Option<String>,
    /// Say-as interpret-as (e.g. "date", "number", "telephone", "characters", etc.)
    #[serde(default)]
    pub say_as_interpret_as: Option<String>,
    /// Say-as format (e.g. "mdy", "dmy", etc.)
    #[serde(default)]
    pub say_as_format: Option<String>,
    /// Say-as detail attribute.
    #[serde(default)]
    pub say_as_detail: Option<String>,
    /// Sub alias — substitute pronunciation for displayed text.
    #[serde(default)]
    pub sub_alias: Option<String>,
}

#[async_trait]
pub trait TtsProvider: Send + Sync {
    fn id(&self) -> &'static str;
    async fn synthesize(&self, req: TtsRequest) -> Result<Vec<u8>, ProviderError>;
}

// ═══ STT (Speech-to-Text) Provider ═══

#[derive(Debug, Clone)]
pub struct SttRequest {
    pub audio_data: Vec<u8>,
    pub language: String,
    pub profanity_filter: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
pub struct SttResponse {
    pub text: String,
    pub language: String,
    pub duration_ms: u64,
}

#[async_trait]
pub trait SttProvider: Send + Sync {
    fn id(&self) -> &'static str;
    async fn transcribe(&self, req: SttRequest) -> Result<SttResponse, ProviderError>;
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

// ═══ Live Speech Translation Provider ═══

/// Credentials and connection info resolved for a live speech translation session.
#[derive(Debug, Clone)]
pub struct LiveTranslateSessionConfig {
    /// The upstream WebSocket URL to connect to (fully assembled with query params).
    pub upstream_url: String,
    /// Headers to include on the upstream WebSocket handshake (auth, etc.).
    pub auth_headers: Vec<(String, String)>,
    /// The Azure region (used for constructing speech.config messages).
    pub region: String,
}

/// Configuration parameters for starting a live translation session.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct LiveTranslateRequest {
    /// Source language BCP-47 locale (e.g. "en-US"). Empty = auto-detect.
    pub source_lang: String,
    /// Target language(s) — ISO 639-1 codes (e.g. ["es", "fr"]).
    pub target_langs: Vec<String>,
    /// Auto-detect candidate languages (BCP-47 locales). Used when source_lang is empty.
    #[serde(default)]
    pub auto_detect_languages: Vec<String>,
    /// Profanity handling: "Raw" (default), "Masked", or "Removed".
    /// Maps to SpeechServiceResponse_ProfanityOption.
    #[serde(default)]
    pub profanity_option: Option<String>,
    /// Enable TrueText post-processing (filters disfluencies, normalizes text).
    /// Maps to SpeechServiceResponse_PostProcessingOption = "TrueText".
    #[serde(default)]
    pub enable_true_text: Option<bool>,
    /// Initial silence timeout in milliseconds (how long to wait before giving up).
    #[serde(default)]
    pub initial_silence_timeout_ms: Option<u32>,
    /// End silence timeout in milliseconds (how long to wait after speech ends).
    #[serde(default)]
    pub end_silence_timeout_ms: Option<u32>,
    /// Enable word-level timing information.
    #[serde(default)]
    pub enable_word_level_timestamps: Option<bool>,
}

/// Trait for providers that can establish a live speech translation WebSocket session.
#[async_trait]
pub trait LiveSpeechTranslator: Send + Sync {
    fn id(&self) -> &'static str;

    /// Resolve credentials and build the upstream WebSocket URL + auth headers.
    async fn build_session_config(
        &self,
        req: &LiveTranslateRequest,
    ) -> Result<LiveTranslateSessionConfig, ProviderError>;
}
