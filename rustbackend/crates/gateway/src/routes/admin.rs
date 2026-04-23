//! Admin management endpoints.

use crate::state::AppState;
use crate::error::ApiError;
use domain::auth::UserContext;
use domain::models::*;
use std::sync::Arc;
use axum::{Router, routing::{get, put}, extract::{State, Path, Json, Query}, Extension};
use serde::Deserialize;
use serde_json::{json, Value};

#[derive(Debug, Deserialize)]
struct PaginationQuery {
    #[serde(default)]
    offset: Option<i64>,
    #[serde(default)]
    limit: Option<i64>,
    #[serde(default)]
    search: Option<String>,
}

pub fn routes() -> Router<Arc<AppState>> {
    Router::new()
        .route("/v1/admin/users", get(list_users))
        .route("/v1/admin/users/{id}", put(update_user))
        .route("/v1/admin/providers", get(list_providers))
        .route("/v1/admin/providers/{id}", put(upsert_provider))
        .route("/v1/admin/providers/{id}/toggle", put(toggle_provider))
        .route("/v1/admin/capabilities", get(list_capabilities))
        .route("/v1/admin/capabilities/{id}", put(upsert_capability))
        .route("/v1/admin/credentials/{provider_id}", get(list_credentials))
        .route("/v1/admin/credentials/{provider_id}/{key}", put(store_credential))
        .route("/v1/admin/plans", get(list_plans))
        .route("/v1/admin/plans/{id}", put(upsert_plan))
        .route("/v1/admin/stats", get(system_stats))
        .route("/v1/admin/billing/config", get(get_billing_config))
        .route("/v1/admin/billing/config", put(set_billing_config))
        .route("/v1/admin/usage/{user_id}", get(get_user_usage))
        .route("/v1/admin/audit", get(list_audit_logs))
}

// ─── Users ───

async fn list_users(
    State(state): State<Arc<AppState>>,
    Query(params): Query<PaginationQuery>,
) -> Result<Json<Value>, ApiError> {
    let offset = params.offset.unwrap_or(0);
    let limit = params.limit.unwrap_or(50).min(100);
    let total = state.storage.count_users().await
        .map_err(|e| ApiError::Internal(e.to_string()))?;
    let users = state.storage.list_users(offset, limit).await
        .map_err(|e| ApiError::Internal(e.to_string()))?;
    Ok(Json(json!({
        "users": users,
        "pagination": {
            "offset": offset,
            "limit": limit,
            "total": total,
        }
    })))
}

#[derive(Debug, Deserialize)]
struct UpdateUserRequest {
    display_name: Option<String>,
    role: Option<String>,
    plan_id: Option<String>,
    is_active: Option<bool>,
}

async fn update_user(
    State(state): State<Arc<AppState>>,
    Extension(ctx): Extension<UserContext>,
    Path(id): Path<String>,
    Json(req): Json<UpdateUserRequest>,
) -> Result<Json<Value>, ApiError> {
    let mut user = state.storage.get_user(&id).await
        .map_err(|e| ApiError::Internal(e.to_string()))?
        .ok_or_else(|| ApiError::NotFound("user not found".into()))?;

    if let Some(name) = req.display_name { user.display_name = name; }
    if let Some(role) = req.role { user.role = role.parse().map_err(|e: String| ApiError::BadRequest(e))?; }
    if let Some(plan) = req.plan_id { user.plan_id = plan; }
    if let Some(active) = req.is_active { user.is_active = active; }

    state.storage.upsert_user(&user).await
        .map_err(|e| ApiError::Internal(e.to_string()))?;

    let _ = state.storage.write_audit_log(&ctx.user_id, "admin.update_user", Some(&id), None).await;

    Ok(Json(json!({ "user": user })))
}

// ─── Providers ───

async fn list_providers(
    State(state): State<Arc<AppState>>,
) -> Result<Json<Value>, ApiError> {
    let providers = state.storage.get_providers().await
        .map_err(|e| ApiError::Internal(e.to_string()))?;
    let total = providers.len();
    Ok(Json(json!({
        "providers": providers,
        "total": total,
    })))
}

async fn upsert_provider(
    State(state): State<Arc<AppState>>,
    Extension(ctx): Extension<UserContext>,
    Path(id): Path<String>,
    Json(mut provider): Json<ProviderInfo>,
) -> Result<Json<Value>, ApiError> {
    provider.id = id.clone();
    state.storage.upsert_provider(&provider).await
        .map_err(|e| ApiError::Internal(e.to_string()))?;
    if let Err(e) = state.reload_providers().await {
        tracing::warn!(error = %e, "Failed to reload provider registry after upsert");
    }

    let _ = state.storage.write_audit_log(&ctx.user_id, "admin.upsert_provider", Some(&id), None).await;

    Ok(Json(json!({ "provider": provider })))
}

#[derive(Debug, Deserialize)]
struct ToggleRequest {
    is_enabled: bool,
}

async fn toggle_provider(
    State(state): State<Arc<AppState>>,
    Extension(ctx): Extension<UserContext>,
    Path(id): Path<String>,
    Json(req): Json<ToggleRequest>,
) -> Result<Json<Value>, ApiError> {
    let mut provider = state.storage.get_providers().await
        .map_err(|e| ApiError::Internal(e.to_string()))?
        .into_iter()
        .find(|p| p.id == id)
        .ok_or_else(|| ApiError::NotFound("provider not found".into()))?;

    provider.is_enabled = req.is_enabled;
    state.storage.upsert_provider(&provider).await
        .map_err(|e| ApiError::Internal(e.to_string()))?;
    if let Err(e) = state.reload_providers().await {
        tracing::warn!(error = %e, "Failed to reload provider registry after toggle");
    }

    let _ = state.storage.write_audit_log(&ctx.user_id, "admin.toggle_provider", Some(&id), None).await;

    Ok(Json(json!({ "provider": provider })))
}

// ─── Capabilities ───

async fn list_capabilities(
    State(state): State<Arc<AppState>>,
) -> Result<Json<Value>, ApiError> {
    let caps = state.storage.get_capabilities().await
        .map_err(|e| ApiError::Internal(e.to_string()))?;
    Ok(Json(json!({ "capabilities": caps })))
}

async fn upsert_capability(
    State(state): State<Arc<AppState>>,
    Extension(ctx): Extension<UserContext>,
    Path(id): Path<String>,
    Json(mut cap): Json<Capability>,
) -> Result<Json<Value>, ApiError> {
    cap.id = id.clone();
    state.storage.upsert_capability(&cap).await
        .map_err(|e| ApiError::Internal(e.to_string()))?;

    let _ = state.storage.write_audit_log(&ctx.user_id, "admin.upsert_capability", Some(&id), None).await;

    Ok(Json(json!({ "capability": cap })))
}

// ─── Credentials ───

async fn list_credentials(
    State(state): State<Arc<AppState>>,
    Path(provider_id): Path<String>,
) -> Result<Json<Value>, ApiError> {
    let creds = state.storage.list_credentials(&provider_id).await
        .map_err(|e| ApiError::Internal(e.to_string()))?;
    Ok(Json(json!({ "credentials": creds })))
}

#[derive(Debug, Deserialize)]
struct StoreCredentialRequest {
    value: String,
}

async fn store_credential(
    State(state): State<Arc<AppState>>,
    Extension(ctx): Extension<UserContext>,
    Path((provider_id, key)): Path<(String, String)>,
    Json(req): Json<StoreCredentialRequest>,
) -> Result<Json<Value>, ApiError> {
    state.credentials.store(&provider_id, &key, &req.value).await
        .map_err(|e| ApiError::Internal(e.to_string()))?;

    let detail = format!("{}/{}", provider_id, key);
    let _ = state.storage.write_audit_log(&ctx.user_id, "admin.store_credential", Some(&detail), None).await;

    Ok(Json(json!({ "status": "stored", "provider_id": provider_id, "key": key })))
}

// ─── Plans ───

async fn list_plans(
    State(state): State<Arc<AppState>>,
) -> Result<Json<Value>, ApiError> {
    let plans = state.storage.list_plans().await
        .map_err(|e| ApiError::Internal(e.to_string()))?;
    Ok(Json(json!({ "plans": plans })))
}

async fn upsert_plan(
    State(state): State<Arc<AppState>>,
    Extension(ctx): Extension<UserContext>,
    Path(id): Path<String>,
    Json(mut plan): Json<SubscriptionPlan>,
) -> Result<Json<Value>, ApiError> {
    plan.id = id.clone();
    state.storage.upsert_plan(&plan).await
        .map_err(|e| ApiError::Internal(e.to_string()))?;

    let _ = state.storage.write_audit_log(&ctx.user_id, "admin.upsert_plan", Some(&id), None).await;

    Ok(Json(json!({ "plan": plan })))
}

// ─── Stats ───

async fn system_stats(
    State(state): State<Arc<AppState>>,
) -> Result<Json<Value>, ApiError> {
    let total_users = state.storage.count_users().await
        .map_err(|e| ApiError::Internal(e.to_string()))?;
    let providers = state.storage.get_providers().await
        .map_err(|e| ApiError::Internal(e.to_string()))?;
    let capabilities = state.storage.get_capabilities().await
        .map_err(|e| ApiError::Internal(e.to_string()))?;

    let enabled_providers = providers.iter().filter(|p| p.is_enabled).count();
    let enabled_capabilities = capabilities.iter().filter(|c| c.is_enabled).count();

    Ok(Json(json!({
        "total_users": total_users,
        "total_providers": providers.len(),
        "enabled_providers": enabled_providers,
        "total_capabilities": capabilities.len(),
        "enabled_capabilities": enabled_capabilities,
        "billing_enabled": state.billing.is_enabled(),
    })))
}

// ─── Billing Config ───

async fn get_billing_config(
    State(state): State<Arc<AppState>>,
) -> Result<Json<Value>, ApiError> {
    let enabled = state.storage.get_config("billing.enabled").await
        .map_err(|e| ApiError::Internal(e.to_string()))?
        .map(|v| v == "true")
        .unwrap_or(false);

    Ok(Json(json!({ "billing": { "enabled": enabled } })))
}

#[derive(Debug, Deserialize)]
struct BillingConfigRequest {
    enabled: bool,
}

async fn set_billing_config(
    State(state): State<Arc<AppState>>,
    Extension(ctx): Extension<UserContext>,
    Json(req): Json<BillingConfigRequest>,
) -> Result<Json<Value>, ApiError> {
    state.storage.set_config("billing.enabled", if req.enabled { "true" } else { "false" }).await
        .map_err(|e| ApiError::Internal(e.to_string()))?;

    let _ = state.storage.write_audit_log(&ctx.user_id, "admin.set_billing_config", Some(if req.enabled { "enabled" } else { "disabled" }), None).await;

    Ok(Json(json!({ "billing": { "enabled": req.enabled } })))
}

// ─── Usage ───

async fn get_user_usage(
    State(state): State<Arc<AppState>>,
    Path(user_id): Path<String>,
    Query(params): Query<PaginationQuery>,
) -> Result<Json<Value>, ApiError> {
    let offset = params.offset.unwrap_or(0);
    let limit = params.limit.unwrap_or(50).min(100);
    let records = state.storage.get_usage_records(&user_id, offset, limit).await
        .map_err(|e| ApiError::Internal(e.to_string()))?;
    Ok(Json(json!({
        "user_id": user_id,
        "usage": records,
        "pagination": { "offset": offset, "limit": limit },
    })))
}

// ─── Audit Logs ───

async fn list_audit_logs(
    State(state): State<Arc<AppState>>,
    Query(params): Query<PaginationQuery>,
) -> Result<Json<Value>, ApiError> {
    let offset = params.offset.unwrap_or(0);
    let limit = params.limit.unwrap_or(50).min(100);
    let logs = state.storage.list_audit_logs(offset, limit).await
        .map_err(|e| ApiError::Internal(e.to_string()))?;
    Ok(Json(json!({
        "audit_logs": logs,
        "pagination": { "offset": offset, "limit": limit },
    })))
}
