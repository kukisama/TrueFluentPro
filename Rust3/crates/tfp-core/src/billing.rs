//! Billing tiers configuration and cost calculation.

use serde::{Deserialize, Serialize};
use std::collections::HashMap;

const BILLING_TIERS_JSON: &str = include_str!("../../../src-tauri/assets/billing-tiers.json");

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BillingTiersConfig {
    pub schema_version: u32,
    pub updated_at: String,
    pub source: String,
    pub models: HashMap<String, BillingTierModel>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BillingTierModel {
    pub billing_unit: String,
    pub price_per_million_output_tokens: f64,
    pub price_per_million_input_tokens: f64,
    pub price_per_million_image_input_tokens: f64,
    pub tiers: Vec<BillingTier>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BillingTier {
    pub width: u32,
    pub height: u32,
    pub quality: String,
    pub tokens: u32,
    #[serde(default)]
    pub price_usd: Option<f64>,
}

impl BillingTier {
    pub fn pixel_area(&self) -> u64 {
        self.width as u64 * self.height as u64
    }
}

/// Load billing tiers from embedded JSON asset.
pub fn load_billing_tiers() -> BillingTiersConfig {
    serde_json::from_str(BILLING_TIERS_JSON)
        .expect("embedded billing-tiers.json must be valid")
}

/// Find the smallest tier whose dimensions are >= the requested size (snap-up).
/// Tiers are matched by quality first, then by pixel area.
pub fn snap_up(
    config: &BillingTiersConfig,
    model_id: &str,
    width: u32,
    height: u32,
    quality: &str,
) -> Option<BillingTier> {
    let model = config.models.get(model_id)?;
    let requested_area = width as u64 * height as u64;
    let quality_lower = quality.to_lowercase();

    let mut candidates: Vec<&BillingTier> = model
        .tiers
        .iter()
        .filter(|t| t.quality.to_lowercase() == quality_lower)
        .collect();

    // Sort by pixel area ascending
    candidates.sort_by_key(|t| t.pixel_area());

    // Find first tier with area >= requested
    candidates
        .into_iter()
        .find(|t| t.pixel_area() >= requested_area)
        .or_else(|| {
            // If no tier is large enough, use the largest available
            model
                .tiers
                .iter()
                .filter(|t| t.quality.to_lowercase() == quality_lower)
                .max_by_key(|t| t.pixel_area())
        })
        .cloned()
}

/// Calculate the cost for an image generation based on the matched tier.
pub fn calculate_cost(model: &BillingTierModel, tier: &BillingTier) -> f64 {
    // Fixed per-image pricing takes priority
    if let Some(price) = tier.price_usd {
        if price > 0.0 {
            return price;
        }
    }
    // Token-based pricing
    if tier.tokens > 0 {
        return tier.tokens as f64 * model.price_per_million_output_tokens / 1_000_000.0;
    }
    0.0
}

/// Calculate cost for actual output tokens (when actual count is known).
pub fn calculate_token_cost(model: &BillingTierModel, output_tokens: u32) -> f64 {
    output_tokens as f64 * model.price_per_million_output_tokens / 1_000_000.0
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_load_billing_tiers() {
        let config = load_billing_tiers();
        assert_eq!(config.schema_version, 1);
        assert!(config.models.contains_key("gpt-image-2"));
        assert!(config.models.contains_key("gpt-image-1.5"));
        let gpt2 = &config.models["gpt-image-2"];
        assert_eq!(gpt2.billing_unit, "token");
        assert_eq!(gpt2.tiers.len(), 9);
    }

    #[test]
    fn test_snap_up_exact_match() {
        let config = load_billing_tiers();
        let tier = snap_up(&config, "gpt-image-2", 1024, 1024, "medium").unwrap();
        assert_eq!(tier.width, 1024);
        assert!(tier.height == 1024 || tier.height == 1536);
        assert_eq!(tier.tokens, 1767);
    }

    #[test]
    fn test_snap_up_rounds_up() {
        let config = load_billing_tiers();
        // 800x800 should snap up to 1024x1024
        let tier = snap_up(&config, "gpt-image-2", 800, 800, "low").unwrap();
        assert_eq!(tier.width, 1024);
        assert_eq!(tier.height, 1024);
        assert_eq!(tier.tokens, 200);
    }

    #[test]
    fn test_snap_up_larger_than_all() {
        let config = load_billing_tiers();
        // 2048x2048 is larger than all defined tiers → should return largest
        let tier = snap_up(&config, "gpt-image-2", 2048, 2048, "high").unwrap();
        // Largest high-quality tier by pixel area is 1024x1536 or 1536x1024 (5500 tokens)
        assert_eq!(tier.tokens, 5500);
        assert_eq!(tier.pixel_area(), 1536 * 1024);

    }

    #[test]
    fn test_snap_up_unknown_model() {
        let config = load_billing_tiers();
        assert!(snap_up(&config, "nonexistent-model", 1024, 1024, "medium").is_none());
    }

    #[test]
    fn test_calculate_cost_token_billing() {
        let config = load_billing_tiers();
        let model = &config.models["gpt-image-2"];
        let tier = BillingTier {
            width: 1024,
            height: 1024,
            quality: "medium".into(),
            tokens: 1767,
            price_usd: None,
        };
        let cost = calculate_cost(model, &tier);
        // 1767 * 30.0 / 1_000_000 = 0.05301
        assert!((cost - 0.05301).abs() < 0.001);
    }

    #[test]
    fn test_calculate_cost_fixed_billing() {
        let config = load_billing_tiers();
        let model = &config.models["gpt-image-1.5"];
        let tier = BillingTier {
            width: 1024,
            height: 1024,
            quality: "low".into(),
            tokens: 272,
            price_usd: Some(0.009),
        };
        let cost = calculate_cost(model, &tier);
        assert!((cost - 0.009).abs() < 0.0001);
    }

    #[test]
    fn test_calculate_token_cost() {
        let config = load_billing_tiers();
        let model = &config.models["gpt-image-2"];
        let cost = calculate_token_cost(model, 1000);
        // 1000 * 30.0 / 1_000_000 = 0.03
        assert!((cost - 0.03).abs() < 0.001);
    }
}
