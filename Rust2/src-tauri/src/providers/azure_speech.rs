//! Azure Speech SDK — 实时语音翻译 Provider
//!
//! 桥接 speech-sdk crate 到 Tauri Provider 插槽系统。
//! 使用 TranslationRecognizer 的连续识别模式，将 SDK 回调
//! 转换为 mpsc channel 事件流推送给前端。

use async_trait::async_trait;
use std::sync::Arc;
use tokio::sync::{mpsc, Mutex};

use crate::models::*;
use crate::providers::{
    ProviderCapability, ProviderError, ProviderMeta,
    RealtimeSessionHandle, RealtimeSpeechSlot,
};

use speech_sdk::audio::AudioConfig as SdkAudioConfig;
use speech_sdk::common::ResultReason;
use speech_sdk::speech::{SpeechTranslationConfig, TranslationRecognizer};

/// Azure Speech SDK 实时语音翻译 Provider
pub struct AzureSpeechProvider {
    endpoint: AiEndpoint,
}

impl AzureSpeechProvider {
    pub fn new(endpoint: AiEndpoint) -> Self {
        Self { endpoint }
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
        let ep = &self.endpoint;

        // 优先使用 Speech 专属字段，兼容旧配置回退到通用字段
        let region = if !ep.speech_region.is_empty() {
            ep.speech_region.clone()
        } else {
            ep.region.as_deref().unwrap_or("").to_string()
        };
        let api_key = if !ep.speech_subscription_key.is_empty() {
            ep.speech_subscription_key.clone()
        } else {
            ep.api_key.clone()
        };
        let source_lang = config.source_lang.clone();
        let target_langs = config.target_langs.clone();

        if region.is_empty() {
            return Err(ProviderError::NotConfigured(
                "Azure Speech 端点缺少区域 (Region) 配置".into(),
            ));
        }
        if api_key.is_empty() {
            return Err(ProviderError::Auth(
                "Azure Speech 端点缺少订阅密钥".into(),
            ));
        }
        if target_langs.is_empty() {
            return Err(ProviderError::NotConfigured(
                "未配置目标翻译语言".into(),
            ));
        }

        let (tx, rx) = mpsc::unbounded_channel::<RealtimeEvent>();

        // S-1: 提取超时设置传入 Speech SDK
        let initial_silence_timeout_seconds = config.initial_silence_timeout_seconds;
        let end_silence_timeout_seconds = config.end_silence_timeout_seconds;

        // Speech SDK 的 FFI 调用是阻塞的，在 spawn_blocking 中初始化
        let tx_clone = tx.clone();
        let handle = tokio::task::spawn_blocking(move || {
            create_recognizer_session(
                &api_key,
                &region,
                &source_lang,
                &target_langs,
                tx_clone,
                initial_silence_timeout_seconds,
                end_silence_timeout_seconds,
            )
        })
        .await
        .map_err(|e| ProviderError::Internal(format!("spawn_blocking 失败: {e}")))?
        .map_err(|e| ProviderError::Internal(format!("创建 Speech 会话失败: {e}")))?;

        // 启动连续识别
        handle.start().await?;

        Ok((rx, Box::new(handle)))
    }
}

/// 在阻塞线程中创建识别器并注册回调
fn create_recognizer_session(
    api_key: &str,
    region: &str,
    source_lang: &str,
    target_langs: &[String],
    tx: mpsc::UnboundedSender<RealtimeEvent>,
    initial_silence_timeout_seconds: Option<u32>,
    end_silence_timeout_seconds: Option<u32>,
) -> Result<SpeechSessionHandle, String> {
    // 创建翻译配置
    let mut speech_config = SpeechTranslationConfig::from_subscription(api_key, region)
        .map_err(|e| format!("创建 SpeechTranslationConfig 失败: {e}"))?;

    speech_config
        .set_speech_recognition_language(source_lang)
        .map_err(|e| format!("设置识别语言失败: {e}"))?;

    for lang in target_langs {
        speech_config
            .add_target_language(lang)
            .map_err(|e| format!("添加目标语言 {lang} 失败: {e}"))?;
    }

    // S-1: 将 RecognitionSettings 的超时参数透传到 Speech SDK
    if let Some(initial_secs) = initial_silence_timeout_seconds {
        let ms_str = (initial_secs as u64 * 1000).to_string();
        let _ = speech_config.set_property_by_name(
            "SpeechServiceConnection_InitialSilenceTimeoutMs",
            &ms_str,
        );
    }
    if let Some(end_secs) = end_silence_timeout_seconds {
        let ms_str = (end_secs as u64 * 1000).to_string();
        let _ = speech_config.set_property_by_name(
            "SpeechServiceConnection_EndSilenceTimeoutMs",
            &ms_str,
        );
    }

    // 创建音频输入（默认麦克风）
    let audio_config = SdkAudioConfig::from_default_microphone_input()
        .map_err(|e| format!("创建麦克风音频配置失败: {e}"))?;

    // 创建识别器
    let mut recognizer = TranslationRecognizer::from_config(&speech_config, &audio_config)
        .map_err(|e| format!("创建 TranslationRecognizer 失败: {e}"))?;

    // ── 注册回调 ──

    // SessionStarted
    let tx_started = tx.clone();
    recognizer
        .set_session_started_cb(move |event| {
            let _ = tx_started.send(RealtimeEvent::SessionStarted {
                session_id: event.session_id.clone(),
            });
        })
        .map_err(|e| format!("注册 session_started 回调失败: {e}"))?;

    // SessionStopped
    let tx_stopped = tx.clone();
    recognizer
        .set_session_stopped_cb(move |event| {
            let _ = tx_stopped.send(RealtimeEvent::SessionStopped {
                session_id: event.session_id.clone(),
            });
        })
        .map_err(|e| format!("注册 session_stopped 回调失败: {e}"))?;

    // Recognizing（中间结果）
    let tx_recognizing = tx.clone();
    recognizer
        .set_recognizing_cb(move |event| {
            let text = event.result.base.text.clone();
            let offset_ms = event.result.base.offset.as_millis() as u64;
            let translations = event.result.translations.clone();

            // 同时发送 Recognizing + Translated
            let _ = tx_recognizing.send(RealtimeEvent::Recognizing {
                text: text.clone(),
                offset_ms,
            });
            if !translations.is_empty() {
                let _ = tx_recognizing.send(RealtimeEvent::Translated {
                    source_text: text,
                    translations,
                });
            }
        })
        .map_err(|e| format!("注册 recognizing 回调失败: {e}"))?;

    // Recognized（最终结果）
    let tx_recognized = tx.clone();
    recognizer
        .set_recognized_cb(move |event| {
            let reason = event.result.base.reason;
            let text = event.result.base.text.clone();
            let duration_ms = event.result.base.duration.as_millis() as u64;
            let translations = event.result.translations.clone();

            match reason {
                ResultReason::TranslatedSpeech => {
                    let _ = tx_recognized.send(RealtimeEvent::Recognized {
                        text: text.clone(),
                        duration_ms,
                    });
                    if !translations.is_empty() {
                        let _ = tx_recognized.send(RealtimeEvent::Translated {
                            source_text: text,
                            translations,
                        });
                    }
                }
                ResultReason::NoMatch => {
                    // 静默忽略无匹配
                }
                _ => {
                    let _ = tx_recognized.send(RealtimeEvent::Recognized {
                        text,
                        duration_ms,
                    });
                }
            }
        })
        .map_err(|e| format!("注册 recognized 回调失败: {e}"))?;

    // Canceled
    let tx_canceled = tx.clone();
    recognizer
        .set_canceled_cb(move |event| {
            let msg = if event.error_details.is_empty() {
                format!("语音翻译已取消: {:?}", event.reason)
            } else {
                format!(
                    "语音翻译出错: {:?} ({:?}) — {}",
                    event.reason, event.error_code, event.error_details
                )
            };
            let _ = tx_canceled.send(RealtimeEvent::Error { message: msg });
        })
        .map_err(|e| format!("注册 canceled 回调失败: {e}"))?;

    // 将 recognizer 包装到 Arc<Mutex> 中以支持 async stop
    let recognizer = Arc::new(Mutex::new(recognizer));

    Ok(SpeechSessionHandle {
        recognizer,
        started: std::sync::atomic::AtomicBool::new(false),
    })
}

/// 实时语音会话句柄 — 控制启停
pub struct SpeechSessionHandle {
    recognizer: Arc<Mutex<TranslationRecognizer>>,
    started: std::sync::atomic::AtomicBool,
}

#[async_trait]
impl RealtimeSessionHandle for SpeechSessionHandle {
    async fn push_audio(&self, _pcm_data: &[u8]) -> Result<(), ProviderError> {
        // 麦克风模式下 SDK 自行采集音频，push_audio 不需要实现
        // 如果将来支持 PushStream 模式再实现
        Ok(())
    }

    async fn stop(&self) -> Result<(), ProviderError> {
        if self
            .started
            .load(std::sync::atomic::Ordering::Relaxed)
        {
            let recognizer = self.recognizer.clone();
            tokio::task::spawn_blocking(move || {
                let rt = tokio::runtime::Handle::current();
                rt.block_on(async {
                    let mut reco = recognizer.lock().await;
                    reco.stop_continuous_recognition_async().await
                })
            })
            .await
            .map_err(|e| ProviderError::Internal(format!("stop spawn_blocking 失败: {e}")))?
            .map_err(|e| {
                ProviderError::Internal(format!("停止连续识别失败: {e}"))
            })?;

            self.started
                .store(false, std::sync::atomic::Ordering::Relaxed);
        }
        Ok(())
    }
}

impl SpeechSessionHandle {
    /// 启动连续识别（由 start_realtime_translation 命令调用）
    pub async fn start(&self) -> Result<(), ProviderError> {
        let recognizer = self.recognizer.clone();
        tokio::task::spawn_blocking(move || {
            let rt = tokio::runtime::Handle::current();
            rt.block_on(async {
                let mut reco = recognizer.lock().await;
                reco.start_continuous_recognition_async().await
            })
        })
        .await
        .map_err(|e| ProviderError::Internal(format!("start spawn_blocking 失败: {e}")))?
        .map_err(|e| {
            ProviderError::Internal(format!("启动连续识别失败: {e}"))
        })?;

        self.started
            .store(true, std::sync::atomic::Ordering::Relaxed);
        Ok(())
    }
}
