//! 语音资源模型。
//!
//! 对应 C# `Models/SpeechResource.cs`，描述一个语音服务连接（厂商、连接类型、能力、凭据）。
//! 实时翻译先只用到 Microsoft Speech；其余厂商/连接类型保留以便后续扩展。

use serde::{Deserialize, Serialize};

use crate::capability::SpeechCapability;

/// 语音厂商类型。序列化值与 C# 枚举名保持一致。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
pub enum SpeechVendorType {
    #[default]
    Microsoft,
    #[serde(rename = "OpenAI")]
    OpenAi,
    Tencent,
    Alibaba,
    Other,
}

/// 语音连接类型。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
pub enum SpeechConnectorType {
    /// 原生 Microsoft 语音 SDK（密钥 + 区域 / 终结点）
    #[default]
    MicrosoftSpeech,
    /// 走 AI 网关 / OpenAI 兼容协议的语音
    AiSpeech,
    /// 自定义
    CustomSpeech,
}

/// 鉴权方式。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum SpeechAuthMode {
    #[default]
    ApiKey,
    Aad,
}

/// 一个语音资源。
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SpeechResource {
    #[serde(default)]
    pub id: String,
    pub name: String,
    #[serde(default)]
    pub vendor: SpeechVendorType,
    #[serde(default)]
    pub connector_type: SpeechConnectorType,
    #[serde(default = "default_true")]
    pub is_enabled: bool,
    #[serde(default)]
    pub capabilities: SpeechCapability,
    #[serde(default)]
    pub auth_mode: SpeechAuthMode,
    #[serde(default)]
    pub subscription_name: String,
    #[serde(default)]
    pub subscription_key: String,
    #[serde(default)]
    pub service_region: String,
    #[serde(default)]
    pub endpoint: String,
}

fn default_true() -> bool {
    true
}

impl Default for SpeechResource {
    fn default() -> Self {
        Self {
            id: new_resource_id(),
            name: String::new(),
            vendor: SpeechVendorType::Microsoft,
            connector_type: SpeechConnectorType::MicrosoftSpeech,
            is_enabled: true,
            capabilities: SpeechCapability::REALTIME_SPEECH_TO_TEXT,
            auth_mode: SpeechAuthMode::ApiKey,
            subscription_name: String::new(),
            subscription_key: String::new(),
            service_region: String::new(),
            endpoint: String::new(),
        }
    }
}

impl SpeechResource {
    /// 创建一个 Microsoft 实时语音资源。
    pub fn new_microsoft(name: impl Into<String>, key: impl Into<String>, region: impl Into<String>) -> Self {
        Self {
            name: name.into(),
            subscription_key: key.into(),
            service_region: region.into(),
            capabilities: SpeechCapability::REALTIME_SPEECH_TO_TEXT
                | SpeechCapability::BATCH_SPEECH_TO_TEXT,
            ..Default::default()
        }
    }

    /// 凭据是否完整可用。
    pub fn is_valid(&self) -> bool {
        if self.subscription_key.trim().is_empty() {
            return false;
        }
        !self.service_region.trim().is_empty() || !self.endpoint.trim().is_empty()
    }

    /// 是否中国区终结点（`.azure.cn`）。
    pub fn is_china(&self) -> bool {
        let ep = self.endpoint.to_ascii_lowercase();
        ep.contains(".azure.cn") || self.service_region.to_ascii_lowercase().starts_with("china")
    }

    /// 有效区域：优先 service_region，否则尝试从终结点解析。
    pub fn effective_region(&self) -> Option<String> {
        let r = self.service_region.trim();
        if !r.is_empty() {
            return Some(r.to_string());
        }
        parse_region_from_endpoint(&self.endpoint)
    }
}

/// 从形如 `wss://southeastasia.stt.speech.microsoft.com` /
/// `https://southeastasia.api.cognitive.microsoft.com` 的终结点中解析区域。
pub fn parse_region_from_endpoint(endpoint: &str) -> Option<String> {
    let ep = endpoint.trim();
    if ep.is_empty() {
        return None;
    }
    let without_scheme = ep
        .split_once("://")
        .map(|(_, rest)| rest)
        .unwrap_or(ep);
    let host = without_scheme.split(['/', ':']).next().unwrap_or("");
    let first = host.split('.').next().unwrap_or("");
    if first.is_empty() {
        None
    } else {
        Some(first.to_string())
    }
}

/// 生成一个时间可排序的资源 ID（ULID 风格，Crockford Base32）。
pub fn new_resource_id() -> String {
    crate::storage::new_ulid()
}
