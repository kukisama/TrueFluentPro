//! DuckDuckGo HTML search provider.

use async_trait::async_trait;
use regex::Regex;
use std::time::Duration;

use crate::models::{SearchError, WebSearchResult};
use crate::provider::WebSearchProvider;

/// DuckDuckGo search provider using the HTML-only endpoint.
pub struct DuckDuckGoProvider {
    client: reqwest::Client,
}

impl DuckDuckGoProvider {
    /// Create a new DuckDuckGo provider with a default HTTP client.
    pub fn new() -> Self {
        let client = reqwest::Client::builder()
            .timeout(Duration::from_secs(15))
            .user_agent("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36")
            .build()
            .unwrap_or_default();
        Self { client }
    }
}

#[async_trait]
impl WebSearchProvider for DuckDuckGoProvider {
    fn id(&self) -> &str {
        "duckduckgo"
    }

    fn display_name(&self) -> &str {
        "DuckDuckGo"
    }

    async fn search(
        &self,
        query: &str,
        max_results: usize,
    ) -> Result<Vec<WebSearchResult>, SearchError> {
        let resp = self
            .client
            .post("https://html.duckduckgo.com/html/")
            .form(&[("q", query)])
            .send()
            .await
            .map_err(|e| {
                if e.is_timeout() {
                    SearchError::Timeout
                } else {
                    SearchError::Network(e.to_string())
                }
            })?;

        let html = resp
            .text()
            .await
            .map_err(|e| SearchError::Network(e.to_string()))?;

        let mut results = parse_duckduckgo_html(&html);
        results.truncate(max_results);
        Ok(results)
    }
}

/// Parse DuckDuckGo HTML search results page into structured results.
pub fn parse_duckduckgo_html(html: &str) -> Vec<WebSearchResult> {
    let title_re = Regex::new(r#"class="result__a"[^>]*href="([^"]*)"[^>]*>(.*?)</a>"#).unwrap();
    let snippet_re = Regex::new(r#"class="result__snippet"[^>]*>(.*?)</(?:a|td|span|div)>"#).unwrap();

    let titles: Vec<(String, String)> = title_re
        .captures_iter(html)
        .map(|cap| {
            let href = cap.get(1).map(|m| m.as_str()).unwrap_or("");
            let title_html = cap.get(2).map(|m| m.as_str()).unwrap_or("");
            let real_url = decode_uddg_url(href).unwrap_or_else(|| href.to_string());
            (strip_html(title_html), real_url)
        })
        .collect();

    let snippets: Vec<String> = snippet_re
        .captures_iter(html)
        .map(|cap| strip_html(cap.get(1).map(|m| m.as_str()).unwrap_or("")))
        .collect();

    titles
        .into_iter()
        .enumerate()
        .filter(|(_, (title, url))| !title.is_empty() && !url.is_empty())
        .map(|(i, (title, url))| WebSearchResult {
            title,
            url,
            snippet: snippets.get(i).cloned().unwrap_or_default(),
            content: String::new(),
        })
        .collect()
}

/// Extract the real URL from a DuckDuckGo redirect link's `uddg` parameter.
pub fn decode_uddg_url(raw: &str) -> Option<String> {
    // DDG links look like: //duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com&...
    if let Some(start) = raw.find("uddg=") {
        let rest = &raw[start + 5..];
        let end = rest.find('&').unwrap_or(rest.len());
        let encoded = &rest[..end];
        Some(url::form_urlencoded::parse(encoded.as_bytes())
            .map(|(k, _)| k.into_owned())
            .next()
            .unwrap_or_else(|| urlencoding_decode(encoded)))
    } else if raw.starts_with("http") {
        Some(raw.to_string())
    } else {
        None
    }
}

fn urlencoding_decode(s: &str) -> String {
    url::form_urlencoded::parse(s.as_bytes())
        .map(|(k, v)| {
            if v.is_empty() {
                k.into_owned()
            } else {
                format!("{k}={v}")
            }
        })
        .collect::<Vec<_>>()
        .join("&")
}

/// Strip HTML tags and decode common HTML entities.
pub fn strip_html(s: &str) -> String {
    let tag_re = Regex::new(r"<[^>]*>").unwrap();
    let stripped = tag_re.replace_all(s, "");
    stripped
        .replace("&amp;", "&")
        .replace("&lt;", "<")
        .replace("&gt;", ">")
        .replace("&quot;", "\"")
        .replace("&#39;", "'")
        .replace("&apos;", "'")
        .replace("&#x27;", "'")
        .replace("&nbsp;", " ")
        .trim()
        .to_string()
}

#[cfg(test)]
mod tests {
    use super::*;

    const SAMPLE_HTML: &str = r#"
    <div class="result results_links results_links_deep web-result">
      <div class="links_main links_deep result__body">
        <h2 class="result__title">
          <a rel="nofollow" class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fwww.rust-lang.org%2F&rut=abc">Rust Programming Language</a>
        </h2>
        <a class="result__snippet" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fwww.rust-lang.org%2F">A language empowering everyone to build reliable and efficient software.</a>
      </div>
    </div>
    <div class="result results_links results_links_deep web-result">
      <div class="links_main links_deep result__body">
        <h2 class="result__title">
          <a rel="nofollow" class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fdoc.rust-lang.org%2Fbook%2F&rut=xyz">The Rust Book</a>
        </h2>
        <a class="result__snippet" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fdoc.rust-lang.org%2Fbook%2F">The Rust Programming Language book.</a>
      </div>
    </div>
    "#;

    #[test]
    fn test_parse_duckduckgo_html() {
        let results = parse_duckduckgo_html(SAMPLE_HTML);
        assert_eq!(results.len(), 2);
        assert_eq!(results[0].title, "Rust Programming Language");
        assert_eq!(results[0].url, "https://www.rust-lang.org/");
        assert!(results[0].snippet.contains("reliable"));
        assert_eq!(results[1].title, "The Rust Book");
        assert_eq!(results[1].url, "https://doc.rust-lang.org/book/");
    }

    #[test]
    fn test_decode_uddg_url() {
        let raw = "//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2Fpath&rut=abc";
        let decoded = decode_uddg_url(raw);
        assert_eq!(decoded, Some("https://example.com/path".to_string()));
    }

    #[test]
    fn test_decode_uddg_url_direct() {
        let raw = "https://example.com";
        assert_eq!(decode_uddg_url(raw), Some("https://example.com".to_string()));
    }

    #[test]
    fn test_decode_uddg_url_none() {
        assert_eq!(decode_uddg_url("/relative/path"), None);
    }

    #[test]
    fn test_strip_html() {
        assert_eq!(strip_html("<b>bold</b> &amp; <i>italic</i>"), "bold & italic");
        assert_eq!(strip_html("no tags"), "no tags");
        assert_eq!(strip_html("&lt;script&gt;"), "<script>");
    }

    #[test]
    fn test_parse_empty_html() {
        let results = parse_duckduckgo_html("<html><body></body></html>");
        assert!(results.is_empty());
    }

    #[tokio::test]
    #[ignore] // requires network — run with: cargo test -p tfp-search test_duckduckgo_live_search -- --ignored
    async fn test_duckduckgo_live_search() {
        let provider = DuckDuckGoProvider::new();
        let results = provider.search("Rust programming language", 5).await.unwrap();
        assert!(!results.is_empty(), "Expected at least 1 result from DuckDuckGo");
        assert!(results.len() <= 5);
        for r in &results {
            assert!(!r.title.is_empty());
            assert!(r.url.starts_with("http"));
        }
    }
}
