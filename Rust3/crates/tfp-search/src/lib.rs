//! tfp-search — 网络搜索域
//!
//! Provides web search providers (DuckDuckGo, MCP), a page fetcher,
//! intent analysis, and a search agent that orchestrates them.

pub mod models;
pub mod provider;
pub mod duckduckgo;
pub mod mcp;
pub mod fetcher;
pub mod agent;
pub mod factory;

pub use models::*;
pub use provider::WebSearchProvider;
pub use duckduckgo::DuckDuckGoProvider;
pub use mcp::McpSearchProvider;
pub use fetcher::WebPageFetcher;
pub use agent::{SearchAgent, SearchAgentConfig};
pub use factory::{create_provider, AVAILABLE_PROVIDERS, AvailableProvider};
