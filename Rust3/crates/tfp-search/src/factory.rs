//! Search provider factory and available engine registry.

use crate::duckduckgo::DuckDuckGoProvider;
use crate::mcp::McpSearchProvider;
use crate::provider::WebSearchProvider;

/// Metadata for a built-in search provider.
#[derive(Debug, Clone, Copy)]
pub struct AvailableProvider {
    pub id: &'static str,
    pub display_name: &'static str,
}

/// All built-in search providers.
pub const AVAILABLE_PROVIDERS: &[AvailableProvider] = &[
    AvailableProvider {
        id: "duckduckgo",
        display_name: "DuckDuckGo",
    },
    AvailableProvider {
        id: "mcp",
        display_name: "MCP Search",
    },
];

/// Normalize a provider ID to a canonical form.
pub fn normalize_provider_id(id: &str) -> &'static str {
    match id.to_lowercase().trim() {
        "duckduckgo" | "ddg" | "duck" => "duckduckgo",
        "mcp" | "mcp_search" | "mcp-search" => "mcp",
        _ => "duckduckgo", // fallback
    }
}

/// Create a search provider by ID.
///
/// For MCP, requires a valid endpoint. If MCP is requested without an endpoint,
/// falls back to DuckDuckGo.
pub fn create_provider(
    provider_id: &str,
    mcp_endpoint: Option<&str>,
    mcp_tool_name: Option<&str>,
    mcp_api_key: Option<&str>,
) -> Box<dyn WebSearchProvider> {
    let id = normalize_provider_id(provider_id);
    match id {
        "mcp" => {
            let endpoint = mcp_endpoint.unwrap_or("").trim();
            if endpoint.is_empty() {
                tracing::warn!("MCP search requested but no endpoint configured, falling back to DuckDuckGo");
                Box::new(DuckDuckGoProvider::new())
            } else {
                Box::new(McpSearchProvider::new(
                    endpoint.to_string(),
                    mcp_tool_name.unwrap_or("web_search").to_string(),
                    mcp_api_key.filter(|k| !k.is_empty()).map(|k| k.to_string()),
                ))
            }
        }
        _ => Box::new(DuckDuckGoProvider::new()),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_normalize_provider_id() {
        assert_eq!(normalize_provider_id("duckduckgo"), "duckduckgo");
        assert_eq!(normalize_provider_id("DDG"), "duckduckgo");
        assert_eq!(normalize_provider_id("duck"), "duckduckgo");
        assert_eq!(normalize_provider_id("mcp"), "mcp");
        assert_eq!(normalize_provider_id("mcp_search"), "mcp");
        assert_eq!(normalize_provider_id("mcp-search"), "mcp");
        assert_eq!(normalize_provider_id("unknown"), "duckduckgo");
    }

    #[test]
    fn test_create_duckduckgo() {
        let p = create_provider("duckduckgo", None, None, None);
        assert_eq!(p.id(), "duckduckgo");
        assert_eq!(p.display_name(), "DuckDuckGo");
    }

    #[test]
    fn test_create_mcp_with_endpoint() {
        let p = create_provider(
            "mcp",
            Some("https://mcp.example.com/rpc"),
            Some("brave_search"),
            Some("sk-test-key"),
        );
        assert_eq!(p.id(), "mcp");
    }

    #[test]
    fn test_create_mcp_without_endpoint_falls_back() {
        let p = create_provider("mcp", None, None, None);
        assert_eq!(p.id(), "duckduckgo");
    }

    #[test]
    fn test_create_mcp_empty_endpoint_falls_back() {
        let p = create_provider("mcp", Some(""), None, None);
        assert_eq!(p.id(), "duckduckgo");
    }

    #[test]
    fn test_available_providers() {
        assert_eq!(AVAILABLE_PROVIDERS.len(), 2);
        assert_eq!(AVAILABLE_PROVIDERS[0].id, "duckduckgo");
        assert_eq!(AVAILABLE_PROVIDERS[1].id, "mcp");
    }
}
