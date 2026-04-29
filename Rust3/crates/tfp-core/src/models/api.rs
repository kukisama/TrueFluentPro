use serde::{Deserialize, Serialize};
use std::collections::HashMap;

fn default_video_size() -> String { "1080x1920".into() }
fn default_video_duration() -> u32 { 10 }

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

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct RealtimeSessionConfig {
    pub source_lang: String,
    pub target_langs: Vec<String>,
    pub endpoint_id: String,
    pub enable_partial: bool,
    pub profanity_filter: bool,
    #[serde(default)]
    pub initial_silence_timeout_seconds: Option<u32>,
    #[serde(default)]
    pub end_silence_timeout_seconds: Option<u32>,
}

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
    #[serde(default)]
    pub text_model: Option<String>,
    #[serde(default)]
    pub image_model: Option<String>,
    #[serde(default)]
    pub previous_response_id: Option<String>,
    #[serde(default)]
    pub reference_image_path: Option<String>,
    #[serde(default)]
    pub image_edit_mode: Option<super::config::ImageEditMode>,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
pub struct ImageGenResult {
    #[serde(default)]
    pub url: Option<String>,
    #[serde(default)]
    pub base64: Option<String>,
    #[serde(default)]
    pub revised_prompt: Option<String>,
    #[serde(default)]
    pub response_id: Option<String>,
    #[serde(default)]
    pub request_url: String,
    #[serde(default)]
    pub attempted_urls: Vec<String>,
    #[serde(default)]
    pub generate_seconds: f64,
    #[serde(default)]
    pub download_seconds: f64,
    #[serde(default)]
    pub actual_input_tokens: Option<u32>,
    #[serde(default)]
    pub actual_output_tokens: Option<u32>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "snake_case")]
pub enum VideoApiMode {
    SoraJobs,
    Videos,
}

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

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct VideoGenResult {
    pub video_id: String,
    pub status: String,
    pub download_url: Option<String>,
    pub file_path: Option<String>,
    pub generate_seconds: Option<f64>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ChatMessage {
    pub role: String,
    pub content: serde_json::Value,
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
    pub request_summary: Option<String>,
    pub duration_ms: u64,
    pub test_branch: Option<String>,
    pub urls_tried: Vec<String>,
}

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

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DiscoveredModel {
    pub id: String,
    pub display_name: Option<String>,
    pub owned_by: Option<String>,
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
pub struct TranscriptSegment {
    pub text: String,
    pub start_ms: u64,
    pub end_ms: u64,
    pub confidence: f64,
    pub speaker: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct VoiceInfo {
    pub id: String,
    pub name: String,
    pub locale: String,
    pub gender: String,
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

// ── Audio library & task engine models ──

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

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
pub struct MonitorGlobalStats {
    pub total_executions: i64,
    pub billable_executions: i64,
    pub billable_tokens_in: i64,
    pub billable_tokens_out: i64,
}

fn default_billing_status() -> String { "Committed".to_string() }

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
    #[serde(default = "default_billing_status")]
    pub status: String,
}

/// Aggregated billing summary across all records.
#[derive(Debug, Clone, Serialize, Deserialize, Default)]
pub struct BillingSummary {
    pub total_prompt_tokens: i64,
    pub total_completion_tokens: i64,
    pub total_cost_usd: f64,
    pub record_count: i64,
    pub by_model: Vec<BillingByModel>,
}

/// Per-model breakdown within a billing summary.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct BillingByModel {
    pub model_id: String,
    pub prompt_tokens: i64,
    pub completion_tokens: i64,
    pub cost_usd: f64,
    pub count: i64,
}

/// Legacy translation history record (deprecated, kept for backward compat)
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
