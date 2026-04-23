//! Route modules and router assembly.

pub mod health;
pub mod setup;
pub mod auth_routes;
pub mod user;
pub mod admin;
pub mod chat;
pub mod images;
pub mod tts;
pub mod stt;
pub mod translate;
pub mod ws_translate;
pub mod video;

use crate::state::AppState;
use crate::middleware;
use std::sync::Arc;
use axum::{Router, middleware as axum_mw};
use axum::http::HeaderName;
use tower_http::trace::TraceLayer;
use tower_http::request_id::{MakeRequestUuid, SetRequestIdLayer, PropagateRequestIdLayer};

pub fn build_router(state: Arc<AppState>) -> Router {
    let x_request_id = HeaderName::from_static("x-request-id");

    // Public routes (no auth)
    let public = Router::new()
        .merge(health::routes())
        .merge(setup::routes())
        .merge(auth_routes::routes());

    // Protected routes (require auth + rate limiting)
    let protected = Router::new()
        .merge(user::routes())
        .merge(chat::routes())
        .merge(images::routes())
        .merge(tts::routes())
        .merge(stt::routes())
        .merge(translate::routes())
        .merge(video::routes())
        .layer(axum_mw::from_fn_with_state(
            state.clone(),
            middleware::rate_limit::rate_limit,
        ))
        .layer(axum_mw::from_fn_with_state(
            state.clone(),
            middleware::auth::require_auth,
        ));

    // WebSocket routes (auth handled inside the handler via query param)
    let websocket = Router::new()
        .merge(ws_translate::routes());

    // Admin routes (require admin role)
    let admin = Router::new()
        .merge(admin::routes())
        .layer(axum_mw::from_fn_with_state(
            state.clone(),
            middleware::auth::require_admin,
        ));

    Router::new()
        .merge(public)
        .merge(protected)
        .merge(websocket)
        .merge(admin)
        .layer(TraceLayer::new_for_http())
        .layer(PropagateRequestIdLayer::new(x_request_id.clone()))
        .layer(SetRequestIdLayer::new(x_request_id, MakeRequestUuid))
        .with_state(state)
}
