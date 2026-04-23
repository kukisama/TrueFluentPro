use serde::{Deserialize, Serialize};

/// Top-level gateway configuration.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct GatewayConfig {
    #[serde(default = "default_server")]
    pub server: ServerConfig,
    #[serde(default)]
    pub auth: AuthConfig,
    #[serde(default)]
    pub database: DatabaseConfig,
    #[serde(default)]
    pub cache: CacheConfig,
    #[serde(default)]
    pub billing: BillingConfig,
    #[serde(default)]
    pub rate_limit: RateLimitConfig,
    #[serde(default)]
    pub credentials: CredentialConfig,
    #[serde(default)]
    pub observability: ObservabilityConfig,
    #[serde(default = "default_storage")]
    pub storage: StoragePathConfig,
}

fn default_server() -> ServerConfig {
    ServerConfig::default()
}
fn default_storage() -> StoragePathConfig {
    StoragePathConfig::default()
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ServerConfig {
    #[serde(default = "default_bind")]
    pub bind: String,
    #[serde(default)]
    pub tls_cert: Option<String>,
    #[serde(default)]
    pub tls_key: Option<String>,
}

impl Default for ServerConfig {
    fn default() -> Self {
        Self {
            bind: default_bind(),
            tls_cert: None,
            tls_key: None,
        }
    }
}

fn default_bind() -> String {
    "0.0.0.0:8080".into()
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AuthConfig {
    #[serde(default = "default_auth_mode")]
    pub mode: String,
    #[serde(default)]
    pub aad: AadConfig,
    #[serde(default)]
    pub local: LocalAuthConfig,
}

impl Default for AuthConfig {
    fn default() -> Self {
        Self {
            mode: default_auth_mode(),
            aad: AadConfig::default(),
            local: LocalAuthConfig::default(),
        }
    }
}

fn default_auth_mode() -> String {
    "local".into()
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct AadConfig {
    #[serde(default)]
    pub tenant_id: String,
    #[serde(default)]
    pub client_id: String,
    #[serde(default = "default_audience")]
    pub audience: String,
}

fn default_audience() -> String {
    "api://truefluentpro".into()
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct LocalAuthConfig {
    #[serde(default)]
    pub jwt_secret: String,
    #[serde(default = "default_jwt_expiry")]
    pub jwt_expiry_hours: u64,
    #[serde(default)]
    pub allow_registration: bool,
}

impl Default for LocalAuthConfig {
    fn default() -> Self {
        Self {
            jwt_secret: String::new(),
            jwt_expiry_hours: default_jwt_expiry(),
            allow_registration: false,
        }
    }
}

fn default_jwt_expiry() -> u64 {
    24
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DatabaseConfig {
    #[serde(default)]
    pub url: String,
    #[serde(default = "default_max_conn")]
    pub max_connections: u32,
}

impl Default for DatabaseConfig {
    fn default() -> Self {
        Self {
            url: String::new(),
            max_connections: default_max_conn(),
        }
    }
}

fn default_max_conn() -> u32 {
    10
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CacheConfig {
    #[serde(default = "default_cache_mode")]
    pub mode: String,
}

impl Default for CacheConfig {
    fn default() -> Self {
        Self {
            mode: default_cache_mode(),
        }
    }
}

fn default_cache_mode() -> String {
    "memory".into()
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct BillingConfig {
    #[serde(default)]
    pub enabled: bool,
}

impl Default for BillingConfig {
    fn default() -> Self {
        Self { enabled: false }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct RateLimitConfig {
    #[serde(default = "default_true")]
    pub enabled: bool,
    #[serde(default = "default_rpm")]
    pub requests_per_minute: u32,
}

impl Default for RateLimitConfig {
    fn default() -> Self {
        Self {
            enabled: true,
            requests_per_minute: default_rpm(),
        }
    }
}

fn default_true() -> bool {
    true
}
fn default_rpm() -> u32 {
    60
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
pub struct CredentialConfig {
    #[serde(default)]
    pub master_key_base64: String,
    #[serde(default)]
    pub keyvault_url: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ObservabilityConfig {
    #[serde(default = "default_log_level")]
    pub log_level: String,
    #[serde(default = "default_log_format")]
    pub log_format: String,
}

impl Default for ObservabilityConfig {
    fn default() -> Self {
        Self {
            log_level: default_log_level(),
            log_format: default_log_format(),
        }
    }
}

fn default_log_level() -> String {
    "info".into()
}
fn default_log_format() -> String {
    "json".into()
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StoragePathConfig {
    #[serde(default = "default_data_dir")]
    pub data_dir: String,
}

impl Default for StoragePathConfig {
    fn default() -> Self {
        Self {
            data_dir: default_data_dir(),
        }
    }
}

fn default_data_dir() -> String {
    "./data".into()
}
