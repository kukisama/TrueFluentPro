use tfp_core::{AiEndpoint, ApiKeyHeaderMode};

/// Apply authentication header to a reqwest RequestBuilder based on endpoint config.
///
/// - `ApiKeyHeader` → `api-key: {api_key}` header
/// - `Bearer` → `Authorization: Bearer {api_key}` header
/// - `Auto` → bearer by default
pub(crate) fn apply_auth(
    endpoint: &AiEndpoint,
    req: reqwest::RequestBuilder,
) -> reqwest::RequestBuilder {
    match &endpoint.auth_header_mode {
        ApiKeyHeaderMode::ApiKeyHeader => req.header("api-key", &endpoint.api_key),
        ApiKeyHeaderMode::Bearer => req.header(
            "Authorization",
            format!("Bearer {}", endpoint.api_key),
        ),
        ApiKeyHeaderMode::Auto => {
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

    fn make_endpoint(auth_mode: ApiKeyHeaderMode) -> AiEndpoint {
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
            auth_header_mode: auth_mode,
            ..AiEndpoint::default()
        }
    }

    #[test]
    fn test_apply_auth_api_key() {
        let ep = make_endpoint(ApiKeyHeaderMode::ApiKeyHeader);
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
        let ep = make_endpoint(ApiKeyHeaderMode::Bearer);
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
        let ep = make_endpoint(ApiKeyHeaderMode::Auto);
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