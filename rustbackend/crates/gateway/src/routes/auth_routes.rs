//! Authentication endpoints (local mode: login, register).

use crate::state::AppState;
use crate::error::ApiError;
use domain::auth::{LoginRequest, LoginResponse, RegisterRequest, LocalClaims};
use domain::models::{User, UserRole, AuthProvider};
use std::sync::Arc;
use axum::{Router, routing::post, extract::State, Json};

pub fn routes() -> Router<Arc<AppState>> {
    Router::new()
        .route("/v1/auth/login", post(login))
        .route("/v1/auth/register", post(register))
}

async fn login(
    State(state): State<Arc<AppState>>,
    Json(req): Json<LoginRequest>,
) -> Result<Json<LoginResponse>, ApiError> {
    if state.config.auth.mode != "local" {
        return Err(ApiError::BadRequest(
            "Local auth is not enabled. Use AAD authentication.".into(),
        ));
    }

    // Find user by username
    let user = state.storage.get_user_by_username(&req.username).await
        .map_err(|e| ApiError::Internal(e.to_string()))?
        .ok_or_else(|| ApiError::Unauthorized("invalid username or password".into()))?;

    // Verify password
    let hash = state.storage.get_password_hash(&user.id).await
        .map_err(|e| ApiError::Internal(e.to_string()))?
        .ok_or_else(|| ApiError::Unauthorized("invalid username or password".into()))?;

    verify_password(&req.password, &hash)?;

    // Issue JWT
    let token = issue_jwt(
        &state.jwt_secret,
        &user.id,
        user.role.as_str(),
        &user.display_name,
        state.config.auth.local.jwt_expiry_hours,
    )?;

    let expires_in = state.config.auth.local.jwt_expiry_hours * 3600;

    Ok(Json(LoginResponse {
        token,
        expires_in,
        user,
    }))
}

async fn register(
    State(state): State<Arc<AppState>>,
    Json(req): Json<RegisterRequest>,
) -> Result<Json<LoginResponse>, ApiError> {
    if state.config.auth.mode != "local" {
        return Err(ApiError::BadRequest(
            "Local auth is not enabled. Use AAD authentication.".into(),
        ));
    }

    if !state.config.auth.local.allow_registration {
        return Err(ApiError::Forbidden(
            "Registration is disabled. Contact an administrator.".into(),
        ));
    }

    if req.password.len() < 8 {
        return Err(ApiError::BadRequest("Password must be at least 8 characters".into()));
    }

    // Check username doesn't exist
    let existing = state.storage.get_user_by_username(&req.username).await
        .map_err(|e| ApiError::Internal(e.to_string()))?;

    if existing.is_some() {
        return Err(ApiError::Conflict("Username already taken".into()));
    }

    // Create user
    let user_id = uuid::Uuid::new_v4().to_string();
    let now = chrono::Utc::now();
    let user = User {
        id: user_id.clone(),
        username: Some(req.username.clone()),
        display_name: req.display_name.clone(),
        email: None,
        role: UserRole::User,
        plan_id: "free".into(),
        is_active: true,
        auth_provider: AuthProvider::Local,
        tenant_id: "default".into(),
        first_seen_at: now,
        last_seen_at: now,
    };

    state.storage.upsert_user(&user).await
        .map_err(|e| ApiError::Internal(format!("failed to create user: {e}")))?;

    // Hash and store password
    let hash = hash_password(&req.password)?;
    state.storage.set_password_hash(&user_id, &hash).await
        .map_err(|e| ApiError::Internal(format!("failed to store password: {e}")))?;

    // Issue JWT
    let token = issue_jwt(
        &state.jwt_secret,
        &user_id,
        "user",
        &req.display_name,
        state.config.auth.local.jwt_expiry_hours,
    )?;

    Ok(Json(LoginResponse {
        token,
        expires_in: state.config.auth.local.jwt_expiry_hours * 3600,
        user,
    }))
}

fn verify_password(password: &str, hash: &str) -> Result<(), ApiError> {
    use argon2::{Argon2, PasswordVerifier, PasswordHash};
    let parsed = PasswordHash::new(hash)
        .map_err(|_| ApiError::Internal("invalid password hash in DB".into()))?;
    Argon2::default()
        .verify_password(password.as_bytes(), &parsed)
        .map_err(|_| ApiError::Unauthorized("invalid username or password".into()))
}

fn hash_password(password: &str) -> Result<String, ApiError> {
    use argon2::{Argon2, PasswordHasher, password_hash::SaltString};
    let salt = SaltString::generate(&mut rand::thread_rng());
    let argon2 = Argon2::new(
        argon2::Algorithm::Argon2id,
        argon2::Version::V0x13,
        argon2::Params::new(65536, 3, 1, None)
            .map_err(|e| ApiError::Internal(format!("argon2 params: {e}")))?,
    );
    let hash = argon2.hash_password(password.as_bytes(), &salt)
        .map_err(|e| ApiError::Internal(format!("password hash: {e}")))?;
    Ok(hash.to_string())
}

fn issue_jwt(secret: &str, user_id: &str, role: &str, display_name: &str, expiry_hours: u64) -> Result<String, ApiError> {
    use jsonwebtoken::{encode, Header, EncodingKey};
    let now = chrono::Utc::now().timestamp() as usize;
    let claims = LocalClaims {
        sub: user_id.to_string(),
        role: role.to_string(),
        display_name: Some(display_name.to_string()),
        exp: now + (expiry_hours as usize * 3600),
        iat: now,
    };
    encode(
        &Header::default(),
        &claims,
        &EncodingKey::from_secret(secret.as_bytes()),
    )
    .map_err(|e| ApiError::Internal(format!("JWT encoding failed: {e}")))
}
