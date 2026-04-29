#[cfg(test)]
pub(crate) mod factories {
    use tfp_core::{AiEndpoint, ApiKeyHeaderMode, AzureAuthMode, EndpointType};

    pub fn azure_openai_endpoint(id: &str, name: &str) -> AiEndpoint {
        AiEndpoint {
            id: id.into(),
            name: name.into(),
            endpoint_type: EndpointType::AzureOpenAi,
            url: "https://myresource.openai.azure.com".into(),
            api_key: "test-key".into(),
            api_version: Some("2025-03-01-preview".into()),
            region: None,
            models: vec![],
            enabled: true,
            auth_header_mode: ApiKeyHeaderMode::ApiKeyHeader,
            auth_mode: AzureAuthMode::ApiKey,
            ..AiEndpoint::default()
        }
    }

    pub fn openai_compatible_endpoint(id: &str, name: &str) -> AiEndpoint {
        AiEndpoint {
            id: id.into(),
            name: name.into(),
            endpoint_type: EndpointType::OpenAiCompatible,
            url: "https://api.openai.com".into(),
            api_key: "sk-xxx".into(),
            api_version: None,
            region: None,
            models: vec![],
            enabled: true,
            auth_header_mode: ApiKeyHeaderMode::Bearer,
            auth_mode: AzureAuthMode::ApiKey,
            ..AiEndpoint::default()
        }
    }

    pub fn speech_endpoint(id: &str, name: &str) -> AiEndpoint {
        AiEndpoint {
            id: id.into(),
            name: name.into(),
            endpoint_type: EndpointType::AzureSpeech,
            url: String::new(),
            api_key: String::new(),
            api_version: None,
            region: Some("eastus".into()),
            models: vec![],
            enabled: true,
            auth_header_mode: ApiKeyHeaderMode::ApiKeyHeader,
            auth_mode: AzureAuthMode::ApiKey,
            speech_subscription_key: "test-key".into(),
            speech_region: "eastus".into(),
            ..AiEndpoint::default()
        }
    }
}