use std::sync::Arc;

use tfp_core::{AiEndpoint, EndpointType};

use crate::azure_stt::AzureSttProvider;
use crate::azure_tts::AzureTtsProvider;
use crate::openai_chat::OpenAiChatProvider;
use crate::openai_image::OpenAiImageProvider;
use crate::openai_translation::OpenAiTranslationProvider;
use crate::openai_video::OpenAiVideoProvider;
use crate::azure_speech::AzureSpeechProvider;
use crate::openai_realtime::OpenAiRealtimeProvider;
use crate::registry::ProviderRegistry;

/// Register providers into the registry based on endpoint configurations.
///
/// Skips disabled endpoints. Registers all available provider implementations.
/// OpenAiRealtime providers (deferred to batch 3).
pub fn register_providers(registry: &mut ProviderRegistry, endpoints: &[AiEndpoint]) {
    for ep in endpoints.iter().filter(|e| e.enabled) {
        match ep.endpoint_type {
            EndpointType::AzureOpenAi
            | EndpointType::ApiManagementGateway
            | EndpointType::OpenAiCompatible
            | EndpointType::Custom => {
                registry.register_ai_completion(Arc::new(OpenAiChatProvider::new(ep.clone())));
                registry.register_image_gen(Arc::new(OpenAiImageProvider::new(ep.clone())));
                registry
                    .register_text_translation(Arc::new(OpenAiTranslationProvider::new(ep.clone())));
                registry.register_video_gen(Arc::new(OpenAiVideoProvider::new(ep.clone())));
                registry.register_realtime_speech(Arc::new(OpenAiRealtimeProvider::new(ep.clone())));
                tracing::info!(
                    "Registered AI+Image+Translation+Video+Realtime Provider: {} ({})",
                    ep.name,
                    ep.id
                );
            }
            EndpointType::AzureSpeech => {
                registry.register_stt(Arc::new(AzureSttProvider::new(ep.clone())));
                registry.register_tts(Arc::new(AzureTtsProvider::new(ep.clone())));
                registry.register_realtime_speech(Arc::new(AzureSpeechProvider::new(ep.clone())));
                tracing::info!("Registered STT+TTS+Realtime Provider: {} ({})", ep.name, ep.id);
            }
            _ => {
                tracing::debug!(
                    "Endpoint {} ({:?}) has no provider implementation yet",
                    ep.name,
                    ep.endpoint_type
                );
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::traits::ProviderCapability;

    fn make_endpoint(id: &str, name: &str, ep_type: EndpointType, enabled: bool) -> AiEndpoint {
        AiEndpoint {
            id: id.into(),
            name: name.into(),
            endpoint_type: ep_type,
            url: "https://example.com".into(),
            api_key: "key".into(),
            enabled,
            speech_subscription_key: "speech-key".into(),
            speech_region: "eastus".into(),
            ..AiEndpoint::default()
        }
    }

    #[test]
    fn test_register_azure_openai() {
        let mut reg = ProviderRegistry::new();
        let ep = make_endpoint("ep1", "Azure EP", EndpointType::AzureOpenAi, true);
        register_providers(&mut reg, &[ep]);

        assert!(reg.get_ai_completion("ep1").is_some());
        assert!(reg.get_image_gen("ep1").is_some());
        assert!(reg.get_text_translation("ep1").is_some());
        assert!(reg.get_video_gen("ep1").is_some());

        let providers = reg.list_providers();
        assert_eq!(providers.len(), 1);
        let caps = &providers[0].capabilities;
        assert!(caps.contains(&ProviderCapability::AiCompletion));
        assert!(caps.contains(&ProviderCapability::ImageGeneration));
        assert!(caps.contains(&ProviderCapability::TextTranslation));
        assert!(caps.contains(&ProviderCapability::VideoGeneration));
    }

    #[test]
    fn test_register_azure_speech() {
        let mut reg = ProviderRegistry::new();
        let ep = make_endpoint("sp1", "Speech EP", EndpointType::AzureSpeech, true);
        register_providers(&mut reg, &[ep]);

        assert!(reg.get_stt("sp1").is_some());
        assert!(reg.get_tts("sp1").is_some());
        assert!(reg.get_ai_completion("sp1").is_none());
    }

    #[test]
    fn test_skip_disabled() {
        let mut reg = ProviderRegistry::new();
        let ep = make_endpoint("ep-off", "Disabled", EndpointType::AzureOpenAi, false);
        register_providers(&mut reg, &[ep]);

        assert!(reg.get_ai_completion("ep-off").is_none());
        assert!(reg.list_providers().is_empty());
    }

    #[test]
    fn test_register_openai_compatible() {
        let mut reg = ProviderRegistry::new();
        let ep = make_endpoint("oai1", "OAI EP", EndpointType::OpenAiCompatible, true);
        register_providers(&mut reg, &[ep]);

        assert!(reg.get_ai_completion("oai1").is_some());
        assert!(reg.get_image_gen("oai1").is_some());
        assert!(reg.get_text_translation("oai1").is_some());
        assert!(reg.get_video_gen("oai1").is_some());
    }

    #[test]
    fn test_register_multiple() {
        let mut reg = ProviderRegistry::new();
        let eps = vec![
            make_endpoint("ep1", "Azure", EndpointType::AzureOpenAi, true),
            make_endpoint("sp1", "Speech", EndpointType::AzureSpeech, true),
            make_endpoint("ep2", "Disabled", EndpointType::OpenAiCompatible, false),
        ];
        register_providers(&mut reg, &eps);

        assert!(reg.get_ai_completion("ep1").is_some());
        assert!(reg.get_stt("sp1").is_some());
        assert!(reg.get_ai_completion("ep2").is_none());
        assert_eq!(reg.list_providers().len(), 2);
    }
}
