use serde::{Deserialize, Serialize};
use std::collections::HashMap;

// ─── 配置模型 ───

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AppConfig {
    pub endpoints: Vec<AiEndpoint>,
    pub default_source_lang: String,
    pub default_target_langs: Vec<String>,
    pub audio: AudioConfig,
    pub ui: UiConfig,
}

impl Default for AppConfig {
    fn default() -> Self {
        Self {
            endpoints: Vec::new(),
            default_source_lang: "zh-Hans".into(),
            default_target_langs: vec!["en".into()],
            audio: AudioConfig::default(),
            ui: UiConfig::default(),
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AiEndpoint {
    pub id: String,
    pub name: String,
    pub endpoint_type: EndpointType,
    pub url: String,
    #[serde(default)]
    pub api_key: String,
    pub region: Option<String>,
    pub deployment: Option<String>,
    pub enabled: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "snake_case")]
pub enum EndpointType {
    AzureOpenAi,
    AzureSpeech,
    AzureTranslator,
    OpenAi,
    DeepL,
    Google,
    TencentCloud,
    AlibabaCloud,
    Custom,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AudioConfig {
    pub input_device_id: Option<String>,
    pub loopback_device_id: Option<String>,
    pub sample_rate: u32,
    pub enable_aec: bool,
    pub enable_ns: bool,
    pub enable_agc: bool,
}

impl Default for AudioConfig {
    fn default() -> Self {
        Self {
            input_device_id: None,
            loopback_device_id: None,
            sample_rate: 16000,
            enable_aec: true,
            enable_ns: true,
            enable_agc: true,
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct UiConfig {
    pub theme: String,
    pub sidebar_collapsed: bool,
    pub font_size: u32,
    pub language: String,
}

impl Default for UiConfig {
    fn default() -> Self {
        Self {
            theme: "dark".into(),
            sidebar_collapsed: false,
            font_size: 14,
            language: "zh-CN".into(),
        }
    }
}

// ─── 翻译模型 ───

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct TranslateRequest {
    pub text: String,
    pub source_lang: String,
    pub target_lang: String,
    pub endpoint_id: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct TranslateResponse {
    pub translated_text: String,
    pub source_lang: String,
    pub target_lang: String,
    pub confidence: Option<f64>,
    pub provider: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct LanguageInfo {
    pub code: String,
    pub name: String,
    pub native_name: String,
}

/// 实时语音翻译会话配置
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct RealtimeSessionConfig {
    pub source_lang: String,
    pub target_langs: Vec<String>,
    pub endpoint_id: String,
    pub enable_partial: bool,
    pub profanity_filter: bool,
}

/// 实时翻译事件 — 通过 Tauri Event 推送给前端
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "type", content = "data")]
pub enum RealtimeEvent {
    SessionStarted { session_id: String },
    Recognizing { text: String, offset_ms: u64 },
    Recognized { text: String, duration_ms: u64 },
    Translated {
        source_text: String,
        translations: HashMap<String, String>,
    },
    SessionStopped { session_id: String },
    Error { message: String },
}

// ─── 音频模型 ───

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AudioDeviceInfo {
    pub id: String,
    pub name: String,
    pub device_type: AudioDeviceType,
    pub is_default: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "snake_case")]
pub enum AudioDeviceType {
    Input,
    Output,
    Loopback,
}

// ─── AI 媒体模型 ───

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ImageGenRequest {
    pub prompt: String,
    pub negative_prompt: Option<String>,
    pub width: u32,
    pub height: u32,
    pub model: String,
    pub quality: Option<String>,
    pub style: Option<String>,
    pub endpoint_id: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ImageGenResult {
    pub url: Option<String>,
    pub base64: Option<String>,
    pub revised_prompt: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ChatMessage {
    pub role: String,
    pub content: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CompletionRequest {
    pub messages: Vec<ChatMessage>,
    pub model: String,
    pub temperature: Option<f64>,
    pub max_tokens: Option<u32>,
    pub endpoint_id: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CompletionResponse {
    pub content: String,
    pub model: String,
    pub usage: Option<TokenUsage>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct TokenUsage {
    pub prompt_tokens: u32,
    pub completion_tokens: u32,
    pub total_tokens: u32,
}

// ─── 批量处理模型 ───

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct BatchTask {
    pub id: String,
    pub name: String,
    pub status: TaskStatus,
    pub task_type: BatchTaskType,
    pub progress: f64,
    pub created_at: String,
    pub updated_at: String,
    pub error: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "snake_case")]
pub enum TaskStatus {
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum BatchTaskType {
    SubtitleTranslation,
    AudioTranscription,
    TextTranslation,
    ImageGeneration,
}

// ─── 音频实验室模型 ───

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AudioLabSession {
    pub id: String,
    pub name: String,
    pub stage: AudioLabStage,
    pub file_path: Option<String>,
    pub duration_ms: u64,
    pub created_at: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "snake_case")]
pub enum AudioLabStage {
    Recording,
    Processing,
    Transcribing,
    Reviewing,
    Exporting,
}

// ─── 存储模型 ───

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct TranslationHistory {
    pub id: String,
    pub source_text: String,
    pub translated_text: String,
    pub source_lang: String,
    pub target_lang: String,
    pub provider: String,
    pub created_at: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct MediaSession {
    pub id: String,
    pub name: String,
    pub session_type: String,
    pub items: Vec<MediaItem>,
    pub created_at: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct MediaItem {
    pub id: String,
    pub prompt: String,
    pub result_url: Option<String>,
    pub status: TaskStatus,
    pub created_at: String,
}
