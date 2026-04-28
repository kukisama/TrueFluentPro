use std::collections::HashMap;
use std::sync::Arc;

use crate::traits::*;

#[derive(Debug, Clone, serde::Serialize, serde::Deserialize)]
pub struct ProviderInfo {
    pub id: String,
    pub name: String,
    pub capabilities: Vec<ProviderCapability>,
}

pub struct ProviderRegistry {
    text_translation: HashMap<String, Arc<dyn TextTranslationSlot>>,
    realtime_speech: HashMap<String, Arc<dyn RealtimeSpeechSlot>>,
    stt: HashMap<String, Arc<dyn SpeechToTextSlot>>,
    tts: HashMap<String, Arc<dyn TextToSpeechSlot>>,
    ai_completion: HashMap<String, Arc<dyn AiCompletionSlot>>,
    image_gen: HashMap<String, Arc<dyn ImageGenSlot>>,
    video_gen: HashMap<String, Arc<dyn VideoGenSlot>>,
}

impl ProviderRegistry {
    pub fn new() -> Self {
        Self {
            text_translation: HashMap::new(),
            realtime_speech: HashMap::new(),
            stt: HashMap::new(),
            tts: HashMap::new(),
            ai_completion: HashMap::new(),
            image_gen: HashMap::new(),
            video_gen: HashMap::new(),
        }
    }

    // ── Register ──

    pub fn register_text_translation(&mut self, provider: Arc<dyn TextTranslationSlot>) {
        self.text_translation
            .insert(provider.id().to_string(), provider);
    }

    pub fn register_realtime_speech(&mut self, provider: Arc<dyn RealtimeSpeechSlot>) {
        self.realtime_speech
            .insert(provider.id().to_string(), provider);
    }

    pub fn register_stt(&mut self, provider: Arc<dyn SpeechToTextSlot>) {
        self.stt.insert(provider.id().to_string(), provider);
    }

    pub fn register_tts(&mut self, provider: Arc<dyn TextToSpeechSlot>) {
        self.tts.insert(provider.id().to_string(), provider);
    }

    pub fn register_ai_completion(&mut self, provider: Arc<dyn AiCompletionSlot>) {
        self.ai_completion
            .insert(provider.id().to_string(), provider);
    }

    pub fn register_image_gen(&mut self, provider: Arc<dyn ImageGenSlot>) {
        self.image_gen.insert(provider.id().to_string(), provider);
    }

    pub fn register_video_gen(&mut self, provider: Arc<dyn VideoGenSlot>) {
        self.video_gen.insert(provider.id().to_string(), provider);
    }

    // ── Query ──

    pub fn get_text_translation(&self, id: &str) -> Option<Arc<dyn TextTranslationSlot>> {
        self.text_translation.get(id).cloned()
    }

    pub fn get_realtime_speech(&self, id: &str) -> Option<Arc<dyn RealtimeSpeechSlot>> {
        self.realtime_speech.get(id).cloned()
    }

    pub fn get_stt(&self, id: &str) -> Option<Arc<dyn SpeechToTextSlot>> {
        self.stt.get(id).cloned()
    }

    pub fn get_tts(&self, id: &str) -> Option<Arc<dyn TextToSpeechSlot>> {
        self.tts.get(id).cloned()
    }

    pub fn get_ai_completion(&self, id: &str) -> Option<Arc<dyn AiCompletionSlot>> {
        self.ai_completion.get(id).cloned()
    }

    pub fn get_image_gen(&self, id: &str) -> Option<Arc<dyn ImageGenSlot>> {
        self.image_gen.get(id).cloned()
    }

    pub fn get_video_gen(&self, id: &str) -> Option<Arc<dyn VideoGenSlot>> {
        self.video_gen.get(id).cloned()
    }

    // ── Clear ──

    pub fn clear(&mut self) {
        self.text_translation.clear();
        self.realtime_speech.clear();
        self.stt.clear();
        self.tts.clear();
        self.ai_completion.clear();
        self.image_gen.clear();
        self.video_gen.clear();
    }

    // ── List ──

    pub fn list_providers(&self) -> Vec<ProviderInfo> {
        let mut result: Vec<ProviderInfo> = Vec::new();
        let mut merge = |id: &str, name: &str, caps: Vec<ProviderCapability>| {
            if let Some(existing) = result.iter_mut().find(|r| r.id == id) {
                for cap in caps {
                    if !existing.capabilities.contains(&cap) {
                        existing.capabilities.push(cap);
                    }
                }
            } else {
                result.push(ProviderInfo {
                    id: id.to_string(),
                    name: name.to_string(),
                    capabilities: caps,
                });
            }
        };

        for p in self.text_translation.values() {
            merge(p.id(), p.display_name(), p.capabilities());
        }
        for p in self.realtime_speech.values() {
            merge(p.id(), p.display_name(), p.capabilities());
        }
        for p in self.stt.values() {
            merge(p.id(), p.display_name(), p.capabilities());
        }
        for p in self.tts.values() {
            merge(p.id(), p.display_name(), p.capabilities());
        }
        for p in self.ai_completion.values() {
            merge(p.id(), p.display_name(), p.capabilities());
        }
        for p in self.image_gen.values() {
            merge(p.id(), p.display_name(), p.capabilities());
        }
        for p in self.video_gen.values() {
            merge(p.id(), p.display_name(), p.capabilities());
        }
        result
    }
}

impl Default for ProviderRegistry {
    fn default() -> Self {
        Self::new()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use async_trait::async_trait;
    use tfp_core::{CompletionRequest, CompletionResponse, ProviderError};
    use tokio::sync::mpsc;

    struct MockProvider {
        id: String,
    }

    impl ProviderMeta for MockProvider {
        fn id(&self) -> &str {
            &self.id
        }
        fn display_name(&self) -> &str {
            "Mock"
        }
        fn capabilities(&self) -> Vec<ProviderCapability> {
            vec![ProviderCapability::AiCompletion]
        }
    }

    #[async_trait]
    impl AiCompletionSlot for MockProvider {
        async fn complete(
            &self,
            _request: &CompletionRequest,
        ) -> Result<CompletionResponse, ProviderError> {
            Ok(CompletionResponse {
                content: "mock response".into(),
                model: "mock".into(),
                usage: None,
            })
        }

        async fn complete_stream(
            &self,
            _request: &CompletionRequest,
        ) -> Result<mpsc::UnboundedReceiver<Result<StreamChunk, ProviderError>>, ProviderError>
        {
            let (tx, rx) = mpsc::unbounded_channel();
            tx.send(Ok(StreamChunk::Token("hi".into()))).ok();
            Ok(rx)
        }
    }

    struct MockImageProvider {
        id: String,
    }

    impl ProviderMeta for MockImageProvider {
        fn id(&self) -> &str {
            &self.id
        }
        fn display_name(&self) -> &str {
            "Mock"
        }
        fn capabilities(&self) -> Vec<ProviderCapability> {
            vec![ProviderCapability::ImageGeneration]
        }
    }

    #[async_trait]
    impl ImageGenSlot for MockImageProvider {
        async fn generate(
            &self,
            _request: &tfp_core::ImageGenRequest,
        ) -> Result<Vec<tfp_core::ImageGenResult>, ProviderError> {
            Ok(vec![])
        }
    }

    #[test]
    fn test_register_and_get() {
        let mut reg = ProviderRegistry::new();
        let provider = Arc::new(MockProvider {
            id: "mock-1".into(),
        });
        reg.register_ai_completion(provider);
        assert!(reg.get_ai_completion("mock-1").is_some());
    }

    #[test]
    fn test_get_missing() {
        let reg = ProviderRegistry::new();
        assert!(reg.get_ai_completion("nope").is_none());
    }

    #[test]
    fn test_list_providers_merges_capabilities() {
        let mut reg = ProviderRegistry::new();
        let p1 = Arc::new(MockProvider {
            id: "multi".into(),
        });
        let p2 = Arc::new(MockImageProvider {
            id: "multi".into(),
        });
        reg.register_ai_completion(p1);
        reg.register_image_gen(p2);

        let list = reg.list_providers();
        assert_eq!(list.len(), 1);
        assert_eq!(list[0].id, "multi");
        assert!(list[0]
            .capabilities
            .contains(&ProviderCapability::AiCompletion));
        assert!(list[0]
            .capabilities
            .contains(&ProviderCapability::ImageGeneration));
    }

    #[test]
    fn test_clear() {
        let mut reg = ProviderRegistry::new();
        let provider = Arc::new(MockProvider {
            id: "mock-1".into(),
        });
        reg.register_ai_completion(provider);
        assert!(reg.get_ai_completion("mock-1").is_some());

        reg.clear();
        assert!(reg.get_ai_completion("mock-1").is_none());
    }
}
