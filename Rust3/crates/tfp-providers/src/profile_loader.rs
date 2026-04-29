//! Endpoint profile loader
//!
//! Embeds C#-side JSON profiles (`Assets/EndpointProfiles/Profiles/*.json`)
//! via `include_str!` at compile time. Deserializes to `VendorProfile`.

use std::collections::HashMap;

use tfp_core::{EndpointType, VendorProfile};

use crate::profile_raw::RawProfile;

// ─── Compile-time embedded JSON ───

const AZURE_OPENAI_JSON: &str =
    include_str!("../assets/EndpointProfiles/Profiles/azure-openai.json");
const APIM_GATEWAY_JSON: &str =
    include_str!("../assets/EndpointProfiles/Profiles/apim-gateway.json");
const OPENAI_COMPATIBLE_JSON: &str =
    include_str!("../assets/EndpointProfiles/Profiles/openai-compatible.json");
const AZURE_SPEECH_JSON: &str =
    include_str!("../assets/EndpointProfiles/Profiles/azure-speech.json");

// ─── Conversion helpers ───

fn parse_endpoint_type(s: &str) -> Option<EndpointType> {
    match s {
        "AzureOpenAi" => Some(EndpointType::AzureOpenAi),
        "ApiManagementGateway" => Some(EndpointType::ApiManagementGateway),
        "OpenAiCompatible" => Some(EndpointType::OpenAiCompatible),
        "AzureSpeech" => Some(EndpointType::AzureSpeech),
        _ => None,
    }
}

fn map_auth_header(mode: &str) -> String {
    match mode {
        "ApiKeyHeader" => "api_key".into(),
        "Bearer" => "bearer".into(),
        _ => "auto".into(),
    }
}

fn map_text_protocol(mode: &str) -> String {
    match mode {
        "Responses" => "responses".into(),
        "ChatCompletionsV1" => "chat_completions".into(),
        "Auto" => "auto".into(),
        _ => "chat_completions".into(),
    }
}

fn push_unique(list: &mut Vec<String>, item: &str) {
    if !item.is_empty() && !list.iter().any(|x| x == item) {
        list.push(item.to_string());
    }
}

fn extend_unique(list: &mut Vec<String>, items: &[String]) {
    for item in items {
        push_unique(list, item);
    }
}

fn convert_raw_to_vendor_profile(raw: RawProfile) -> Option<VendorProfile> {
    let endpoint_type = parse_endpoint_type(&raw.endpoint_type)?;
    let default_auth_header = map_auth_header(&raw.defaults.api_key_header_mode);
    let default_api_version = raw.defaults.api_version.clone();
    let supports_aad = raw.defaults.supports_aad;
    let text_protocol = map_text_protocol(&raw.defaults.text_api_protocol_mode);

    let model_discovery_urls = raw
        .model_discovery
        .as_ref()
        .map(|md| md.url_candidates.clone())
        .unwrap_or_default();
    let supports_model_discovery = !model_discovery_urls.is_empty();

    let mut test_url_templates: HashMap<String, String> = HashMap::new();
    if let Some(ref r) = raw.overrides.routes.text {
        if !r.primary_url.is_empty() {
            test_url_templates.insert("text".into(), r.primary_url.clone());
        }
    }
    if let Some(ref r) = raw.overrides.routes.image {
        if !r.generate_primary_url.is_empty() {
            test_url_templates.insert("image".into(), r.generate_primary_url.clone());
        }
    }
    if let Some(ref r) = raw.overrides.routes.audio {
        if !r.primary_url.is_empty() {
            test_url_templates.insert("audio".into(), r.primary_url.clone());
        }
    }
    if let Some(ref r) = raw.overrides.routes.speech {
        if !r.primary_url.is_empty() {
            test_url_templates.insert("speech".into(), r.primary_url.clone());
        }
    }

    let mut text_url_candidates = Vec::new();
    if let Some(ref r) = raw.overrides.routes.text {
        push_unique(&mut text_url_candidates, &r.primary_url);
        push_unique(&mut text_url_candidates, &r.deployment_primary_url);
    }
    if let Some(ref txt) = raw.text {
        let ordered: Vec<&Vec<String>> = match text_protocol.as_str() {
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
            extend_unique(&mut text_url_candidates, list);
        }
    }
    extend_unique(&mut text_url_candidates, &raw.fallbacks.text);

    let mut image_url_candidates = Vec::new();
    if let Some(ref r) = raw.overrides.routes.image {
        push_unique(&mut image_url_candidates, &r.generate_primary_url);
    }
    if let Some(ref img) = raw.image {
        extend_unique(&mut image_url_candidates, &img.generate_url_candidates);
        extend_unique(&mut image_url_candidates, &img.edit_url_candidates);
    }
    extend_unique(&mut image_url_candidates, &raw.fallbacks.image_generate);
    extend_unique(&mut image_url_candidates, &raw.fallbacks.image_edit);

    let mut video_url_candidates = Vec::new();
    if let Some(ref v) = raw.video {
        extend_unique(&mut video_url_candidates, &v.create_url_candidates);
    }

    let mut audio_url_candidates = Vec::new();
    if let Some(ref r) = raw.overrides.routes.audio {
        push_unique(&mut audio_url_candidates, &r.primary_url);
    }
    if let Some(ref a) = raw.audio {
        extend_unique(&mut audio_url_candidates, &a.transcription_url_candidates);
    }
    extend_unique(&mut audio_url_candidates, &raw.fallbacks.audio);

    let mut speech_url_candidates = Vec::new();
    if let Some(ref r) = raw.overrides.routes.speech {
        push_unique(&mut speech_url_candidates, &r.primary_url);
    }
    if let Some(ref s) = raw.speech {
        extend_unique(&mut speech_url_candidates, &s.synthesis_url_candidates);
    }
    extend_unique(&mut speech_url_candidates, &raw.fallbacks.speech);

    let supported_auth_modes = if raw.auth.supported_modes.is_empty() {
        raw.overrides.auth.allowed_modes.clone()
    } else {
        raw.auth.supported_modes.clone()
    };

    let badge = match endpoint_type {
        EndpointType::AzureOpenAi => "AZ",
        EndpointType::ApiManagementGateway => "AP",
        EndpointType::OpenAiCompatible => "OA",
        EndpointType::AzureSpeech => "SP",
        _ => "??",
    };

    Some(VendorProfile {
        endpoint_type,
        label: raw.display_name,
        badge: badge.into(),
        subtitle: raw.subtitle,
        glyph: raw.glyph,
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
        raw_json: None,
    })
}

/// Load all built-in vendor profiles from embedded JSON.
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
                tracing::info!("Parsed profile JSON: {name}");
                if let Some(p) = convert_raw_to_vendor_profile(raw) {
                    profiles.push(p);
                } else {
                    tracing::warn!("Unknown endpointType, skipping profile: {name}");
                }
            }
            Err(e) => {
                tracing::error!("Failed to parse profile JSON ({name}): {e}");
            }
        }
    }
    profiles
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_load_profiles_count() {
        let profiles = load_profiles();
        assert_eq!(profiles.len(), 4, "Should load 4 built-in profiles");
    }

    #[test]
    fn test_azure_openai_profile() {
        let profiles = load_profiles();
        let aoai = profiles
            .iter()
            .find(|p| p.endpoint_type == EndpointType::AzureOpenAi)
            .expect("AzureOpenAi profile should exist");
        assert_eq!(aoai.default_auth_header, "api_key");
        assert!(aoai.supports_aad);
        assert_eq!(aoai.text_protocol, "responses");
    }

    #[test]
    fn test_profile_url_candidates() {
        let profiles = load_profiles();
        assert!(profiles.iter().any(|p| !p.text_url_candidates.is_empty()));
    }

    #[test]
    fn test_apim_profile() {
        let profiles = load_profiles();
        let apim = profiles
            .iter()
            .find(|p| p.endpoint_type == EndpointType::ApiManagementGateway)
            .expect("APIM profile should exist");
        assert_eq!(apim.default_auth_header, "api_key");
        assert_eq!(apim.text_protocol, "responses");
    }

    #[test]
    fn test_openai_compatible_profile() {
        let profiles = load_profiles();
        let oai = profiles
            .iter()
            .find(|p| p.endpoint_type == EndpointType::OpenAiCompatible)
            .expect("OpenAiCompatible profile should exist");
        assert_eq!(oai.default_auth_header, "bearer");
        assert!(!oai.supports_aad);
        assert_eq!(oai.text_protocol, "chat_completions");
    }

    #[test]
    fn test_parse_endpoint_type() {
        assert_eq!(parse_endpoint_type("AzureOpenAi"), Some(EndpointType::AzureOpenAi));
        assert_eq!(parse_endpoint_type("ApiManagementGateway"), Some(EndpointType::ApiManagementGateway));
        assert_eq!(parse_endpoint_type("OpenAiCompatible"), Some(EndpointType::OpenAiCompatible));
        assert_eq!(parse_endpoint_type("AzureSpeech"), Some(EndpointType::AzureSpeech));
        assert_eq!(parse_endpoint_type("Unknown"), None);
        assert_eq!(parse_endpoint_type(""), None);
    }

    #[test]
    fn test_map_auth_header() {
        assert_eq!(map_auth_header("ApiKeyHeader"), "api_key");
        assert_eq!(map_auth_header("Bearer"), "bearer");
        assert_eq!(map_auth_header("Other"), "auto");
        assert_eq!(map_auth_header(""), "auto");
    }

    #[test]
    fn test_map_text_protocol() {
        assert_eq!(map_text_protocol("Responses"), "responses");
        assert_eq!(map_text_protocol("ChatCompletionsV1"), "chat_completions");
        assert_eq!(map_text_protocol("Auto"), "auto");
        assert_eq!(map_text_protocol("Unknown"), "chat_completions");
    }

    #[test]
    fn test_push_and_extend_unique() {
        let mut list = Vec::new();

        // push_unique adds non-empty, non-duplicate items
        push_unique(&mut list, "a");
        assert_eq!(list, vec!["a"]);

        // doesn't add empty string
        push_unique(&mut list, "");
        assert_eq!(list, vec!["a"]);

        // doesn't add duplicate
        push_unique(&mut list, "a");
        assert_eq!(list, vec!["a"]);

        // extend_unique batch dedup
        let extras = vec!["a".into(), "b".into(), "c".into(), "b".into()];
        extend_unique(&mut list, &extras);
        assert_eq!(list, vec!["a", "b", "c"]);
    }
}
