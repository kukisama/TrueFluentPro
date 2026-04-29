//! Web page fetcher — downloads and extracts article text from URLs.

use std::time::Duration;

use scraper::{Html, Selector};

use crate::models::SearchError;

/// Maximum characters to keep from a fetched page.
pub const MAX_CONTENT_CHARS: usize = 3500;

/// Fetches web pages and extracts their text content.
pub struct WebPageFetcher {
    client: reqwest::Client,
}

impl WebPageFetcher {
    /// Create a new fetcher with a browser-like user agent.
    pub fn new() -> Self {
        let client = reqwest::Client::builder()
            .timeout(Duration::from_secs(15))
            .user_agent("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36")
            .redirect(reqwest::redirect::Policy::limited(5))
            .build()
            .unwrap_or_default();
        Self { client }
    }

    /// Fetch a URL and return cleaned article text.
    pub async fn fetch_text(&self, url: &str) -> Result<String, SearchError> {
        let resp = self
            .client
            .get(url)
            .send()
            .await
            .map_err(|e| {
                if e.is_timeout() {
                    SearchError::Timeout
                } else {
                    SearchError::Network(e.to_string())
                }
            })?;

        if !resp.status().is_success() {
            return Err(SearchError::Network(format!(
                "HTTP {} for {url}",
                resp.status()
            )));
        }

        let html = resp
            .text()
            .await
            .map_err(|e| SearchError::Network(e.to_string()))?;

        let text = extract_article_text(&html);
        Ok(clean_text(&text))
    }
}

/// Extract readable text from HTML by removing boilerplate elements
/// and focusing on article/main/body content.
pub fn extract_article_text(html: &str) -> String {
    let doc = Html::parse_document(html);

    // Try progressively broader selectors: article > main > body
    for selector_str in &["article", "main", "[role=main]", "body"] {
        if let Ok(sel) = Selector::parse(selector_str) {
            if let Some(element) = doc.select(&sel).next() {
                let text = extract_text_from_element(&doc, &element);
                if !text.is_empty() {
                    return text;
                }
            }
        }
    }

    // Fallback: get all text from the document
    doc.root_element()
        .text()
        .collect::<Vec<_>>()
        .join(" ")
}

fn extract_text_from_element(
    _doc: &Html,
    element: &scraper::ElementRef,
) -> String {
    // Tags to skip
    let skip_tags = ["script", "style", "nav", "header", "footer", "aside", "noscript", "svg", "iframe"];

    let mut parts: Vec<String> = Vec::new();
    collect_text_recursive(element, &skip_tags, &mut parts);
    parts.join("\n")
}

fn collect_text_recursive(
    node: &scraper::ElementRef,
    skip_tags: &[&str],
    parts: &mut Vec<String>,
) {
    for child in node.children() {
        match child.value() {
            scraper::node::Node::Text(text) => {
                let trimmed = text.trim();
                if !trimmed.is_empty() {
                    parts.push(trimmed.to_string());
                }
            }
            scraper::node::Node::Element(el) => {
                if !skip_tags.contains(&el.name()) {
                    if let Some(child_ref) = scraper::ElementRef::wrap(child) {
                        collect_text_recursive(&child_ref, skip_tags, parts);
                    }
                }
            }
            _ => {}
        }
    }
}

/// Clean extracted text: remove consecutive blank lines, pure URL lines, and truncate.
pub fn clean_text(text: &str) -> String {
    let url_prefix = |line: &str| -> bool {
        let t = line.trim();
        t.starts_with("http://") || t.starts_with("https://")
    };

    let lines: Vec<&str> = text
        .lines()
        .map(|l| l.trim())
        .filter(|l| !l.is_empty() && !url_prefix(l))
        .collect();

    let mut result = String::new();
    let mut prev_empty = false;
    for line in lines {
        if line.is_empty() {
            if !prev_empty {
                result.push('\n');
                prev_empty = true;
            }
        } else {
            if !result.is_empty() {
                result.push('\n');
            }
            result.push_str(line);
            prev_empty = false;
        }
    }

    // Truncate to MAX_CONTENT_CHARS on a char boundary
    if result.chars().count() > MAX_CONTENT_CHARS {
        result = result.chars().take(MAX_CONTENT_CHARS).collect();
        // Try to truncate at last sentence boundary
        if let Some(pos) = result.rfind(|c: char| c == '.' || c == '。' || c == '\n') {
            if pos > MAX_CONTENT_CHARS / 2 {
                result.truncate(pos + 1);
            }
        }
    }

    result
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_extract_article_text() {
        let html = r#"
        <html>
        <head><title>Test</title></head>
        <body>
            <nav>Menu items</nav>
            <header>Site header</header>
            <article>
                <h1>Article Title</h1>
                <p>This is the main content of the article.</p>
                <script>var x = 1;</script>
                <style>.cls { color: red; }</style>
                <p>Second paragraph with important information.</p>
            </article>
            <footer>Site footer</footer>
        </body>
        </html>
        "#;

        let text = extract_article_text(html);
        assert!(text.contains("Article Title"), "Should contain article title");
        assert!(text.contains("main content"), "Should contain article content");
        assert!(text.contains("Second paragraph"), "Should contain second paragraph");
        assert!(!text.contains("var x = 1"), "Should not contain script content");
        assert!(!text.contains("color: red"), "Should not contain style content");
        // nav/header/footer are outside <article> so they're naturally excluded
    }

    #[test]
    fn test_extract_fallback_to_body() {
        let html = r#"
        <html><body>
            <div>Some content here</div>
            <p>More text</p>
        </body></html>
        "#;

        let text = extract_article_text(html);
        assert!(text.contains("Some content"));
        assert!(text.contains("More text"));
    }

    #[test]
    fn test_clean_text() {
        let input = "Line one\n\n\n\nLine two\nhttps://example.com\nLine three";
        let cleaned = clean_text(input);
        assert!(cleaned.contains("Line one"));
        assert!(cleaned.contains("Line two"));
        assert!(cleaned.contains("Line three"));
        assert!(!cleaned.contains("https://example.com"));
    }

    #[test]
    fn test_truncation_at_max_chars() {
        let long_text = "A".repeat(5000);
        let cleaned = clean_text(&long_text);
        assert!(cleaned.chars().count() <= MAX_CONTENT_CHARS);
    }

    #[test]
    fn test_truncation_at_sentence_boundary() {
        // Create text where a period appears around 2/3 of MAX_CONTENT_CHARS
        let mut text = "X".repeat(2500);
        text.push_str(". ");
        text.push_str(&"Y".repeat(2000));
        let cleaned = clean_text(&text);
        assert!(cleaned.ends_with('.'));
        assert!(cleaned.chars().count() <= MAX_CONTENT_CHARS);
    }

    #[test]
    fn test_clean_empty_input() {
        assert_eq!(clean_text(""), "");
        assert_eq!(clean_text("   "), "");
    }
}
