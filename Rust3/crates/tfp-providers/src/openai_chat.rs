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

    pub(crate) fn build_url(&self) -> String {
        let base = self.endpoint.url.trim_end_matches('/');
        match self.endpoint.endpoint_type {
            EndpointType::AzureOpenAi => {
                format!("{base}/openai/v1/responses")
            }
            EndpointType::ApiManagementGateway => {
                let api_ver = self
                    .endpoint
                    .api_version
                    .as_deref()
                    .unwrap_or("2025-03-01-preview");
                format!("{base}/responses?api-version={api_ver}")
            }
            _ => {
                if base.ends_with("/v1") || base.ends_with("/v1/chat/completions") {
                    if base.ends_with("/v1/chat/completions") {
                        base.to_string()
                    } else {
                        format!("{base}/chat/completions")
                    }
                } else {
                    format!("{base}/v1/chat/completions")
                }
            }
        }
    }

    fn is_responses_api(&self) -> bool {
        matches!(
            self.endpoint.endpoint_type,
            EndpointType::AzureOpenAi | EndpointType::ApiManagementGateway
        )
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
        let url = self.build_url();
        let is_responses = self.is_responses_api();

        let body = if is_responses {
            let input = Self::build_responses_input(&request.messages);
            let mut b = json!({ "model": &request.model, "input": input, "stream": false });
            if let Some(temp) = request.temperature {
                b["temperature"] = json!(temp);
            }
            b
        } else {
            let messages: Vec<serde_json::Value> = request
                .messages
                .iter()
                .map(|m| json!({ "role": &m.role, "content": &m.content }))
                .collect();
            let mut b = json!({ "model": &request.model, "messages": messages, "stream": false });
            if url.contains("/openai/deployments/") {
                b.as_object_mut().unwrap().remove("model");
            }
            if let Some(temp) = request.temperature {
                b["temperature"] = json!(temp);
            }
            if let Some(max) = request.max_tokens {
                b["max_tokens"] = json!(max);
            }
            b
        };

        let resp = apply_auth(&self.endpoint, self.client.post(&url))
            .json(&body)
            .send()
            .await
            .map_err(|e| ProviderError::Network(e.to_string()))?;

        let status = resp.status();
        if !status.is_success() {
            let text = resp.text().await.unwrap_or_default();
            if status.as_u16() == 401 || status.as_u16() == 403 {
                return Err(ProviderError::Auth(format!("{status}: {text}")));
            }
            if status.as_u16() == 429 {
                return Err(ProviderError::RateLimited { retry_after_ms: 5000 });
            }
            return Err(ProviderError::Network(format!("{status}: {text}")));
        }

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
        let url = self.build_url();
        let is_responses = self.is_responses_api();

        let body = if is_responses {
            let input = Self::build_responses_input(&request.messages);
            let mut b = json!({ "model": &request.model, "input": input, "stream": true });
            if let Some(temp) = request.temperature {
                b["temperature"] = json!(temp);
            }
            b
        } else {
            let messages: Vec<serde_json::Value> = request
                .messages
                .iter()
                .map(|m| json!({ "role": &m.role, "content": &m.content }))
                .collect();
            let mut b = json!({
                "model": &request.model,
                "messages": messages,
                "stream": true,
                "stream_options": { "include_usage": true },
            });
            if url.contains("/openai/deployments/") {
                b.as_object_mut().unwrap().remove("model");
            }
            if let Some(temp) = request.temperature {
                b["temperature"] = json!(temp);
            }
            if let Some(max) = request.max_tokens {
                b["max_tokens"] = json!(max);
            }
            b
        };

        let resp = apply_auth(&self.endpoint, self.client.post(&url))
            .json(&body)
            .send()
            .await
            .map_err(|e| ProviderError::Network(e.to_string()))?;

        if !resp.status().is_success() {
            let status = resp.status();
            let text = resp.text().await.unwrap_or_default();
            return Err(ProviderError::Network(format!("{status}: {text}")));
        }

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
                                if v["type"].as_str() == Some("response.output_text.delta") {
                                    if let Some(d) = v["delta"].as_str().filter(|s| !s.is_empty()) {
                                        let _ = tx.send(Ok(StreamChunk::Token(d.to_string())));
                                    }
                                }
                                if v["type"].as_str() == Some("response.completed") {
                                    if let Some(u) = v["response"]["usage"].as_object() {
                                        let _ = tx.send(Ok(parse_usage(u, "input_tokens", "output_tokens")));
                                    }
                                }
                            } else {
                                if let Some(d) = v["choices"][0]["delta"]["content"].as_str().filter(|s| !s.is_empty()) {
                                    let _ = tx.send(Ok(StreamChunk::Token(d.to_string())));
                                }
                                if let Some(r) = v["choices"][0]["delta"]["reasoning_content"].as_str().filter(|s| !s.is_empty()) {
                                    let _ = tx.send(Ok(StreamChunk::Reasoning(r.to_string())));
                                }
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
    use crate::traits::StreamChunk;

    #[test]
    fn test_build_url_azure() {
        let p = OpenAiChatProvider::new(factories::azure_openai_endpoint("ep-azure", "My Azure"));
        assert_eq!(
            p.build_url(),
            "https://myresource.openai.azure.com/openai/v1/responses"
        );
    }

    #[test]
    fn test_build_url_openai_compatible() {
        let p = OpenAiChatProvider::new(factories::openai_compatible_endpoint("ep-oai", "OpenAI"));
        assert_eq!(p.build_url(), "https://api.openai.com/v1/chat/completions");
    }

    #[test]
    fn test_build_url_apim() {
        let mut ep = factories::azure_openai_endpoint("ep-apim", "APIM");
        ep.endpoint_type = EndpointType::ApiManagementGateway;
        ep.url = "https://apim.azure-api.net/ai01/openai".into();
        let p = OpenAiChatProvider::new(ep);
        assert_eq!(
            p.build_url(),
            "https://apim.azure-api.net/ai01/openai/responses?api-version=2025-03-01-preview"
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

        // empty map → 0, 0
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
        assert!(azure.is_responses_api());

        let mut apim_ep = factories::azure_openai_endpoint("b", "B");
        apim_ep.endpoint_type = EndpointType::ApiManagementGateway;
        let apim = OpenAiChatProvider::new(apim_ep);
        assert!(apim.is_responses_api());

        let oai = OpenAiChatProvider::new(factories::openai_compatible_endpoint("c", "C"));
        assert!(!oai.is_responses_api());

        let mut custom_ep = factories::openai_compatible_endpoint("d", "D");
        custom_ep.endpoint_type = EndpointType::Custom;
        let custom = OpenAiChatProvider::new(custom_ep);
        assert!(!custom.is_responses_api());
    }

    #[test]
    fn test_build_responses_input() {
        use tfp_core::ChatMessage;

        // empty messages → ""
        let result = OpenAiChatProvider::build_responses_input(&[]);
        assert_eq!(result, json!(""));

        // single string content → direct string
        let msgs = vec![ChatMessage {
            role: "user".into(),
            content: serde_json::Value::String("hello".into()),
        }];
        let result = OpenAiChatProvider::build_responses_input(&msgs);
        assert_eq!(result, json!("hello"));

        // single array content → wrapped in message object
        let msgs = vec![ChatMessage {
            role: "user".into(),
            content: json!([{"type": "text", "text": "hi"}]),
        }];
        let result = OpenAiChatProvider::build_responses_input(&msgs);
        assert_eq!(
            result,
            json!([{"type": "message", "role": "user", "content": [{"type": "text", "text": "hi"}]}])
        );

        // multiple messages → array of message objects
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
}
