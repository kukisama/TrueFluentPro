//! OpenAI Realtime WebSocket translation provider.
//!
//! Dual-mode: Azure OpenAI (api-key header) and OpenAI (Bearer token).
//! Protocol: Send PCM audio as base64, receive transcription/translation events.

use async_trait::async_trait;
use futures_util::{SinkExt, StreamExt};
use serde_json::json;
use std::sync::Arc;
use tokio::sync::{mpsc, Mutex, RwLock};

use tfp_core::{AiEndpoint, EndpointType, ProviderError, RealtimeEvent, RealtimeSessionConfig};

use crate::traits::{ProviderCapability, ProviderMeta, RealtimeSessionHandle, RealtimeSpeechSlot};

/// OpenAI Realtime WebSocket translation provider.
pub struct OpenAiRealtimeProvider {
    endpoint: AiEndpoint,
}

impl OpenAiRealtimeProvider {
    pub fn new(endpoint: AiEndpoint) -> Self {
        Self { endpoint }
    }

    pub(crate) fn build_ws_url(&self, model: &str) -> String {
        let base = self.endpoint.url.trim_end_matches('/');
        if self.endpoint.endpoint_type == EndpointType::AzureOpenAi
            || self.endpoint.endpoint_type == EndpointType::ApiManagementGateway
        {
            let api_ver = self
                .endpoint
                .api_version
                .as_deref()
                .unwrap_or("2025-04-01-preview");
            let ws_base = base
                .replace("https://", "wss://")
                .replace("http://", "ws://");
            format!("{ws_base}/openai/realtime?api-version={api_ver}&deployment={model}")
        } else {
            format!("wss://api.openai.com/v1/realtime?model={model}")
        }
    }
}

impl ProviderMeta for OpenAiRealtimeProvider {
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
impl RealtimeSpeechSlot for OpenAiRealtimeProvider {
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
        let model = "gpt-4o-realtime-preview";
        let ws_url = self.build_ws_url(model);
        let key = &self.endpoint.api_key;

        let request = ws_url
            .parse::<url::Url>()
            .map_err(|e| ProviderError::Internal(format!("Invalid WS URL: {e}")))?;

        let is_azure = matches!(
            self.endpoint.endpoint_type,
            EndpointType::AzureOpenAi | EndpointType::ApiManagementGateway
        );

        let host = request.host_str().unwrap_or("").to_string();
        let ws_key = tokio_tungstenite::tungstenite::handshake::client::generate_key();

        let mut req_builder = tokio_tungstenite::tungstenite::http::Request::builder()
            .uri(request.as_str())
            .header("Sec-WebSocket-Version", "13")
            .header("Sec-WebSocket-Key", &ws_key)
            .header("Host", &host)
            .header("Connection", "Upgrade")
            .header("Upgrade", "websocket");

        if is_azure {
            req_builder = req_builder.header("api-key", key);
        } else {
            req_builder = req_builder
                .header("Authorization", format!("Bearer {key}"))
                .header("OpenAI-Beta", "realtime=v1");
        }

        let http_req = req_builder
            .body(())
            .map_err(|e| ProviderError::Internal(format!("Build WS request: {e}")))?;

        let (ws_stream, _) = tokio_tungstenite::connect_async(http_req)
            .await
            .map_err(|e| ProviderError::Network(format!("WS connect failed: {e}")))?;

        let (write, read) = ws_stream.split();
        let write = Arc::new(Mutex::new(write));
        let (event_tx, event_rx) = mpsc::unbounded_channel();
        let session_id = uuid::Uuid::new_v4().to_string();
        let stopped = Arc::new(RwLock::new(false));

        let target_lang = config
            .target_langs
            .first()
            .cloned()
            .unwrap_or_else(|| "en".into());
        let source_lang = config.source_lang.clone();

        let session_update = json!({
            "type": "session.update",
            "session": {
                "modalities": ["text"],
                "instructions": format!(
                    "You are a real-time translator. Translate all speech from {} to {}. Output only the translation, nothing else.",
                    source_lang, target_lang
                ),
                "input_audio_transcription": { "model": "whisper-1" },
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

        let _ = event_tx.send(RealtimeEvent::SessionStarted {
            session_id: session_id.clone(),
        });

        // Spawn receive loop
        let event_tx_clone = event_tx.clone();
        let session_id_clone = session_id.clone();
        let stopped_clone = stopped.clone();
        let target_lang_clone = target_lang.clone();

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
                                "conversation.item.input_audio_transcription.completed" => {
                                    if let Some(transcript) = json["transcript"].as_str() {
                                        let _ = event_tx_clone.send(RealtimeEvent::Recognized {
                                            text: transcript.to_string(),
                                            duration_ms: 0,
                                        });
                                    }
                                }
                                "response.text.delta" => {
                                    if let Some(delta) = json["delta"].as_str() {
                                        let _ = event_tx_clone.send(RealtimeEvent::Recognizing {
                                            text: delta.to_string(),
                                            offset_ms: 0,
                                        });
                                    }
                                }
                                "response.text.done" => {
                                    if let Some(text) = json["text"].as_str() {
                                        let mut translations = std::collections::HashMap::new();
                                        translations.insert(
                                            target_lang_clone.clone(),
                                            text.to_string(),
                                        );
                                        let _ = event_tx_clone.send(RealtimeEvent::Translated {
                                            source_text: String::new(),
                                            translations,
                                        });
                                    }
                                }
                                "error" => {
                                    let msg = json["error"]["message"]
                                        .as_str()
                                        .unwrap_or("Unknown error")
                                        .to_string();
                                    let _ = event_tx_clone
                                        .send(RealtimeEvent::Error { message: msg });
                                }
                                _ => {
                                    tracing::debug!("OpenAI Realtime event: {event_type}");
                                }
                            }
                        }
                    }
                    Ok(tokio_tungstenite::tungstenite::Message::Close(_)) => break,
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
    write: Arc<
        Mutex<
            futures_util::stream::SplitSink<
                tokio_tungstenite::WebSocketStream<
                    tokio_tungstenite::MaybeTlsStream<tokio::net::TcpStream>,
                >,
                tokio_tungstenite::tungstenite::Message,
            >,
        >,
    >,
    stopped: Arc<RwLock<bool>>,
}

#[async_trait]
impl RealtimeSessionHandle for OpenAiRealtimeSessionHandle {
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
        let _ = ws
            .send(tokio_tungstenite::tungstenite::Message::Close(None))
            .await;
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::test_helpers::factories;

    #[test]
    fn test_build_ws_url_azure() {
        let mut ep = factories::azure_endpoint("ep1", "Azure EP");
        ep.api_version = Some("2025-04-01-preview".into());
        let p = OpenAiRealtimeProvider::new(ep);
        let url = p.build_ws_url("gpt-4o-realtime-preview");
        assert!(url.starts_with("wss://"));
        assert!(url.contains("openai/realtime"));
        assert!(url.contains("api-version=2025-04-01-preview"));
        assert!(url.contains("deployment=gpt-4o-realtime-preview"));
    }

    #[test]
    fn test_build_ws_url_openai() {
        let ep = factories::openai_endpoint("ep2", "OpenAI EP");
        let p = OpenAiRealtimeProvider::new(ep);
        let url = p.build_ws_url("gpt-4o-realtime-preview");
        assert_eq!(
            url,
            "wss://api.openai.com/v1/realtime?model=gpt-4o-realtime-preview"
        );
    }

    #[test]
    fn test_provider_meta() {
        let ep = factories::azure_endpoint("rt1", "Realtime EP");
        let p = OpenAiRealtimeProvider::new(ep);
        assert_eq!(p.id(), "rt1");
        assert_eq!(
            p.capabilities(),
            vec![ProviderCapability::RealtimeSpeechTranslation]
        );
    }
}
