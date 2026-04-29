use async_trait::async_trait;
use futures_util::StreamExt;
use serde_json::json;
use tokio::sync::mpsc;

use tfp_core::{
    AiEndpoint, CompletionRequest, CompletionResponse, EndpointType, ProviderError, TokenUsage,
};

use crate::auth::apply_auth;
use crate::traits::{AiCompletionSlot, ProviderCapability, ProviderMeta, StreamChunk};

fn parse_usage(usage: &serde_json::Map<String, serde_json::Value>, pt_key: &str, ct_key: &str) -> StreamChunk {
    let pt = usage.get(pt_key).and_then(|v| v.as_u64()).unwrap_or(0) as u32;
    let ct = usage.get(ct_key).and_then(|v| v.as_u64()).unwrap_or(0) as u32;
    StreamChunk::Usage { prompt_tokens: pt, completion_tokens: ct }
}

/// Try to extract reasoning text from an SSE delta object.
/// Supports 3 field names x 2 value formats (string or {text}).
pub(crate) fn try_read_reasoning(delta: &serde_json::Value) -> Option<String> {
    for key in &["reasoning", "reasoning_content", "thinking"] {
        if let Some(v) = delta.get(*key) {
            // Direct string
            if let Some(s) = v.as_str() {
                if !s.is_empty() {
                    return Some(s.to_string());
                }
            }
            // Object with "text" field
            if let Some(s) = v.get("text").and_then(|t| t.as_str()) {
                if !s.is_empty() {
                    return Some(s.to_string());
                }
            }
        }
    }
    None
}

/// Check if a URL targets the Responses API (contains "/responses").
fn is_responses_url(url: &str) -> bool {
    url.contains("/responses")
}

pub struct OpenAiChatProvider {
    client: reqwest::Client,
    endpoint: AiEndpoint,
}

impl OpenAiChatProvider {
    pub fn new(endpoint: AiEndpoint) -> Self {
        Self {
            client: reqwest::Client::new(),
            endpoint,
        }
    }

    /// Build candidate URLs for chat/completions or responses API.
    pub(crate) fn build_chat_urls(&self) -> Vec<String> {
        let base = self.endpoint.url.trim_end_matches('/');
        match self.endpoint.endpoint_type {
            EndpointType::AzureOpenAi => {
                vec![format!("{base}/openai/v1/responses")]
            }
            EndpointType::ApiManagementGateway => {
                let api_ver = self
                    .endpoint
                    .api_version
                    .as_deref()
                    .unwrap_or("2025-03-01-preview");
                vec![
                    format!("{base}/v1/responses"),
                    format!("{base}/responses?api-version={api_ver}"),
                    format!("{base}/v1/chat/completions"),
                ]
            }
            _ => {
                if base.ends_with("/v1") || base.ends_with("/v1/chat/completions") {
                    if base.ends_with("/v1/chat/completions") {
                        vec![base.to_string()]
                    } else {
                        vec![format!("{base}/chat/completions")]
                    }
                } else {
                    vec![format!("{base}/v1/chat/completions")]
                }
            }
        }
    }

    fn build_responses_input(messages: &[tfp_core::ChatMessage]) -> serde_json::Value {
        if messages.is_empty() {
            return json!("");
        }
        if messages.len() > 1 {
            let input: Vec<serde_json::Value> = messages
                .iter()
                .map(|msg| {
                    let content = match &msg.content {
                        serde_json::Value::String(s) => json!(s),
                        serde_json::Value::Array(parts) => json!(parts),
                        other => json!(other.to_string()),
                    };
                    json!({
                        "type": "message",
                        "role": &msg.role,
                        "content": content
                    })
                })
                .collect();
            return json!(input);
        }
        let last_msg = &messages[0];
        match &last_msg.content {
            serde_json::Value::String(s) => json!(s),
            serde_json::Value::Array(parts) => {
                json!([{
                    "type": "message",
                    "role": "user",
                    "content": parts
                }])
            }
            other => json!(other.to_string()),
        }
    }

    /// Build request body for Responses API.
    fn build_responses_body(&self, request: &CompletionRequest) -> serde_json::Value {
        let input = Self::build_responses_input(&request.messages);
        let mut body = json!({ "model": &request.model, "input": input });

        if let Some(ref effort) = request.reasoning_effort {
            body["reasoning"] = json!({"effort": effort, "summary": "auto"});
        }
        if let Some(temp) = request.temperature {
            body["temperature"] = json!(temp);
        }
        if request.enable_image_generation {
            body["tools"] = json!([{"type": "image_generation"}]);
        }
        body
    }

    /// Build request body for Chat Completions API.
    fn build_chat_completions_body(&self, request: &CompletionRequest, url: &str) -> serde_json::Value {
        let messages: Vec<serde_json::Value> = request
            .messages
            .iter()
            .map(|m| json!({ "role": &m.role, "content": &m.content }))
            .collect();
        let mut body = json!({ "model": &request.model, "messages": messages });

        if url.contains("/openai/deployments/") {
            body.as_object_mut().unwrap().remove("model");
        }
        if let Some(ref effort) = request.reasoning_effort {
            body["reasoning_effort"] = json!(effort);
        }
        if let Some(temp) = request.temperature {
            body["temperature"] = json!(temp);
        }
        if let Some(max) = request.max_tokens {
            body["max_completion_tokens"] = json!(max);
        }
        body
    }

    /// Apply image_model_deployment header if needed.
    fn apply_image_gen_header(&self, builder: reqwest::RequestBuilder, request: &CompletionRequest) -> reqwest::RequestBuilder {
        if let Some(ref deployment) = request.image_model_deployment {
            builder.header("x-ms-oai-image-generation-deployment", deployment)
        } else {
            builder
        }
    }

    /// Try candidate URLs in order, returning the first successful response.
    /// 404/405 → try next. Auth/rate-limit → return immediately.
    async fn try_send_candidates(
        &self,
        urls: &[String],
        request: &CompletionRequest,
        stream: bool,
    ) -> Result<(reqwest::Response, String, bool), ProviderError> {
        let mut last_error = String::new();

        for url in urls {
            let is_resp = is_responses_url(url);
            let mut body = if is_resp {
                self.build_responses_body(request)
            } else {
                self.build_chat_completions_body(request, url)
            };

            // Set stream flag
            body["stream"] = json!(stream);
            if stream && !is_resp {
                body["stream_options"] = json!({"include_usage": true});
            }

            let builder = apply_auth(&self.endpoint, self.client.post(url)).json(&body);
            let builder = self.apply_image_gen_header(builder, request);

            match builder.send().await {
                Ok(response) => {
                    let status = response.status();
                    if status.is_success() {
                        return Ok((response, url.clone(), is_resp));
                    }
                    let text = response.text().await.unwrap_or_default();
                    if status.as_u16() == 401 || status.as_u16() == 403 {
                        return Err(ProviderError::Auth(format!("{status}: {text}")));
                    }
                    if status.as_u16() == 429 {
                        return Err(ProviderError::RateLimited { retry_after_ms: 5000 });
                    }
                    if status.as_u16() == 404 || status.as_u16() == 405 {
                        last_error = format!("{status}: {text}");
                        continue;
                    }
                    return Err(ProviderError::Network(format!("{status}: {text}")));
                }
                Err(e) => {
                    last_error = e.to_string();
                    continue;
                }
            }
        }

        Err(ProviderError::Network(format!(
            "All {} candidate URLs failed: {last_error}",
            urls.len()
        )))
    }
}

impl ProviderMeta for OpenAiChatProvider {
    fn id(&self) -> &str {
        &self.endpoint.id
    }
    fn display_name(&self) -> &str {
        &self.endpoint.name
    }
    fn capabilities(&self) -> Vec<ProviderCapability> {
        vec![ProviderCapability::AiCompletion]
    }
}

#[async_trait]
impl AiCompletionSlot for OpenAiChatProvider {
    async fn complete(
        &self,
        request: &CompletionRequest,
    ) -> Result<CompletionResponse, ProviderError> {
        let candidates = self.build_chat_urls();
        let (resp, _url, is_responses) = self.try_send_candidates(&candidates, request, false).await?;

        let json_val: serde_json::Value = resp
            .json()
            .await
            .map_err(|e| ProviderError::Internal(format!("JSON parse error: {e}")))?;

        if is_responses {
            let content = json_val["output"]
                .as_array()
                .and_then(|arr| arr.iter().find(|o| o["type"].as_str() == Some("message")))
                .and_then(|msg| msg["content"].as_array())
                .and_then(|c| c.first())
                .and_then(|t| t["text"].as_str())
                .unwrap_or("")
                .to_string();
            let model_name = json_val["model"]
                .as_str()
                .unwrap_or(&request.model)
                .to_string();
            let usage = json_val.get("usage").map(|u| TokenUsage {
                prompt_tokens: u["input_tokens"].as_u64().unwrap_or(0) as u32,
                completion_tokens: u["output_tokens"].as_u64().unwrap_or(0) as u32,
                total_tokens: u["total_tokens"].as_u64().unwrap_or(0) as u32,
            });
            Ok(CompletionResponse {
                content,
                model: model_name,
                usage,
            })
        } else {
            let content = json_val["choices"][0]["message"]["content"]
                .as_str()
                .unwrap_or("")
                .to_string();
            let model_name = json_val["model"]
                .as_str()
                .unwrap_or(&request.model)
                .to_string();
            let usage = json_val.get("usage").map(|u| TokenUsage {
                prompt_tokens: u["prompt_tokens"].as_u64().unwrap_or(0) as u32,
                completion_tokens: u["completion_tokens"].as_u64().unwrap_or(0) as u32,
                total_tokens: u["total_tokens"].as_u64().unwrap_or(0) as u32,
            });
            Ok(CompletionResponse {
                content,
                model: model_name,
                usage,
            })
        }
    }

    async fn complete_stream(
        &self,
        request: &CompletionRequest,
    ) -> Result<mpsc::UnboundedReceiver<Result<StreamChunk, ProviderError>>, ProviderError> {
        let candidates = self.build_chat_urls();
        let (resp, _url, is_responses) = self.try_send_candidates(&candidates, request, true).await?;

        let (tx, rx) = mpsc::unbounded_channel();

        tokio::spawn(async move {
            let mut byte_stream = resp.bytes_stream();
            let mut buffer = String::new();

            while let Some(chunk_result) = byte_stream.next().await {
                let chunk = match chunk_result {
                    Ok(bytes) => String::from_utf8_lossy(&bytes).to_string(),
                    Err(e) => {
                        let _ = tx.send(Err(ProviderError::Network(e.to_string())));
                        return;
                    }
                };
                buffer.push_str(&chunk);

                while let Some(pos) = buffer.find('\n') {
                    let line = buffer[..pos].trim().to_string();
                    buffer = buffer[pos + 1..].to_string();

                    if line.is_empty() || line.starts_with(':') {
                        continue;
                    }
                    if line == "data: [DONE]" {
                        return;
                    }
                    if let Some(json_str) = line.strip_prefix("data: ") {
                        if let Ok(v) = serde_json::from_str::<serde_json::Value>(json_str) {
                            if is_responses {
                                // Responses API SSE events
                                match v["type"].as_str() {
                                    Some("response.output_text.delta") => {
                                        if let Some(d) = v["delta"].as_str().filter(|s| !s.is_empty()) {
                                            let _ = tx.send(Ok(StreamChunk::Token(d.to_string())));
                                        }
                                    }
                                    Some("response.reasoning_summary_text.delta") => {
                                        if let Some(d) = v["delta"].as_str().filter(|s| !s.is_empty()) {
                                            let _ = tx.send(Ok(StreamChunk::ReasoningSummary(d.to_string())));
                                        }
                                    }
                                    Some("response.output_item.added") => {
                                        if v["item"]["type"].as_str() == Some("image_generation_call") {
                                            let _ = tx.send(Ok(StreamChunk::ImageGenerating));
                                        }
                                    }
                                    Some("response.completed") => {
                                        // Usage
                                        if let Some(u) = v["response"]["usage"].as_object() {
                                            let _ = tx.send(Ok(parse_usage(u, "input_tokens", "output_tokens")));
                                        }
                                        // Image results
                                        if let Some(outputs) = v["response"]["output"].as_array() {
                                            for output in outputs {
                                                if output["type"].as_str() == Some("image_generation_call") {
                                                    if let Some(b64) = output["result"].as_str() {
                                                        let _ = tx.send(Ok(StreamChunk::ImageResult {
                                                            base64_data: b64.to_string(),
                                                            content_type: "image/png".to_string(),
                                                        }));
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    _ => {}
                                }
                            } else {
                                // Chat Completions SSE events
                                if let Some(delta) = v["choices"].get(0).and_then(|c| c.get("delta")) {
                                    // Content
                                    if let Some(d) = delta["content"].as_str().filter(|s| !s.is_empty()) {
                                        let _ = tx.send(Ok(StreamChunk::Token(d.to_string())));
                                    }
                                    // Reasoning (3-field compat)
                                    if let Some(r) = try_read_reasoning(delta) {
                                        let _ = tx.send(Ok(StreamChunk::Reasoning(r)));
                                    }
                                }
                                // Usage (usually in last chunk)
                                if let Some(u) = v["usage"].as_object() {
                                    let _ = tx.send(Ok(parse_usage(u, "prompt_tokens", "completion_tokens")));
                                }
                            }
                        }
                    }
                }
            }
        });

        Ok(rx)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::test_helpers::factories;
    use tfp_core::EndpointType;

    // --- T-001: build_chat_urls ---

    #[test]
    fn test_build_chat_urls_azure() {
        let p = OpenAiChatProvider::new(factories::azure_openai_endpoint("ep-azure", "My Azure"));
        let urls = p.build_chat_urls();
        assert_eq!(urls.len(), 1);
        assert_eq!(urls[0], "https://myresource.openai.azure.com/openai/v1/responses");
    }

    #[test]
    fn test_build_chat_urls_apim() {
        let mut ep = factories::azure_openai_endpoint("ep-apim", "APIM");
        ep.endpoint_type = EndpointType::ApiManagementGateway;
        ep.url = "https://apim.azure-api.net/ai01/openai".into();
        let p = OpenAiChatProvider::new(ep);
        let urls = p.build_chat_urls();
        assert_eq!(urls.len(), 3);
        assert!(urls[0].contains("/v1/responses"));
        assert!(urls[1].contains("/responses?api-version="));
        assert!(urls[2].contains("/v1/chat/completions"));
    }

    #[test]
    fn test_build_chat_urls_openai_compatible() {
        let p = OpenAiChatProvider::new(factories::openai_compatible_endpoint("ep-oai", "OpenAI"));
        let urls = p.build_chat_urls();
        assert_eq!(urls.len(), 1);
        assert_eq!(urls[0], "https://api.openai.com/v1/chat/completions");
    }

    #[test]
    fn test_build_chat_urls_openai_with_v1_base() {
        let mut ep = factories::openai_compatible_endpoint("ep-v1", "V1");
        ep.url = "https://api.openai.com/v1".into();
        let p = OpenAiChatProvider::new(ep);
        let urls = p.build_chat_urls();
        assert_eq!(urls.len(), 1);
        assert_eq!(urls[0], "https://api.openai.com/v1/chat/completions");
    }

    #[test]
    fn test_build_chat_urls_returns_at_least_one() {
        let p = OpenAiChatProvider::new(factories::azure_openai_endpoint("a", "A"));
        assert!(!p.build_chat_urls().is_empty());
    }

    // Original tests preserved

    #[test]
    fn test_build_url_azure() {
        let p = OpenAiChatProvider::new(factories::azure_openai_endpoint("ep-azure", "My Azure"));
        assert_eq!(
            p.build_chat_urls()[0],
            "https://myresource.openai.azure.com/openai/v1/responses"
        );
    }

    #[test]
    fn test_build_url_openai_compatible() {
        let p = OpenAiChatProvider::new(factories::openai_compatible_endpoint("ep-oai", "OpenAI"));
        assert_eq!(p.build_chat_urls()[0], "https://api.openai.com/v1/chat/completions");
    }

    #[test]
    fn test_build_url_apim() {
        let mut ep = factories::azure_openai_endpoint("ep-apim", "APIM");
        ep.endpoint_type = EndpointType::ApiManagementGateway;
        ep.url = "https://apim.azure-api.net/ai01/openai".into();
        let p = OpenAiChatProvider::new(ep);
        assert_eq!(
            p.build_chat_urls()[0],
            "https://apim.azure-api.net/ai01/openai/v1/responses"
        );
    }

    #[test]
    fn test_provider_meta() {
        let p = OpenAiChatProvider::new(factories::azure_openai_endpoint("ep-azure", "My Azure"));
        assert_eq!(p.id(), "ep-azure");
        assert_eq!(p.display_name(), "My Azure");
        assert_eq!(p.capabilities(), vec![ProviderCapability::AiCompletion]);
    }

    #[test]
    fn test_parse_usage() {
        let mut map = serde_json::Map::new();
        map.insert("prompt_tokens".into(), json!(100));
        map.insert("completion_tokens".into(), json!(50));
        let chunk = parse_usage(&map, "prompt_tokens", "completion_tokens");
        match chunk {
            StreamChunk::Usage { prompt_tokens, completion_tokens } => {
                assert_eq!(prompt_tokens, 100);
                assert_eq!(completion_tokens, 50);
            }
            _ => panic!("Expected Usage chunk"),
        }

        let empty = serde_json::Map::new();
        let chunk2 = parse_usage(&empty, "prompt_tokens", "completion_tokens");
        match chunk2 {
            StreamChunk::Usage { prompt_tokens, completion_tokens } => {
                assert_eq!(prompt_tokens, 0);
                assert_eq!(completion_tokens, 0);
            }
            _ => panic!("Expected Usage chunk"),
        }
    }

    #[test]
    fn test_is_responses_api() {
        let azure = OpenAiChatProvider::new(factories::azure_openai_endpoint("a", "A"));
        assert!(azure.build_chat_urls()[0].contains("responses"));

        let mut apim_ep = factories::azure_openai_endpoint("b", "B");
        apim_ep.endpoint_type = EndpointType::ApiManagementGateway;
        let apim = OpenAiChatProvider::new(apim_ep);
        assert!(apim.build_chat_urls()[0].contains("responses"));

        let oai = OpenAiChatProvider::new(factories::openai_compatible_endpoint("c", "C"));
        assert!(!oai.build_chat_urls()[0].contains("responses"));

        let mut custom_ep = factories::openai_compatible_endpoint("d", "D");
        custom_ep.endpoint_type = EndpointType::Custom;
        let custom = OpenAiChatProvider::new(custom_ep);
        assert!(!custom.build_chat_urls()[0].contains("responses"));
    }

    #[test]
    fn test_build_responses_input() {
        use tfp_core::ChatMessage;

        let result = OpenAiChatProvider::build_responses_input(&[]);
        assert_eq!(result, json!(""));

        let msgs = vec![ChatMessage {
            role: "user".into(),
            content: serde_json::Value::String("hello".into()),
        }];
        let result = OpenAiChatProvider::build_responses_input(&msgs);
        assert_eq!(result, json!("hello"));

        let msgs = vec![ChatMessage {
            role: "user".into(),
            content: json!([{"type": "text", "text": "hi"}]),
        }];
        let result = OpenAiChatProvider::build_responses_input(&msgs);
        assert_eq!(
            result,
            json!([{"type": "message", "role": "user", "content": [{"type": "text", "text": "hi"}]}])
        );

        let msgs = vec![
            ChatMessage {
                role: "system".into(),
                content: serde_json::Value::String("You are a helper.".into()),
            },
            ChatMessage {
                role: "user".into(),
                content: serde_json::Value::String("Hi".into()),
            },
        ];
        let result = OpenAiChatProvider::build_responses_input(&msgs);
        assert_eq!(
            result,
            json!([
                {"type": "message", "role": "system", "content": "You are a helper."},
                {"type": "message", "role": "user", "content": "Hi"}
            ])
        );
    }

    // --- T-003: try_read_reasoning ---

    #[test]
    fn test_try_read_reasoning_reasoning_string() {
        let delta = json!({"reasoning": "Step 1"});
        assert_eq!(try_read_reasoning(&delta), Some("Step 1".into()));
    }

    #[test]
    fn test_try_read_reasoning_reasoning_object() {
        let delta = json!({"reasoning": {"text": "Step 2"}});
        assert_eq!(try_read_reasoning(&delta), Some("Step 2".into()));
    }

    #[test]
    fn test_try_read_reasoning_reasoning_content_string() {
        let delta = json!({"reasoning_content": "Step 3"});
        assert_eq!(try_read_reasoning(&delta), Some("Step 3".into()));
    }

    #[test]
    fn test_try_read_reasoning_reasoning_content_object() {
        let delta = json!({"reasoning_content": {"text": "Step 4"}});
        assert_eq!(try_read_reasoning(&delta), Some("Step 4".into()));
    }

    #[test]
    fn test_try_read_reasoning_thinking_string() {
        let delta = json!({"thinking": "Step 5"});
        assert_eq!(try_read_reasoning(&delta), Some("Step 5".into()));
    }

    #[test]
    fn test_try_read_reasoning_thinking_object() {
        let delta = json!({"thinking": {"text": "Step 6"}});
        assert_eq!(try_read_reasoning(&delta), Some("Step 6".into()));
    }

    #[test]
    fn test_try_read_reasoning_none() {
        let delta = json!({"content": "hello"});
        assert_eq!(try_read_reasoning(&delta), None);
    }

    #[test]
    fn test_try_read_reasoning_empty_string() {
        let delta = json!({"reasoning": ""});
        assert_eq!(try_read_reasoning(&delta), None);
    }

    // --- T-004/T-005: request body construction ---

    #[test]
    fn test_build_responses_body_with_reasoning() {
        let ep = factories::azure_openai_endpoint("a", "A");
        let p = OpenAiChatProvider::new(ep);
        let req = CompletionRequest {
            messages: vec![],
            model: "gpt-4o".into(),
            temperature: None,
            max_tokens: None,
            endpoint_id: "a".into(),
            reasoning_effort: Some("medium".into()),
            enable_image_generation: true,
            image_model_deployment: Some("gpt-image-1".into()),
            image_size: None,
            image_quality: None,
        };
        let body = p.build_responses_body(&req);
        assert_eq!(body["reasoning"]["effort"], "medium");
        assert_eq!(body["reasoning"]["summary"], "auto");
        assert_eq!(body["tools"][0]["type"], "image_generation");
    }

    #[test]
    fn test_build_chat_completions_body_with_reasoning() {
        let ep = factories::openai_compatible_endpoint("b", "B");
        let p = OpenAiChatProvider::new(ep);
        let req = CompletionRequest {
            messages: vec![],
            model: "deepseek-r1".into(),
            temperature: Some(0.7),
            max_tokens: Some(100),
            endpoint_id: "b".into(),
            reasoning_effort: Some("high".into()),
            enable_image_generation: false,
            image_model_deployment: None,
            image_size: None,
            image_quality: None,
        };
        let url = "https://example.com/v1/chat/completions";
        let body = p.build_chat_completions_body(&req, url);
        assert_eq!(body["reasoning_effort"], "high");
        assert_eq!(body["temperature"], 0.7);
        assert_eq!(body["max_completion_tokens"], 100);
        assert!(body.get("tools").is_none());
    }

    // --- T-006: StreamChunk variants ---

    #[test]
    fn test_stream_chunk_new_variants_exist() {
        let _ig = StreamChunk::ImageGenerating;
        let _ir = StreamChunk::ImageResult {
            base64_data: "abc".into(),
            content_type: "image/png".into(),
        };
        let _rs = StreamChunk::ReasoningSummary("summary".into());
    }

    #[test]
    fn test_is_responses_url() {
        assert!(is_responses_url("https://example.com/v1/responses"));
        assert!(is_responses_url("https://example.com/responses?api-version=v1"));
        assert!(!is_responses_url("https://example.com/v1/chat/completions"));
    }
}
