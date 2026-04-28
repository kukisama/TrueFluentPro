use async_trait::async_trait;
use std::collections::HashMap;
use std::sync::Arc;
use tokio::sync::mpsc;

use crate::models::*;

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  Provider 插槽架构
//
//  每个插槽定义一种能力接口。任何外部服务（微软 Speech、
//  Google、DeepL…）只需实现对应 trait 即可接入。
//  ProviderRegistry 统一管理所有已注册 provider。
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

/// 所有 Provider 共享的基础特征
pub trait ProviderMeta: Send + Sync {
    /// 唯一标识（如 "azure-speech", "deepl", "google-translate"）
    fn id(&self) -> &str;
    /// 显示名称
    fn display_name(&self) -> &str;
    /// 支持的能力列表
    fn capabilities(&self) -> Vec<ProviderCapability>;
}

#[derive(Debug, Clone, PartialEq, serde::Serialize, serde::Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ProviderCapability {
    TextTranslation,
    RealtimeSpeechTranslation,
    SpeechToText,
    TextToSpeech,
    AiCompletion,
    ImageGeneration,
    VideoGeneration,
}

// ─── 插槽 1: 文本翻译 ───

#[async_trait]
#[allow(dead_code)]
pub trait TextTranslationSlot: ProviderMeta {
    async fn translate(
        &self,
        request: &TranslateRequest,
    ) -> Result<TranslateResponse, ProviderError>;

    async fn detect_language(&self, text: &str) -> Result<String, ProviderError>;

    fn supported_languages(&self) -> Vec<LanguageInfo>;
}

// ─── 插槽 2: 实时语音翻译（Speech SDK / 其他流式服务 ） ───

#[async_trait]
pub trait RealtimeSpeechSlot: ProviderMeta {
    /// 创建一个实时翻译会话。
    /// 返回 (event_receiver, session_handle)。
    /// 前端通过 Tauri Event 接收 RealtimeEvent。
    async fn create_session(
        &self,
        config: &RealtimeSessionConfig,
    ) -> Result<(mpsc::UnboundedReceiver<RealtimeEvent>, Box<dyn RealtimeSessionHandle>), ProviderError>;
}

/// 实时会话控制接口
#[async_trait]
#[allow(dead_code)]
pub trait RealtimeSessionHandle: Send + Sync {
    /// 推送 PCM 音频数据（16kHz, 16bit, mono）
    async fn push_audio(&self, pcm_data: &[u8]) -> Result<(), ProviderError>;
    /// 结束会话
    async fn stop(&self) -> Result<(), ProviderError>;
}

// ─── 插槽 3: 语音识别（STT） ───

#[async_trait]
pub trait SpeechToTextSlot: ProviderMeta {
    async fn transcribe(
        &self,
        audio_data: &[u8],
        lang: &str,
    ) -> Result<Vec<TranscriptSegment>, ProviderError>;
}

#[derive(Debug, Clone, serde::Serialize, serde::Deserialize)]
pub struct TranscriptSegment {
    pub text: String,
    pub start_ms: u64,
    pub end_ms: u64,
    pub confidence: f64,
    pub speaker: Option<String>,
}

// ─── 插槽 4: 语音合成（TTS） ───

#[async_trait]
#[allow(dead_code)]
pub trait TextToSpeechSlot: ProviderMeta {
    async fn synthesize(
        &self,
        text: &str,
        voice: &str,
        format: &str,
    ) -> Result<Vec<u8>, ProviderError>;

    /// P3-7: 多发言人合成 — speakers: [(角色标签, voice_name), ...]
    /// 默认实现: 忽略 speakers，使用第一个 voice 合成全文
    async fn synthesize_multi_speaker(
        &self,
        text: &str,
        speakers: &[(String, String)],
        format: &str,
    ) -> Result<Vec<u8>, ProviderError> {
        let voice = speakers.first().map(|(_, v)| v.as_str()).unwrap_or("en-US-JennyNeural");
        self.synthesize(text, voice, format).await
    }

    async fn list_voices(&self, locale: &str) -> Result<Vec<VoiceInfo>, ProviderError>;
}

#[derive(Debug, Clone, serde::Serialize, serde::Deserialize)]
#[allow(dead_code)]
pub struct VoiceInfo {
    pub id: String,
    pub name: String,
    pub locale: String,
    pub gender: String,
}

// ─── 插槽 5: AI 补全 ───

#[async_trait]
pub trait AiCompletionSlot: ProviderMeta {
    async fn complete(
        &self,
        request: &CompletionRequest,
    ) -> Result<CompletionResponse, ProviderError>;

    /// 流式补全：通过 channel 逐 token 推送
    async fn complete_stream(
        &self,
        request: &CompletionRequest,
    ) -> Result<mpsc::UnboundedReceiver<Result<StreamChunk, ProviderError>>, ProviderError>;
}

/// 流式补全事件块
#[derive(Debug, Clone)]
pub enum StreamChunk {
    /// 正文 token
    Token(String),
    /// 推理过程 token（reasoning_content）
    Reasoning(String),
    /// 最终 usage 统计
    Usage { prompt_tokens: u32, completion_tokens: u32 },
}

// ─── 插槽 6: 图片生成 ───

#[async_trait]
pub trait ImageGenSlot: ProviderMeta {
    async fn generate(
        &self,
        request: &ImageGenRequest,
    ) -> Result<Vec<ImageGenResult>, ProviderError>;
}

// ─── Provider 错误类型 ───

#[derive(Debug, thiserror::Error)]
#[allow(dead_code)]
pub enum ProviderError {
    #[error("network error: {0}")]
    Network(String),
    #[error("authentication failed: {0}")]
    Auth(String),
    #[error("rate limited: retry after {retry_after_ms}ms")]
    RateLimited { retry_after_ms: u64 },
    #[error("provider not configured: {0}")]
    NotConfigured(String),
    #[error("unsupported operation: {0}")]
    Unsupported(String),
    #[error("internal error: {0}")]
    Internal(String),
}

impl serde::Serialize for ProviderError {
    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: serde::ser::Serializer,
    {
        serializer.serialize_str(&self.to_string())
    }
}

// ─── Provider Registry：统一管理所有已注册 Provider ───

pub struct ProviderRegistry {
    text_translation: HashMap<String, Arc<dyn TextTranslationSlot>>,
    realtime_speech: HashMap<String, Arc<dyn RealtimeSpeechSlot>>,
    stt: HashMap<String, Arc<dyn SpeechToTextSlot>>,
    tts: HashMap<String, Arc<dyn TextToSpeechSlot>>,
    ai_completion: HashMap<String, Arc<dyn AiCompletionSlot>>,
    image_gen: HashMap<String, Arc<dyn ImageGenSlot>>,
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
        }
    }

    // ── 注册 ──

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

    // ── 清空 ──

    pub fn clear(&mut self) {
        self.text_translation.clear();
        self.realtime_speech.clear();
        self.stt.clear();
        self.tts.clear();
        self.ai_completion.clear();
        self.image_gen.clear();
    }

    // ── 查询 ──

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

    /// 列出所有已注册 provider 的元信息（B-06 修复: 合并全部 6 个 slot）
    pub fn list_providers(&self) -> Vec<ProviderInfo> {
        let mut result = Vec::new();
        let mut merge = |id: &str, name: &str, caps: Vec<ProviderCapability>| {
            if let Some(existing) = result.iter_mut().find(|r: &&mut ProviderInfo| r.id == id) {
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
        result
    }
}

#[derive(Debug, Clone, serde::Serialize, serde::Deserialize)]
pub struct ProviderInfo {
    pub id: String,
    pub name: String,
    pub capabilities: Vec<ProviderCapability>,
}
