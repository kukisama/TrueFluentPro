//! Raw serde containers for endpoint profile JSON deserialization.
//! These mirror the C# EndpointProfile JSON schema.

use serde::Deserialize;

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
#[allow(dead_code)]
pub(crate) struct RawProfile {
    #[serde(default)]
    pub schema_version: u32,
    pub id: String,
    #[serde(default)]
    pub vendor: String,
    pub endpoint_type: String,
    pub display_name: String,
    #[serde(default)]
    pub subtitle: String,
    #[serde(default)]
    pub glyph: String,
    #[serde(default)]
    pub summary: String,
    #[serde(default)]
    pub default_name_prefix: String,
    #[serde(default)]
    pub tags: Vec<String>,
    #[serde(default)]
    pub overrides: RawOverrides,
    #[serde(default)]
    pub fallbacks: RawFallbacks,
    #[serde(default)]
    pub defaults: RawDefaults,
    #[serde(default)]
    pub auth: RawAuth,
    #[serde(default)]
    pub special_policies: RawSpecialPolicies,
    #[serde(default)]
    pub model_discovery: Option<RawModelDiscovery>,
    #[serde(default)]
    pub text: Option<RawTextRoutes>,
    #[serde(default)]
    pub image: Option<RawImageRoutes>,
    #[serde(default)]
    pub audio: Option<RawAudioRoutes>,
    #[serde(default)]
    pub speech: Option<RawSpeechRoutes>,
    #[serde(default)]
    pub video: Option<RawVideoRoutes>,
}

#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
#[allow(dead_code)]
pub(crate) struct RawOverrides {
    #[serde(default)]
    pub auth: RawOverridesAuth,
    #[serde(default)]
    pub routes: RawOverridesRoutes,
    #[serde(default)]
    pub version: RawOverridesVersion,
}

#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
#[allow(dead_code)]
pub(crate) struct RawOverridesAuth {
    #[serde(default)]
    pub default_mode: String,
    #[serde(default)]
    pub allowed_modes: Vec<String>,
    #[serde(default)]
    pub default_api_key_header_mode: String,
    #[serde(default)]
    pub allowed_api_key_header_modes: Vec<String>,
}

#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
#[allow(dead_code)]
pub(crate) struct RawOverridesRoutes {
    #[serde(default)]
    pub text: Option<RawOverridesTextRoute>,
    #[serde(default)]
    pub model_discovery: Option<RawOverridesModelDiscovery>,
    #[serde(default)]
    pub audio: Option<RawOverridesAudioRoute>,
    #[serde(default)]
    pub speech: Option<RawOverridesSpeechRoute>,
    #[serde(default)]
    pub image: Option<RawOverridesImageRoute>,
    #[serde(default)]
    pub video: Option<RawOverridesVideoRoute>,
}

#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
#[allow(dead_code)]
pub(crate) struct RawOverridesTextRoute {
    #[serde(default)]
    pub primary_protocol: String,
    #[serde(default)]
    pub primary_url: String,
    #[serde(default)]
    pub deployment_primary_url: String,
}

#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
#[allow(dead_code)]
pub(crate) struct RawOverridesModelDiscovery {
    #[serde(default)]
    pub primary_url: String,
}

#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
#[allow(dead_code)]
pub(crate) struct RawOverridesAudioRoute {
    #[serde(default)]
    pub primary_url: String,
    #[serde(default)]
    pub default_api_version: String,
}

#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
#[allow(dead_code)]
pub(crate) struct RawOverridesSpeechRoute {
    #[serde(default)]
    pub primary_url: String,
    #[serde(default)]
    pub default_api_version: String,
}

#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
#[allow(dead_code)]
pub(crate) struct RawOverridesImageRoute {
    #[serde(default)]
    pub generate_primary_url: String,
    #[serde(default)]
    pub edit_primary_url: String,
    #[serde(default)]
    pub deployment_generate_primary_url: String,
}

#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
#[allow(dead_code)]
pub(crate) struct RawOverridesVideoRoute {
    #[serde(default)]
    pub default_mode: String,
    #[serde(default)]
    pub create_primary_url: String,
    #[serde(default)]
    pub poll_primary_url: String,
    #[serde(default)]
    pub download_primary_url: String,
    #[serde(default)]
    pub download_video_content_primary_url: String,
}

#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
#[allow(dead_code)]
pub(crate) struct RawOverridesVersion {
    #[serde(default)]
    pub endpoint_api_version: String,
    #[serde(default)]
    pub text_api_version: String,
    #[serde(default)]
    pub audio_api_version: String,
    #[serde(default)]
    pub speech_api_version: String,
    #[serde(default)]
    pub video_api_version: String,
}

#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
#[allow(dead_code)]
pub(crate) struct RawDefaults {
    #[serde(default)]
    pub provider_type: String,
    #[serde(default)]
    pub auth_mode: String,
    #[serde(default)]
    pub api_key_header_mode: String,
    #[serde(default)]
    pub text_api_protocol_mode: String,
    #[serde(default)]
    pub image_api_route_mode: String,
    #[serde(default)]
    pub api_version: String,
    #[serde(default)]
    pub supports_aad: bool,
    #[serde(default)]
    pub clear_azure_identity_fields: bool,
}

#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
#[allow(dead_code)]
pub(crate) struct RawAuth {
    #[serde(default)]
    pub supported_modes: Vec<String>,
    #[serde(default)]
    pub supported_api_key_header_modes: Vec<String>,
    #[serde(default)]
    pub default_mode: String,
    #[serde(default)]
    pub default_api_key_header_mode: String,
    #[serde(default)]
    pub supports_subscription_key_query_fallback: bool,
}

#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
#[allow(dead_code)]
pub(crate) struct RawSpecialPolicies {
    #[serde(default)]
    pub allow_apim_subscription_key_query_retry: bool,
    #[serde(default)]
    pub allow_preview_fallback: bool,
}

#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
#[allow(dead_code)]
pub(crate) struct RawFallbacks {
    #[serde(default)]
    pub model_discovery: Vec<String>,
    #[serde(default)]
    pub text: Vec<String>,
    #[serde(default)]
    pub image_generate: Vec<String>,
    #[serde(default)]
    pub image_edit: Vec<String>,
    #[serde(default)]
    pub audio: Vec<String>,
    #[serde(default)]
    pub speech: Vec<String>,
    #[serde(default)]
    pub video_create: Vec<String>,
    #[serde(default)]
    pub video_poll: Vec<String>,
    #[serde(default)]
    pub video_download: Vec<String>,
    #[serde(default)]
    pub video_download_video_content: Vec<String>,
}

#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
#[allow(dead_code)]
pub(crate) struct RawModelDiscovery {
    #[serde(default)]
    pub url_candidates: Vec<String>,
}

#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
#[allow(dead_code)]
pub(crate) struct RawTextRoutes {
    #[serde(default)]
    pub preferred_protocol: String,
    #[serde(default)]
    pub deployment_chat_completions_url_candidates: Vec<String>,
    #[serde(default)]
    pub responses_url_candidates: Vec<String>,
    #[serde(default)]
    pub chat_completions_v1_url_candidates: Vec<String>,
    #[serde(default)]
    pub chat_completions_raw_url_candidates: Vec<String>,
}

#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
#[allow(dead_code)]
pub(crate) struct RawImageRoutes {
    #[serde(default)]
    pub deployment_generate_url_candidates: Vec<String>,
    #[serde(default)]
    pub generate_url_candidates: Vec<String>,
    #[serde(default)]
    pub edit_url_candidates: Vec<String>,
}

#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
#[allow(dead_code)]
pub(crate) struct RawAudioRoutes {
    #[serde(default)]
    pub default_api_version: String,
    #[serde(default)]
    pub transcription_url_candidates: Vec<String>,
}

#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
#[allow(dead_code)]
pub(crate) struct RawSpeechRoutes {
    #[serde(default)]
    pub default_api_version: String,
    #[serde(default)]
    pub synthesis_url_candidates: Vec<String>,
}

#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
#[allow(dead_code)]
pub(crate) struct RawVideoRoutes {
    #[serde(default)]
    pub supported_api_modes: Vec<String>,
    #[serde(default)]
    pub create_url_candidates: Vec<String>,
    #[serde(default)]
    pub poll_url_candidates: Vec<String>,
    #[serde(default)]
    pub download_url_candidates: Vec<String>,
    #[serde(default)]
    pub download_video_content_url_candidates: Vec<String>,
}
