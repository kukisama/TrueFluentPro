//! OpenAI 兼容 / Azure OpenAI / APIM 网关的对话 Provider 实现。
//!
//! 把 C# 里散落在多处的「URL 拼接 + 鉴权头 + 协议选择」逻辑收敛到一处。

use serde::Deserialize;

use crate::endpoint::{AiEndpoint, ApiKeyHeaderMode, EndpointKind, TextProtocol};
use crate::error::{CoreError, Result};

use super::{ChatProvider, ChatRequest, ChatResponse, ChatRole};

/// 基于一个 `AiEndpoint` 构建的对话客户端。
pub struct OpenAiCompatibleClient {
    endpoint: AiEndpoint,
    http: reqwest::Client,
}

impl OpenAiCompatibleClient {
    pub fn new(endpoint: AiEndpoint) -> Self {
        Self {
            endpoint,
            http: reqwest::Client::new(),
        }
    }

    pub fn with_client(endpoint: AiEndpoint, http: reqwest::Client) -> Self {
        Self { endpoint, http }
    }

    /// 解析实际请求的模型/部署标识。
    ///
    /// Azure OpenAI 用部署名（deployment），其余用 model_id。
    fn resolve_deployment<'a>(&'a self, model: &'a str) -> &'a str {
        if self.endpoint.kind.is_azure_openai() {
            if let Some(entry) = self.endpoint.find_model(model) {
                if !entry.deployment_name.trim().is_empty() {
                    return &entry.deployment_name;
                }
            }
        }
        model
    }

    /// 构建 chat completions 的目标 URL。
    fn chat_url(&self, model: &str) -> String {
        let base = self.endpoint.base_url.trim_end_matches('/');
        match self.endpoint.kind {
            EndpointKind::AzureOpenAi => {
                let deployment = self.resolve_deployment(model);
                let api_version = if self.endpoint.api_version.trim().is_empty() {
                    "2024-02-01"
                } else {
                    self.endpoint.api_version.trim()
                };
                format!(
                    "{base}/openai/deployments/{deployment}/chat/completions?api-version={api_version}"
                )
            }
            _ => match self.endpoint.text_protocol {
                TextProtocol::ChatCompletionsRaw => format!("{base}/chat/completions"),
                TextProtocol::Responses => format!("{base}/responses"),
                // Auto / ChatCompletionsV1
                _ => {
                    if base.ends_with("/v1") {
                        format!("{base}/chat/completions")
                    } else {
                        format!("{base}/v1/chat/completions")
                    }
                }
            },
        }
    }

    /// 是否使用 Azure 风格的 `api-key` 头。
    fn use_api_key_header(&self) -> bool {
        match self.endpoint.api_key_header_mode {
            ApiKeyHeaderMode::ApiKeyHeader => true,
            ApiKeyHeaderMode::Bearer => false,
            ApiKeyHeaderMode::Auto => self.endpoint.kind.is_azure_openai(),
        }
    }
}

#[async_trait::async_trait]
impl ChatProvider for OpenAiCompatibleClient {
    async fn complete(&self, req: ChatRequest) -> Result<ChatResponse> {
        let url = self.chat_url(&req.model);

        let messages: Vec<_> = req
            .messages
            .iter()
            .map(|m| {
                serde_json::json!({
                    "role": match m.role {
                        ChatRole::System => "system",
                        ChatRole::User => "user",
                        ChatRole::Assistant => "assistant",
                    },
                    "content": m.content,
                })
            })
            .collect();

        let mut body = serde_json::json!({
            "model": req.model,
            "messages": messages,
        });
        if let Some(t) = req.temperature {
            body["temperature"] = serde_json::json!(t);
        }
        if let Some(mt) = req.max_tokens {
            body["max_tokens"] = serde_json::json!(mt);
        }

        let mut builder = self.http.post(&url).json(&body);
        let key = self.endpoint.api_key.trim();
        if !key.is_empty() {
            builder = if self.use_api_key_header() {
                builder.header("api-key", key)
            } else {
                builder.bearer_auth(key)
            };
        }

        let resp = builder.send().await?;
        let status = resp.status();
        let text = resp.text().await?;
        if !status.is_success() {
            return Err(CoreError::Provider(format!(
                "HTTP {status}: {}",
                text.chars().take(500).collect::<String>()
            )));
        }

        let parsed: ChatCompletionResponse = serde_json::from_str(&text)?;
        let content = parsed
            .choices
            .into_iter()
            .next()
            .map(|c| c.message.content)
            .unwrap_or_default();

        Ok(ChatResponse {
            content,
            prompt_tokens: parsed.usage.as_ref().and_then(|u| u.prompt_tokens),
            completion_tokens: parsed.usage.as_ref().and_then(|u| u.completion_tokens),
        })
    }
}

// ---- OpenAI / Azure chat completions 响应反序列化 ----

#[derive(Debug, Deserialize)]
struct ChatCompletionResponse {
    #[serde(default)]
    choices: Vec<Choice>,
    #[serde(default)]
    usage: Option<Usage>,
}

#[derive(Debug, Deserialize)]
struct Choice {
    message: ResponseMessage,
}

#[derive(Debug, Deserialize)]
struct ResponseMessage {
    #[serde(default)]
    content: String,
}

#[derive(Debug, Deserialize)]
struct Usage {
    #[serde(default)]
    prompt_tokens: Option<u64>,
    #[serde(default)]
    completion_tokens: Option<u64>,
}
