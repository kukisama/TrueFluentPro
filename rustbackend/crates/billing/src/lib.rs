//! Billing crate — optional billing engine with disabled/active implementations.

use serde::{Deserialize, Serialize};
use async_trait::async_trait;
use storage::StorageBackend;
use chrono::Datelike;
use std::sync::Arc;

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

/// Active billing engine — enforces quotas based on user plan.
pub struct ActiveBillingEngine {
    storage: Arc<dyn StorageBackend>,
}

impl ActiveBillingEngine {
    pub fn new(storage: Arc<dyn StorageBackend>) -> Self {
        Self { storage }
    }

    /// Get the monthly limit for a resource type from the user's plan.
    async fn get_plan_limit(&self, user_id: &str, resource_type: &str) -> anyhow::Result<i64> {
        let user = self.storage.get_user(user_id).await
            .map_err(|e| anyhow::anyhow!("{e}"))?
            .ok_or_else(|| anyhow::anyhow!("user not found"))?;

        let plan = self.storage.get_plan(&user.plan_id).await
            .map_err(|e| anyhow::anyhow!("{e}"))?
            .ok_or_else(|| anyhow::anyhow!("plan not found: {}", user.plan_id))?;

        // Parse limits_json: { "chat_token": 50000, "image": 5, ... }
        // -1 means unlimited
        let limit = plan.limits_json.get(resource_type)
            .and_then(|v| v.as_i64())
            .unwrap_or(0); // Default: 0 (not allowed) if resource type not in plan

        Ok(limit)
    }
}

#[async_trait]
impl BillingEngine for ActiveBillingEngine {
    fn is_enabled(&self) -> bool {
        true
    }

    async fn check_quota(&self, user_id: &str, resource_type: &str) -> anyhow::Result<QuotaStatus> {
        let limit = self.get_plan_limit(user_id, resource_type).await?;

        // -1 = unlimited
        if limit < 0 {
            return Ok(QuotaStatus::Unlimited);
        }

        // Get usage since start of current month
        let now = chrono::Utc::now();
        let month_start = now.date_naive()
            .with_day(1)
            .unwrap_or(now.date_naive());
        let month_start_dt = month_start
            .and_hms_opt(0, 0, 0)
            .unwrap()
            .and_utc();

        let used = self.storage.get_usage_total(user_id, resource_type, month_start_dt).await
            .map_err(|e| anyhow::anyhow!("{e}"))?;

        if used >= limit {
            Ok(QuotaStatus::Exceeded { used, limit })
        } else {
            Ok(QuotaStatus::Ok { remaining: limit - used })
        }
    }

    async fn record_usage(&self, user_id: &str, capability_id: &str, resource_type: &str, amount: i64) -> anyhow::Result<()> {
        self.storage.record_usage(user_id, capability_id, resource_type, amount).await
            .map_err(|e| anyhow::anyhow!("{e}"))
    }
}
