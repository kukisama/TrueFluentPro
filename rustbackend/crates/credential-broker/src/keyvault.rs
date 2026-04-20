//! Azure Key Vault secret client.
//!
//! Fetches secrets from Azure Key Vault using a managed identity or service principal.
//!
//! Authentication flow:
//!   1. Try Managed Identity (IMDS endpoint) — works on Azure VMs, App Service, AKS, etc.
//!   2. Fall back to service principal (client_id + client_secret + tenant_id env vars)
//!
//! API reference:
//!   https://learn.microsoft.com/en-us/rest/api/keyvault/secrets/get-secret/get-secret

use reqwest::Client;
use secrecy::{ExposeSecret, SecretString};
use std::sync::Arc;
use tokio::sync::RwLock;
use tracing::{debug, warn};

/// Azure Key Vault REST API version.
const KV_API_VERSION: &str = "7.4";

/// Cached AAD access token.
struct CachedToken {
    token: String,
    expires_at: chrono::DateTime<chrono::Utc>,
}

/// Azure Key Vault secret client with token caching.
pub struct KeyVaultClient {
    vault_url: String,
    http: Client,
    token_cache: Arc<RwLock<Option<CachedToken>>>,
}

impl KeyVaultClient {
    /// Create a new Key Vault client for the given vault URL.
    ///
    /// `vault_url` — e.g. `https://my-vault.vault.azure.net`
    pub fn new(vault_url: &str) -> Self {
        Self {
            vault_url: vault_url.trim_end_matches('/').to_string(),
            http: Client::new(),
            token_cache: Arc::new(RwLock::new(None)),
        }
    }

    /// Get a secret by name from Key Vault.
    pub async fn get_secret(&self, name: &str) -> anyhow::Result<Option<SecretString>> {
        let token = self.get_access_token().await?;

        let url = format!(
            "{}/secrets/{}?api-version={}",
            self.vault_url, name, KV_API_VERSION
        );

        debug!(url = %url, "Key Vault GET secret");

        let resp = self.http
            .get(&url)
            .header("Authorization", format!("Bearer {token}"))
            .send()
            .await
            .map_err(|e| anyhow::anyhow!("Key Vault request failed: {e}"))?;

        if resp.status() == reqwest::StatusCode::NOT_FOUND {
            return Ok(None);
        }

        if !resp.status().is_success() {
            let status = resp.status();
            let body = resp.text().await.unwrap_or_default();
            return Err(anyhow::anyhow!("Key Vault HTTP {status}: {body}"));
        }

        let body: serde_json::Value = resp.json().await
            .map_err(|e| anyhow::anyhow!("Key Vault response parse error: {e}"))?;

        let value = body["value"]
            .as_str()
            .map(|s| SecretString::from(s.to_string()));

        Ok(value)
    }

    /// Acquire an AAD access token for Key Vault, with caching.
    async fn get_access_token(&self) -> anyhow::Result<String> {
        // Check cache
        {
            let cache = self.token_cache.read().await;
            if let Some(cached) = cache.as_ref() {
                if cached.expires_at > chrono::Utc::now() + chrono::Duration::minutes(2) {
                    return Ok(cached.token.clone());
                }
            }
        }

        // Acquire new token
        let token_response = self.acquire_token().await?;
        let expires_in = token_response.expires_in.unwrap_or(3600);
        let cached = CachedToken {
            token: token_response.access_token.clone(),
            expires_at: chrono::Utc::now() + chrono::Duration::seconds(expires_in),
        };

        let mut cache = self.token_cache.write().await;
        *cache = Some(cached);

        Ok(token_response.access_token)
    }

    /// Acquire token — try managed identity first, then service principal.
    async fn acquire_token(&self) -> anyhow::Result<TokenResponse> {
        // Strategy 1: Managed Identity (Azure IMDS)
        match self.acquire_managed_identity_token().await {
            Ok(token) => {
                debug!("Key Vault auth: using managed identity");
                return Ok(token);
            }
            Err(e) => {
                debug!("Managed identity not available: {e}");
            }
        }

        // Strategy 2: Service principal (env vars)
        let client_id = std::env::var("AZURE_CLIENT_ID")
            .map_err(|_| anyhow::anyhow!("AZURE_CLIENT_ID not set"))?;
        let client_secret = std::env::var("AZURE_CLIENT_SECRET")
            .map_err(|_| anyhow::anyhow!("AZURE_CLIENT_SECRET not set"))?;
        let tenant_id = std::env::var("AZURE_TENANT_ID")
            .map_err(|_| anyhow::anyhow!("AZURE_TENANT_ID not set"))?;

        debug!("Key Vault auth: using service principal (tenant={tenant_id})");

        let url = format!(
            "https://login.microsoftonline.com/{tenant_id}/oauth2/v2.0/token"
        );

        let resp = self.http
            .post(&url)
            .form(&[
                ("grant_type", "client_credentials"),
                ("client_id", &client_id),
                ("client_secret", &client_secret),
                ("scope", "https://vault.azure.net/.default"),
            ])
            .send()
            .await
            .map_err(|e| anyhow::anyhow!("Token request failed: {e}"))?;

        if !resp.status().is_success() {
            let body = resp.text().await.unwrap_or_default();
            return Err(anyhow::anyhow!("Service principal auth failed: {body}"));
        }

        resp.json::<TokenResponse>().await
            .map_err(|e| anyhow::anyhow!("Token parse error: {e}"))
    }

    /// Try to acquire token via Azure Managed Identity (IMDS endpoint).
    async fn acquire_managed_identity_token(&self) -> anyhow::Result<TokenResponse> {
        let url = "http://169.254.169.254/metadata/identity/oauth2/token";

        let resp = self.http
            .get(url)
            .header("Metadata", "true")
            .query(&[
                ("api-version", "2018-02-01"),
                ("resource", "https://vault.azure.net"),
            ])
            .timeout(std::time::Duration::from_secs(2))
            .send()
            .await
            .map_err(|e| anyhow::anyhow!("IMDS not reachable: {e}"))?;

        if !resp.status().is_success() {
            return Err(anyhow::anyhow!("IMDS returned {}", resp.status()));
        }

        resp.json::<TokenResponse>().await
            .map_err(|e| anyhow::anyhow!("IMDS token parse error: {e}"))
    }
}

#[derive(serde::Deserialize)]
struct TokenResponse {
    access_token: String,
    #[serde(default)]
    expires_in: Option<i64>,
}
