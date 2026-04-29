//! Search domain models.

use serde::{Deserialize, Serialize};
use std::fmt;

/// A single web search result from a search provider.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct WebSearchResult {
    pub title: String,
    pub url: String,
    pub snippet: String,
    /// Full page content (populated by fetcher, empty until then).
    pub content: String,
}

/// A numbered citation derived from a search result, ready for display.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SearchCitation {
    pub number: usize,
    pub title: String,
    pub url: String,
    pub snippet: String,
    pub hostname: String,
}

impl SearchCitation {
    /// Build a citation from a search result and an ordinal number.
    pub fn from_result(result: &WebSearchResult, number: usize) -> Self {
        Self {
            number,
            title: result.title.clone(),
            url: result.url.clone(),
            snippet: result.snippet.clone(),
            hostname: extract_hostname(&result.url),
        }
    }
}

/// Intent classification for user messages.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum IntentType {
    Chat,
    Search,
    Image,
}

/// Parsed result of intent XML analysis.
#[derive(Debug, Clone)]
pub struct IntentResult {
    pub needs_search: bool,
    pub questions: Vec<String>,
}

/// Final result of a search agent run.
#[derive(Debug, Clone)]
pub struct AgentResult {
    pub needs_search: bool,
    pub results: Vec<WebSearchResult>,
    pub all_queries: Vec<String>,
    pub citations: Vec<SearchCitation>,
    pub context_prompt: String,
}

impl AgentResult {
    /// Create a result indicating no search was needed.
    pub fn no_search() -> Self {
        Self {
            needs_search: false,
            results: Vec::new(),
            all_queries: Vec::new(),
            citations: Vec::new(),
            context_prompt: String::new(),
        }
    }
}

/// Errors that can occur during search operations.
#[derive(Debug)]
pub enum SearchError {
    Network(String),
    Parse(String),
    Timeout,
}

impl fmt::Display for SearchError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            SearchError::Network(msg) => write!(f, "Network error: {msg}"),
            SearchError::Parse(msg) => write!(f, "Parse error: {msg}"),
            SearchError::Timeout => write!(f, "Request timed out"),
        }
    }
}

impl std::error::Error for SearchError {}

/// Extract the hostname from a URL string.
pub fn extract_hostname(url: &str) -> String {
    url::Url::parse(url)
        .ok()
        .and_then(|u| u.host_str().map(|h| h.to_string()))
        .unwrap_or_default()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_citation_from_result() {
        let result = WebSearchResult {
            title: "Rust Programming Language".into(),
            url: "https://www.rust-lang.org/learn".into(),
            snippet: "A language empowering everyone to build reliable software.".into(),
            content: String::new(),
        };
        let citation = SearchCitation::from_result(&result, 1);
        assert_eq!(citation.number, 1);
        assert_eq!(citation.title, "Rust Programming Language");
        assert_eq!(citation.url, "https://www.rust-lang.org/learn");
        assert_eq!(citation.hostname, "www.rust-lang.org");
    }

    #[test]
    fn test_citation_hostname_extraction() {
        assert_eq!(extract_hostname("https://docs.rs/tokio/latest/tokio/"), "docs.rs");
        assert_eq!(extract_hostname("http://localhost:8080/path"), "localhost");
        assert_eq!(extract_hostname("not a url"), "");
        assert_eq!(extract_hostname(""), "");
    }

    #[test]
    fn test_agent_result_no_search() {
        let r = AgentResult::no_search();
        assert!(!r.needs_search);
        assert!(r.results.is_empty());
        assert!(r.citations.is_empty());
        assert!(r.context_prompt.is_empty());
    }

    #[test]
    fn test_search_error_display() {
        let e = SearchError::Network("connection refused".into());
        assert!(e.to_string().contains("connection refused"));
        let e2 = SearchError::Timeout;
        assert!(e2.to_string().contains("timed out"));
    }

    #[test]
    fn test_intent_type_serde() {
        let json = serde_json::to_string(&IntentType::Search).unwrap();
        assert_eq!(json, r#""search""#);
        let parsed: IntentType = serde_json::from_str(r#""chat""#).unwrap();
        assert_eq!(parsed, IntentType::Chat);
    }
}
