//! 统一错误类型。

use thiserror::Error;

/// 核心层错误。
#[derive(Debug, Error)]
pub enum CoreError {
    #[error("配置错误: {0}")]
    Config(String),

    #[error("找不到终结点: {0}")]
    EndpointNotFound(String),

    #[error("终结点不支持该能力: {0}")]
    CapabilityUnsupported(String),

    #[error("HTTP 请求失败: {0}")]
    Http(#[from] reqwest::Error),

    #[error("序列化失败: {0}")]
    Serde(#[from] serde_json::Error),

    #[error("IO 错误: {0}")]
    Io(#[from] std::io::Error),

    #[error("数据库错误: {0}")]
    Db(#[from] rusqlite::Error),

    #[error("Provider 响应异常: {0}")]
    Provider(String),

    #[error("{0}")]
    Other(String),
}

pub type Result<T> = std::result::Result<T, CoreError>;
