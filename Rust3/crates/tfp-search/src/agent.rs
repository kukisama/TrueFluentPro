//! Search agent service — orchestrates intent analysis, search, and content fetching.

use std::collections::HashSet;
use std::sync::Arc;

use tfp_core::{ChatMessage, CompletionRequest};
use tfp_providers::AiCompletionSlot;

use crate::fetcher::WebPageFetcher;
use crate::models::*;
use crate::provider::WebSearchProvider;

/// Configuration for a search agent run.
#[derive(Debug, Clone)]
pub struct SearchAgentConfig {
    pub max_results: usize,
    pub enable_intent_analysis: bool,
    pub endpoint_id: String,
    pub model: String,
}

const INTENT_PROMPT: &str = r#"You are a search intent classifier. Given the user's question and conversation context, determine if a web search is needed.

If search IS needed, respond with:
<websearch>
<question>first search query</question>
<question>optional second search query</question>
</websearch>

If search is NOT needed (e.g. greetings, math, coding, general knowledge), respond with:
<websearch>not_needed</websearch>

Be concise. Output only the XML tags, nothing else."#;

const REFERENCE_PROMPT: &str = r#"Below are web search results for reference. Use them to answer the user's question accurately. Cite sources using [N] notation where N is the source number.

Search results:
```json
{RESULTS}
```

Instructions:
- Use the search results to provide an informed answer
- Cite sources with [1], [2], etc. when referencing specific information
- If results don't contain relevant information, say so and answer from your knowledge
- Be concise and accurate"#;

/// Orchestrates web search: intent analysis → search → fetch → build context.
pub struct SearchAgent {
    fetcher: WebPageFetcher,
}

impl SearchAgent {
    /// Create a new search agent.
    pub fn new() -> Self {
        Self {
            fetcher: WebPageFetcher::new(),
        }
    }

    /// Run the search agent pipeline.
    ///
    /// 1. (Optional) Intent analysis via LLM
    /// 2. Multi-query parallel search
    /// 3. URL dedup + parallel content fetch
    /// 4. Build context prompt + citations
    pub async fn run(
        &self,
        provider: &dyn WebSearchProvider,
        ai: Arc<dyn AiCompletionSlot>,
        user_message: &str,
        chat_history: &[ChatMessage],
        config: &SearchAgentConfig,
    ) -> AgentResult {
        // Step 1: Intent analysis
        let queries = if config.enable_intent_analysis {
            match self.analyze_intent(ai.clone(), user_message, chat_history, config).await {
                Some(intent) => {
                    if !intent.needs_search {
                        return AgentResult::no_search();
                    }
                    intent.questions
                }
                None => vec![user_message.to_string()],
            }
        } else {
            vec![user_message.to_string()]
        };

        // Step 2: Search across all queries (sequential to avoid 'static requirement)
        let mut all_results: Vec<WebSearchResult> = Vec::new();
        let mut seen_urls: HashSet<String> = HashSet::new();

        for q in &queries {
            match provider.search(q, config.max_results).await {
                Ok(results) => {
                    for r in results {
                        let key = r.url.to_lowercase();
                        if !key.is_empty() && seen_urls.insert(key) {
                            all_results.push(r);
                        }
                    }
                }
                Err(e) => {
                    tracing::warn!("Search query '{q}' failed: {e}");
                }
            }
        }

        if all_results.is_empty() {
            return AgentResult {
                needs_search: true,
                results: Vec::new(),
                all_queries: queries,
                citations: Vec::new(),
                context_prompt: String::new(),
            };
        }

        // Step 3: Content fetch (sequential to avoid lifetime issues)
        for i in 0..all_results.len() {
            let url = all_results[i].url.clone();
            if url.is_empty() {
                continue;
            }
            match self.fetcher.fetch_text(&url).await {
                Ok(content) if !content.is_empty() => {
                    all_results[i].content = content;
                }
                Ok(_) => {} // empty content
                Err(e) => {
                    tracing::debug!("Fetch failed for {url}: {e}");
                }
            }
        }

        // Step 4: Filter out results with no content; fall back to snippets
        let has_content = all_results.iter().any(|r| !r.content.is_empty());
        if !has_content {
            for r in &mut all_results {
                if r.content.is_empty() && !r.snippet.is_empty() {
                    r.content = r.snippet.clone();
                }
            }
        }

        // Step 5: Build citations and context
        let citations: Vec<SearchCitation> = all_results
            .iter()
            .enumerate()
            .map(|(i, r)| SearchCitation::from_result(r, i + 1))
            .collect();

        let context_prompt = format_search_context(&all_results);

        AgentResult {
            needs_search: true,
            results: all_results,
            all_queries: queries,
            citations,
            context_prompt,
        }
    }

    async fn analyze_intent(
        &self,
        ai: Arc<dyn AiCompletionSlot>,
        user_message: &str,
        chat_history: &[ChatMessage],
        config: &SearchAgentConfig,
    ) -> Option<IntentResult> {
        let mut messages = vec![ChatMessage {
            role: "system".into(),
            content: serde_json::Value::String(INTENT_PROMPT.to_string()),
        }];

        // Include last few turns of history for context
        let history_tail = if chat_history.len() > 6 {
            &chat_history[chat_history.len() - 6..]
        } else {
            chat_history
        };
        messages.extend_from_slice(history_tail);

        messages.push(ChatMessage {
            role: "user".into(),
            content: serde_json::Value::String(user_message.to_string()),
        });

        let req = CompletionRequest {
            messages,
            model: config.model.clone(),
            temperature: Some(0.1),
            max_tokens: Some(300),
            endpoint_id: config.endpoint_id.clone(),
            reasoning_effort: None,
            enable_image_generation: false,
            image_model_deployment: None,
            image_size: None,
            image_quality: None,
        };

        match ai.complete(&req).await {
            Ok(resp) => Some(parse_intent_xml(&resp.content)),
            Err(e) => {
                tracing::warn!("Intent analysis failed: {e}, proceeding with search");
                None
            }
        }
    }
}

/// Parse the LLM's intent analysis XML response.
pub fn parse_intent_xml(response: &str) -> IntentResult {
    // Check for "not_needed"
    if response.contains("not_needed") {
        return IntentResult {
            needs_search: false,
            questions: Vec::new(),
        };
    }

    // Extract <question>...</question> tags
    let re = regex::Regex::new(r"<question>(.*?)</question>").unwrap();
    let questions: Vec<String> = re
        .captures_iter(response)
        .filter_map(|cap| {
            let q = cap.get(1)?.as_str().trim().to_string();
            if q.is_empty() { None } else { Some(q) }
        })
        .collect();

    if questions.is_empty() {
        IntentResult {
            needs_search: false,
            questions: Vec::new(),
        }
    } else {
        IntentResult {
            needs_search: true,
            questions,
        }
    }
}

/// Build a context prompt from search results in Cherry-style JSON array format.
pub fn format_search_context(results: &[WebSearchResult]) -> String {
    if results.is_empty() {
        return String::new();
    }

    let json_array: Vec<serde_json::Value> = results
        .iter()
        .enumerate()
        .map(|(i, r)| {
            serde_json::json!({
                "id": i + 1,
                "title": r.title,
                "content": if r.content.is_empty() { &r.snippet } else { &r.content },
                "url": r.url,
            })
        })
        .collect();

    let json_str = serde_json::to_string_pretty(&json_array).unwrap_or_default();
    REFERENCE_PROMPT.replace("{RESULTS}", &json_str)
}

/// Parse intent from a JSON response (for IntentAnalysisService compatibility).
pub fn parse_intent_json(response: &str, search_enabled: bool) -> IntentType {
    if let Ok(val) = serde_json::from_str::<serde_json::Value>(response) {
        if let Some(intent) = val.get("intent").and_then(|v| v.as_str()) {
            return match intent.to_lowercase().as_str() {
                "chat" | "conversation" => {
                    if search_enabled {
                        // When search is enabled, promote ambiguous chat to search
                        IntentType::Search
                    } else {
                        IntentType::Chat
                    }
                }
                "search" | "web_search" | "query" => IntentType::Search,
                "image" | "image_generation" | "generate_image" => IntentType::Image,
                _ => {
                    if search_enabled {
                        IntentType::Search
                    } else {
                        IntentType::Chat
                    }
                }
            };
        }
    }

    // Fallback
    if search_enabled {
        IntentType::Search
    } else {
        IntentType::Chat
    }
}

/// Simple join_all polyfill (for potential future parallel use).
#[allow(dead_code)]
async fn futures_join_all<F, T>(futures: Vec<F>) -> Vec<T>
where
    F: std::future::Future<Output = T> + Send + 'static,
    T: Send + 'static,
{
    let mut handles = Vec::new();
    for f in futures {
        handles.push(tokio::spawn(f));
    }
    let mut results = Vec::new();
    for h in handles {
        if let Ok(r) = h.await {
            results.push(r);
        }
    }
    results
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_parse_intent_xml_multi() {
        let xml = r#"<websearch>
<question>What is Rust programming language?</question>
<question>Rust vs Go performance comparison</question>
</websearch>"#;
        let result = parse_intent_xml(xml);
        assert!(result.needs_search);
        assert_eq!(result.questions.len(), 2);
        assert_eq!(result.questions[0], "What is Rust programming language?");
        assert_eq!(result.questions[1], "Rust vs Go performance comparison");
    }

    #[test]
    fn test_parse_intent_xml_not_needed() {
        let xml = "<websearch>not_needed</websearch>";
        let result = parse_intent_xml(xml);
        assert!(!result.needs_search);
        assert!(result.questions.is_empty());
    }

    #[test]
    fn test_parse_intent_xml_single() {
        let xml = "<websearch><question>latest Rust release version</question></websearch>";
        let result = parse_intent_xml(xml);
        assert!(result.needs_search);
        assert_eq!(result.questions.len(), 1);
    }

    #[test]
    fn test_parse_intent_xml_empty() {
        let result = parse_intent_xml("some random text without xml");
        assert!(!result.needs_search);
    }

    #[test]
    fn test_format_context() {
        let results = vec![
            WebSearchResult {
                title: "Rust Lang".into(),
                url: "https://rust-lang.org".into(),
                snippet: "Systems programming".into(),
                content: "Rust is a multi-paradigm systems programming language.".into(),
            },
        ];
        let ctx = format_search_context(&results);
        assert!(ctx.contains("Rust is a multi-paradigm"));
        assert!(ctx.contains("rust-lang.org"));
        assert!(ctx.contains("[N]") || ctx.contains("Cite sources"));
    }

    #[test]
    fn test_format_context_empty() {
        let ctx = format_search_context(&[]);
        assert!(ctx.is_empty());
    }

    #[test]
    fn test_format_context_uses_snippet_fallback() {
        let results = vec![
            WebSearchResult {
                title: "Example".into(),
                url: "https://example.com".into(),
                snippet: "A snippet".into(),
                content: String::new(), // empty content
            },
        ];
        let ctx = format_search_context(&results);
        assert!(ctx.contains("A snippet"));
    }

    #[test]
    fn test_parse_intent_json_chat() {
        let json = r#"{"intent":"chat","reason":"greeting"}"#;
        assert_eq!(parse_intent_json(json, false), IntentType::Chat);
    }

    #[test]
    fn test_parse_intent_json_search() {
        let json = r#"{"intent":"search","reason":"needs current info"}"#;
        assert_eq!(parse_intent_json(json, false), IntentType::Search);
    }

    #[test]
    fn test_parse_intent_json_chat_promoted_when_search_enabled() {
        let json = r#"{"intent":"chat","reason":"ambiguous"}"#;
        assert_eq!(parse_intent_json(json, true), IntentType::Search);
    }

    #[test]
    fn test_parse_intent_json_image() {
        let json = r#"{"intent":"image_generation","reason":"wants an image"}"#;
        assert_eq!(parse_intent_json(json, false), IntentType::Image);
    }

    #[test]
    fn test_parse_intent_json_fallback() {
        assert_eq!(parse_intent_json("invalid json", false), IntentType::Chat);
        assert_eq!(parse_intent_json("invalid json", true), IntentType::Search);
    }

    #[test]
    fn test_parse_intent_json_unknown_intent() {
        let json = r#"{"intent":"unknown_type","reason":"test"}"#;
        assert_eq!(parse_intent_json(json, false), IntentType::Chat);
        assert_eq!(parse_intent_json(json, true), IntentType::Search);
    }
}
