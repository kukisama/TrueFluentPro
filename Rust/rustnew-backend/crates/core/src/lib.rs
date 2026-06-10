//! 译见 Pro 核心库。
//!
//! 提供与具体 UI / 平台无关的能力：
//! - [`capability`]：模型/语音能力位标志
//! - [`endpoint`]：AI / Speech 终结点模型
//! - [`config`]：应用配置与持久化
//! - [`model`]：任务、音频生命周期、翻译记录等领域模型
//! - [`provider`]：AI 对话 / Speech 连接的 Provider 抽象

pub mod capability;
pub mod config;
pub mod endpoint;
pub mod error;
pub mod model;
pub mod provider;
pub mod speech_resource;
pub mod storage;

pub use capability::{ModelCapability, SpeechCapability};
pub use config::{AppConfig, Language, ModelRole, Theme};
pub use endpoint::{AiEndpoint, AiModelEntry, EndpointKind, ModelReference};
pub use error::{CoreError, Result};
pub use provider::{ChatMessage, ChatProvider, ChatRequest, ChatResponse, ChatRole};
pub use speech_resource::{SpeechConnectorType, SpeechResource, SpeechVendorType};
pub use storage::TranslationRecord;
