//! Billing crate — optional billing engine with disabled/active implementations.

use serde::{Deserialize, Serialize};
use async_trait::async_trait;

/// Quota check result.
#[derive(Debug, Clone, Serialize)]
pub enum QuotaStatus {
    Ok { remaining: i64 },
    Exceeded { used: i64, limit: i64 },
    Unlimited,
}

/// Billing engine trait — all billing goes through this.
#[async_trait]
pub trait BillingEngine: Send + Sync {
    fn is_enabled(&self) -> bool;
    async fn check_quota(&self, user_id: &str, resource_type: &str) -> anyhow::Result<QuotaStatus>;
    async fn record_usage(&self, user_id: &str, capability_id: &str, resource_type: &str, amount: i64) -> anyhow::Result<()>;
}

/// Disabled billing engine — zero overhead pass-through.
pub struct DisabledBillingEngine;

#[async_trait]
impl BillingEngine for DisabledBillingEngine {
    fn is_enabled(&self) -> bool {
        false
    }

    async fn check_quota(&self, _user_id: &str, _resource_type: &str) -> anyhow::Result<QuotaStatus> {
        Ok(QuotaStatus::Unlimited)
    }

    async fn record_usage(&self, _user_id: &str, _capability_id: &str, _resource_type: &str, _amount: i64) -> anyhow::Result<()> {
        Ok(())
    }
}
