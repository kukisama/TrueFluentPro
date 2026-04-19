//! Route modules and router assembly.

pub mod health;
pub mod setup;
pub mod auth_routes;
pub mod user;
pub mod admin;

use crate::state::AppState;
use crate::middleware;
use std::sync::Arc;
use axum::{Router, middleware as axum_mw};
use tower_http::trace::TraceLayer;

pub fn build_router(state: Arc<AppState>) -> Router {
    // Public routes (no auth)
    let public = Router::new()
        .merge(health::routes())
        .merge(setup::routes())
        .merge(auth_routes::routes());

    // Protected routes (require auth)
    let protected = Router::new()
        .merge(user::routes())
        .layer(axum_mw::from_fn_with_state(
            state.clone(),
            middleware::auth::require_auth,
        ));

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
        .merge(admin)
        .layer(TraceLayer::new_for_http())
        .with_state(state)
}
