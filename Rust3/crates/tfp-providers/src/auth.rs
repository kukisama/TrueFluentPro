use tfp_core::AiEndpoint;

/// Apply authentication header to a reqwest RequestBuilder based on endpoint config.
///
/// - `"api_key"` / `"api_key_header"` → `api-key: {api_key}` header
/// - `"bearer"` → `Authorization: Bearer {api_key}` header
/// - anything else (including `"auto"`) → bearer by default
pub(crate) fn apply_auth(
    endpoint: &AiEndpoint,
    req: reqwest::RequestBuilder,
) -> reqwest::RequestBuilder {
    match endpoint.auth_header_mode.as_str() {
        "api_key" | "api_key_header" => req.header("api-key", &endpoint.api_key),
        "bearer" => req.header(
            "Authorization",
            format!("Bearer {}", endpoint.api_key),
        ),
        _ => {
            // "auto" or unknown: default to bearer
            req.header(
                "Authorization",
                format!("Bearer {}", endpoint.api_key),
            )
        }
    }
}

/// Append `api-version` query parameter to a URL string.
///
/// Handles both URLs that already have query params (`?`) and those that don't.
pub(crate) fn append_api_version(url: &str, api_version: &str) -> String {
    if api_version.is_empty() {
        return url.to_string();
    }
    if url.contains('?') {
        format!("{url}&api-version={api_version}")
    } else {
        format!("{url}?api-version={api_version}")
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use tfp_core::EndpointType;

    fn make_endpoint(auth_mode: &str) -> AiEndpoint {
        AiEndpoint {
            id: "test".into(),
            name: "Test".into(),
            endpoint_type: EndpointType::AzureOpenAi,
            url: "https://example.com".into(),
            api_key: "my-secret-key".into(),
            api_version: None,
            region: None,
            models: vec![],
            enabled: true,
            auth_header_mode: auth_mode.into(),
            auth_mode: "api_key".into(),
            azure_tenant_id: String::new(),
            azure_client_id: String::new(),
            speech_subscription_key: String::new(),
            speech_region: String::new(),
            speech_endpoint: String::new(),
        }
    }

    #[test]
    fn test_apply_auth_api_key() {
        let ep = make_endpoint("api_key");
        let client = reqwest::Client::new();
        let req = apply_auth(&ep, client.get("https://example.com"));
        let built = req.build().unwrap();
        assert_eq!(
            built.headers().get("api-key").unwrap().to_str().unwrap(),
            "my-secret-key"
        );
    }

    #[test]
    fn test_apply_auth_bearer() {
        let ep = make_endpoint("bearer");
        let client = reqwest::Client::new();
        let req = apply_auth(&ep, client.get("https://example.com"));
        let built = req.build().unwrap();
        assert_eq!(
            built.headers().get("Authorization").unwrap().to_str().unwrap(),
            "Bearer my-secret-key"
        );
    }

    #[test]
    fn test_apply_auth_auto_defaults_to_bearer() {
        let ep = make_endpoint("auto");
        let client = reqwest::Client::new();
        let req = apply_auth(&ep, client.get("https://example.com"));
        let built = req.build().unwrap();
        assert!(built.headers().get("Authorization").is_some());
    }

    #[test]
    fn test_append_api_version() {
        assert_eq!(
            append_api_version("https://example.com/api", "2024-01-01"),
            "https://example.com/api?api-version=2024-01-01"
        );
        assert_eq!(
            append_api_version("https://example.com/api?foo=bar", "2024-01-01"),
            "https://example.com/api?foo=bar&api-version=2024-01-01"
        );
        assert_eq!(
            append_api_version("https://example.com/api", ""),
            "https://example.com/api"
        );
    }
}
