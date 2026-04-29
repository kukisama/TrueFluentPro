use serde::{Deserialize, Serialize};
use std::collections::HashMap;

use super::enums::ServiceMode;

fn default_subscription() -> String {
    "free".into()
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CloudSettings {
    #[serde(default)]
    pub mode: ServiceMode,
    #[serde(default)]
    pub backend_url: String,
    #[serde(default)]
    pub aad_tenant_id: String,
    #[serde(default)]
    pub aad_client_id: String,
    #[serde(default)]
    pub aad_scope: String,
}

impl Default for CloudSettings {
    fn default() -> Self {
        Self {
            mode: ServiceMode::SelfHosted,
            backend_url: String::new(),
            aad_tenant_id: String::new(),
            aad_client_id: String::new(),
            aad_scope: String::new(),
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
pub struct CloudUserProfile {
    #[serde(default)]
    pub user_id: String,
    #[serde(default)]
    pub display_name: String,
    #[serde(default)]
    pub email: String,
    #[serde(default = "default_subscription")]
    pub subscription: String,
    #[serde(default)]
    pub is_admin: bool,
    #[serde(default)]
    pub quotas: HashMap<String, QuotaInfo>,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
pub struct QuotaInfo {
    pub used: i64,
    pub limit: i64,
}

impl QuotaInfo {
    pub fn remaining(&self) -> i64 {
        self.limit - self.used
    }
}
