use serde::{Deserialize, Serialize};
use std::collections::HashMap;

use super::settings::{
    MediaSettings, RecognitionSettings, StorageSettings, WebSearchSettings,
};

// ── Serde default helpers ──

fn default_true() -> bool { true }
fn default_max_turns() -> u32 { 20 }
fn default_auth_header_mode() -> String { "api_key".into() }
fn default_auth_mode() -> String { "api_key".into() }

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
pub struct ModelReference {
    #[serde(default)]
    pub endpoint_id: String,
    #[serde(default)]
    pub model_id: String,
}

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
    #[serde(default)]
    pub task_engine_concurrency: Option<u32>,
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

impl Default for AiSettings {
    fn default() -> Self {
        Self {
            insight_model: ModelReference::default(),
            summary_model: ModelReference::default(),
            quick_model: ModelReference::default(),
            review_model: ModelReference::default(),
            conversation_model: ModelReference::default(),
            intent_model: ModelReference::default(),
            insight_system_prompt: String::new(),
            enable_reasoning: true,
            max_conversation_turns: 20,
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
    pub api_version: Option<String>,
    pub region: Option<String>,
    #[serde(default)]
    pub models: Vec<AiModelEntry>,
    pub enabled: bool,
    #[serde(default = "default_auth_header_mode")]
    pub auth_header_mode: String,
    #[serde(default = "default_auth_mode")]
    pub auth_mode: String,
    #[serde(default)]
    pub azure_tenant_id: String,
    #[serde(default)]
    pub azure_client_id: String,
    #[serde(default)]
    pub speech_subscription_key: String,
    #[serde(default)]
    pub speech_region: String,
    #[serde(default)]
    pub speech_endpoint: String,
}

impl AiEndpoint {
    pub fn migrate_auth_header_mode(&mut self) {
        if self.auth_header_mode == "auto" {
            self.auth_header_mode = if self.is_azure() {
                "api_key".into()
            } else {
                "bearer".into()
            };
        }
    }

    pub fn is_azure(&self) -> bool {
        matches!(
            self.endpoint_type,
            EndpointType::AzureOpenAi
                | EndpointType::ApiManagementGateway
                | EndpointType::AzureSpeech
        )
    }

    pub fn is_speech(&self) -> bool {
        self.endpoint_type == EndpointType::AzureSpeech
    }

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
    AzureTranslator,
    DeepL,
    TencentCloud,
    AlibabaCloud,
    Custom,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct VendorProfile {
    pub endpoint_type: EndpointType,
    pub label: String,
    pub badge: String,
    pub subtitle: String,
    pub glyph: String,
    pub default_auth_header: String,
    pub default_api_version: String,
    #[serde(default)]
    pub supports_aad: bool,
    pub supports_model_discovery: bool,
    pub model_discovery_urls: Vec<String>,
    pub test_url_templates: HashMap<String, String>,
    #[serde(default)]
    pub text_url_candidates: Vec<String>,
    #[serde(default)]
    pub image_url_candidates: Vec<String>,
    #[serde(default)]
    pub video_url_candidates: Vec<String>,
    #[serde(default)]
    pub audio_url_candidates: Vec<String>,
    #[serde(default)]
    pub speech_url_candidates: Vec<String>,
    #[serde(default)]
    pub text_protocol: String,
    #[serde(default)]
    pub supported_auth_modes: Vec<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub raw_json: Option<serde_json::Value>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AiModelEntry {
    pub model_id: String,
    pub display_name: String,
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
