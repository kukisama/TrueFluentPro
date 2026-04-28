//! 终结点资料包加载器
//!
//! 将 C# 侧的 JSON 资料包（`Assets/EndpointProfiles/Profiles/*.json`）
//! 通过 `include_str!` 编译期嵌入，运行时反序列化为 `VendorProfile`。
//! 两边共用同一份 JSON，保持一致。

use std::collections::HashMap;
use serde::Deserialize;
use crate::models::{EndpointType, VendorProfile};

// ─── 编译期嵌入 JSON ───

const AZURE_OPENAI_JSON: &str =
    include_str!("../assets/EndpointProfiles/Profiles/azure-openai.json");
const APIM_GATEWAY_JSON: &str =
    include_str!("../assets/EndpointProfiles/Profiles/apim-gateway.json");
const OPENAI_COMPATIBLE_JSON: &str =
    include_str!("../assets/EndpointProfiles/Profiles/openai-compatible.json");
const AZURE_SPEECH_JSON: &str =
    include_str!("../assets/EndpointProfiles/Profiles/azure-speech.json");

// ─── JSON 结构体（匹配 C# EndpointProfile JSON schema）───

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
#[allow(dead_code)]
struct RawProfile {
    #[serde(default)]
    schema_version: u32,
    id: String,
    #[serde(default)]
    vendor: String,
    endpoint_type: String,
    display_name: String,
    #[serde(default)]
    subtitle: String,
    #[serde(default)]
    glyph: String,
    #[serde(default)]
    summary: String,
    #[serde(default)]
    default_name_prefix: String,
    #[serde(default)]
    tags: Vec<String>,

    #[serde(default)]
    overrides: RawOverrides,
    #[serde(default)]
    fallbacks: RawFallbacks,
    #[serde(default)]
    defaults: RawDefaults,
    #[serde(default)]
    auth: RawAuth,
    #[serde(default)]
    special_policies: RawSpecialPolicies,

    // 各能力路由配置
    #[serde(default)]
    model_discovery: Option<RawModelDiscovery>,
    #[serde(default)]
    text: Option<RawTextRoutes>,
    #[serde(default)]
    image: Option<RawImageRoutes>,
    #[serde(default)]
    audio: Option<RawAudioRoutes>,
    #[serde(default)]
    speech: Option<RawSpeechRoutes>,
    #[serde(default)]
    video: Option<RawVideoRoutes>,
}

#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
#[allow(dead_code)]
struct RawOverrides {
    #[serde(default)]
    auth: RawOverridesAuth,
    #[serde(default)]
    routes: RawOverridesRoutes,
    #[serde(default)]
    version: RawOverridesVersion,
}

#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
#[allow(dead_code)]
struct RawOverridesAuth {
    #[serde(default)]
    default_mode: String,
    #[serde(default)]
    allowed_modes: Vec<String>,
    #[serde(default)]
    default_api_key_header_mode: String,
    #[serde(default)]
    allowed_api_key_header_modes: Vec<String>,
}

#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
#[allow(dead_code)]
struct RawOverridesRoutes {
    #[serde(default)]
    text: Option<RawOverridesTextRoute>,
    #[serde(default)]
    model_discovery: Option<RawOverridesModelDiscovery>,
    #[serde(default)]
    audio: Option<RawOverridesAudioRoute>,
    #[serde(default)]
    speech: Option<RawOverridesSpeechRoute>,
    #[serde(default)]
    image: Option<RawOverridesImageRoute>,
    #[serde(default)]
    video: Option<RawOverridesVideoRoute>,
}

#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
#[allow(dead_code)]
struct RawOverridesTextRoute {
    #[serde(default)]
    primary_protocol: String,
    #[serde(default)]
    primary_url: String,
    #[serde(default)]
    deployment_primary_url: String,
}

#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
#[allow(dead_code)]
struct RawOverridesModelDiscovery {
    #[serde(default)]
    primary_url: String,
}

#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
#[allow(dead_code)]
struct RawOverridesAudioRoute {
    #[serde(default)]
    primary_url: String,
    #[serde(default)]
    default_api_version: String,
}

#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
#[allow(dead_code)]
struct RawOverridesSpeechRoute {
    #[serde(default)]
    primary_url: String,
    #[serde(default)]
    default_api_version: String,
}

#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
#[allow(dead_code)]
struct RawOverridesImageRoute {
    #[serde(default)]
    generate_primary_url: String,
    #[serde(default)]
    edit_primary_url: String,
    #[serde(default)]
    deployment_generate_primary_url: String,
}

#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
#[allow(dead_code)]
struct RawOverridesVideoRoute {
    #[serde(default)]
    default_mode: String,
    #[serde(default)]
    create_primary_url: String,
    #[serde(default)]
    poll_primary_url: String,
    #[serde(default)]
    download_primary_url: String,
    #[serde(default)]
    download_video_content_primary_url: String,
    // ... 其余字段按需添加
}

#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
#[allow(dead_code)]
struct RawOverridesVersion {
    #[serde(default)]
    endpoint_api_version: String,
    #[serde(default)]
    text_api_version: String,
    #[serde(default)]
    audio_api_version: String,
    #[serde(default)]
    speech_api_version: String,
    #[serde(default)]
    video_api_version: String,
}

#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
#[allow(dead_code)]
struct RawDefaults {
    #[serde(default)]
    provider_type: String,
    #[serde(default)]
    auth_mode: String,
    #[serde(default)]
    api_key_header_mode: String,
    #[serde(default)]
    text_api_protocol_mode: String,
    #[serde(default)]
    image_api_route_mode: String,
    #[serde(default)]
    api_version: String,
    #[serde(default)]
    supports_aad: bool,
    #[serde(default)]
    clear_azure_identity_fields: bool,
}

#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
#[allow(dead_code)]
struct RawAuth {
    #[serde(default)]
    supported_modes: Vec<String>,
    #[serde(default)]
    supported_api_key_header_modes: Vec<String>,
    #[serde(default)]
    default_mode: String,
    #[serde(default)]
    default_api_key_header_mode: String,
    #[serde(default)]
    supports_subscription_key_query_fallback: bool,
}

#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
#[allow(dead_code)]
struct RawSpecialPolicies {
    #[serde(default)]
    allow_apim_subscription_key_query_retry: bool,
    #[serde(default)]
    allow_preview_fallback: bool,
}

#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
#[allow(dead_code)]
struct RawFallbacks {
    #[serde(default)]
    model_discovery: Vec<String>,
    #[serde(default)]
    text: Vec<String>,
    #[serde(default)]
    image_generate: Vec<String>,
    #[serde(default)]
    image_edit: Vec<String>,
    #[serde(default)]
    audio: Vec<String>,
    #[serde(default)]
    speech: Vec<String>,
    #[serde(default)]
    video_create: Vec<String>,
    #[serde(default)]
    video_poll: Vec<String>,
    #[serde(default)]
    video_download: Vec<String>,
    #[serde(default)]
    video_download_video_content: Vec<String>,
}

#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
#[allow(dead_code)]
struct RawModelDiscovery {
    #[serde(default)]
    url_candidates: Vec<String>,
}

#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
#[allow(dead_code)]
struct RawTextRoutes {
    #[serde(default)]
    preferred_protocol: String,
    #[serde(default)]
    deployment_chat_completions_url_candidates: Vec<String>,
    #[serde(default)]
    responses_url_candidates: Vec<String>,
    #[serde(default)]
    chat_completions_v1_url_candidates: Vec<String>,
    #[serde(default)]
    chat_completions_raw_url_candidates: Vec<String>,
}

#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
#[allow(dead_code)]
struct RawImageRoutes {
    #[serde(default)]
    deployment_generate_url_candidates: Vec<String>,
    #[serde(default)]
    generate_url_candidates: Vec<String>,
    #[serde(default)]
    edit_url_candidates: Vec<String>,
}

#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
#[allow(dead_code)]
struct RawAudioRoutes {
    #[serde(default)]
    default_api_version: String,
    #[serde(default)]
    transcription_url_candidates: Vec<String>,
}

#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
#[allow(dead_code)]
struct RawSpeechRoutes {
    #[serde(default)]
    default_api_version: String,
    #[serde(default)]
    synthesis_url_candidates: Vec<String>,
}

#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
#[allow(dead_code)]
struct RawVideoRoutes {
    #[serde(default)]
    supported_api_modes: Vec<String>,
    #[serde(default)]
    create_url_candidates: Vec<String>,
    #[serde(default)]
    poll_url_candidates: Vec<String>,
    #[serde(default)]
    download_url_candidates: Vec<String>,
    #[serde(default)]
    download_video_content_url_candidates: Vec<String>,
}

// ─── 转换逻辑 ───

fn parse_endpoint_type(s: &str) -> Option<EndpointType> {
    match s {
        "AzureOpenAi" => Some(EndpointType::AzureOpenAi),
        "ApiManagementGateway" => Some(EndpointType::ApiManagementGateway),
        "OpenAiCompatible" => Some(EndpointType::OpenAiCompatible),
        "AzureSpeech" => Some(EndpointType::AzureSpeech),
        _ => None,
    }
}

/// 将 C# apiKeyHeaderMode 字符串映射为 Rust 端使用的值
fn map_auth_header(mode: &str) -> String {
    match mode {
        "ApiKeyHeader" => "api_key".into(),
        "Bearer" => "bearer".into(),
        _ => "auto".into(),
    }
}

/// 将 C# textApiProtocolMode 映射为 Rust 端 text_protocol 值
fn map_text_protocol(mode: &str) -> String {
    match mode {
        "Responses" => "responses".into(),
        "ChatCompletionsV1" => "chat_completions".into(),
        "Auto" => "auto".into(),
        _ => "chat_completions".into(),
    }
}

fn convert_raw_to_vendor_profile(raw: RawProfile) -> Option<VendorProfile> {
    let endpoint_type = parse_endpoint_type(&raw.endpoint_type)?;

    // ── 从 defaults 取基础值 ──
    let default_auth_header = map_auth_header(&raw.defaults.api_key_header_mode);
    let default_api_version = raw.defaults.api_version.clone();
    let supports_aad = raw.defaults.supports_aad;
    let text_protocol = map_text_protocol(&raw.defaults.text_api_protocol_mode);

    // ── 模型发现 URL ──
    let model_discovery_urls = if let Some(ref md) = raw.model_discovery {
        md.url_candidates.clone()
    } else {
        vec![]
    };
    let supports_model_discovery = !model_discovery_urls.is_empty();

    // ── test_url_templates: 取 overrides.routes 的 primaryUrl ──
    let mut test_url_templates: HashMap<String, String> = HashMap::new();
    if let Some(ref routes) = raw.overrides.routes.text {
        if !routes.primary_url.is_empty() {
            test_url_templates.insert("text".into(), routes.primary_url.clone());
        }
    }
    if let Some(ref routes) = raw.overrides.routes.image {
        if !routes.generate_primary_url.is_empty() {
            test_url_templates.insert("image".into(), routes.generate_primary_url.clone());
        }
    }
    if let Some(ref routes) = raw.overrides.routes.audio {
        if !routes.primary_url.is_empty() {
            test_url_templates.insert("audio".into(), routes.primary_url.clone());
        }
    }
    if let Some(ref routes) = raw.overrides.routes.speech {
        if !routes.primary_url.is_empty() {
            test_url_templates.insert("speech".into(), routes.primary_url.clone());
        }
    }

    // ── 文字 URL 候选: overrides.routes.text.primaryUrl + text.*UrlCandidates + fallbacks.text ──
    let mut text_url_candidates = Vec::new();
    // 1. overrides primary URL 优先
    if let Some(ref routes) = raw.overrides.routes.text {
        if !routes.primary_url.is_empty() {
            text_url_candidates.push(routes.primary_url.clone());
        }
        if !routes.deployment_primary_url.is_empty()
            && !text_url_candidates.contains(&routes.deployment_primary_url)
        {
            text_url_candidates.push(routes.deployment_primary_url.clone());
        }
    }
    // 2. text section candidates (按 protocol 优先级)
    if let Some(ref txt) = raw.text {
        let ordered = match text_protocol.as_str() {
            "responses" => vec![
                &txt.responses_url_candidates,
                &txt.chat_completions_v1_url_candidates,
                &txt.deployment_chat_completions_url_candidates,
                &txt.chat_completions_raw_url_candidates,
            ],
            _ => vec![
                &txt.chat_completions_v1_url_candidates,
                &txt.deployment_chat_completions_url_candidates,
                &txt.responses_url_candidates,
                &txt.chat_completions_raw_url_candidates,
            ],
        };
        for list in ordered {
            for url in list {
                if !text_url_candidates.contains(url) {
                    text_url_candidates.push(url.clone());
                }
            }
        }
    }
    // 3. fallbacks
    for url in &raw.fallbacks.text {
        if !text_url_candidates.contains(url) {
            text_url_candidates.push(url.clone());
        }
    }

    // ── 图片 URL 候选 ──
    let mut image_url_candidates = Vec::new();
    if let Some(ref routes) = raw.overrides.routes.image {
        if !routes.generate_primary_url.is_empty() {
            image_url_candidates.push(routes.generate_primary_url.clone());
        }
    }
    if let Some(ref img) = raw.image {
        for url in &img.generate_url_candidates {
            if !image_url_candidates.contains(url) {
                image_url_candidates.push(url.clone());
            }
        }
        for url in &img.edit_url_candidates {
            if !image_url_candidates.contains(url) {
                image_url_candidates.push(url.clone());
            }
        }
    }
    for url in &raw.fallbacks.image_generate {
        if !image_url_candidates.contains(url) {
            image_url_candidates.push(url.clone());
        }
    }
    for url in &raw.fallbacks.image_edit {
        if !image_url_candidates.contains(url) {
            image_url_candidates.push(url.clone());
        }
    }

    // ── 视频 URL 候选 ──
    let mut video_url_candidates = Vec::new();
    if let Some(ref v) = raw.video {
        for url in &v.create_url_candidates {
            if !video_url_candidates.contains(url) {
                video_url_candidates.push(url.clone());
            }
        }
    }

    // ── 音频 URL 候选 ──
    let mut audio_url_candidates = Vec::new();
    if let Some(ref routes) = raw.overrides.routes.audio {
        if !routes.primary_url.is_empty() {
            audio_url_candidates.push(routes.primary_url.clone());
        }
    }
    if let Some(ref a) = raw.audio {
        for url in &a.transcription_url_candidates {
            if !audio_url_candidates.contains(url) {
                audio_url_candidates.push(url.clone());
            }
        }
    }
    for url in &raw.fallbacks.audio {
        if !audio_url_candidates.contains(url) {
            audio_url_candidates.push(url.clone());
        }
    }

    // ── 语音合成 URL 候选 ──
    let mut speech_url_candidates = Vec::new();
    if let Some(ref routes) = raw.overrides.routes.speech {
        if !routes.primary_url.is_empty() {
            speech_url_candidates.push(routes.primary_url.clone());
        }
    }
    if let Some(ref s) = raw.speech {
        for url in &s.synthesis_url_candidates {
            if !speech_url_candidates.contains(url) {
                speech_url_candidates.push(url.clone());
            }
        }
    }
    for url in &raw.fallbacks.speech {
        if !speech_url_candidates.contains(url) {
            speech_url_candidates.push(url.clone());
        }
    }

    // ── auth 信息 ──
    let supported_auth_modes = if raw.auth.supported_modes.is_empty() {
        raw.overrides.auth.allowed_modes.clone()
    } else {
        raw.auth.supported_modes.clone()
    };

    // 短 label 用 endpointType 首两字作 badge
    let badge = match endpoint_type {
        EndpointType::AzureOpenAi => "AZ",
        EndpointType::ApiManagementGateway => "AP",
        EndpointType::OpenAiCompatible => "OA",
        EndpointType::AzureSpeech => "SP",
        _ => "??",
    };

    Some(VendorProfile {
        endpoint_type,
        label: raw.display_name.clone(),
        badge: badge.into(),
        subtitle: raw.subtitle.clone(),
        glyph: raw.glyph.clone(),
        default_auth_header,
        default_api_version,
        supports_aad,
        supports_model_discovery,
        model_discovery_urls,
        test_url_templates,
        text_url_candidates,
        image_url_candidates,
        video_url_candidates,
        text_protocol,
        audio_url_candidates,
        speech_url_candidates,
        supported_auth_modes,
        // 保存完整 JSON 以供前端直接使用
        raw_json: None,
    })
}

/// 加载所有内置厂商资料包（从嵌入的 JSON 解析）
pub fn load_profiles() -> Vec<VendorProfile> {
    let jsons = [
        ("azure-openai", AZURE_OPENAI_JSON),
        ("apim-gateway", APIM_GATEWAY_JSON),
        ("openai-compatible", OPENAI_COMPATIBLE_JSON),
        ("azure-speech", AZURE_SPEECH_JSON),
    ];

    let mut profiles = Vec::new();
    for (name, json) in &jsons {
        match serde_json::from_str::<RawProfile>(json) {
            Ok(raw) => {
                tracing::info!("✓ 已解析资料包 JSON: {name}");
                if let Some(p) = convert_raw_to_vendor_profile(raw) {
                    tracing::info!(
                        "  → {:?} | auth={} | api_ver={} | aad={} | text_urls={} | img_urls={} | protocol={}",
                        p.endpoint_type,
                        p.default_auth_header,
                        p.default_api_version,
                        p.supports_aad,
                        p.text_url_candidates.len(),
                        p.image_url_candidates.len(),
                        p.text_protocol,
                    );
                    profiles.push(p);
                } else {
                    tracing::warn!("⚠ 未知 endpointType，跳过资料包: {name}");
                }
            }
            Err(e) => {
                tracing::error!("✗ 解析资料包 JSON 失败 ({name}): {e}");
            }
        }
    }
    profiles
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_load_all_profiles() {
        let profiles = load_profiles();
        assert_eq!(profiles.len(), 4, "应该加载 4 个内置资料包");

        // Azure OpenAI
        let aoai = profiles.iter().find(|p| p.endpoint_type == EndpointType::AzureOpenAi).unwrap();
        assert_eq!(aoai.default_auth_header, "api_key");
        assert_eq!(aoai.default_api_version, "2024-02-01");
        assert!(aoai.supports_aad);
        assert_eq!(aoai.text_protocol, "responses");
        assert!(!aoai.text_url_candidates.is_empty());

        // APIM
        let apim = profiles.iter().find(|p| p.endpoint_type == EndpointType::ApiManagementGateway).unwrap();
        assert_eq!(apim.default_auth_header, "api_key");
        assert_eq!(apim.default_api_version, "2025-03-01-preview");
        assert!(!apim.supports_aad);
        assert_eq!(apim.text_protocol, "responses");

        // OpenAI Compatible
        let oai = profiles.iter().find(|p| p.endpoint_type == EndpointType::OpenAiCompatible).unwrap();
        assert_eq!(oai.default_auth_header, "bearer");
        assert!(oai.default_api_version.is_empty());
        assert!(!oai.supports_aad);
        assert_eq!(oai.text_protocol, "chat_completions");

        // Azure Speech
        let speech = profiles.iter().find(|p| p.endpoint_type == EndpointType::AzureSpeech).unwrap();
        assert_eq!(speech.default_auth_header, "api_key");
        assert!(!speech.supports_aad);
    }
}
