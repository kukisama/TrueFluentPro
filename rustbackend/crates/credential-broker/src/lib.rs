//! Credential broker — three-level secret chain: env → DB(AES-256-GCM) → KeyVault.

use secrecy::{ExposeSecret, SecretString};
use storage::StorageBackend;
use std::sync::Arc;
use aes_gcm::{Aes256Gcm, KeyInit, Nonce};
use aes_gcm::aead::Aead;
use tracing::warn;

/// Credential broker that chains multiple secret sources.
pub struct CredentialBroker {
    storage: Arc<dyn StorageBackend>,
    master_key: Option<[u8; 32]>,
}

impl CredentialBroker {
    pub fn new(storage: Arc<dyn StorageBackend>, master_key_base64: &str) -> Self {
        let master_key = if master_key_base64.is_empty() {
            warn!("No master key configured — DB credential decryption disabled");
            None
        } else {
            use base64::Engine;
            base64::engine::general_purpose::STANDARD
                .decode(master_key_base64)
                .ok()
                .and_then(|bytes| {
                    let arr: [u8; 32] = bytes.try_into().ok()?;
                    Some(arr)
                })
        };
        Self { storage, master_key }
    }

    /// Get a secret by key. Checks: 1) env var  2) DB (AES decrypted)  3) returns None.
    pub async fn get(&self, provider_id: &str, key: &str) -> anyhow::Result<Option<SecretString>> {
        // Level 1: environment variable (e.g. AZURE_OPENAI_API_KEY)
        let env_key = format!("{}_{}", provider_id, key).to_uppercase().replace('.', "_");
        if let Ok(val) = std::env::var(&env_key) {
            if !val.is_empty() {
                return Ok(Some(SecretString::from(val)));
            }
        }

        // Level 2: DB encrypted credential
        if let Some(master_key) = &self.master_key {
            if let Some(cred) = self.storage.get_credential(provider_id, key).await? {
                let cipher = Aes256Gcm::new_from_slice(master_key)
                    .map_err(|e| anyhow::anyhow!("invalid master key: {e}"))?;
                let nonce = Nonce::from_slice(&cred.nonce);
                let plaintext = cipher
                    .decrypt(nonce, cred.encrypted_value.as_ref())
                    .map_err(|e| anyhow::anyhow!("credential decryption failed: {e}"))?;
                let secret = String::from_utf8(plaintext)
                    .map_err(|e| anyhow::anyhow!("credential not valid UTF-8: {e}"))?;
                return Ok(Some(SecretString::from(secret)));
            }
        }

        // Level 3: KeyVault (future)
        Ok(None)
    }

    /// Encrypt and store a credential in DB.
    pub async fn store(&self, provider_id: &str, key: &str, value: &str) -> anyhow::Result<()> {
        let master_key = self.master_key
            .ok_or_else(|| anyhow::anyhow!("master key not configured — cannot encrypt credentials"))?;

        let cipher = Aes256Gcm::new_from_slice(&master_key)
            .map_err(|e| anyhow::anyhow!("invalid master key: {e}"))?;

        let mut nonce_bytes = [0u8; 12];
        use rand::RngCore;
        rand::thread_rng().fill_bytes(&mut nonce_bytes);
        let nonce = Nonce::from_slice(&nonce_bytes);

        let encrypted = cipher
            .encrypt(nonce, value.as_bytes())
            .map_err(|e| anyhow::anyhow!("encryption failed: {e}"))?;

        let cred = storage::EncryptedCredential {
            provider_id: provider_id.to_string(),
            credential_key: key.to_string(),
            encrypted_value: encrypted,
            nonce: nonce_bytes.to_vec(),
            version: 1,
            is_active: true,
        };

        self.storage.upsert_credential(&cred).await
            .map_err(|e| anyhow::anyhow!("failed to store credential: {e}"))
    }
}

#[cfg(test)]
mod tests {
    #[tokio::test]
    async fn test_env_var_resolution() {
        // Verify the env key naming convention used by the broker
        let env_key = format!("{}_{}", "test_provider", "api_key").to_uppercase().replace('.', "_");
        assert_eq!(env_key, "TEST_PROVIDER_API_KEY");

        // Verify dot replacement
        let env_key2 = format!("{}_{}", "azure.openai", "api_key").to_uppercase().replace('.', "_");
        assert_eq!(env_key2, "AZURE_OPENAI_API_KEY");
    }
}
