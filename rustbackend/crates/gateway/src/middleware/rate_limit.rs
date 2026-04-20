//! Per-user rate limiting middleware using the cache backend.

use crate::state::AppState;
use crate::error::ApiError;
use domain::auth::UserContext;
use axum::{
    extract::{Request, State},
    middleware::Next,
    response::Response,
};
use std::sync::Arc;
use tracing::debug;
/// Middleware: enforce per-user rate limiting via cache counters.
pub async fn rate_limit(
    State(state): State<Arc<AppState>>,
    req: Request,
    next: Next,
) -> Result<Response, ApiError> {
    if !state.config.rate_limit.enabled {
        return Ok(next.run(req).await);
    }

    // Get user from extensions (set by auth middleware)
    let user_id = req.extensions()
        .get::<UserContext>()
        .map(|ctx| ctx.user_id.clone())
        .unwrap_or_else(|| "anonymous".to_string());

    let window_key = format!("rate:{}:{}", user_id, current_minute_key());
    let limit = state.config.rate_limit.requests_per_minute as i64;

    // Increment counter
    let count = state.cache.increment(&window_key, 1).await
        .unwrap_or(1);

    if count > limit {
        debug!(user = %user_id, count, limit, "Rate limit exceeded");
        return Err(ApiError::TooManyRequests(format!(
            "Rate limit exceeded: {count}/{limit} requests per minute"
        )));
    }

    Ok(next.run(req).await)
}

fn current_minute_key() -> String {
    let now = chrono::Utc::now();
    format!("{}", now.format("%Y%m%d%H%M"))
}
