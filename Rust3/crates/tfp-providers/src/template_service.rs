//! Endpoint template service — maps VendorProfile to UI-consumable templates.

use tfp_core::{
    AiEndpoint, AiProviderType, ApiKeyHeaderMode, AzureAuthMode, EndpointTemplateDefinition,
    EndpointType, VendorProfile,
};

/// Convert all vendor profiles into endpoint template definitions.
pub fn get_templates(profiles: &[VendorProfile]) -> Vec<EndpointTemplateDefinition> {
    profiles
        .iter()
        .map(|p| EndpointTemplateDefinition {
            profile_id: format!("{:?}", p.endpoint_type).to_lowercase(),
            endpoint_type: p.endpoint_type.clone(),
            display_name: p.label.clone(),
            subtitle: p.subtitle.clone(),
            glyph: p.glyph.clone(),
            summary: String::new(),
            default_name_prefix: p.label.clone(),
            default_api_version: p.default_api_version.clone(),
            icon_asset_path: String::new(),
            supports_aad: p.supports_aad,
        })
        .collect()
}

/// Apply template defaults to an endpoint.
pub fn apply_template(endpoint: &mut AiEndpoint, profile: &VendorProfile) {
    endpoint.endpoint_type = profile.endpoint_type.clone();
    endpoint.profile_id = format!("{:?}", profile.endpoint_type).to_lowercase();
    endpoint.provider_type = match profile.endpoint_type {
        EndpointType::AzureOpenAi | EndpointType::ApiManagementGateway => AiProviderType::AzureOpenAi,
        _ => AiProviderType::OpenAiCompatible,
    };
    endpoint.auth_header_mode = match profile.default_auth_header.as_str() {
        "bearer" => ApiKeyHeaderMode::Bearer,
        "api_key" | "api-key" => ApiKeyHeaderMode::ApiKeyHeader,
        _ => ApiKeyHeaderMode::Auto,
    };
    if !profile.default_api_version.is_empty() {
        endpoint.api_version = Some(profile.default_api_version.clone());
    }
    if profile.supports_aad {
        endpoint.auth_mode = AzureAuthMode::ApiKey; // default to api_key, user can switch
    }
}

/// Build a one-line behavior summary for the endpoint.
pub fn build_behavior_summary(endpoint: &AiEndpoint, profile: Option<&VendorProfile>) -> String {
    let auth = match endpoint.auth_header_mode {
        ApiKeyHeaderMode::Bearer => "Bearer Token",
        ApiKeyHeaderMode::ApiKeyHeader => "api-key Header",
        ApiKeyHeaderMode::Auto => {
            if endpoint.is_azure() { "api-key Header (auto)" } else { "Bearer (auto)" }
        }
    };

    let text_route = if profile.map_or(false, |p| !p.text_url_candidates.is_empty()) {
        "按资料包声明"
    } else {
        "默认路由"
    };

    let image_route = if profile.map_or(false, |p| !p.image_url_candidates.is_empty()) {
        "按资料包声明"
    } else {
        "默认路由"
    };

    let api_ver = endpoint.api_version.as_deref().unwrap_or("无");

    format!("文本 {text_route}；图片 {image_route}；认证 {auth}；API 版本 {api_ver}")
}

/// Build a Markdown-formatted inspection report for the endpoint.
pub fn build_inspection_report(endpoint: &AiEndpoint, profile: Option<&VendorProfile>) -> String {
    let mut md = String::new();

    md.push_str(&format!("# 端点检查报告: {}\n\n", endpoint.name));
    md.push_str(&format!("- **类型**: {:?}\n", endpoint.endpoint_type));
    md.push_str(&format!("- **地址**: {}\n", endpoint.url));
    md.push_str(&format!("- **认证**: {:?}\n", endpoint.auth_header_mode));
    md.push_str(&format!("- **API 版本**: {}\n", endpoint.api_version.as_deref().unwrap_or("无")));
    md.push_str(&format!("- **AAD 支持**: {}\n\n", endpoint.auth_mode == AzureAuthMode::Aad));

    if let Some(prof) = profile {
        md.push_str(&format!("## 资料包: {}\n\n", prof.label));
        md.push_str(&format!("{}\n\n", prof.subtitle));

        if !prof.text_url_candidates.is_empty() {
            md.push_str("### 文本 API 路由\n");
            for (i, url) in prof.text_url_candidates.iter().enumerate() {
                let marker = if i == 0 { " *" } else { "" };
                md.push_str(&format!("- `{}`{}\n", url, marker));
            }
            md.push('\n');
        }

        if !prof.image_url_candidates.is_empty() {
            md.push_str("### 图片 API 路由\n");
            for (i, url) in prof.image_url_candidates.iter().enumerate() {
                let marker = if i == 0 { " *" } else { "" };
                md.push_str(&format!("- `{}`{}\n", url, marker));
            }
            md.push('\n');
        }

        if !prof.video_url_candidates.is_empty() {
            md.push_str("### 视频 API 路由\n");
            for (i, url) in prof.video_url_candidates.iter().enumerate() {
                let marker = if i == 0 { " *" } else { "" };
                md.push_str(&format!("- `{}`{}\n", url, marker));
            }
            md.push('\n');
        }
    }

    // List configured models
    if !endpoint.models.is_empty() {
        md.push_str("## 已配置模型\n\n");
        for model in &endpoint.models {
            let caps: Vec<String> = model.capabilities.iter().map(|c| format!("{:?}", c)).collect();
            md.push_str(&format!(
                "- **{}** (deployment: `{}`) — {}\n",
                model.display_name,
                model.effective_deployment(),
                caps.join(", ")
            ));
        }
    }

    md
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::HashMap;
    use tfp_core::EndpointType;

    fn make_profile(ep_type: EndpointType, label: &str) -> VendorProfile {
        VendorProfile {
            endpoint_type: ep_type,
            label: label.into(),
            badge: String::new(),
            subtitle: "Test subtitle".into(),
            glyph: "\u{e900}".into(),
            default_auth_header: "api-key".into(),
            default_api_version: "2025-03-01-preview".into(),
            supports_aad: true,
            supports_model_discovery: true,
            model_discovery_urls: vec![],
            test_url_templates: HashMap::new(),
            text_url_candidates: vec!["/v1/responses".into(), "/v1/chat/completions".into()],
            image_url_candidates: vec!["/v1/images/generations".into()],
            video_url_candidates: vec![],
            audio_url_candidates: vec![],
            speech_url_candidates: vec![],
            text_protocol: "auto".into(),
            supported_auth_modes: vec!["api_key".into(), "aad".into()],
            raw_json: None,
        }
    }

    #[test]
    fn test_get_templates() {
        let profiles = vec![
            make_profile(EndpointType::AzureOpenAi, "Azure OpenAI"),
            make_profile(EndpointType::OpenAiCompatible, "OpenAI Compatible"),
        ];
        let templates = get_templates(&profiles);
        assert_eq!(templates.len(), 2);
        assert_eq!(templates[0].display_name, "Azure OpenAI");
        assert!(templates[0].supports_aad);
        assert_eq!(templates[1].display_name, "OpenAI Compatible");
    }

    #[test]
    fn test_apply_template_azure() {
        let profile = make_profile(EndpointType::AzureOpenAi, "Azure OpenAI");
        let mut ep = AiEndpoint::default();
        apply_template(&mut ep, &profile);
        assert_eq!(ep.endpoint_type, EndpointType::AzureOpenAi);
        assert!(matches!(ep.provider_type, AiProviderType::AzureOpenAi));
        assert!(matches!(ep.auth_header_mode, ApiKeyHeaderMode::ApiKeyHeader));
        assert_eq!(ep.api_version.as_deref(), Some("2025-03-01-preview"));
    }

    #[test]
    fn test_apply_template_compatible() {
        let mut profile = make_profile(EndpointType::OpenAiCompatible, "Compatible");
        profile.default_auth_header = "bearer".into();
        profile.supports_aad = false;
        let mut ep = AiEndpoint::default();
        apply_template(&mut ep, &profile);
        assert_eq!(ep.endpoint_type, EndpointType::OpenAiCompatible);
        assert!(matches!(ep.provider_type, AiProviderType::OpenAiCompatible));
        assert!(matches!(ep.auth_header_mode, ApiKeyHeaderMode::Bearer));
    }

    #[test]
    fn test_behavior_summary() {
        let profile = make_profile(EndpointType::AzureOpenAi, "Azure OpenAI");
        let mut ep = AiEndpoint::default();
        apply_template(&mut ep, &profile);
        let summary = build_behavior_summary(&ep, Some(&profile));
        assert!(summary.contains("资料包声明"));
        assert!(summary.contains("api-key Header"));
        assert!(summary.contains("2025-03-01-preview"));
    }

    #[test]
    fn test_inspection_report() {
        let profile = make_profile(EndpointType::AzureOpenAi, "Azure OpenAI");
        let mut ep = AiEndpoint::default();
        ep.name = "My Azure Endpoint".into();
        ep.url = "https://myresource.openai.azure.com".into();
        apply_template(&mut ep, &profile);
        let report = build_inspection_report(&ep, Some(&profile));
        assert!(report.contains("# 端点检查报告: My Azure Endpoint"));
        assert!(report.contains("/v1/responses"));
        assert!(report.contains("/v1/chat/completions"));
        assert!(report.contains("Test subtitle"));
    }
}
