//! Provider 抽象层：把不同 AI / Speech 厂商统一到一组 Rust trait 之后。
//!
//! 设计取向（区别于 C# 版）：
//! - 用 trait + async 抽象「能做什么」，而不是用大枚举 switch；
//! - URL 拼接 / 鉴权 / 协议差异收敛在各 Provider 实现内部；
//! - 调用方只面对 `ChatProvider` 等接口。

pub mod ai;
pub mod speech;

use serde::{Deserialize, Serialize};

use crate::error::Result;

/// 对话角色。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ChatRole {
    System,
    User,
    Assistant,
}

/// 一条对话消息。
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ChatMessage {
    pub role: ChatRole,
    pub content: String,
}

impl ChatMessage {
    pub fn system(content: impl Into<String>) -> Self {
        Self { role: ChatRole::System, content: content.into() }
    }
    pub fn user(content: impl Into<String>) -> Self {
        Self { role: ChatRole::User, content: content.into() }
    }
    pub fn assistant(content: impl Into<String>) -> Self {
        Self { role: ChatRole::Assistant, content: content.into() }
    }
}

/// 对话请求。
#[derive(Debug, Clone)]
pub struct ChatRequest {
    /// 模型 ID 或 Azure 部署名
    pub model: String,
    pub messages: Vec<ChatMessage>,
    pub temperature: Option<f32>,
    pub max_tokens: Option<u32>,
}

impl ChatRequest {
    pub fn new(model: impl Into<String>, messages: Vec<ChatMessage>) -> Self {
        Self {
            model: model.into(),
            messages,
            temperature: None,
            max_tokens: None,
        }
    }
}

/// 对话响应。
#[derive(Debug, Clone)]
pub struct ChatResponse {
    pub content: String,
    pub prompt_tokens: Option<u64>,
    pub completion_tokens: Option<u64>,
}

/// 流式增量回调返回的片段。
#[derive(Debug, Clone)]
pub struct ChatChunk {
    pub delta: String,
    pub done: bool,
}

/// 文本对话 Provider 抽象。
#[async_trait::async_trait]
pub trait ChatProvider: Send + Sync {
    /// 一次性（非流式）对话补全。
    async fn complete(&self, req: ChatRequest) -> Result<ChatResponse>;
}
