//! Health check endpoints.

use crate::state::AppState;
use std::sync::Arc;
use axum::{Router, routing::get, extract::State, Json};
use serde_json::{json, Value};

pub fn routes() -> Router<Arc<AppState>> {
    Router::new()
        .route("/healthz", get(healthz))
        .route("/readyz", get(readyz))
}

async fn healthz() -> &'static str {
    "ok"
}

async fn readyz(State(state): State<Arc<AppState>>) -> Json<Value> {
    let db_ok = state.storage.health_check().await.is_ok();
    let cache_ok = state.cache.health_check().await.is_ok();
    let initialized = state.storage.is_initialized().await.unwrap_or(false);

    let status = if db_ok && initialized { "ready" } else { "not_ready" };

    Json(json!({
        "status": status,
        "checks": {
            "database": if db_ok { "ok" } else { "error" },
            "cache": if cache_ok { "ok" } else { "error" },
            "setup": if initialized { "ok" } else { "pending" },
        }
    }))
}
