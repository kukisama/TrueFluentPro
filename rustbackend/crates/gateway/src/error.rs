//! Unified error types for the gateway.

use axum::http::StatusCode;
use axum::response::{IntoResponse, Response};
use serde_json::json;

#[derive(Debug)]
pub enum ApiError {
    BadRequest(String),
    Unauthorized(String),
    Forbidden(String),
    NotFound(String),
    Conflict(String),
    TooManyRequests(String),
    Internal(String),
    SetupRequired,
}

impl IntoResponse for ApiError {
    fn into_response(self) -> Response {
        let (status, code, message) = match self {
            ApiError::BadRequest(m) => (StatusCode::BAD_REQUEST, "bad_request", m),
            ApiError::Unauthorized(m) => (StatusCode::UNAUTHORIZED, "unauthorized", m),
            ApiError::Forbidden(m) => (StatusCode::FORBIDDEN, "forbidden", m),
            ApiError::NotFound(m) => (StatusCode::NOT_FOUND, "not_found", m),
            ApiError::Conflict(m) => (StatusCode::CONFLICT, "conflict", m),
            ApiError::TooManyRequests(m) => (StatusCode::TOO_MANY_REQUESTS, "quota_exceeded", m),
            ApiError::Internal(m) => (StatusCode::INTERNAL_SERVER_ERROR, "internal", m),
            ApiError::SetupRequired => (
                StatusCode::SERVICE_UNAVAILABLE,
                "setup_required",
                "System not initialized. Complete setup at POST /v1/setup/init".into(),
            ),
        };

        let body = json!({
            "error": code,
            "message": message,
        });

        (status, axum::Json(body)).into_response()
    }
}

impl From<anyhow::Error> for ApiError {
    fn from(err: anyhow::Error) -> Self {
        ApiError::Internal(err.to_string())
    }
}
