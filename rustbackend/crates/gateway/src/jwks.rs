//! JWKS (JSON Web Key Set) client for AAD token verification.
//!
//! Fetches signing keys from Microsoft's OpenID Connect discovery endpoint
//! and caches them with periodic refresh.

use jsonwebtoken::DecodingKey;
use serde::Deserialize;
use std::collections::HashMap;
use std::sync::Arc;
use tokio::sync::RwLock;
use tracing::{info, warn};

/// Cached JWKS key store.
pub struct JwksKeyStore {
    keys: Arc<RwLock<HashMap<String, DecodingKey>>>,
    jwks_uri: String,
}

#[derive(Debug, Deserialize)]
struct OpenIdConfig {
    jwks_uri: String,
}

#[derive(Debug, Deserialize)]
struct JwksResponse {
    keys: Vec<JwkKey>,
}

#[derive(Debug, Deserialize)]
#[allow(dead_code)]
struct JwkKey {
    kid: String,
    #[serde(default)]
    kty: String,
    #[serde(default)]
    n: String,
    #[serde(default)]
    e: String,
    #[serde(default)]
    x5c: Vec<String>,
}

impl JwksKeyStore {
    /// Create a new JWKS key store for the given AAD tenant.
    /// Fetches the OpenID configuration and initial keys.
    pub async fn new(tenant_id: &str) -> anyhow::Result<Self> {
        let discovery_url = format!(
            "https://login.microsoftonline.com/{}/v2.0/.well-known/openid-configuration",
            tenant_id
        );

        let client = reqwest::Client::new();

        // Fetch OpenID configuration to get jwks_uri
        let jwks_uri = match client.get(&discovery_url).send().await {
            Ok(resp) => {
                let config: OpenIdConfig = resp.json().await
                    .map_err(|e| anyhow::anyhow!("Failed to parse OpenID config: {e}"))?;
                config.jwks_uri
            }
            Err(e) => {
                warn!("Failed to fetch OpenID config: {e}, using default jwks_uri");
                format!("https://login.microsoftonline.com/{}/discovery/v2.0/keys", tenant_id)
            }
        };

        let store = Self {
            keys: Arc::new(RwLock::new(HashMap::new())),
            jwks_uri,
        };

        // Fetch initial keys (don't fail startup if this fails)
        if let Err(e) = store.refresh_keys().await {
            warn!("Initial JWKS fetch failed (will retry on demand): {e}");
        }

        Ok(store)
    }

    /// Refresh keys from the JWKS endpoint.
    pub async fn refresh_keys(&self) -> anyhow::Result<()> {
        let client = reqwest::Client::new();
        let resp = client.get(&self.jwks_uri).send().await
            .map_err(|e| anyhow::anyhow!("JWKS fetch failed: {e}"))?;

        let jwks: JwksResponse = resp.json().await
            .map_err(|e| anyhow::anyhow!("JWKS parse failed: {e}"))?;

        let mut new_keys = HashMap::new();
        for key in &jwks.keys {
            if key.kty != "RSA" || key.n.is_empty() || key.e.is_empty() {
                continue;
            }
            match DecodingKey::from_rsa_components(&key.n, &key.e) {
                Ok(decoding_key) => {
                    new_keys.insert(key.kid.clone(), decoding_key);
                }
                Err(e) => {
                    warn!(kid = %key.kid, "Failed to build decoding key from RSA components: {e}");
                }
            }
        }

        info!(key_count = new_keys.len(), "JWKS keys refreshed");
        *self.keys.write().await = new_keys;
        Ok(())
    }

    /// Get the decoding key for a given key ID.
    /// If the key is not found, tries to refresh keys once.
    pub async fn get_key(&self, kid: &str) -> Option<DecodingKey> {
        // Try cached keys first
        {
            let keys = self.keys.read().await;
            if let Some(key) = keys.get(kid) {
                return Some(key.clone());
            }
        }

        // Key not found — refresh and try again
        if let Err(e) = self.refresh_keys().await {
            warn!("JWKS refresh failed: {e}");
            return None;
        }

        let keys = self.keys.read().await;
        keys.get(kid).cloned()
    }
}
