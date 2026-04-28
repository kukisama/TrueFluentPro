use async_trait::async_trait;
use futures_util::{SinkExt, StreamExt};
use serde_json::json;
use std::sync::Arc;
use tokio::sync::{mpsc, Mutex, RwLock};

use crate::models::*;
use super::registry::*;

/// P3-1: OpenAI Realtime WebSocket 翻译 Provider
///
/// 对齐 C# OpenAiRealtimeTranslationService — 双模式:
/// - Conversation 模式: wss://...realtime?model=gpt-4o-realtime-preview
/// - Transcription 模式: wss://...realtime?model=gpt-4o-transcribe
///
/// 协议: 发送 PCM audio → 收到 transcription/translation events
pub struct OpenAiRealtimeProvider {
    endpoint: AiEndpoint,
}

impl OpenAiRealtimeProvider {
    pub fn new(endpoint: AiEndpoint) -> Self {
        Self { endpoint }
    }

    fn build_ws_url(&self, model: &str) -> String {
        let base = self.endpoint.url.trim_end_matches('/');
        // Azure: wss://{host}/openai/realtime?api-version=...&deployment=...
        // OpenAI: wss://api.openai.com/v1/realtime?model=...
        if self.endpoint.endpoint_type == EndpointType::AzureOpenAi {
            let api_ver = self.endpoint.api_version.as_deref().unwrap_or("2025-04-01-preview");
            let ws_base = base.replace("https://", "wss://").replace("http://", "ws://");
            format!("{ws_base}/openai/realtime?api-version={api_ver}&deployment={model}")
        } else {
            format!("wss://api.openai.com/v1/realtime?model={model}")
        }
    }
}

impl ProviderMeta for OpenAiRealtimeProvider {
    fn id(&self) -> &str { &self.endpoint.id }
    fn display_name(&self) -> &str { &self.endpoint.name }
    fn capabilities(&self) -> Vec<ProviderCapability> {
        vec![ProviderCapability::RealtimeSpeechTranslation]
    }
}

#[async_trait]
impl RealtimeSpeechSlot for OpenAiRealtimeProvider {
    async fn create_session(
        &self,
        config: &RealtimeSessionConfig,
    ) -> Result<(mpsc::UnboundedReceiver<RealtimeEvent>, Box<dyn RealtimeSessionHandle>), ProviderError> {
        let model = "gpt-4o-realtime-preview";
        let ws_url = self.build_ws_url(model);

        // 构建 WebSocket 请求
        let request = ws_url.parse::<url::Url>()
            .map_err(|e| ProviderError::Internal(format!("Invalid WS URL: {e}")))?;

        let key = &self.endpoint.api_key;

        // 连接 WebSocket
        let (ws_stream, _) = if self.endpoint.endpoint_type == EndpointType::AzureOpenAi {
            // Azure: api-key header
            let req = tokio_tungstenite::tungstenite::http::Request::builder()
                .uri(request.as_str())
                .header("api-key", key)
                .header("Sec-WebSocket-Version", "13")
                .header("Sec-WebSocket-Key", tokio_tungstenite::tungstenite::handshake::client::generate_key())
                .header("Host", request.host_str().unwrap_or(""))
                .header("Connection", "Upgrade")
                .header("Upgrade", "websocket")
                .body(())
                .map_err(|e| ProviderError::Internal(format!("Build WS request: {e}")))?;

            tokio_tungstenite::connect_async(req).await
                .map_err(|e| ProviderError::Network(format!("WS connect failed: {e}")))?
        } else {
            // OpenAI: Bearer token in header
            let req = tokio_tungstenite::tungstenite::http::Request::builder()
                .uri(request.as_str())
                .header("Authorization", format!("Bearer {key}"))
                .header("OpenAI-Beta", "realtime=v1")
                .header("Sec-WebSocket-Version", "13")
                .header("Sec-WebSocket-Key", tokio_tungstenite::tungstenite::handshake::client::generate_key())
                .header("Host", "api.openai.com")
                .header("Connection", "Upgrade")
                .header("Upgrade", "websocket")
                .body(())
                .map_err(|e| ProviderError::Internal(format!("Build WS request: {e}")))?;

            tokio_tungstenite::connect_async(req).await
                .map_err(|e| ProviderError::Network(format!("WS connect failed: {e}")))?
        };

        let (write, read) = ws_stream.split();
        let write = Arc::new(Mutex::new(write));
        let (event_tx, event_rx) = mpsc::unbounded_channel();
        let session_id = uuid::Uuid::new_v4().to_string();
        let stopped = Arc::new(RwLock::new(false));

        // 发送 session.update 配置翻译
        let target_lang = config.target_langs.first().cloned().unwrap_or_else(|| "en".into());
        let source_lang = config.source_lang.clone();

        let session_update = json!({
            "type": "session.update",
            "session": {
                "modalities": ["text"],
                "instructions": format!(
                    "You are a real-time translator. Translate all speech from {} to {}. Output only the translation, nothing else.",
                    source_lang, target_lang
                ),
                "input_audio_transcription": {
                    "model": "whisper-1",
                },
                "turn_detection": {
                    "type": "server_vad",
                    "threshold": 0.5,
                    "prefix_padding_ms": 300,
                    "silence_duration_ms": 500,
                },
            }
        });

        {
            let mut ws = write.lock().await;
            ws.send(tokio_tungstenite::tungstenite::Message::Text(
                session_update.to_string().into(),
            ))
            .await
            .map_err(|e| ProviderError::Network(format!("Send session.update: {e}")))?;
        }

        // 通知前端 session 已启动
        let _ = event_tx.send(RealtimeEvent::SessionStarted {
            session_id: session_id.clone(),
        });

        // 启动接收循环
        let event_tx_clone = event_tx.clone();
        let session_id_clone = session_id.clone();
        let stopped_clone = stopped.clone();
        let target_lang_clone = target_lang.clone();
        let _source_lang_clone = source_lang.clone();

        tokio::spawn(async move {
            let mut read = read;
            while let Some(msg) = read.next().await {
                if *stopped_clone.read().await {
                    break;
                }

                match msg {
                    Ok(tokio_tungstenite::tungstenite::Message::Text(text)) => {
                        let text_str: &str = text.as_ref();
                        if let Ok(json) = serde_json::from_str::<serde_json::Value>(text_str) {
                            let event_type = json["type"].as_str().unwrap_or("");
                            match event_type {
                                "input_audio_buffer.speech_started" => {
                                    // VAD 检测到语音开始
                                }
                                "conversation.item.input_audio_transcription.completed" => {
                                    // 识别完成
                                    if let Some(transcript) = json["transcript"].as_str() {
                                        let _ = event_tx_clone.send(RealtimeEvent::Recognized {
                                            text: transcript.to_string(),
                                            duration_ms: 0,
                                        });
                                    }
                                }
                                "response.text.delta" => {
                                    // 翻译中间结果
                                    if let Some(delta) = json["delta"].as_str() {
                                        let _ = event_tx_clone.send(RealtimeEvent::Recognizing {
                                            text: delta.to_string(),
                                            offset_ms: 0,
                                        });
                                    }
                                }
                                "response.text.done" => {
                                    // 翻译完成
                                    if let Some(text) = json["text"].as_str() {
                                        let mut translations = std::collections::HashMap::new();
                                        translations.insert(target_lang_clone.clone(), text.to_string());
                                        let _ = event_tx_clone.send(RealtimeEvent::Translated {
                                            source_text: String::new(),
                                            translations,
                                        });
                                    }
                                }
                                "response.done" => {
                                    // 完整响应结束
                                }
                                "error" => {
                                    let msg = json["error"]["message"]
                                        .as_str()
                                        .unwrap_or("Unknown error")
                                        .to_string();
                                    let _ = event_tx_clone.send(RealtimeEvent::Error { message: msg });
                                }
                                _ => {
                                    tracing::debug!("OpenAI Realtime event: {event_type}");
                                }
                            }
                        }
                    }
                    Ok(tokio_tungstenite::tungstenite::Message::Close(_)) => {
                        break;
                    }
                    Err(e) => {
                        let _ = event_tx_clone.send(RealtimeEvent::Error {
                            message: format!("WS error: {e}"),
                        });
                        break;
                    }
                    _ => {}
                }
            }

            let _ = event_tx_clone.send(RealtimeEvent::SessionStopped {
                session_id: session_id_clone,
            });
        });

        let handle = OpenAiRealtimeSessionHandle {
            write,
            stopped,
        };

        Ok((event_rx, Box::new(handle)))
    }
}

struct OpenAiRealtimeSessionHandle {
    write: Arc<Mutex<futures_util::stream::SplitSink<
        tokio_tungstenite::WebSocketStream<tokio_tungstenite::MaybeTlsStream<tokio::net::TcpStream>>,
        tokio_tungstenite::tungstenite::Message,
    >>>,
    stopped: Arc<RwLock<bool>>,
}

#[async_trait]
impl RealtimeSessionHandle for OpenAiRealtimeSessionHandle {
    /// 推送 PCM 音频 (16kHz, 16bit, mono) → base64 编码后发送
    async fn push_audio(&self, pcm_data: &[u8]) -> Result<(), ProviderError> {
        use base64::Engine;
        let b64 = base64::engine::general_purpose::STANDARD.encode(pcm_data);

        let msg = json!({
            "type": "input_audio_buffer.append",
            "audio": b64,
        });

        let mut ws = self.write.lock().await;
        ws.send(tokio_tungstenite::tungstenite::Message::Text(
            msg.to_string().into(),
        ))
        .await
        .map_err(|e| ProviderError::Network(format!("Push audio: {e}")))?;

        Ok(())
    }

    async fn stop(&self) -> Result<(), ProviderError> {
        *self.stopped.write().await = true;

        let mut ws = self.write.lock().await;
        let _ = ws.send(tokio_tungstenite::tungstenite::Message::Close(None)).await;

        Ok(())
    }
}
