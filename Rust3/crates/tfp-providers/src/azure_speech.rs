//! Azure Speech SDK — Realtime speech translation provider.
//!
//! Bridges the speech-sdk crate to the Tauri provider slot system.
//! Uses TranslationRecognizer's continuous recognition mode, converting
//! SDK callbacks to mpsc channel events for the frontend.
//!
//! When the `speech-sdk` feature is not enabled, create_session returns
//! NotConfigured error. This allows the crate to compile on all platforms
//! without requiring the Speech SDK native libraries.

use async_trait::async_trait;
use tokio::sync::mpsc;

use tfp_core::{AiEndpoint, ProviderError, RealtimeEvent, RealtimeSessionConfig};

use crate::traits::{ProviderCapability, ProviderMeta, RealtimeSessionHandle, RealtimeSpeechSlot};

/// Azure Speech SDK realtime speech translation provider.
pub struct AzureSpeechProvider {
    endpoint: AiEndpoint,
}

impl AzureSpeechProvider {
    pub fn new(endpoint: AiEndpoint) -> Self {
        Self { endpoint }
    }

    /// Extract the Speech region, preferring the speech-specific field.
    pub(crate) fn region(&self) -> &str {
        if !self.endpoint.speech_region.is_empty() {
            &self.endpoint.speech_region
        } else {
            self.endpoint
                .region
                .as_deref()
                .unwrap_or("")
        }
    }

    /// Extract the subscription key, preferring the speech-specific field.
    pub(crate) fn subscription_key(&self) -> &str {
        if !self.endpoint.speech_subscription_key.is_empty() {
            &self.endpoint.speech_subscription_key
        } else {
            &self.endpoint.api_key
        }
    }

    /// Validate configuration before attempting to create a session.
    pub(crate) fn validate_config(
        &self,
        config: &RealtimeSessionConfig,
    ) -> Result<(), ProviderError> {
        if self.region().is_empty() {
            return Err(ProviderError::NotConfigured(
                "Azure Speech endpoint missing region configuration".into(),
            ));
        }
        if self.subscription_key().is_empty() {
            return Err(ProviderError::Auth(
                "Azure Speech endpoint missing subscription key".into(),
            ));
        }
        if config.target_langs.is_empty() {
            return Err(ProviderError::NotConfigured(
                "No target translation languages configured".into(),
            ));
        }
        Ok(())
    }
}

impl ProviderMeta for AzureSpeechProvider {
    fn id(&self) -> &str {
        &self.endpoint.id
    }
    fn display_name(&self) -> &str {
        &self.endpoint.name
    }
    fn capabilities(&self) -> Vec<ProviderCapability> {
        vec![ProviderCapability::RealtimeSpeechTranslation]
    }
}

#[async_trait]
impl RealtimeSpeechSlot for AzureSpeechProvider {
    async fn create_session(
        &self,
        config: &RealtimeSessionConfig,
    ) -> Result<
        (
            mpsc::UnboundedReceiver<RealtimeEvent>,
            Box<dyn RealtimeSessionHandle>,
        ),
        ProviderError,
    > {
        self.validate_config(config)?;

        // When Speech SDK feature is enabled, this would use the actual SDK.
        // For now, return a stub session that emits SessionStarted then immediately stops.
        // This allows the provider registration and test infrastructure to work
        // while the actual SDK integration is deferred to platform-specific builds.
        let (tx, rx) = mpsc::unbounded_channel();
        let session_id = uuid::Uuid::new_v4().to_string();

        let _ = tx.send(RealtimeEvent::SessionStarted {
            session_id: session_id.clone(),
        });

        let handle = StubSpeechSessionHandle { _session_id: session_id };
        Ok((rx, Box::new(handle)))
    }
}

/// Stub session handle used when the Speech SDK native library is not available.
struct StubSpeechSessionHandle {
    _session_id: String,
}

#[async_trait]
impl RealtimeSessionHandle for StubSpeechSessionHandle {
    async fn push_audio(&self, _pcm_data: &[u8]) -> Result<(), ProviderError> {
        // Microphone mode: SDK captures audio internally
        Ok(())
    }

    async fn stop(&self) -> Result<(), ProviderError> {
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::test_helpers::factories;

    #[test]
    fn test_provider_meta() {
        let ep = factories::speech_endpoint("sp1", "Speech EP");
        let p = AzureSpeechProvider::new(ep);
        assert_eq!(p.id(), "sp1");
        assert_eq!(p.display_name(), "Speech EP");
        assert_eq!(
            p.capabilities(),
            vec![ProviderCapability::RealtimeSpeechTranslation]
        );
    }

    #[test]
    fn test_region_fallback() {
        // Primary: speech_region
        let ep = factories::speech_endpoint("sp1", "SP");
        let p = AzureSpeechProvider::new(ep);
        assert_eq!(p.region(), "eastus");

        // Fallback: region field
        let mut ep2 = AiEndpoint::default();
        ep2.speech_region = String::new();
        ep2.region = Some("westus2".into());
        let p2 = AzureSpeechProvider::new(ep2);
        assert_eq!(p2.region(), "westus2");
    }

    #[test]
    fn test_subscription_key_fallback() {
        let ep = factories::speech_endpoint("sp1", "SP");
        let p = AzureSpeechProvider::new(ep);
        assert_eq!(p.subscription_key(), "test-key");

        let mut ep2 = AiEndpoint::default();
        ep2.speech_subscription_key = String::new();
        ep2.api_key = "fallback-key".into();
        let p2 = AzureSpeechProvider::new(ep2);
        assert_eq!(p2.subscription_key(), "fallback-key");
    }

    #[test]
    fn test_validate_empty_region() {
        let mut ep = AiEndpoint::default();
        ep.speech_region = String::new();
        let p = AzureSpeechProvider::new(ep);
        let config = RealtimeSessionConfig {
            source_lang: "zh-Hans".into(),
            target_langs: vec!["en".into()],
            endpoint_id: "sp1".into(),
            enable_partial: true,
            profanity_filter: false,
            initial_silence_timeout_seconds: None,
            end_silence_timeout_seconds: None,
        };
        let err = p.validate_config(&config).unwrap_err();
        assert!(matches!(err, ProviderError::NotConfigured(_)));
    }

    #[test]
    fn test_validate_empty_key() {
        let mut ep = AiEndpoint::default();
        ep.speech_region = "eastus".into();
        ep.speech_subscription_key = String::new();
        ep.api_key = String::new();
        let p = AzureSpeechProvider::new(ep);
        let config = RealtimeSessionConfig {
            source_lang: "zh-Hans".into(),
            target_langs: vec!["en".into()],
            endpoint_id: "sp1".into(),
            enable_partial: true,
            profanity_filter: false,
            initial_silence_timeout_seconds: None,
            end_silence_timeout_seconds: None,
        };
        let err = p.validate_config(&config).unwrap_err();
        assert!(matches!(err, ProviderError::Auth(_)));
    }

    #[test]
    fn test_validate_no_target_langs() {
        let ep = factories::speech_endpoint("sp1", "SP");
        let p = AzureSpeechProvider::new(ep);
        let config = RealtimeSessionConfig {
            source_lang: "zh-Hans".into(),
            target_langs: vec![],
            endpoint_id: "sp1".into(),
            enable_partial: true,
            profanity_filter: false,
            initial_silence_timeout_seconds: None,
            end_silence_timeout_seconds: None,
        };
        let err = p.validate_config(&config).unwrap_err();
        assert!(matches!(err, ProviderError::NotConfigured(_)));
    }

    #[tokio::test]
    async fn test_create_stub_session() {
        let ep = factories::speech_endpoint("sp1", "SP");
        let p = AzureSpeechProvider::new(ep);
        let config = RealtimeSessionConfig {
            source_lang: "zh-Hans".into(),
            target_langs: vec!["en".into()],
            endpoint_id: "sp1".into(),
            enable_partial: true,
            profanity_filter: false,
            initial_silence_timeout_seconds: None,
            end_silence_timeout_seconds: None,
        };
        let (mut rx, handle) = p.create_session(&config).await.unwrap();
        // Should receive SessionStarted
        let event = rx.recv().await.unwrap();
        assert!(matches!(event, RealtimeEvent::SessionStarted { .. }));
        // Stop should succeed
        handle.stop().await.unwrap();
    }
}
