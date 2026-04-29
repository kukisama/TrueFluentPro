//! MCP (Model Context Protocol) search provider.

use async_trait::async_trait;
use serde_json::Value;
use std::time::Duration;

use crate::models::{SearchError, WebSearchResult};
use crate::provider::WebSearchProvider;

/// MCP search provider that calls a JSON-RPC 2.0 endpoint.
pub struct McpSearchProvider {
    client: reqwest::Client,
    endpoint: String,
    tool_name: String,
    api_key: Option<String>,
}

impl McpSearchProvider {
    /// Create a new MCP search provider.
    pub fn new(
        endpoint: String,
        tool_name: String,
        api_key: Option<String>,
    ) -> Self {
        let client = reqwest::Client::builder()
            .timeout(Duration::from_secs(15))
            .build()
            .unwrap_or_default();
        Self {
            client,
            endpoint,
            tool_name,
            api_key,
        }
    }
}

#[async_trait]
impl WebSearchProvider for McpSearchProvider {
    fn id(&self) -> &str {
        "mcp"
    }

    fn display_name(&self) -> &str {
        "MCP Search"
    }

    async fn search(
        &self,
        query: &str,
        max_results: usize,
    ) -> Result<Vec<WebSearchResult>, SearchError> {
        let body = serde_json::json!({
            "jsonrpc": "2.0",
            "method": "tools/call",
            "params": {
                "name": self.tool_name,
                "arguments": {
                    "query": query,
                    "numResults": max_results,
                }
            },
            "id": 1
        });

        let mut req = self.client.post(&self.endpoint).json(&body);
        if let Some(key) = &self.api_key {
            req = req.bearer_auth(key);
        }

        let resp = req.send().await.map_err(|e| {
            if e.is_timeout() {
                SearchError::Timeout
            } else {
                SearchError::Network(e.to_string())
            }
        })?;

        let json: Value = resp
            .json()
            .await
            .map_err(|e| SearchError::Parse(e.to_string()))?;

        parse_mcp_response(&json, max_results)
    }
}

/// Parse a JSON-RPC 2.0 MCP response into search results.
///
/// Supports both structured responses (objects with title/url/snippet)
/// and plain text responses.
pub fn parse_mcp_response(
    json: &Value,
    max_results: usize,
) -> Result<Vec<WebSearchResult>, SearchError> {
    // Check for JSON-RPC error
    if let Some(err) = json.get("error") {
        let msg = err
            .get("message")
            .and_then(|m| m.as_str())
            .unwrap_or("Unknown MCP error");
        return Err(SearchError::Network(msg.to_string()));
    }

    let content = json
        .pointer("/result/content")
        .and_then(|c| c.as_array())
        .ok_or_else(|| SearchError::Parse("Missing result.content array".into()))?;

    let mut results = Vec::new();

    for item in content.iter().take(max_results) {
        if let Some(result) = parse_mcp_content_item(item) {
            results.push(result);
        }
    }

    Ok(results)
}

fn parse_mcp_content_item(item: &Value) -> Option<WebSearchResult> {
    // Structured: has title/url/snippet or text field
    if let (Some(title), Some(url)) = (
        item.get("title").and_then(|v| v.as_str()),
        item.get("url").and_then(|v| v.as_str()),
    ) {
        let snippet = item
            .get("snippet")
            .or_else(|| item.get("description"))
            .and_then(|v| v.as_str())
            .unwrap_or("")
            .to_string();
        return Some(WebSearchResult {
            title: title.to_string(),
            url: url.to_string(),
            snippet,
            content: String::new(),
        });
    }

    // Plain text fallback
    if let Some(text) = item.get("text").and_then(|v| v.as_str()) {
        // Try to parse as JSON in case content is a serialized object
        if let Ok(parsed) = serde_json::from_str::<Value>(text) {
            if let Some(r) = parse_mcp_content_item(&parsed) {
                return Some(r);
            }
        }
        // Use text as snippet with no URL
        if !text.is_empty() {
            return Some(WebSearchResult {
                title: text.chars().take(80).collect::<String>(),
                url: String::new(),
                snippet: text.to_string(),
                content: String::new(),
            });
        }
    }

    None
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_parse_mcp_structured_response() {
        let json = serde_json::json!({
            "jsonrpc": "2.0",
            "result": {
                "content": [
                    {
                        "title": "Rust Language",
                        "url": "https://rust-lang.org",
                        "snippet": "Systems programming language"
                    },
                    {
                        "title": "Cargo",
                        "url": "https://doc.rust-lang.org/cargo/",
                        "description": "Rust package manager"
                    }
                ]
            },
            "id": 1
        });

        let results = parse_mcp_response(&json, 10).unwrap();
        assert_eq!(results.len(), 2);
        assert_eq!(results[0].title, "Rust Language");
        assert_eq!(results[0].url, "https://rust-lang.org");
        assert_eq!(results[0].snippet, "Systems programming language");
        assert_eq!(results[1].snippet, "Rust package manager");
    }

    #[test]
    fn test_parse_mcp_text_response() {
        let json = serde_json::json!({
            "jsonrpc": "2.0",
            "result": {
                "content": [
                    { "text": "Rust is a systems programming language focused on safety." }
                ]
            },
            "id": 1
        });

        let results = parse_mcp_response(&json, 5).unwrap();
        assert_eq!(results.len(), 1);
        assert!(results[0].snippet.contains("systems programming"));
    }

    #[test]
    fn test_parse_mcp_error_response() {
        let json = serde_json::json!({
            "jsonrpc": "2.0",
            "error": { "code": -32000, "message": "Service unavailable" },
            "id": 1
        });

        let err = parse_mcp_response(&json, 5).unwrap_err();
        match err {
            SearchError::Network(msg) => assert!(msg.contains("Service unavailable")),
            _ => panic!("Expected Network error"),
        }
    }

    #[test]
    fn test_parse_mcp_max_results() {
        let json = serde_json::json!({
            "jsonrpc": "2.0",
            "result": {
                "content": [
                    { "title": "A", "url": "https://a.com", "snippet": "" },
                    { "title": "B", "url": "https://b.com", "snippet": "" },
                    { "title": "C", "url": "https://c.com", "snippet": "" },
                ]
            },
            "id": 1
        });

        let results = parse_mcp_response(&json, 2).unwrap();
        assert_eq!(results.len(), 2);
    }
}
