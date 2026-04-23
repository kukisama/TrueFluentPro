use serde::{Deserialize, Serialize};
use chrono::{DateTime, Utc};

// ═══ User ═══

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct User {
    pub id: String,
    pub username: Option<String>,
    pub display_name: String,
    pub email: Option<String>,
    pub role: UserRole,
    pub plan_id: String,
    pub is_active: bool,
    pub auth_provider: AuthProvider,
    pub tenant_id: String,
    pub first_seen_at: DateTime<Utc>,
    pub last_seen_at: DateTime<Utc>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum UserRole {
    User,
    Admin,
}

impl UserRole {
    pub fn as_str(&self) -> &'static str {
        match self {
            UserRole::User => "user",
            UserRole::Admin => "admin",
        }
    }
}

impl std::fmt::Display for UserRole {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(self.as_str())
    }
}

impl std::str::FromStr for UserRole {
    type Err = String;
    fn from_str(s: &str) -> Result<Self, Self::Err> {
        match s {
            "user" => Ok(UserRole::User),
            "admin" => Ok(UserRole::Admin),
            other => Err(format!("unknown role: {other}")),
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum AuthProvider {
    Local,
    Aad,
}

impl AuthProvider {
    pub fn as_str(&self) -> &'static str {
        match self {
            AuthProvider::Local => "local",
            AuthProvider::Aad => "aad",
        }
    }
}

impl std::fmt::Display for AuthProvider {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(self.as_str())
    }
}

impl std::str::FromStr for AuthProvider {
    type Err = String;
    fn from_str(s: &str) -> Result<Self, Self::Err> {
        match s {
            "local" => Ok(AuthProvider::Local),
            "aad" => Ok(AuthProvider::Aad),
            other => Err(format!("unknown auth provider: {other}")),
        }
    }
}

// ═══ Capability ═══

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Capability {
    pub id: String,
    pub display_name: String,
    pub category: String,
    pub is_enabled: bool,
    pub description: Option<String>,
}

// ═══ Provider ═══

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ProviderInfo {
    pub id: String,
    pub vendor: String,
    pub display_name: String,
    pub is_enabled: bool,
    pub config_json: Option<serde_json::Value>,
}

// ═══ SubscriptionPlan ═══

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SubscriptionPlan {
    pub id: String,
    pub display_name: String,
    pub price_monthly: Option<f64>,
    pub limits_json: serde_json::Value,
    pub is_active: bool,
}

// ═══ Usage Record ═══

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct UsageRecord {
    pub id: String,
    pub user_id: String,
    pub capability_id: String,
    pub resource_type: String,
    pub amount: i64,
    pub recorded_at: DateTime<Utc>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct UserCapabilityStatus {
    pub id: String,
    pub display_name: String,
    pub category: String,
    pub status: CapabilityAvailability,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub quota: Option<QuotaInfo>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum CapabilityAvailability {
    Available,
    QuotaExceeded,
    Disabled,
    Unavailable,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct QuotaInfo {
    pub used: i64,
    pub limit: i64,
    pub unit: String,
    pub resets_at: DateTime<Utc>,
}

// ═══ Audit Log Entry ═══

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AuditLogEntry {
    pub id: i64,
    pub user_id: Option<String>,
    pub action: String,
    pub detail: Option<String>,
    pub ip_address: Option<String>,
    pub created_at: DateTime<Utc>,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_user_role_from_str() {
        assert_eq!("admin".parse::<UserRole>().unwrap(), UserRole::Admin);
        assert_eq!("user".parse::<UserRole>().unwrap(), UserRole::User);
        assert!("invalid".parse::<UserRole>().is_err());
    }

    #[test]
    fn test_auth_provider_display() {
        assert_eq!(format!("{}", AuthProvider::Local), "local");
        assert_eq!(format!("{}", AuthProvider::Aad), "aad");
    }

    #[test]
    fn test_auth_provider_from_str() {
        assert_eq!("local".parse::<AuthProvider>().unwrap(), AuthProvider::Local);
        assert_eq!("aad".parse::<AuthProvider>().unwrap(), AuthProvider::Aad);
        assert!("invalid".parse::<AuthProvider>().is_err());
    }

    #[test]
    fn test_user_role_display() {
        assert_eq!(format!("{}", UserRole::User), "user");
        assert_eq!(format!("{}", UserRole::Admin), "admin");
    }
}
