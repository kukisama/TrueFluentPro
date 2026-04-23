//! JWT authentication middleware — supports local and AAD modes.

use crate::state::AppState;
use crate::error::ApiError;
use domain::auth::{UserContext, LocalClaims, AadClaims};
use domain::models::UserRole;
use axum::{
    extract::{Request, State},
    http::header,
    middleware::Next,
    response::Response,
};
use jsonwebtoken::{decode, Algorithm, DecodingKey, Validation};
use std::sync::Arc;
use tracing::debug;

/// Middleware: require a valid JWT and inject UserContext into request extensions.
pub async fn require_auth(
    State(state): State<Arc<AppState>>,
    mut req: Request,
    next: Next,
) -> Result<Response, ApiError> {
    let token = extract_bearer_token(&req)?;
    let user_ctx = validate_token(&state, &token).await?;

    // JIT: ensure user exists in DB, update last_seen
    ensure_user_exists(&state, &user_ctx).await?;

    req.extensions_mut().insert(user_ctx);
    Ok(next.run(req).await)
}

/// Middleware: require admin role.
pub async fn require_admin(
    State(state): State<Arc<AppState>>,
    mut req: Request,
    next: Next,
) -> Result<Response, ApiError> {
    let token = extract_bearer_token(&req)?;
    let user_ctx = validate_token(&state, &token).await?;

    if user_ctx.role != UserRole::Admin {
        return Err(ApiError::Forbidden("admin role required".into()));
    }

    ensure_user_exists(&state, &user_ctx).await?;

    req.extensions_mut().insert(user_ctx);
    Ok(next.run(req).await)
}

fn extract_bearer_token(req: &Request) -> Result<String, ApiError> {
    req.headers()
        .get(header::AUTHORIZATION)
        .and_then(|v| v.to_str().ok())
        .and_then(|v| v.strip_prefix("Bearer "))
        .map(|s| s.to_string())
        .ok_or_else(|| ApiError::Unauthorized("missing or invalid Authorization header".into()))
}

async fn validate_token(state: &AppState, token: &str) -> Result<UserContext, ApiError> {
    match state.config.auth.mode.as_str() {
        "local" => validate_local_token(state, token),
        "aad" => validate_aad_token(state, token).await,
        other => Err(ApiError::Internal(format!("unknown auth mode: {other}"))),
    }
}

/// Validate a token from a WebSocket query parameter.
/// Exported for use by ws_translate.
pub async fn validate_ws_auth(state: &AppState, token: &str) -> Result<UserContext, ApiError> {
    validate_token(state, token).await
}

fn validate_local_token(state: &AppState, token: &str) -> Result<UserContext, ApiError> {
    let key = DecodingKey::from_secret(state.jwt_secret.as_bytes());
    let mut validation = Validation::new(Algorithm::HS256);
    validation.validate_exp = true;
    validation.leeway = 30;

    let data = decode::<LocalClaims>(token, &key, &validation)
        .map_err(|e| ApiError::Unauthorized(format!("invalid token: {e}")))?;

    let claims = data.claims;
    Ok(UserContext {
        user_id: claims.sub,
        tenant_id: "default".into(),
        role: claims.role.parse().unwrap_or(UserRole::User),
        display_name: claims.display_name,
        email: None,
    })
}

async fn validate_aad_token(state: &AppState, token: &str) -> Result<UserContext, ApiError> {
    let jwks = state.jwks.as_ref()
        .ok_or_else(|| ApiError::Internal("AAD JWKS not initialized — check tenant_id configuration".into()))?;

    // Decode header to get kid
    let header = jsonwebtoken::decode_header(token)
        .map_err(|e| ApiError::Unauthorized(format!("invalid token header: {e}")))?;

    let kid = header.kid
        .ok_or_else(|| ApiError::Unauthorized("AAD token missing 'kid' header".into()))?;

    // Get the decoding key from JWKS
    let key = jwks.get_key(&kid).await
        .ok_or_else(|| ApiError::Unauthorized(format!("Unknown signing key: {kid}")))?;

    let mut validation = Validation::new(Algorithm::RS256);
    validation.validate_exp = true;
    validation.leeway = 30;

    if !state.config.auth.aad.audience.is_empty() {
        validation.set_audience(&[&state.config.auth.aad.audience]);
    }

    // Validate issuer if tenant_id is set
    if !state.config.auth.aad.tenant_id.is_empty() {
        validation.set_issuer(&[
            &format!("https://login.microsoftonline.com/{}/v2.0", state.config.auth.aad.tenant_id),
            &format!("https://sts.windows.net/{}/", state.config.auth.aad.tenant_id),
        ]);
    }

    let data = decode::<AadClaims>(token, &key, &validation)
        .map_err(|e| ApiError::Unauthorized(format!("invalid AAD token: {e}")))?;

    let claims = data.claims;
    let role = claims.roles
        .as_ref()
        .and_then(|roles| roles.iter().find(|r| *r == "admin"))
        .map(|_| UserRole::Admin)
        .unwrap_or(UserRole::User);

    Ok(UserContext {
        user_id: claims.oid,
        tenant_id: claims.tid.unwrap_or_else(|| "default".into()),
        role,
        display_name: claims.name,
        email: claims.email.or(claims.preferred_username),
    })
}

/// JIT user provisioning — create user record on first access.
async fn ensure_user_exists(state: &AppState, ctx: &UserContext) -> Result<(), ApiError> {
    use domain::models::{User, AuthProvider};

    let existing = state.storage.get_user(&ctx.user_id).await
        .map_err(|e| ApiError::Internal(e.to_string()))?;

    if existing.is_some() {
        // Update last_seen
        let _ = state.storage.update_user_last_seen(&ctx.user_id).await;
    } else {
        // JIT provisioning: first-time user
        debug!(user_id = %ctx.user_id, "JIT creating user");

        let auth_provider = match state.config.auth.mode.as_str() {
            "aad" => AuthProvider::Aad,
            _ => AuthProvider::Local,
        };

        let now = chrono::Utc::now();
        let user = User {
            id: ctx.user_id.clone(),
            username: None,
            display_name: ctx.display_name.clone().unwrap_or_else(|| ctx.user_id.clone()),
            email: ctx.email.clone(),
            role: ctx.role,
            plan_id: "free".into(),
            is_active: true,
            auth_provider,
            tenant_id: ctx.tenant_id.clone(),
            first_seen_at: now,
            last_seen_at: now,
        };
        state.storage.upsert_user(&user).await
            .map_err(|e| ApiError::Internal(e.to_string()))?;
    }

    Ok(())
}
