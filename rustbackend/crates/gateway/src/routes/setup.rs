//! Setup wizard endpoints — first-boot initialization.

use crate::state::AppState;
use crate::error::ApiError;
use domain::models::{User, UserRole, AuthProvider};
use std::sync::Arc;
use axum::{Router, routing::{get, post}, extract::State, Json};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};

pub fn routes() -> Router<Arc<AppState>> {
    Router::new()
        .route("/v1/setup/status", get(setup_status))
        .route("/v1/setup/init", post(setup_init))
}

async fn setup_status(State(state): State<Arc<AppState>>) -> Result<Json<Value>, ApiError> {
    let initialized = state.storage.is_initialized().await
        .map_err(|e| ApiError::Internal(e.to_string()))?;

    if initialized {
        return Ok(Json(json!({ "initialized": true })));
    }

    Ok(Json(json!({
        "initialized": false,
        "steps": [
            { "id": "database", "status": "done", "label": "Database ready" },
            { "id": "admin", "status": "pending", "label": "Create admin account" },
            { "id": "provider", "status": "pending", "label": "Configure first provider (optional)" }
        ]
    })))
}

#[derive(Debug, Deserialize)]
struct SetupRequest {
    admin: AdminSetup,
    #[serde(default = "default_auth_mode")]
    auth_mode: String,
    #[serde(default)]
    aad_config: Option<AadSetup>,
    #[serde(default)]
    provider: Option<ProviderSetup>,
    #[serde(default)]
    billing_enabled: bool,
}

fn default_auth_mode() -> String {
    "local".into()
}

#[derive(Debug, Deserialize)]
struct AdminSetup {
    username: String,
    password: String,
    display_name: String,
}

#[derive(Debug, Deserialize)]
struct AadSetup {
    tenant_id: String,
    client_id: String,
    audience: String,
}

#[derive(Debug, Deserialize)]
struct ProviderSetup {
    id: String,
    vendor: Option<String>,
    display_name: Option<String>,
    credentials: std::collections::HashMap<String, String>,
}

#[derive(Debug, Serialize)]
struct SetupResponse {
    success: bool,
    admin_token: Option<String>,
    message: String,
}

async fn setup_init(
    State(state): State<Arc<AppState>>,
    Json(req): Json<SetupRequest>,
) -> Result<Json<SetupResponse>, ApiError> {
    // Check if already initialized
    let initialized = state.storage.is_initialized().await
        .map_err(|e| ApiError::Internal(e.to_string()))?;

    if initialized {
        return Err(ApiError::Conflict("System already initialized".into()));
    }

    // Validate password
    if req.admin.password.len() < 8 {
        return Err(ApiError::BadRequest("Password must be at least 8 characters".into()));
    }

    // Create admin user
    let user_id = uuid::Uuid::new_v4().to_string();
    let now = chrono::Utc::now();

    let admin_user = User {
        id: user_id.clone(),
        username: Some(req.admin.username.clone()),
        display_name: req.admin.display_name.clone(),
        email: None,
        role: UserRole::Admin,
        plan_id: "unlimited".into(),
        is_active: true,
        auth_provider: AuthProvider::Local,
        tenant_id: "default".into(),
        first_seen_at: now,
        last_seen_at: now,
    };

    state.storage.upsert_user(&admin_user).await
        .map_err(|e| ApiError::Internal(format!("failed to create admin user: {e}")))?;

    // Hash and store password
    let password_hash = hash_password(&req.admin.password)
        .map_err(|e| ApiError::Internal(format!("password hashing failed: {e}")))?;

    state.storage.set_password_hash(&user_id, &password_hash).await
        .map_err(|e| ApiError::Internal(format!("failed to store password: {e}")))?;

    // Store auth mode config
    state.storage.set_config("auth.mode", &req.auth_mode).await
        .map_err(|e| ApiError::Internal(e.to_string()))?;

    // If AAD config provided, store it
    if let Some(aad) = &req.aad_config {
        state.storage.set_config("auth.aad.tenant_id", &aad.tenant_id).await
            .map_err(|e| ApiError::Internal(e.to_string()))?;
        state.storage.set_config("auth.aad.client_id", &aad.client_id).await
            .map_err(|e| ApiError::Internal(e.to_string()))?;
        state.storage.set_config("auth.aad.audience", &aad.audience).await
            .map_err(|e| ApiError::Internal(e.to_string()))?;
    }

    // Store billing config
    state.storage.set_config("billing.enabled", if req.billing_enabled { "true" } else { "false" }).await
        .map_err(|e| ApiError::Internal(e.to_string()))?;

    // Configure first provider if provided
    if let Some(provider) = &req.provider {
        let provider_info = domain::models::ProviderInfo {
            id: provider.id.clone(),
            vendor: provider.vendor.clone().unwrap_or_else(|| "unknown".into()),
            display_name: provider.display_name.clone().unwrap_or_else(|| provider.id.clone()),
            is_enabled: true,
            config_json: None,
        };
        state.storage.upsert_provider(&provider_info).await
            .map_err(|e| ApiError::Internal(format!("failed to create provider: {e}")))?;

        // Store credentials
        for (key, value) in &provider.credentials {
            state.credentials.store(&provider.id, key, value).await
                .map_err(|e| ApiError::Internal(format!("failed to store credential: {e}")))?;
        }
    }

    // Mark as initialized
    state.storage.set_config("initialized", "true").await
        .map_err(|e| ApiError::Internal(e.to_string()))?;

    // Issue admin JWT token
    let token = issue_local_jwt(&state.jwt_secret, &user_id, "admin", &req.admin.display_name, state.config.auth.local.jwt_expiry_hours)?;

    Ok(Json(SetupResponse {
        success: true,
        admin_token: Some(token),
        message: "System initialized successfully. Use the admin token to access management APIs.".into(),
    }))
}

fn hash_password(password: &str) -> anyhow::Result<String> {
    use argon2::{Argon2, PasswordHasher, password_hash::SaltString};
    let salt = SaltString::generate(&mut rand::thread_rng());
    let argon2 = Argon2::new(
        argon2::Algorithm::Argon2id,
        argon2::Version::V0x13,
        argon2::Params::new(65536, 3, 1, None)
            .map_err(|e| anyhow::anyhow!("argon2 params: {e}"))?,
    );
    let hash = argon2.hash_password(password.as_bytes(), &salt)
        .map_err(|e| anyhow::anyhow!("password hash failed: {e}"))?;
    Ok(hash.to_string())
}

fn issue_local_jwt(secret: &str, user_id: &str, role: &str, display_name: &str, expiry_hours: u64) -> Result<String, ApiError> {
    use jsonwebtoken::{encode, Header, EncodingKey};
    use domain::auth::LocalClaims;

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
