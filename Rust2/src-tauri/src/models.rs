use serde::{Deserialize, Serialize};
use std::collections::HashMap;

// ─── 模型引用（对齐 C# ModelReference）───

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
pub struct ModelReference {
    #[serde(default)]
    pub endpoint_id: String,
    #[serde(default)]
    pub model_id: String,
}

// ─── 配置模型 ───

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AppConfig {
    pub endpoints: Vec<AiEndpoint>,
    pub default_source_lang: String,
    pub default_target_langs: Vec<String>,
    pub audio: AudioConfig,
    pub ui: UiConfig,
    #[serde(default)]
    pub ai: AiSettings,
    #[serde(default)]
    pub media: MediaSettings,
    #[serde(default)]
    pub storage: StorageSettings,
    #[serde(default)]
    pub recognition: RecognitionSettings,
    #[serde(default)]
    pub web_search: WebSearchSettings,
    /// O-08: 任务引擎并发数
    #[serde(default)]
    pub task_engine_concurrency: Option<u32>,
    /// O-08: 任务引擎超时(秒)
    #[serde(default)]
    pub task_engine_timeout_secs: Option<u64>,
}

impl Default for AppConfig {
    fn default() -> Self {
        Self {
            endpoints: Vec::new(),
            default_source_lang: "zh-Hans".into(),
            default_target_langs: vec!["en".into()],
            audio: AudioConfig::default(),
            ui: UiConfig::default(),
            ai: AiSettings::default(),
            media: MediaSettings::default(),
            storage: StorageSettings::default(),
            recognition: RecognitionSettings::default(),
            web_search: WebSearchSettings::default(),
            task_engine_concurrency: None,
            task_engine_timeout_secs: None,
        }
    }
}

// ─── AI 洞察设置（对齐 C# AiConfig）───

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AiSettings {
    #[serde(default)]
    pub insight_model: ModelReference,
    #[serde(default)]
    pub summary_model: ModelReference,
    #[serde(default)]
    pub quick_model: ModelReference,
    #[serde(default)]
    pub review_model: ModelReference,
    #[serde(default)]
    pub conversation_model: ModelReference,
    #[serde(default)]
    pub intent_model: ModelReference,
    #[serde(default)]
    pub insight_system_prompt: String,
    #[serde(default = "default_true")]
    pub enable_reasoning: bool,
    #[serde(default = "default_max_turns")]
    pub max_conversation_turns: u32,
}

fn default_true() -> bool { true }
fn default_max_turns() -> u32 { 20 }

impl Default for AiSettings {
    fn default() -> Self {
        Self {
            insight_model: ModelReference::default(),
            summary_model: ModelReference::default(),
            quick_model: ModelReference::default(),
            review_model: ModelReference::default(),
            conversation_model: ModelReference::default(),
            intent_model: ModelReference::default(),
            insight_system_prompt: "你是一个专业的会议/翻译分析助手。".into(),
            enable_reasoning: true,
            max_conversation_turns: 20,
        }
    }
}

// ─── 媒体生成设置（对齐 C# MediaGenConfig）───

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct MediaSettings {
    #[serde(default)]
    pub image_model: ModelReference,
    #[serde(default)]
    pub video_model: ModelReference,
    #[serde(default = "default_image_quality")]
    pub image_quality: String,
    #[serde(default = "default_image_format")]
    pub image_format: String,
    #[serde(default = "default_image_size")]
    pub image_size: String,
    #[serde(default = "default_one")]
    pub image_count: u32,
    #[serde(default = "default_image_background")]
    pub image_background: String,
    #[serde(default = "default_video_aspect")]
    pub video_aspect_ratio: String,
    #[serde(default = "default_video_resolution")]
    pub video_resolution: String,
    #[serde(default = "default_video_seconds")]
    pub video_seconds: u32,
    #[serde(default = "default_one")]
    pub video_variants: u32,
    #[serde(default = "default_poll_interval")]
    pub video_poll_interval_ms: u32,
}

fn default_image_quality() -> String { "auto".into() }
fn default_image_format() -> String { "png".into() }
fn default_image_size() -> String { "1024x1024".into() }
fn default_one() -> u32 { 1 }
fn default_image_background() -> String { "auto".into() }
fn default_video_aspect() -> String { "16:9".into() }
fn default_video_resolution() -> String { "720p".into() }
fn default_video_seconds() -> u32 { 5 }
fn default_poll_interval() -> u32 { 3000 }

impl Default for MediaSettings {
    fn default() -> Self {
        Self {
            image_model: ModelReference::default(),
            video_model: ModelReference::default(),
            image_quality: default_image_quality(),
            image_format: default_image_format(),
            image_size: default_image_size(),
            image_count: 1,
            image_background: default_image_background(),
            video_aspect_ratio: default_video_aspect(),
            video_resolution: default_video_resolution(),
            video_seconds: 5,
            video_variants: 1,
            video_poll_interval_ms: 3000,
        }
    }
}

// ─── 存储设置（对齐 C# StorageSectionVM）───

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StorageSettings {
    #[serde(default)]
    pub batch_storage_connection_string: String,
    #[serde(default)]
    pub batch_storage_is_valid: bool,
    #[serde(default = "default_audio_container")]
    pub batch_audio_container_name: String,
    #[serde(default = "default_result_container")]
    pub batch_result_container_name: String,
    #[serde(default = "default_true")]
    pub enable_recording: bool,
    #[serde(default = "default_mp3_bitrate")]
    pub recording_mp3_bitrate_kbps: u32,
    #[serde(default = "default_true")]
    pub export_vtt_subtitles: bool,
    #[serde(default)]
    pub export_srt_subtitles: bool,
}

fn default_audio_container() -> String { "truefluentpro-audio".into() }
fn default_result_container() -> String { "truefluentpro-results".into() }
fn default_mp3_bitrate() -> u32 { 256 }

impl Default for StorageSettings {
    fn default() -> Self {
        Self {
            batch_storage_connection_string: String::new(),
            batch_storage_is_valid: false,
            batch_audio_container_name: default_audio_container(),
            batch_result_container_name: default_result_container(),
            enable_recording: true,
            recording_mp3_bitrate_kbps: 256,
            export_vtt_subtitles: true,
            export_srt_subtitles: false,
        }
    }
}

// ─── 识别设置（对齐 C# RecognitionSectionVM）───

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct RecognitionSettings {
    #[serde(default = "default_true")]
    pub filter_modal_particles: bool,
    #[serde(default = "default_max_history")]
    pub max_history_items: u32,
    #[serde(default = "default_realtime_max")]
    pub realtime_max_length: u32,
    #[serde(default = "default_chunk_duration")]
    pub chunk_duration_ms: u32,
    #[serde(default = "default_true")]
    pub enable_auto_timeout: bool,
    #[serde(default = "default_initial_silence")]
    pub initial_silence_timeout_seconds: u32,
    #[serde(default = "default_end_silence")]
    pub end_silence_timeout_seconds: u32,
    #[serde(default)]
    pub enable_no_response_restart: bool,
    #[serde(default = "default_no_response")]
    pub no_response_restart_seconds: u32,
    #[serde(default = "default_audio_threshold")]
    pub audio_activity_threshold: u32,
    #[serde(default = "default_audio_gain")]
    pub audio_level_gain: f64,
    #[serde(default = "default_true")]
    pub show_reconnect_marker: bool,
}

fn default_max_history() -> u32 { 500 }
fn default_realtime_max() -> u32 { 150 }
fn default_chunk_duration() -> u32 { 200 }
fn default_initial_silence() -> u32 { 25 }
fn default_end_silence() -> u32 { 1 }
fn default_no_response() -> u32 { 3 }
fn default_audio_threshold() -> u32 { 600 }
fn default_audio_gain() -> f64 { 2.0 }

impl Default for RecognitionSettings {
    fn default() -> Self {
        Self {
            filter_modal_particles: true,
            max_history_items: 500,
            realtime_max_length: 150,
            chunk_duration_ms: 200,
            enable_auto_timeout: true,
            initial_silence_timeout_seconds: 25,
            end_silence_timeout_seconds: 1,
            enable_no_response_restart: false,
            no_response_restart_seconds: 3,
            audio_activity_threshold: 600,
            audio_level_gain: 2.0,
            show_reconnect_marker: true,
        }
    }
}

// ─── 网页搜索设置（对齐 C# WebSearchConfig）───

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct WebSearchSettings {
    #[serde(default = "default_search_provider")]
    pub provider_id: String,
    #[serde(default = "default_search_trigger")]
    pub trigger_mode: String,
    #[serde(default = "default_search_max")]
    pub max_results: u32,
    #[serde(default = "default_true")]
    pub enable_intent_analysis: bool,
    #[serde(default)]
    pub enable_result_compression: bool,
    #[serde(default)]
    pub mcp_endpoint: String,
    #[serde(default)]
    pub mcp_tool_name: String,
    #[serde(default)]
    pub mcp_api_key: String,
    #[serde(default)]
    pub debug_mode: bool,
}

fn default_search_provider() -> String { "duckduckgo".into() }
fn default_search_trigger() -> String { "auto".into() }
fn default_search_max() -> u32 { 5 }

impl Default for WebSearchSettings {
    fn default() -> Self {
        Self {
            provider_id: default_search_provider(),
            trigger_mode: default_search_trigger(),
            max_results: 5,
            enable_intent_analysis: true,
            enable_result_compression: false,
            mcp_endpoint: String::new(),
            mcp_tool_name: "web_search".into(),
            mcp_api_key: String::new(),
            debug_mode: false,
        }
    }
}

// ─── 终结点模型（对齐 C# AiEndpoint）───

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AiEndpoint {
    pub id: String,
    pub name: String,
    pub endpoint_type: EndpointType,
    pub url: String,
    #[serde(default)]
    pub api_key: String,
    pub api_version: Option<String>,
    pub region: Option<String>,
    #[serde(default)]
    pub models: Vec<AiModelEntry>,
    pub enabled: bool,
    /// 认证头模式: "api_key" | "bearer" | "auto"
    #[serde(default = "default_auth_header_mode")]
    pub auth_header_mode: String,

    /// 认证方式: "api_key" | "aad"（对齐 C# AiEndpoint.AuthMode）
    #[serde(default = "default_auth_mode")]
    pub auth_mode: String,
    /// AAD 租户 ID（对齐 C# AiEndpoint.AzureTenantId）
    #[serde(default)]
    pub azure_tenant_id: String,
    /// AAD 客户端 ID（对齐 C# AiEndpoint.AzureClientId）
    #[serde(default)]
    pub azure_client_id: String,

    // ── Azure Speech 专属字段（对齐 C# AiEndpoint）──
    #[serde(default)]
    pub speech_subscription_key: String,
    #[serde(default)]
    pub speech_region: String,
    #[serde(default)]
    pub speech_endpoint: String,
}

fn default_auth_header_mode() -> String {
    "api_key".into()
}

fn default_auth_mode() -> String {
    "api_key".into()
}

impl AiEndpoint {
    /// 将遗留的 "auto" auth_header_mode 按端点类型修正为明确值
    pub fn migrate_auth_header_mode(&mut self) {
        if self.auth_header_mode == "auto" {
            self.auth_header_mode = if self.is_azure() {
                "api_key".into()
            } else {
                "bearer".into()
            };
        }
    }

    /// 是否为 Azure 系终结点
    pub fn is_azure(&self) -> bool {
        matches!(
            self.endpoint_type,
            EndpointType::AzureOpenAi | EndpointType::ApiManagementGateway | EndpointType::AzureSpeech
        )
    }

    /// 是否为 Speech 终结点
    pub fn is_speech(&self) -> bool {
        self.endpoint_type == EndpointType::AzureSpeech
    }

    /// 取该终结点下第一个有某能力的模型
    #[allow(dead_code)]
    pub fn first_model_with_capability(&self, cap: ModelCapability) -> Option<&AiModelEntry> {
        self.models.iter().find(|m| m.capabilities.contains(&cap))
    }
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "snake_case")]
pub enum EndpointType {
    AzureOpenAi,
    ApiManagementGateway,
    OpenAiCompatible,
    AzureSpeech,
    // 预留
    AzureTranslator,
    DeepL,
    TencentCloud,
    AlibabaCloud,
    Custom,
}

/// 厂商资料包 — 匹配 C# EndpointProfile
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct VendorProfile {
    pub endpoint_type: EndpointType,
    pub label: String,
    pub badge: String,
    pub subtitle: String,
    pub glyph: String,
    /// 默认认证头模式: "api_key" | "bearer"（对齐 C# defaults.apiKeyHeaderMode）
    pub default_auth_header: String,
    pub default_api_version: String,
    /// 是否支持 AAD 认证（对齐 C# defaults.supportsAad）— 仅 Azure OpenAI 为 true
    #[serde(default)]
    pub supports_aad: bool,
    pub supports_model_discovery: bool,
    pub model_discovery_urls: Vec<String>,
    /// 各能力的测试 URL 模板（{baseUrl}, {deployment}, {apiVersion}, {model}）
    pub test_url_templates: HashMap<String, String>,
    /// 文字能力 URL 候选列表（按优先级排序，主 URL + 回退）
    #[serde(default)]
    pub text_url_candidates: Vec<String>,
    /// 图片能力 URL 候选列表
    #[serde(default)]
    pub image_url_candidates: Vec<String>,
    /// 视频能力 URL 候选列表
    #[serde(default)]
    pub video_url_candidates: Vec<String>,
    /// 音频（STT）URL 候选列表
    #[serde(default)]
    pub audio_url_candidates: Vec<String>,
    /// 语音合成（TTS）URL 候选列表
    #[serde(default)]
    pub speech_url_candidates: Vec<String>,
    /// 首选文字协议: "responses" | "chat_completions"
    #[serde(default)]
    pub text_protocol: String,
    /// 支持的认证模式列表（对齐 C# auth.supportedModes）: ["ApiKey"], ["ApiKey","AAD"]
    #[serde(default)]
    pub supported_auth_modes: Vec<String>,
    /// 完整 JSON（可选，供前端直接使用）
    #[serde(skip_serializing_if = "Option::is_none")]
    pub raw_json: Option<serde_json::Value>,
}

/// 模型条目 — 匹配 C# AiModelEntry
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AiModelEntry {
    pub model_id: String,
    pub display_name: String,
    /// Azure 部署名（仅 Azure 终结点使用）
    pub deployment_name: Option<String>,
    pub capabilities: Vec<ModelCapability>,
}

impl AiModelEntry {
    pub fn effective_deployment(&self) -> &str {
        self.deployment_name
            .as_deref()
            .unwrap_or(&self.model_id)
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ModelCapability {
    Text,
    Image,
    Video,
    SpeechToText,
    TextToSpeech,
}

// ─── 终结点测试结果 ───

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct EndpointTestReport {
    pub endpoint_id: String,
    pub endpoint_name: String,
    pub endpoint_type_name: String,
    pub items: Vec<EndpointTestItem>,
    pub duration_ms: u64,
    pub total_count: usize,
    pub success_count: usize,
    pub failed_count: usize,
    pub skipped_count: usize,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct EndpointTestItem {
    pub model_id: String,
    pub capability: String,
    pub status: TestStatus,
    pub summary: String,
    pub detail: Option<String>,
    pub request_url: Option<String>,
    /// 请求摘要（认证方式、基础地址、API版本、文本协议等）
    pub request_summary: Option<String>,
    pub duration_ms: u64,
    /// 测试分支描述（如"主测试 (资料包第 1 条候选)"）
    pub test_branch: Option<String>,
    /// 尝试过的所有 URL
    pub urls_tried: Vec<String>,
}

/// 实时进度事件（通过 Tauri event 推送）
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct EndpointTestProgress {
    pub endpoint_id: String,
    pub endpoint_name: String,
    pub total_count: usize,
    pub pending_count: usize,
    pub running_count: usize,
    pub success_count: usize,
    pub failed_count: usize,
    pub skipped_count: usize,
    pub items: Vec<EndpointTestItem>,
    pub is_completed: bool,
    pub started_at: String,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum TestStatus {
    Pending,
    Running,
    Success,
    Failed,
    Skipped,
}

// ─── 模型发现结果 ───

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DiscoveredModel {
    pub id: String,
    pub display_name: Option<String>,
    pub owned_by: Option<String>,
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
    /// S-1: 初始静音超时（秒）— 透传到 Speech SDK
    #[serde(default)]
    pub initial_silence_timeout_seconds: Option<u32>,
    /// S-1: 结束静音超时（秒）— 透传到 Speech SDK
    #[serde(default)]
    pub end_silence_timeout_seconds: Option<u32>,
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
    pub width: u32,
    pub height: u32,
    pub model: String,
    pub quality: Option<String>,
    pub output_format: Option<String>,
    pub background: Option<String>,
    pub n: Option<u32>,
    pub endpoint_id: String,
    /// P3-3: Responses API V2 — 文本模型 (如 gpt-4.1)，用于 body.model
    #[serde(default)]
    pub text_model: Option<String>,
    /// P3-3: Responses API V2 — 图片模型 (如 gpt-image-2)，用于 x-ms-oai-image-generation-deployment 头
    #[serde(default)]
    pub image_model: Option<String>,
    /// P3-3: 多轮改图的 previous_response_id
    #[serde(default)]
    pub previous_response_id: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ImageGenResult {
    pub url: Option<String>,
    pub base64: Option<String>,
    pub revised_prompt: Option<String>,
    /// P3-3: Responses API 返回的 response_id，用于多轮改图
    #[serde(default)]
    pub response_id: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ChatMessage {
    pub role: String,
    pub content: serde_json::Value, // 支持 string 或 array（多模态 content parts）
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

// ─── 任务通用枚举 ───

/// P3-4: 视频 API 模式
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "snake_case")]
pub enum VideoApiMode {
    /// sora-1: /openai/v1/video/generations/jobs (JSON)
    SoraJobs,
    /// sora-2: /v1/videos (multipart)
    Videos,
}

/// P3-4: 视频生成请求
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct VideoGenRequest {
    pub prompt: String,
    pub model: String,
    pub endpoint_id: String,
    #[serde(default = "default_video_size")]
    pub size: String,
    #[serde(default = "default_video_duration")]
    pub duration_seconds: u32,
    #[serde(default)]
    pub api_mode: Option<VideoApiMode>,
    #[serde(default)]
    pub reference_image_path: Option<String>,
    #[serde(default)]
    pub n: Option<u32>,
}

fn default_video_size() -> String { "1080x1920".to_string() }
fn default_video_duration() -> u32 { 10 }

/// P3-4: 视频生成结果
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct VideoGenResult {
    pub video_id: String,
    pub status: String,
    pub download_url: Option<String>,
    pub file_path: Option<String>,
    pub generate_seconds: Option<f64>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "snake_case")]
#[allow(dead_code)]
pub enum TaskStatus {
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled,
}

// ─── 音频实验室模型 ───

#[derive(Debug, Clone, Serialize, Deserialize)]
#[allow(dead_code)]
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
#[allow(dead_code)]
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
#[allow(dead_code)]
pub struct MediaSession {
    pub id: String,
    pub name: String,
    pub session_type: String,
    pub items: Vec<MediaItem>,
    pub created_at: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[allow(dead_code)]
pub struct MediaItem {
    pub id: String,
    pub prompt: String,
    pub result_url: Option<String>,
    pub status: TaskStatus,
    pub created_at: String,
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  P1.2: 会话 & 消息（对齐 C# ChatSessionViewModel）
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Session {
    pub id: String,
    pub title: String,
    pub session_type: String,
    pub message_count: i64,
    pub token_total: i64,
    pub created_at: String,
    pub updated_at: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Message {
    pub id: String,
    pub session_id: String,
    pub role: String,
    pub content: String,
    #[serde(default = "default_mode")]
    pub mode: String,
    pub reasoning_text: Option<String>,
    pub prompt_tokens: Option<i64>,
    pub completion_tokens: Option<i64>,
    pub image_base64: Option<String>,
    pub attachments: Option<String>,
    pub created_at: String,
}

fn default_mode() -> String {
    "text".into()
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  P1.2: 音频库 & 生命周期（对齐 C# AudioProcessingSnapshot）
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AudioLibraryItem {
    pub id: String,
    pub file_name: String,
    pub file_path: String,
    pub duration_ms: i64,
    pub sample_rate: i64,
    pub channels: i64,
    pub source_lang: String,
    pub created_at: String,
    pub updated_at: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AudioLifecycleRow {
    pub id: String,
    pub audio_item_id: String,
    pub stage: String,
    pub status: String,
    pub result_text: Option<String>,
    pub result_json: Option<String>,
    pub model_id: Option<String>,
    pub token_used: Option<i64>,
    pub error: Option<String>,
    pub started_at: Option<String>,
    pub completed_at: Option<String>,
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  P2.1: 任务队列模型
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AudioTaskRow {
    pub id: String,
    pub audio_item_id: String,
    pub stage: String,
    pub task_type: String,
    pub status: String,
    pub priority: i64,
    pub retry_count: i64,
    pub max_retries: i64,
    pub progress: f64,
    pub prompt_text: Option<String>,
    pub result_text: Option<String>,
    pub error: Option<String>,
    pub submitted_at: String,
    pub started_at: Option<String>,
    pub completed_at: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct TaskExecutionRow {
    pub id: String,
    pub task_id: String,
    pub attempt: i64,
    pub status: String,
    pub error: Option<String>,
    pub prompt_tokens: Option<i64>,
    pub completion_tokens: Option<i64>,
    pub duration_ms: Option<i64>,
    pub started_at: String,
    pub completed_at: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
pub struct TaskEngineStats {
    pub queued: i64,
    pub executing: i64,
    pub completed: i64,
    pub failed: i64,
    pub cancelled: i64,
    pub total_tokens: i64,
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  P3.5: 计费记录
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct BillingRecord {
    pub id: String,
    pub task_id: Option<String>,
    pub endpoint_id: String,
    pub model_id: String,
    pub prompt_tokens: i64,
    pub completion_tokens: i64,
    pub cost_usd: Option<f64>,
    pub created_at: String,
    /// RV-O3: 状态机 — Staging → Running → Landed → Committed
    #[serde(default = "default_billing_status")]
    pub status: String,
}

fn default_billing_status() -> String { "Committed".to_string() }

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
pub struct BillingSummary {
    pub total_prompt_tokens: i64,
    pub total_completion_tokens: i64,
    pub total_cost_usd: f64,
    pub record_count: i64,
    pub by_model: Vec<BillingByModel>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct BillingByModel {
    pub model_id: String,
    pub prompt_tokens: i64,
    pub completion_tokens: i64,
    pub cost_usd: f64,
    pub count: i64,
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  图片保存记录（对齐 C# ImageSaveResult）
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SavedImage {
    #[serde(default)]
    pub id: String,
    pub prompt: String,
    pub revised_prompt: Option<String>,
    pub file_path: String,
    pub file_size: i64,
    pub width: Option<u32>,
    pub height: Option<u32>,
    pub model_id: Option<String>,
    pub endpoint_id: Option<String>,
    pub generate_seconds: Option<f64>,
    pub source: String,
    #[serde(default)]
    pub created_at: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SaveImageRequest {
    pub base64: String,
    pub prompt: String,
    pub revised_prompt: Option<String>,
    pub format: String,
    pub width: Option<u32>,
    pub height: Option<u32>,
    pub model_id: Option<String>,
    pub endpoint_id: Option<String>,
    pub generate_seconds: Option<f64>,
    pub source: String,
}

// ─── RV-O4: 关联表模型结构 ───

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct MessageAttachment {
    pub id: String,
    pub message_id: String,
    pub file_type: String,
    pub file_path: Option<String>,
    pub file_url: Option<String>,
    pub file_name: Option<String>,
    pub file_size: Option<i64>,
    pub mime_type: Option<String>,
    pub created_at: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SessionTask {
    pub id: String,
    pub session_id: String,
    pub task_id: String,
    pub created_at: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SessionAsset {
    pub id: String,
    pub session_id: String,
    pub asset_type: String,
    pub asset_path: String,
    pub file_size: Option<i64>,
    pub created_at: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct MessageMediaRef {
    pub id: String,
    pub message_id: String,
    pub media_type: String,
    pub media_url: String,
    pub thumbnail_url: Option<String>,
    pub width: Option<i32>,
    pub height: Option<i32>,
    pub created_at: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct MessageCitation {
    pub id: String,
    pub message_id: String,
    pub citation_index: i32,
    pub title: Option<String>,
    pub url: Option<String>,
    pub snippet: Option<String>,
    pub created_at: String,
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  创作工坊 — 对齐 C# StorageModels.cs
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

/// 会话记录（对应 studio_sessions 表，对齐 C# SessionRecord）
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StudioSession {
    pub id: String,
    pub session_type: String,
    pub name: String,
    pub directory_path: String,
    pub canvas_mode: String,
    pub media_kind: String,
    pub is_deleted: bool,
    pub created_at: String,
    pub updated_at: String,
    pub last_accessed_at: Option<String>,
    pub source_session_id: Option<String>,
    pub source_session_name: Option<String>,
    pub source_session_directory_name: Option<String>,
    pub source_asset_id: Option<String>,
    pub source_asset_kind: Option<String>,
    pub source_asset_file_name: Option<String>,
    pub source_asset_path: Option<String>,
    pub source_preview_path: Option<String>,
    pub source_reference_role: Option<String>,
    pub message_count: i64,
    pub task_count: i64,
    pub asset_count: i64,
    pub latest_message_preview: Option<String>,
    pub legacy_source_path: Option<String>,
    pub import_batch_id: Option<String>,
    pub imported_at: Option<String>,
    pub is_legacy_import: bool,
}

/// 消息记录（对应 studio_messages 表，对齐 C# MessageRecord）
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StudioMessage {
    pub id: String,
    pub session_id: String,
    pub sequence_no: i64,
    pub role: String,
    pub content_type: String,
    pub text: String,
    pub reasoning_text: String,
    pub prompt_tokens: Option<i64>,
    pub completion_tokens: Option<i64>,
    pub generate_seconds: Option<f64>,
    pub download_seconds: Option<f64>,
    pub search_summary: Option<String>,
    pub timestamp: String,
    pub is_deleted: bool,
}

/// 消息媒体引用（对应 studio_media_refs 表，对齐 C# MediaRefRecord）
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StudioMediaRef {
    pub id: i64,
    pub message_id: String,
    pub media_path: String,
    pub media_kind: String,
    pub sort_order: i64,
    pub preview_path: Option<String>,
}

/// 消息引用（对应 studio_citations 表，对齐 C# CitationRecord）
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StudioCitation {
    pub id: i64,
    pub message_id: String,
    pub citation_number: i64,
    pub title: String,
    pub url: String,
    pub snippet: String,
    pub hostname: String,
}

/// 消息附件（对应 studio_attachments 表，对齐 C# AttachmentRecord）
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StudioAttachment {
    pub id: i64,
    pub message_id: String,
    pub attachment_type: String,
    pub file_name: String,
    pub file_path: String,
    pub file_size: i64,
    pub sort_order: i64,
}

/// 会话任务（对应 studio_tasks 表，对齐 C# TaskRecord）
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StudioTask {
    pub id: String,
    pub session_id: String,
    pub task_type: String,
    pub status: String,
    pub prompt: String,
    pub progress: f64,
    pub result_file_path: Option<String>,
    pub error_message: Option<String>,
    pub has_reference_input: bool,
    pub remote_video_id: Option<String>,
    pub remote_video_api_mode: Option<String>,
    pub remote_generation_id: Option<String>,
    pub remote_download_url: Option<String>,
    pub generate_seconds: Option<f64>,
    pub download_seconds: Option<f64>,
    pub created_at: String,
    pub updated_at: String,
}

/// 会话资产（对应 studio_assets 表，对齐 C# AssetRecord）
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StudioAsset {
    pub asset_id: String,
    pub session_id: String,
    pub group_id: String,
    pub kind: String,
    pub workflow: String,
    pub file_name: String,
    pub file_path: String,
    pub preview_path: String,
    pub prompt_text: String,
    pub file_size: Option<i64>,
    pub mime_type: Option<String>,
    pub width: Option<i64>,
    pub height: Option<i64>,
    pub duration_ms: Option<i64>,
    pub created_at: String,
    pub modified_at: String,
    pub storage_scope: String,
    pub derived_from_session_id: Option<String>,
    pub derived_from_session_name: Option<String>,
    pub derived_from_asset_id: Option<String>,
    pub derived_from_asset_file_name: Option<String>,
    pub derived_from_asset_kind: Option<String>,
    pub derived_from_reference_role: Option<String>,
}

/// 参考图记录（对应 studio_reference_images 表，对齐 C# ReferenceImageRecord）
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StudioReferenceImage {
    pub id: String,
    pub session_id: String,
    pub file_path: String,
    pub sort_order: i64,
    pub width: Option<i64>,
    pub height: Option<i64>,
    pub created_at: String,
}

/// 批量加载结果：消息 + 关联数据（对齐 C# SessionMessagesBundle）
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StudioSessionBundle {
    pub messages: Vec<StudioMessage>,
    pub media_refs: HashMap<String, Vec<StudioMediaRef>>,
    pub citations: HashMap<String, Vec<StudioCitation>>,
    pub attachments: HashMap<String, Vec<StudioAttachment>>,
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  听析中心（AudioLab）模型 — 对齐 C# StorageModels.cs
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

/// 音频文件记录（对应 audio_files 表，对齐 C# AudioItemRecord 全字段）
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AudioFile {
    pub id: String,
    pub display_name: String,
    pub source_path: String,
    pub mp3_path: Option<String>,
    pub sample_rate: i64,
    pub channels: i64,
    pub duration_ms: i64,
    pub file_size_bytes: i64,
    pub sha256: String,
    pub imported_at: String,
    pub last_opened_at: Option<String>,
    pub is_legacy_import: bool,
    pub legacy_source_path: Option<String>,
    pub import_batch_id: Option<String>,
    /// 关联的 studio_session id（通过 source_asset_id JOIN 获得，非 DB 原生列）
    #[serde(default)]
    pub session_id: Option<String>,
}

/// 转录记录（对应 audio_transcripts 表）
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AudioTranscript {
    pub id: String,
    pub session_id: String,
    pub audio_file_id: String,
    pub language: String,
    pub raw_json: Option<String>,
    pub parser_kind: String,
    pub created_at: String,
}

/// 转录段落（对应 audio_segments 表，对齐 C# TranscriptSegment）
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AudioSegment {
    pub id: String,
    pub transcript_id: String,
    pub sequence: i64,
    pub speaker: String,
    pub speaker_index: i64,
    pub start_ms: i64,
    pub end_ms: i64,
    pub text: String,
    pub confidence: Option<f64>,
}

/// 阶段产出（对应 audio_stage_outputs 表，对齐 C# StageContent / StageContentState）
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AudioStageOutput {
    pub id: String,
    pub session_id: String,
    pub stage_key: String,
    pub content_markdown: String,
    pub status: String,
    pub error_message: Option<String>,
    pub model_ref: Option<String>,
    pub generated_at: Option<String>,
    pub custom_stage_key: Option<String>,
    pub custom_is_mindmap: Option<bool>,
}

/// 深度研究 topic（对应 audio_research_topics 表）
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AudioResearchTopic {
    pub id: String,
    pub session_id: String,
    pub title: String,
    pub description: String,
    pub status: String,
    pub report_markdown: Option<String>,
    pub created_at: String,
}

/// 自动标签（对应 audio_auto_tags 表）
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AudioAutoTag {
    pub id: String,
    pub session_id: String,
    pub tag: String,
    pub source: String,
    pub created_at: String,
}

/// 阶段预设（对应 audio_stage_presets 表，对齐 C# AudioLabStagePreset）
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AudioStagePreset {
    pub id: String,
    pub stage: String,
    pub display_name: String,
    pub system_prompt: String,
    pub show_in_tab: bool,
    pub include_in_batch: bool,
    pub is_enabled: bool,
    pub display_mode: String,
    pub sort_order: i64,
}

/// 懒加载 bundle（audiolab_get_bundle 返回）
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AudioLabBundle {
    pub file: AudioFile,
    pub transcript: Option<AudioTranscript>,
    pub segments: Vec<AudioSegment>,
    pub auto_tags: Vec<AudioAutoTag>,
    pub stage_outputs: Vec<AudioStageOutput>,
    pub research_topics: Vec<AudioResearchTopic>,
    pub custom_presets: Vec<AudioStagePreset>,
}

/// 播放打开返回信息
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AudioPlaybackInfo {
    pub file_id: String,
    pub playback_path: String,
    pub duration_ms: i64,
    pub display_name: String,
}

//  实时翻译数据模型（对齐 C# TranslationHistoryRepository.cs）
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

/// 翻译会话（对应 translation_sessions 表）
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct TranslationSession {
    pub id: String,
    pub started_at: String,
    pub stopped_at: Option<String>,
    pub source_lang: String,
    /// JSON 数组字符串，如 '["en","ja"]'
    pub target_langs: String,
    pub provider: String,
    pub status: String,
}

/// 翻译片段（对应 translation_segments 表，字段完全对齐 C#）
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct TranslationSegment {
    pub id: String,
    pub session_id: String,
    pub sequence: i64,
    pub original_text: String,
    pub translated_text: String,
    pub target_lang: String,
    pub started_at: Option<String>,
    pub ended_at: Option<String>,
    pub is_bookmarked: bool,
    pub bookmark_note: Option<String>,
    pub audio_path: Option<String>,
    pub raw_event_json: Option<String>,
}

/// 支持的语言条目
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SupportedLanguage {
    pub code: String,
    pub label: String,
    /// "source" | "target" | "both"
    pub kind: String,
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  媒体中心（Media Center）— 对齐 C# MediaCenterV2ViewModel / SessionContentRepository
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

/// 工作区元数据（复用 studio_sessions，靠 session_type='canvas_image'/'canvas_video' 区分）
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CenterWorkspace {
    pub id: String,
    pub session_type: String,
    pub name: String,
    pub is_deleted: bool,
    pub created_at: String,
    pub updated_at: String,
    pub last_accessed_at: Option<String>,
    pub current_round_id: Option<String>,
    pub round_count: i64,
    pub asset_count: i64,
    pub has_running_task: bool,
}

/// 生成轮次（对应 canvas_rounds 表，对齐 C# 结果集概念）
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CanvasRound {
    pub id: String,
    pub session_id: String,
    pub round_index: i64,
    pub prompt: String,
    pub params_json: String,
    pub model_ref: String,
    pub created_at: String,
    pub status: String,
}

/// 轮次资产关联（对应 canvas_round_assets 表）
#[derive(Debug, Clone, Serialize, Deserialize)]
#[allow(dead_code)]
pub struct CanvasRoundAsset {
    pub id: String,
    pub round_id: String,
    pub asset_id: String,
    pub sequence: i64,
    pub is_selected: bool,
}

/// 工作区完整 bundle（懒加载时一次性拉取）
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CenterWorkspaceBundle {
    pub workspace: CenterWorkspace,
    pub rounds: Vec<CanvasRound>,
    pub current_round_assets: Vec<CenterAssetDetail>,
    pub reference_images: Vec<StudioReferenceImage>,
    pub running_tasks: Vec<StudioTask>,
}

/// 资产详情（用于前端网格展示）
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CenterAssetDetail {
    pub id: String,
    pub round_id: String,
    pub asset_id: String,
    pub sequence: i64,
    pub is_selected: bool,
    pub file_path: String,
    pub preview_path: String,
    pub kind: String,
    pub width: Option<i64>,
    pub height: Option<i64>,
    pub duration_ms: Option<i64>,
    pub created_at: String,
}

/// 视频能力组合条目
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct VideoCapabilityEntry {
    pub aspect_ratio: String,
    pub resolution: String,
    pub duration_seconds: Vec<i64>,
    pub max_count: i64,
}

/// 批量导出结果
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ExportResult {
    pub copied: i64,
    pub failed: i64,
}
