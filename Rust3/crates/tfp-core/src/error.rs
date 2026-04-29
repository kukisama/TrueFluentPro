use serde::Serialize;

#[derive(Debug, thiserror::Error)]
pub enum AppError {
    #[error("database error: {0}")]
    Database(String),
    #[error("config error: {0}")]
    Config(String),
    #[error("serialization error: {0}")]
    Serialization(String),
    #[error("not found: {0}")]
    NotFound(String),
    #[error("provider error: {0}")]
    Provider(ProviderError),
    #[error("io error: {0}")]
    Io(String),
}

impl Serialize for AppError {
    fn serialize<S>(&self, serializer: S) -> std::result::Result<S::Ok, S::Error>
    where
        S: serde::ser::Serializer,
    {
        serializer.serialize_str(&self.to_string())
    }
}

impl From<serde_json::Error> for AppError {
    fn from(e: serde_json::Error) -> Self {
        AppError::Serialization(e.to_string())
    }
}

impl From<ProviderError> for AppError {
    fn from(e: ProviderError) -> Self {
        AppError::Provider(e)
    }
}

impl From<std::io::Error> for AppError {
    fn from(e: std::io::Error) -> Self {
        AppError::Io(e.to_string())
    }
}

#[derive(Debug, thiserror::Error)]
pub enum ProviderError {
    #[error("network error: {0}")]
    Network(String),
    #[error("authentication failed: {0}")]
    Auth(String),
    #[error("rate limited: retry after {retry_after_ms}ms")]
    RateLimited { retry_after_ms: u64 },
    #[error("provider not configured: {0}")]
    NotConfigured(String),
    #[error("unsupported operation: {0}")]
    Unsupported(String),
    #[error("internal error: {0}")]
    Internal(String),
}

impl Serialize for ProviderError {
    fn serialize<S>(&self, serializer: S) -> std::result::Result<S::Ok, S::Error>
    where
        S: serde::ser::Serializer,
    {
        serializer.serialize_str(&self.to_string())
    }
}

pub type Result<T> = std::result::Result<T, AppError>;

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_error_serialize() {
        let err = AppError::Database("connection failed".into());
        let json = serde_json::to_value(&err).unwrap();
        assert_eq!(json, "database error: connection failed");

        let err2 = AppError::Provider(ProviderError::Auth("bad key".into()));
        let json2 = serde_json::to_value(&err2).unwrap();
        assert_eq!(json2, "provider error: authentication failed: bad key");
    }

    #[test]
    fn test_provider_error_display_all_variants() {
        assert_eq!(
            ProviderError::Network("timeout".into()).to_string(),
            "network error: timeout"
        );
        assert_eq!(
            ProviderError::Auth("bad key".into()).to_string(),
            "authentication failed: bad key"
        );
        assert_eq!(
            ProviderError::RateLimited { retry_after_ms: 5000 }.to_string(),
            "rate limited: retry after 5000ms"
        );
        assert_eq!(
            ProviderError::NotConfigured("missing".into()).to_string(),
            "provider not configured: missing"
        );
        assert_eq!(
            ProviderError::Unsupported("op".into()).to_string(),
            "unsupported operation: op"
        );
        assert_eq!(
            ProviderError::Internal("bug".into()).to_string(),
            "internal error: bug"
        );
    }

    #[test]
    fn test_from_impls() {
        // From<serde_json::Error> → AppError::Serialization
        let bad_json: std::result::Result<serde_json::Value, _> =
            serde_json::from_str("not valid json");
        let serde_err = bad_json.unwrap_err();
        let app_err: AppError = serde_err.into();
        assert!(matches!(app_err, AppError::Serialization(_)));

        // From<ProviderError> → AppError::Provider
        let provider_err = ProviderError::Auth("forbidden".into());
        let app_err: AppError = provider_err.into();
        assert!(matches!(app_err, AppError::Provider(_)));

        // From<std::io::Error> → AppError::Io
        let io_err = std::io::Error::new(std::io::ErrorKind::NotFound, "file missing");
        let app_err: AppError = io_err.into();
        assert!(matches!(app_err, AppError::Io(_)));
    }
}
