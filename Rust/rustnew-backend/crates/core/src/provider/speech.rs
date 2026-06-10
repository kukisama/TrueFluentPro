//! Azure Speech 连接参数解析。
//!
//! core 层只负责把 `AiEndpoint` 解析成「区域 / 密钥 / 自定义终结点」三要素，
//! 真正驱动 speech-sdk 的识别器在 desktop 层完成，从而让 core 保持平台无关、可单测。

use crate::endpoint::AiEndpoint;
use crate::error::{CoreError, Result};

/// 解析后的 Speech 连接参数。
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct SpeechConnection {
    pub subscription_key: String,
    /// 区域，如 `southeastasia` / `chinanorth3`
    pub region: String,
    /// 自定义终结点（可选，优先于 region）
    pub endpoint: String,
}

impl SpeechConnection {
    /// 从 Azure Speech 终结点解析连接参数。
    pub fn from_endpoint(ep: &AiEndpoint) -> Result<Self> {
        if !ep.is_speech() {
            return Err(CoreError::CapabilityUnsupported(format!(
                "终结点 {} 不是 Azure Speech 类型",
                ep.id
            )));
        }

        let key = ep.speech_subscription_key.trim().to_string();
        if key.is_empty() {
            return Err(CoreError::Config("Speech 订阅密钥为空".into()));
        }

        let mut region = ep.speech_region.trim().to_string();
        let endpoint = ep.speech_endpoint.trim().to_string();

        // 区域为空时尝试从终结点 URL 推断（如 https://southeastasia.api.cognitive.microsoft.com）
        if region.is_empty() && !endpoint.is_empty() {
            if let Some(r) = parse_region_from_endpoint(&endpoint) {
                region = r;
            }
        }

        if region.is_empty() && endpoint.is_empty() {
            return Err(CoreError::Config("Speech 区域与终结点均为空".into()));
        }

        Ok(Self {
            subscription_key: key,
            region,
            endpoint,
        })
    }

    /// 是否为中国区（21Vianet）。
    pub fn is_china(&self) -> bool {
        self.endpoint.contains(".azure.cn") || self.region.starts_with("china")
    }
}

/// 从终结点 URL 推断区域。
///
/// 形如 `https://<region>.api.cognitive.microsoft.com` 或
/// `https://<region>.stt.speech.microsoft.com`。
pub fn parse_region_from_endpoint(endpoint: &str) -> Option<String> {
    let host = endpoint
        .trim()
        .trim_start_matches("https://")
        .trim_start_matches("http://");
    let host = host.split('/').next().unwrap_or(host);
    let first = host.split('.').next()?;
    if first.is_empty() {
        None
    } else {
        Some(first.to_string())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parse_region() {
        assert_eq!(
            parse_region_from_endpoint("https://southeastasia.api.cognitive.microsoft.com"),
            Some("southeastasia".to_string())
        );
        assert_eq!(
            parse_region_from_endpoint("https://chinanorth3.api.cognitive.azure.cn/"),
            Some("chinanorth3".to_string())
        );
    }
}
