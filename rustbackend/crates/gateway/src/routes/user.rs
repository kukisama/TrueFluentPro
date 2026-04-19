//! User-facing endpoints.

use crate::state::AppState;
use crate::error::ApiError;
use domain::auth::UserContext;
use domain::models::*;
use std::sync::Arc;
use axum::{Router, routing::get, extract::State, Extension, Json};
use serde_json::{json, Value};

pub fn routes() -> Router<Arc<AppState>> {
    Router::new()
        .route("/v1/user/profile", get(profile))
        .route("/v1/user/capabilities", get(capabilities))
}

async fn profile(
    State(state): State<Arc<AppState>>,
    Extension(ctx): Extension<UserContext>,
) -> Result<Json<Value>, ApiError> {
    let user = state.storage.get_user(&ctx.user_id).await
        .map_err(|e| ApiError::Internal(e.to_string()))?
        .ok_or_else(|| ApiError::NotFound("user not found".into()))?;

    let plan = state.storage.get_plan(&user.plan_id).await
        .map_err(|e| ApiError::Internal(e.to_string()))?;

    let caps = build_user_capabilities(&state, &user).await?;

    Ok(Json(json!({
        "user": {
            "id": user.id,
            "display_name": user.display_name,
            "email": user.email,
            "role": user.role,
            "plan": plan.map(|p| json!({
                "id": p.id,
                "display_name": p.display_name,
                "price_monthly": p.price_monthly,
            })),
        },
        "capabilities": caps,
    })))
}

async fn capabilities(
    State(state): State<Arc<AppState>>,
    Extension(ctx): Extension<UserContext>,
) -> Result<Json<Value>, ApiError> {
    let user = state.storage.get_user(&ctx.user_id).await
        .map_err(|e| ApiError::Internal(e.to_string()))?
        .ok_or_else(|| ApiError::NotFound("user not found".into()))?;

    let caps = build_user_capabilities(&state, &user).await?;
    Ok(Json(json!({ "capabilities": caps })))
}

async fn build_user_capabilities(state: &AppState, _user: &User) -> Result<Vec<UserCapabilityStatus>, ApiError> {
    let capabilities = state.storage.get_capabilities().await
        .map_err(|e| ApiError::Internal(e.to_string()))?;

    let mut result = Vec::new();
    for cap in capabilities {
        let status = if !cap.is_enabled {
            CapabilityAvailability::Disabled
        } else {
            CapabilityAvailability::Available
        };

        let quota = if state.billing.is_enabled() {
            // Future: get actual quota from billing engine
            None
        } else {
            None
        };

        result.push(UserCapabilityStatus {
            id: cap.id,
            display_name: cap.display_name,
            category: cap.category,
            status,
            quota,
        });
    }

    Ok(result)
}
