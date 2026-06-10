//! AI / Speech 终结点模型（Provider 实例描述）。
//!
//! 移植自 C# `AiEndpoint` / `AiModelEntry` / `ModelReference`，
//! 用 Rust 枚举 + serde 重构，去掉了 MVVM 的 ObservableObject 样板。

use serde::{Deserialize, Serialize};

use crate::capability::{ModelCapability, SpeechCapability};

/// Provider 厂商类型。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
pub enum AiProviderType {
    #[default]
    OpenAiCompatible,
    AzureOpenAi,
}

/// 终结点 API 形态。决定 URL 拼接与鉴权方式。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
pub enum EndpointKind {
    #[default]
    OpenAiCompatible,
    AzureOpenAi,
    ApiManagementGateway,
    AzureSpeech,
}

impl EndpointKind {
    pub fn is_azure_openai(self) -> bool {
        matches!(self, EndpointKind::AzureOpenAi)
    }
    pub fn is_speech(self) -> bool {
        matches!(self, EndpointKind::AzureSpeech)
    }
    pub fn display_name(self) -> &'static str {
        match self {
            EndpointKind::OpenAiCompatible => "OpenAI Compatible",
            EndpointKind::AzureOpenAi => "Azure OpenAI",
            EndpointKind::ApiManagementGateway => "APIM 网关",
            EndpointKind::AzureSpeech => "Azure Speech",
        }
    }
}

/// 鉴权模式。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
pub enum AuthMode {
    #[default]
    ApiKey,
    /// Azure Entra ID (AAD) 令牌
    Aad,
}

/// API Key 携带方式。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
pub enum ApiKeyHeaderMode {
    #[default]
    Auto,
    /// `api-key: <key>`（Azure 风格）
    ApiKeyHeader,
    /// `Authorization: Bearer <key>`（OpenAI 风格）
    Bearer,
}

/// 文本对话协议。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
pub enum TextProtocol {
    #[default]
    Auto,
    /// `/v1/chat/completions`
    ChatCompletionsV1,
    /// 直接拼到 BaseUrl 的 chat/completions
    ChatCompletionsRaw,
    /// `/responses`（Responses API）
    Responses,
}

/// 单个模型条目。
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AiModelEntry {
    pub model_id: String,
    #[serde(default)]
    pub display_name: String,
    /// Azure 部署名（Azure OpenAI 需要）
    #[serde(default)]
    pub deployment_name: String,
    #[serde(default)]
    pub group_name: String,
    #[serde(default)]
    pub capabilities: ModelCapability,
}

impl AiModelEntry {
    /// 显示标题：优先 display_name，否则 model_id。
    pub fn title(&self) -> &str {
        if !self.display_name.trim().is_empty() {
            &self.display_name
        } else {
            &self.model_id
        }
    }
}

/// 功能分区对某个模型的引用（终结点ID + 模型ID）。
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ModelReference {
    pub endpoint_id: String,
    pub model_id: String,
}

impl ModelReference {
    pub fn is_empty(&self) -> bool {
        self.endpoint_id.trim().is_empty() || self.model_id.trim().is_empty()
    }
}

/// 一个 AI / Speech 终结点（Provider 实例）。
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AiEndpoint {
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default = "default_true")]
    pub is_enabled: bool,
    #[serde(default)]
    pub kind: EndpointKind,
    #[serde(default)]
    pub provider_type: AiProviderType,
    /// 绑定的厂商资料包 id（对齐 C# `AiEndpoint.ProfileId`）。
    #[serde(default)]
    pub profile_id: String,

    // --- 连接信息 ---
    #[serde(default)]
    pub base_url: String,
    #[serde(default)]
    pub api_key: String,
    #[serde(default)]
    pub api_version: String,

    // --- 鉴权 ---
    #[serde(default)]
    pub auth_mode: AuthMode,
    #[serde(default)]
    pub api_key_header_mode: ApiKeyHeaderMode,
    #[serde(default)]
    pub text_protocol: TextProtocol,
    #[serde(default)]
    pub azure_tenant_id: String,
    #[serde(default)]
    pub azure_client_id: String,

    // --- 模型列表 ---
    #[serde(default)]
    pub models: Vec<AiModelEntry>,

    // --- Azure Speech 专属 ---
    #[serde(default)]
    pub speech_subscription_key: String,
    #[serde(default)]
    pub speech_region: String,
    #[serde(default)]
    pub speech_endpoint: String,
    #[serde(default)]
    pub speech_capabilities: SpeechCapability,
}

fn default_true() -> bool {
    true
}

impl Default for AiEndpoint {
    fn default() -> Self {
        Self {
            id: String::new(),
            name: String::new(),
            is_enabled: true,
            kind: EndpointKind::default(),
            provider_type: AiProviderType::default(),
            profile_id: String::new(),
            base_url: String::new(),
            api_key: String::new(),
            api_version: String::new(),
            auth_mode: AuthMode::default(),
            api_key_header_mode: ApiKeyHeaderMode::default(),
            text_protocol: TextProtocol::default(),
            azure_tenant_id: String::new(),
            azure_client_id: String::new(),
            models: Vec::new(),
            speech_subscription_key: String::new(),
            speech_region: String::new(),
            speech_endpoint: String::new(),
            speech_capabilities: SpeechCapability::NONE,
        }
    }
}

impl AiEndpoint {
    /// 查找指定模型条目。
    pub fn find_model(&self, model_id: &str) -> Option<&AiModelEntry> {
        self.models.iter().find(|m| m.model_id == model_id)
    }

    /// 该终结点是否提供某种文本/图像等能力（聚合所有模型）。
    pub fn aggregate_capabilities(&self) -> ModelCapability {
        self.models
            .iter()
            .fold(ModelCapability::NONE, |acc, m| acc | m.capabilities)
    }

    pub fn is_speech(&self) -> bool {
        self.kind.is_speech()
    }
}
