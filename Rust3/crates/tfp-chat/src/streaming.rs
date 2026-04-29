//! Studio chat streaming service — framework-agnostic async chat completion with persistence.

use std::sync::Arc;
use tfp_core::{
    ChatMessage, CompletionRequest, EventSink,
    StudioMessage, StudioSessionBundle,
};
use tfp_providers::{AiCompletionSlot, StreamChunk};
use tfp_storage::Database;

/// Options for studio chat streaming.
#[derive(Debug, Clone)]
pub struct ChatStreamOptions {
    pub enable_image_generation: bool,
    pub image_model_deployment: Option<String>,
    pub image_size: Option<String>,
    pub image_quality: Option<String>,
    /// Maximum conversation turns (1 turn = user+assistant pair). Default 20.
    pub max_turns: Option<usize>,
    /// Enable web search augmentation.
    pub enable_web_search: bool,
    /// Which search provider to use (e.g. "duckduckgo", "mcp").
    pub web_search_provider_id: Option<String>,
    /// Maximum search results to fetch.
    pub web_search_max_results: Option<usize>,
    /// Whether to run LLM intent analysis before searching.
    pub web_search_enable_intent: Option<bool>,
    /// MCP endpoint URL (required for mcp provider).
    pub web_search_mcp_endpoint: Option<String>,
    /// MCP tool name.
    pub web_search_mcp_tool_name: Option<String>,
    /// MCP API key.
    pub web_search_mcp_api_key: Option<String>,
}

impl Default for ChatStreamOptions {
    fn default() -> Self {
        Self {
            enable_image_generation: false,
            image_model_deployment: None,
            image_size: None,
            image_quality: None,
            max_turns: Some(20),
            enable_web_search: false,
            web_search_provider_id: None,
            web_search_max_results: None,
            web_search_enable_intent: None,
            web_search_mcp_endpoint: None,
            web_search_mcp_tool_name: None,
            web_search_mcp_api_key: None,
        }
    }
}

/// Run a streaming chat completion.
///
/// 1. Persist user message
/// 2. Build chat history from session bundle
/// 3. Stream tokens via EventSink ("studio-message-delta")
/// 4. Persist final assistant message
///
/// Returns `(user_msg, assistant_msg_id)`.
pub async fn run_studio_chat_stream(
    db: &Database,
    sink: &dyn EventSink,
    provider: Arc<dyn AiCompletionSlot>,
    session_id: &str,
    text: &str,
    model: String,
    endpoint_id: &str,
    options: ChatStreamOptions,
) -> Result<(StudioMessage, String), String> {
    // 1. Persist user message
    let now = chrono::Utc::now().to_rfc3339();
    let user_msg_id = uuid::Uuid::new_v4().to_string();
    let seq = db.studio_get_max_sequence(session_id).await.map_err(|e| e.to_string())? + 1;
    let user_msg = StudioMessage {
        id: user_msg_id.clone(),
        session_id: session_id.to_string(),
        sequence_no: seq,
        role: "user".to_string(),
        content_type: "text".to_string(),
        text: text.to_string(),
        reasoning_text: String::new(),
        prompt_tokens: None,
        completion_tokens: None,
        generate_seconds: None,
        download_seconds: None,
        search_summary: None,
        timestamp: now,
        is_deleted: false,
    };
    db.studio_append_message(&user_msg).await.map_err(|e| e.to_string())?;

    let preview: String = text.chars().take(100).collect();
    let _ = db.studio_update_latest_preview(session_id, &preview).await;

    // 2. Build chat history
    let bundle: StudioSessionBundle = db.studio_get_session_bundle(session_id).await.map_err(|e| e.to_string())?;
    let mut chat_messages: Vec<ChatMessage> = Vec::new();
    for msg in &bundle.messages {
        if msg.role == "user" || msg.role == "assistant" {
            chat_messages.push(ChatMessage {
                role: msg.role.clone(),
                content: serde_json::Value::String(msg.text.clone()),
            });
        }
    }
    let max_msgs = options.max_turns.unwrap_or(20) * 2;
    if chat_messages.len() > max_msgs {
        chat_messages = chat_messages[chat_messages.len() - max_msgs..].to_vec();
    }

    // 2b. Web search augmentation
    let mut search_summary: Option<String> = None;
    let mut search_citations: Vec<tfp_search::SearchCitation> = Vec::new();

    if options.enable_web_search {
        sink.emit_json("studio-search-progress", serde_json::json!({
            "session_id": session_id,
            "stage": "intent_analyzed",
        }));

        let search_provider = tfp_search::create_provider(
            options.web_search_provider_id.as_deref().unwrap_or("duckduckgo"),
            options.web_search_mcp_endpoint.as_deref(),
            options.web_search_mcp_tool_name.as_deref(),
            options.web_search_mcp_api_key.as_deref(),
        );

        let agent = tfp_search::SearchAgent::new();
        let agent_config = tfp_search::SearchAgentConfig {
            max_results: options.web_search_max_results.unwrap_or(5),
            enable_intent_analysis: options.web_search_enable_intent.unwrap_or(true),
            endpoint_id: endpoint_id.to_string(),
            model: model.clone(),
        };

        let agent_result = agent.run(
            search_provider.as_ref(),
            provider.clone(),
            text,
            &chat_messages,
            &agent_config,
        ).await;

        sink.emit_json("studio-search-progress", serde_json::json!({
            "session_id": session_id,
            "stage": "search_completed",
            "needs_search": agent_result.needs_search,
            "result_count": agent_result.results.len(),
        }));

        if agent_result.needs_search && !agent_result.context_prompt.is_empty() {
            sink.emit_json("studio-search-progress", serde_json::json!({
                "session_id": session_id,
                "stage": "fetching_content",
            }));

            // Prepend search context as system message
            chat_messages.insert(0, ChatMessage {
                role: "system".into(),
                content: serde_json::Value::String(agent_result.context_prompt),
            });

            search_citations = agent_result.citations;

            // Build search summary for display
            let summary_parts: Vec<String> = search_citations
                .iter()
                .map(|c| format!("[{}] {}", c.number, c.title))
                .collect();
            search_summary = Some(format!(
                "Searched {} sources: {}",
                search_citations.len(),
                summary_parts.join(", ")
            ));
        }
    }

    let req = CompletionRequest {
        messages: chat_messages,
        model,
        temperature: Some(0.7),
        max_tokens: Some(4096),
        endpoint_id: endpoint_id.to_string(),
        reasoning_effort: None,
        enable_image_generation: options.enable_image_generation,
        image_model_deployment: options.image_model_deployment.clone(),
        image_size: options.image_size.clone(),
        image_quality: options.image_quality.clone(),
    };

    let mut rx = provider.complete_stream(&req).await.map_err(|e| e.to_string())?;

    let assistant_msg_id = uuid::Uuid::new_v4().to_string();
    let aid = assistant_msg_id.clone();
    let sid = session_id.to_string();

    // 3. Stream tokens
    let start = std::time::Instant::now();
    let mut full_text = String::new();
    let mut reasoning = String::new();
    let mut p_tokens: Option<i64> = None;
    let mut c_tokens: Option<i64> = None;

    while let Some(result) = rx.recv().await {
        match result {
            Ok(chunk) => match chunk {
                StreamChunk::Token(token) => {
                    full_text.push_str(&token);
                    sink.emit_json("studio-message-delta", serde_json::json!({
                        "session_id": &sid,
                        "message_id": &aid,
                        "token": token,
                    }));
                }
                StreamChunk::Reasoning(text) => {
                    reasoning.push_str(&text);
                    sink.emit_json("studio-message-delta", serde_json::json!({
                        "session_id": &sid,
                        "message_id": &aid,
                        "reasoning": text,
                    }));
                }
                StreamChunk::Usage { prompt_tokens, completion_tokens } => {
                    p_tokens = Some(prompt_tokens as i64);
                    c_tokens = Some(completion_tokens as i64);
                }
                StreamChunk::ReasoningSummary(text) => {
                    reasoning.push_str(&text);
                }
                StreamChunk::ImageGenerating => {
                    sink.emit_json("studio-message-delta", serde_json::json!({
                        "session_id": &sid,
                        "message_id": &aid,
                        "image_generating": true,
                    }));
                }
                StreamChunk::ImageResult { base64_data, content_type } => {
                    sink.emit_json("studio-message-delta", serde_json::json!({
                        "session_id": &sid,
                        "message_id": &aid,
                        "image_result": { "base64_data": base64_data, "content_type": content_type },
                    }));
                }
            },
            Err(e) => {
                sink.emit_json("studio-message-delta", serde_json::json!({
                    "session_id": &sid,
                    "message_id": &aid,
                    "error": e.to_string(),
                    "done": true,
                }));
                return Err(e.to_string());
            }
        }
    }

    // 4. Persist assistant message
    let gen_secs = start.elapsed().as_secs_f64();
    let seq2 = db.studio_get_max_sequence(&sid).await.unwrap_or(0) + 1;
    let msg = StudioMessage {
        id: aid.clone(),
        session_id: sid.clone(),
        sequence_no: seq2,
        role: "assistant".to_string(),
        content_type: "text".to_string(),
        text: full_text,
        reasoning_text: reasoning,
        prompt_tokens: p_tokens,
        completion_tokens: c_tokens,
        generate_seconds: Some(gen_secs),
        download_seconds: None,
        search_summary: search_summary,
        timestamp: chrono::Utc::now().to_rfc3339(),
        is_deleted: false,
    };
    let _ = db.studio_append_message(&msg).await;

    // 4b. Persist search citations
    if !search_citations.is_empty() {
        let core_citations: Vec<tfp_core::StudioCitation> = search_citations
            .iter()
            .map(|c| tfp_core::StudioCitation {
                id: 0,
                message_id: aid.clone(),
                citation_number: c.number as i64,
                title: c.title.clone(),
                url: c.url.clone(),
                snippet: c.snippet.clone(),
                hostname: c.hostname.clone(),
            })
            .collect();
        let _ = db.studio_insert_citations(&aid, &core_citations).await;
    }

    sink.emit_json("studio-message-delta", serde_json::json!({
        "session_id": &sid,
        "message_id": &aid,
        "done": true,
    }));

    Ok((user_msg, assistant_msg_id))
}

/// Run a generic AI streaming completion (for ai_complete_stream command).
///
/// Streams tokens via EventSink ("ai-stream-token") with the given stream_id.
pub async fn run_ai_stream(
    sink: &dyn EventSink,
    provider: Arc<dyn AiCompletionSlot>,
    request: &CompletionRequest,
    stream_id: &str,
) -> Result<(), String> {
    let mut rx = provider.complete_stream(request).await.map_err(|e| e.to_string())?;
    let sid = stream_id.to_string();

    while let Some(result) = rx.recv().await {
        match result {
            Ok(chunk) => {
                let payload = match chunk {
                    StreamChunk::Token(token) => serde_json::json!({
                        "stream_id": &sid,
                        "token": token,
                    }),
                    StreamChunk::Reasoning(text) => serde_json::json!({
                        "stream_id": &sid,
                        "reasoning": text,
                    }),
                    StreamChunk::Usage { prompt_tokens, completion_tokens } => serde_json::json!({
                        "stream_id": &sid,
                        "usage": {
                            "prompt_tokens": prompt_tokens,
                            "completion_tokens": completion_tokens,
                        },
                    }),
                    StreamChunk::ReasoningSummary(text) => serde_json::json!({
                        "stream_id": &sid,
                        "reasoning_summary": text,
                    }),
                    StreamChunk::ImageGenerating => serde_json::json!({
                        "stream_id": &sid,
                        "image_generating": true,
                    }),
                    StreamChunk::ImageResult { base64_data, content_type } => serde_json::json!({
                        "stream_id": &sid,
                        "image_result": { "base64_data": base64_data, "content_type": content_type },
                    }),
                };
                sink.emit_json("ai-stream-token", payload);
            }
            Err(e) => {
                sink.emit_json("ai-stream-token", serde_json::json!({
                    "stream_id": &sid,
                    "error": e.to_string(),
                }));
                break;
            }
        }
    }
    sink.emit_json("ai-stream-token", serde_json::json!({
        "stream_id": &sid,
        "done": true,
    }));

    Ok(())
}

/// Optimize a prompt using an AI completion provider.
pub async fn optimize_prompt(
    provider: Arc<dyn AiCompletionSlot>,
    prompt: &str,
    endpoint_id: &str,
) -> Result<String, String> {
    let request = CompletionRequest {
        messages: vec![
            ChatMessage {
                role: "system".into(),
                content: serde_json::Value::String(
                    "你是一个提示词优化专家。用户会给你一段提示词（可能是对话问题或图片描述），\
                     请优化它使其更精确、更有效。仅返回优化后的提示词文本，不要任何解释。"
                        .into(),
                ),
            },
            ChatMessage {
                role: "user".into(),
                content: serde_json::Value::String(prompt.to_string()),
            },
        ],
        model: String::new(),
        temperature: Some(0.7),
        max_tokens: Some(1000),
        endpoint_id: endpoint_id.to_string(),
        reasoning_effort: None,
        enable_image_generation: false,
        image_model_deployment: None,
        image_size: None,
        image_quality: None,
    };

    let resp = provider.complete(&request).await.map_err(|e| e.to_string())?;
    Ok(resp.content)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_chat_stream_options_default() {
        let opts = ChatStreamOptions::default();
        assert!(!opts.enable_image_generation);
        assert!(opts.image_model_deployment.is_none());
        assert!(opts.image_size.is_none());
        assert!(opts.image_quality.is_none());
        assert_eq!(opts.max_turns, Some(20));
        assert!(!opts.enable_web_search);
        assert!(opts.web_search_provider_id.is_none());
        assert!(opts.web_search_max_results.is_none());
        assert!(opts.web_search_enable_intent.is_none());
    }

    #[test]
    fn test_truncate_history() {
        // max_turns=2 means 4 messages max (2 user + 2 assistant)
        let max_turns = 2usize;
        let max_msgs = max_turns * 2;
        let mut msgs: Vec<ChatMessage> = (0..10).map(|i| ChatMessage {
            role: if i % 2 == 0 { "user".into() } else { "assistant".into() },
            content: serde_json::Value::String(format!("msg {i}")),
        }).collect();

        if msgs.len() > max_msgs {
            msgs = msgs[msgs.len() - max_msgs..].to_vec();
        }
        assert_eq!(msgs.len(), 4);
        assert_eq!(msgs[0].content.as_str().unwrap(), "msg 6");
        assert_eq!(msgs[3].content.as_str().unwrap(), "msg 9");
    }

    #[test]
    fn test_chat_stream_options_custom() {
        let opts = ChatStreamOptions {
            enable_image_generation: true,
            image_model_deployment: Some("gpt-image-1".into()),
            image_size: Some("1024x1024".into()),
            image_quality: Some("high".into()),
            max_turns: Some(10),
            enable_web_search: true,
            web_search_provider_id: Some("duckduckgo".into()),
            web_search_max_results: Some(3),
            web_search_enable_intent: Some(false),
            web_search_mcp_endpoint: None,
            web_search_mcp_tool_name: None,
            web_search_mcp_api_key: None,
        };
        assert!(opts.enable_image_generation);
        assert_eq!(opts.image_model_deployment.as_deref(), Some("gpt-image-1"));
        assert_eq!(opts.max_turns, Some(10));
        assert!(opts.enable_web_search);
        assert_eq!(opts.web_search_provider_id.as_deref(), Some("duckduckgo"));
        assert_eq!(opts.web_search_max_results, Some(3));
    }
}
