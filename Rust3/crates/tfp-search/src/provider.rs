//! Web search provider trait.

use async_trait::async_trait;
use crate::models::{SearchError, WebSearchResult};

/// Trait for pluggable web search engines.
#[async_trait]
pub trait WebSearchProvider: Send + Sync {
    /// Unique identifier for this provider (e.g. "duckduckgo", "mcp").
    fn id(&self) -> &str;

    /// Human-readable display name.
    fn display_name(&self) -> &str;

    /// Execute a web search query and return up to `max_results` results.
    async fn search(
        &self,
        query: &str,
        max_results: usize,
    ) -> Result<Vec<WebSearchResult>, SearchError>;
}
