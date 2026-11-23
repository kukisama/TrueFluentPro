//! 传统微软 Speech 终结点解析与路由推导（专精支持中国区世纪互联 21Vianet 与国际版 Global）。
//!
//! 兼容用户输入的各种常见格式：
//! 1. 完整 STS 令牌地址：`https://chinanorth3.api.cognitive.azure.cn/sts/v1.0/issuetoken`
//! 2. 认知服务基础地址：`https://southeastasia.api.cognitive.microsoft.com` 或 `https://chinanorth3.api.cognitive.azure.cn`
//! 3. 语音专用域名：`wss://southeastasia.stt.speech.microsoft.com` / `wss://chinanorth3.stt.speech.azure.cn`
//! 4. 纯区域名简写：`southeastasia`、`eastasia`、`chinanorth3`、`chinaeast2`

use serde::{Deserialize, Serialize};
use thiserror::Error;
use url::Url;

use crate::secret;

/// 微软语音云环境类型。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum SpeechCloud {
    /// 国际版 Azure Global (`*.microsoft.com`)
    Global,
    /// 中国区世纪互联 21Vianet (`*.azure.cn`)
    China21V,
    /// 本机回环测试端点（仅自动化测试使用 `127.0.0.1` / `localhost`）
    LocalLoopback,
}

impl SpeechCloud {
    pub fn display_label(self) -> &'static str {
        match self {
            SpeechCloud::Global => "国际版 Global",
            SpeechCloud::China21V => "中国区 21Vianet",
            SpeechCloud::LocalLoopback => "本地测试",
        }
    }

    pub fn cognitive_host_suffix(self) -> &'static str {
        match self {
            SpeechCloud::Global | SpeechCloud::LocalLoopback => "api.cognitive.microsoft.com",
            SpeechCloud::China21V => "api.cognitive.azure.cn",
        }
    }

    pub fn speech_host_suffix(self) -> &'static str {
        match self {
            SpeechCloud::Global | SpeechCloud::LocalLoopback => "speech.microsoft.com",
            SpeechCloud::China21V => "speech.azure.cn",
        }
    }
}

#[derive(Debug, Error, PartialEq, Eq)]
pub enum SpeechEndpointError {
    #[error("终结点或区域不能为空")]
    EmptyEndpoint,
    #[error("无法从终结点解析有效的微软 Speech 区域，请使用如 https://southeastasia.api.cognitive.microsoft.com 或 https://chinanorth3.api.cognitive.azure.cn/sts/v1.0/issuetoken")]
    UnrecognizedEndpointFormat,
    #[error("订阅密钥不能为空")]
    EmptySubscriptionKey,
    #[error("无法加密订阅密钥，未保存密钥")]
    SubscriptionKeyProtectionFailed,
}

/// 解析后的标准微软 Speech 路由集合。
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ResolvedSpeechEndpoint {
    pub region: String,
    pub cloud: SpeechCloud,
    /// 标准认知服务基础地址，如 `https://chinanorth3.api.cognitive.azure.cn`
    pub cognitive_base_url: String,
    /// STS 换取 ****** 地址，如 `https://chinanorth3.api.cognitive.azure.cn/sts/v1.0/issueToken`
    pub sts_issue_token_url: String,
    /// Universal v2 WebSocket 地址（支持 Continuous LID 自动语种识别与实时翻译）
    pub universal_v2_ws_url: String,
    /// 标准单语种实时识别 WebSocket 地址（v1 conversation）
    pub stt_v1_ws_url: String,
    /// 标准实时语音翻译 WebSocket 地址（v1 translation）
    pub translation_v1_ws_url: String,
}

/// 解析用户输入的终结点字符串（或区域名），自动识别 21Vianet 与 Global。
pub fn parse_speech_endpoint(raw_input: &str) -> Result<ResolvedSpeechEndpoint, SpeechEndpointError> {
    let trimmed = raw_input.trim();
    if trimmed.is_empty() {
        return Err(SpeechEndpointError::EmptyEndpoint);
    }

    // 支持纯区域名输入，如 "southeastasia" 或 "chinanorth3"
    if !trimmed.contains("://") && !trimmed.contains('.') && !trimmed.contains('/') && !trimmed.contains(':') {
        let region = trimmed.to_ascii_lowercase();
        if !is_valid_region_token(&region) {
            return Err(SpeechEndpointError::UnrecognizedEndpointFormat);
        }
        let cloud = if region.starts_with("china") {
            SpeechCloud::China21V
        } else {
            SpeechCloud::Global
        };
        return Ok(build_standard_routes(&region, cloud));
    }

    let normalized = if trimmed.contains("://") {
        trimmed.to_string()
    } else {
        format!("https://{trimmed}")
    };

    let parsed = Url::parse(&normalized).map_err(|_| SpeechEndpointError::UnrecognizedEndpointFormat)?;
    let host = parsed
        .host_str()
        .ok_or(SpeechEndpointError::UnrecognizedEndpointFormat)?
        .to_ascii_lowercase();

    // 支持本地 127.0.0.1 / localhost 集成测试桩
    if host == "127.0.0.1" || host == "localhost" {
        let port_part = parsed.port().map(|p| format!(":{p}")).unwrap_or_default();
        let http_scheme = if parsed.scheme() == "https" || parsed.scheme() == "wss" {
            "https"
        } else {
            "http"
        };
        let ws_scheme = if http_scheme == "https" { "wss" } else { "ws" };
        let base_http = format!("{http_scheme}://{host}{port_part}");
        let base_ws = format!("{ws_scheme}://{host}{port_part}");
        return Ok(ResolvedSpeechEndpoint {
            region: "local".to_string(),
            cloud: SpeechCloud::LocalLoopback,
            cognitive_base_url: base_http.clone(),
            sts_issue_token_url: format!("{base_http}/sts/v1.0/issueToken"),
            universal_v2_ws_url: format!("{base_ws}/stt/speech/universal/v2"),
            stt_v1_ws_url: format!("{base_ws}/speech/recognition/conversation/cognitiveservices/v1"),
            translation_v1_ws_url: format!("{base_ws}/speech/translation/cognitiveservices/v1"),
        });
    }

    // 匹配传统微软 Speech 国际版与世纪互联 21Vianet 各类域名后缀：
    // - {region}.api.cognitive.azure.cn (/sts/v1.0/issuetoken)
    // - {region}.api.cognitive.microsoft.com (/sts/v1.0/issuetoken)
    // - {region}.stt.speech.azure.cn / {region}.s2s.speech.azure.cn / {region}.tts.speech.azure.cn
    // - {region}.stt.speech.microsoft.com / {region}.s2s.speech.microsoft.com / {region}.tts.speech.microsoft.com
    let known_suffixes: &[(&str, SpeechCloud)] = &[
        (".api.cognitive.azure.cn", SpeechCloud::China21V),
        (".stt.speech.azure.cn", SpeechCloud::China21V),
        (".s2s.speech.azure.cn", SpeechCloud::China21V),
        (".tts.speech.azure.cn", SpeechCloud::China21V),
        (".api.cognitive.microsoft.com", SpeechCloud::Global),
        (".stt.speech.microsoft.com", SpeechCloud::Global),
        (".s2s.speech.microsoft.com", SpeechCloud::Global),
        (".tts.speech.microsoft.com", SpeechCloud::Global),
    ];

    for &(suffix, cloud) in known_suffixes {
        if let Some(region) = host.strip_suffix(suffix) {
            if is_valid_region_token(region) {
                return Ok(build_standard_routes(region, cloud));
            }
        }
    }

    Err(SpeechEndpointError::UnrecognizedEndpointFormat)
}

fn is_valid_region_token(region: &str) -> bool {
    !region.is_empty()
        && region.len() <= 48
        && region
            .bytes()
            .all(|b| b.is_ascii_lowercase() || b.is_ascii_digit() || b == b'-')
}

fn build_standard_routes(region: &str, cloud: SpeechCloud) -> ResolvedSpeechEndpoint {
    let cog_suffix = cloud.cognitive_host_suffix();
    let speech_suffix = cloud.speech_host_suffix();
    let cognitive_base_url = format!("https://{region}.{cog_suffix}");
    ResolvedSpeechEndpoint {
        region: region.to_string(),
        cloud,
        sts_issue_token_url: format!("{cognitive_base_url}/sts/v1.0/issueToken"),
        cognitive_base_url,
        universal_v2_ws_url: format!("wss://{region}.stt.{speech_suffix}/stt/speech/universal/v2"),
        stt_v1_ws_url: format!(
            "wss://{region}.stt.{speech_suffix}/speech/recognition/conversation/cognitiveservices/v1"
        ),
        translation_v1_ws_url: format!(
            "wss://{region}.s2s.{speech_suffix}/speech/translation/cognitiveservices/v1"
        ),
    }
}

/// 一条已保存的传统微软 Speech 订阅配置。
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct SpeechSubscription {
    pub id: String,
    pub name: String,
    /// 用户输入的原始终结点（如 `https://chinanorth3.api.cognitive.azure.cn/sts/v1.0/issuetoken` 或 `https://southeastasia.api.cognitive.microsoft.com`）
    pub endpoint: String,
    /// 自动推导的区域名（如 `chinanorth3` / `southeastasia`）
    #[serde(default)]
    pub service_region: String,
    /// 磁盘存储的订阅密钥（Windows 下自动存为 `dpapi:v1:...` 密文）
    #[serde(default)]
    pub encrypted_key: String,
    #[serde(default = "default_true")]
    pub is_enabled: bool,
}

fn default_true() -> bool {
    true
}

impl SpeechSubscription {
    /// 创建一条新订阅并将明文密钥立即加密封装。
    pub fn create(
        name: impl Into<String>,
        endpoint: impl Into<String>,
        plain_key: &str,
    ) -> Result<Self, SpeechEndpointError> {
        let ep_str = endpoint.into().trim().to_string();
        let resolved = parse_speech_endpoint(&ep_str)?;
        let trimmed_key = plain_key.trim();
        if trimmed_key.is_empty() {
            return Err(SpeechEndpointError::EmptySubscriptionKey);
        }
        let encrypted_key = secret::protect_secret(trimmed_key)
            .map_err(|_| SpeechEndpointError::SubscriptionKeyProtectionFailed)?;
        Ok(Self {
            id: uuid::Uuid::new_v4().simple().to_string(),
            name: name.into().trim().to_string(),
            endpoint: ep_str,
            service_region: resolved.region,
            encrypted_key,
            is_enabled: true,
        })
    }

    /// 解析此订阅的完整路由信息。
    pub fn resolve_endpoint(&self) -> Result<ResolvedSpeechEndpoint, SpeechEndpointError> {
        if !self.endpoint.trim().is_empty() {
            parse_speech_endpoint(&self.endpoint)
        } else {
            parse_speech_endpoint(&self.service_region)
        }
    }

    /// 解密取得运行时使用的明文订阅密钥（严禁用于日志输出）。
    pub fn plain_subscription_key(&self) -> Result<String, SpeechEndpointError> {
        let raw = self.encrypted_key.trim();
        if raw.is_empty() {
            return Err(SpeechEndpointError::EmptySubscriptionKey);
        }
        let decrypted = secret::unprotect_secret(raw)
            .map_err(|_| SpeechEndpointError::EmptySubscriptionKey)?;
        if decrypted.trim().is_empty() {
            return Err(SpeechEndpointError::EmptySubscriptionKey);
        }
        Ok(decrypted)
    }

    /// 生成脱敏展示用的密钥掩码（如 `••••abcd`）。
    pub fn masked_key_hint(&self) -> String {
        match self.plain_subscription_key() {
            Ok(k) if k.chars().count() > 4 => {
                let suffix = k.char_indices().rev().nth(3).map(|(i, _)| &k[i..]).unwrap_or("");
                format!("••••••••{suffix}")
            }
            Ok(_) => "••••".to_string(),
            Err(_) => "未配置密钥".to_string(),
        }
    }

    /// 生成下拉框友好的显示名称。
    pub fn display_title(&self) -> String {
        match self.resolve_endpoint() {
            Ok(r) => format!("{} · {} ({})", self.name, r.region, r.cloud.display_label()),
            Err(_) => self.name.clone(),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_21v_issuetoken_url_exact_user_example() {
        let r = parse_speech_endpoint("https://chinanorth3.api.cognitive.azure.cn/sts/v1.0/issuetoken")
            .expect("21V issuetoken URL 必须成功解析");
        assert_eq!(r.region, "chinanorth3");
        assert_eq!(r.cloud, SpeechCloud::China21V);
        assert_eq!(
            r.sts_issue_token_url,
            "https://chinanorth3.api.cognitive.azure.cn/sts/v1.0/issueToken"
        );
        assert_eq!(
            r.universal_v2_ws_url,
            "wss://chinanorth3.stt.speech.azure.cn/stt/speech/universal/v2"
        );
        assert_eq!(
            r.stt_v1_ws_url,
            "wss://chinanorth3.stt.speech.azure.cn/speech/recognition/conversation/cognitiveservices/v1"
        );
    }

    #[test]
    fn parses_global_cognitive_base_url_exact_user_example() {
        let r = parse_speech_endpoint("https://southeastasia.api.cognitive.microsoft.com")
            .expect("Global 终结点必须成功解析");
        assert_eq!(r.region, "southeastasia");
        assert_eq!(r.cloud, SpeechCloud::Global);
        assert_eq!(
            r.sts_issue_token_url,
            "https://southeastasia.api.cognitive.microsoft.com/sts/v1.0/issueToken"
        );
        assert_eq!(
            r.universal_v2_ws_url,
            "wss://southeastasia.stt.speech.microsoft.com/stt/speech/universal/v2"
        );
    }

    #[test]
    fn parses_stt_wss_and_region_shorthand() {
        let cn_ws = parse_speech_endpoint("wss://chinaeast2.stt.speech.azure.cn/speech/universal/v2").unwrap();
        assert_eq!(cn_ws.region, "chinaeast2");
        assert_eq!(cn_ws.cloud, SpeechCloud::China21V);

        let shorthand = parse_speech_endpoint("chinanorth3").unwrap();
        assert_eq!(shorthand.cloud, SpeechCloud::China21V);

        let global_short = parse_speech_endpoint("eastus").unwrap();
        assert_eq!(global_short.cloud, SpeechCloud::Global);
    }

    #[test]
    fn rejects_unrelated_domains() {
        assert!(parse_speech_endpoint("https://api.openai.com/v1").is_err());
        assert!(parse_speech_endpoint("").is_err());
    }
}
