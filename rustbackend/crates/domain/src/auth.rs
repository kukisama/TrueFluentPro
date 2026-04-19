use serde::{Deserialize, Serialize};

/// Internal user context extracted from JWT — all handlers depend only on this.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct UserContext {
    pub user_id: String,
    pub tenant_id: String,
    pub role: super::models::UserRole,
    pub display_name: Option<String>,
    pub email: Option<String>,
}

/// JWT claims for local auth mode.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct LocalClaims {
    pub sub: String,
    pub role: String,
    pub display_name: Option<String>,
    pub exp: usize,
    pub iat: usize,
}

/// JWT claims for AAD auth mode.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AadClaims {
    pub oid: String,
    pub tid: Option<String>,
    pub preferred_username: Option<String>,
    pub name: Option<String>,
    pub email: Option<String>,
    pub scp: Option<String>,
    pub roles: Option<Vec<String>>,
    pub exp: usize,
}

/// Login request (local mode).
#[derive(Debug, Clone, Deserialize)]
pub struct LoginRequest {
    pub username: String,
    pub password: String,
}

/// Login response.
#[derive(Debug, Clone, Serialize)]
pub struct LoginResponse {
    pub token: String,
    pub expires_in: u64,
    pub user: super::models::User,
}

/// Register request (local mode).
#[derive(Debug, Clone, Deserialize)]
pub struct RegisterRequest {
    pub username: String,
    pub password: String,
    pub display_name: String,
    #[serde(default)]
    pub invite_code: Option<String>,
}
