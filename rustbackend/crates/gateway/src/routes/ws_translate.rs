//! WebSocket live speech translation bridge.
//!
//! Endpoint: `GET /v1/ws/translate?token=<jwt>&from=en-US&to=es,fr`
//!
//! Architecture:
//!   Client ⟵WebSocket⟶ Gateway ⟵WebSocket⟶ Azure Speech Translation
//!
//! The gateway acts as a bidirectional bridge:
//! - Client sends binary audio frames → gateway wraps in Azure Speech Protocol framing → upstream
//! - Upstream sends translation.response text → gateway extracts translations → client JSON
//!
//! Auth: WebSocket connections can't send Authorization headers from browsers,
//! so the JWT token is passed as a `token` query parameter.
//!
//! Protocol reference:
//! https://github.com/Azure-Samples/voice-translator-and-personal-voice/blob/main/HOW_TO_USE_SPEECH_TRANSLATION_WEBSOCKETS.md

use crate::state::AppState;
use crate::error::ApiError;
use domain::auth::UserContext;
use domain::models::UserRole;
use providers::LiveTranslateRequest;
use std::sync::Arc;
use axum::{
    Router, routing::get,
    extract::{State, Query, WebSocketUpgrade, ws::{WebSocket, Message}},
    response::Response,
};
use futures::{SinkExt, StreamExt};
use serde::Deserialize;
use serde_json::json;
use tokio_tungstenite::tungstenite;
use tracing::{info, warn, error, debug};

pub fn routes() -> Router<Arc<AppState>> {
    Router::new()
        .route("/v1/ws/translate", get(ws_translate_upgrade))
}

#[derive(Debug, Deserialize)]
struct WsTranslateQuery {
    /// JWT token (required — WS can't use Authorization header from browsers).
    token: String,
    /// Source language BCP-47 locale (e.g. "en-US"). Omit for auto-detect.
    #[serde(default)]
    from: Option<String>,
    /// Target languages, comma-separated ISO 639-1 (e.g. "es,fr,de").
    to: String,
    /// Auto-detect candidate languages, comma-separated BCP-47 (e.g. "en-US,es-ES,fr-FR").
    #[serde(default)]
    detect: Option<String>,
    /// Optional provider ID.
    #[serde(default)]
    provider_id: Option<String>,
}

/// HTTP GET → WebSocket upgrade handler.
async fn ws_translate_upgrade(
    State(state): State<Arc<AppState>>,
    Query(params): Query<WsTranslateQuery>,
    ws: WebSocketUpgrade,
) -> Result<Response, ApiError> {
    // Authenticate via token query param
    let user_ctx = validate_ws_token(&state, &params.token)?;

    info!(
        user = %user_ctx.user_id,
        from = ?params.from,
        to = %params.to,
        "WebSocket translate upgrade"
    );

    let target_langs: Vec<String> = params.to.split(',')
        .map(|s| s.trim().to_string())
        .filter(|s| !s.is_empty())
        .collect();

    if target_langs.is_empty() {
        return Err(ApiError::BadRequest("'to' parameter required with at least one target language".into()));
    }

    let auto_detect_languages: Vec<String> = params.detect
        .map(|d| d.split(',').map(|s| s.trim().to_string()).filter(|s| !s.is_empty()).collect())
        .unwrap_or_default();

    let live_req = LiveTranslateRequest {
        source_lang: params.from.unwrap_or_default(),
        target_langs,
        auto_detect_languages,
    };

    let provider_id = params.provider_id;

    Ok(ws.on_upgrade(move |socket| {
        handle_ws_translate(socket, state, user_ctx, live_req, provider_id)
    }))
}

/// Main WebSocket handler — bridges client ↔ Azure Speech Translation.
async fn handle_ws_translate(
    client_ws: WebSocket,
    state: Arc<AppState>,
    user_ctx: UserContext,
    live_req: LiveTranslateRequest,
    provider_id: Option<String>,
) {
    // Resolve provider and build session config
    let session_config = {
        let registry = state.providers.read().await;
        let provider = match registry.get_live_translator(provider_id.as_deref()) {
            Ok(p) => p,
            Err(e) => {
                warn!(error = %e, "No live translation provider available");
                let (mut sink, _) = client_ws.split();
                let _ = sink.send(Message::Text(
                    json!({"error": "No live translation provider configured"}).to_string().into()
                )).await;
                return;
            }
        };
        match provider.build_session_config(&live_req).await {
            Ok(cfg) => cfg,
            Err(e) => {
                warn!(error = %e, "Failed to build session config");
                let (mut sink, _) = client_ws.split();
                let _ = sink.send(Message::Text(
                    json!({"error": format!("Provider error: {e}")}).to_string().into()
                )).await;
                return;
            }
        }
    };

    // Connect to upstream Azure WebSocket
    let upstream_url = match url::Url::parse(&session_config.upstream_url) {
        Ok(u) => u,
        Err(e) => {
            error!(error = %e, "Invalid upstream URL");
            return;
        }
    };

    // Build request with auth headers
    let mut request = match tungstenite::http::Request::builder()
        .uri(session_config.upstream_url.as_str())
        .header("Host", upstream_url.host_str().unwrap_or(""))
        .body(())
    {
        Ok(r) => r,
        Err(e) => {
            error!(error = %e, "Failed to build upstream request");
            return;
        }
    };
    for (key, value) in &session_config.auth_headers {
        let header_name = match http::header::HeaderName::from_bytes(key.as_bytes()) {
            Ok(n) => n,
            Err(e) => {
                error!(key = %key, error = %e, "Invalid header name in auth headers");
                return;
            }
        };
        let header_value = match http::header::HeaderValue::from_str(value) {
            Ok(v) => v,
            Err(e) => {
                error!(key = %key, error = %e, "Invalid header value in auth headers");
                return;
            }
        };
        request.headers_mut().insert(header_name, header_value);
    }

    let (upstream_ws, _) = match tokio_tungstenite::connect_async(request).await {
        Ok(conn) => conn,
        Err(e) => {
            error!(error = %e, "Failed to connect to upstream Azure Speech Translation");
            let (mut sink, _) = client_ws.split();
            let _ = sink.send(Message::Text(
                json!({"error": format!("Upstream connection failed: {e}")}).to_string().into()
            )).await;
            return;
        }
    };

    info!(user = %user_ctx.user_id, "Upstream Azure Speech Translation connected");

    let (mut upstream_sink, mut upstream_stream) = upstream_ws.split();
    let (mut client_sink, mut client_stream) = client_ws.split();

    // Generate a request ID for this session
    let request_id = uuid::Uuid::new_v4().to_string().replace('-', "");

    // Send speech.config message to Azure
    let speech_config = build_speech_config(&request_id);
    if let Err(e) = upstream_sink.send(tungstenite::Message::Text(speech_config.into())).await {
        error!(error = %e, "Failed to send speech.config");
        return;
    }

    // Send speech.context message to Azure
    let speech_context = build_speech_context(&request_id, &live_req);
    if let Err(e) = upstream_sink.send(tungstenite::Message::Text(speech_context.into())).await {
        error!(error = %e, "Failed to send speech.context");
        return;
    }

    debug!("speech.config + speech.context sent to Azure");

    // Track whether we've sent the WAV header
    let first_chunk = Arc::new(std::sync::atomic::AtomicBool::new(true));

    // Bridge: client → upstream (audio frames)
    let first_chunk_clone = first_chunk.clone();
    let request_id_clone = request_id.clone();
    let client_to_upstream = async move {
        while let Some(msg) = client_stream.next().await {
            match msg {
                Ok(Message::Binary(audio_data)) => {
                    let pcm = if first_chunk_clone.swap(false, std::sync::atomic::Ordering::Relaxed) {
                        // Prepend WAV header to first audio chunk
                        let mut wav = wav_header();
                        wav.extend_from_slice(&audio_data);
                        wav
                    } else {
                        audio_data.to_vec()
                    };

                    let framed = build_audio_frame(&request_id_clone, &pcm);
                    if let Err(e) = upstream_sink.send(tungstenite::Message::Binary(framed.into())).await {
                        debug!(error = %e, "Upstream send error — closing");
                        break;
                    }
                }
                Ok(Message::Text(text)) => {
                    // Client can send JSON control messages (e.g. {"action": "stop"})
                    if text.contains("\"stop\"") {
                        // Send empty audio to signal end-of-stream
                        let end_frame = build_audio_frame(&request_id_clone, &[]);
                        let _ = upstream_sink.send(tungstenite::Message::Binary(end_frame.into())).await;
                        break;
                    }
                }
                Ok(Message::Close(_)) => break,
                Err(e) => {
                    debug!(error = %e, "Client WebSocket error");
                    break;
                }
                _ => {}
            }
        }
        // Send end-of-stream to Azure
        let end_frame = build_audio_frame(&request_id_clone, &[]);
        let _ = upstream_sink.send(tungstenite::Message::Binary(end_frame.into())).await;
        let _ = upstream_sink.close().await;
    };

    // Bridge: upstream → client (translation results)
    let upstream_to_client = async move {
        while let Some(msg) = upstream_stream.next().await {
            match msg {
                Ok(tungstenite::Message::Text(text)) => {
                    // Parse Azure Speech Protocol text message
                    if let Some(client_msg) = parse_upstream_text(&text) {
                        if let Err(e) = client_sink.send(Message::Text(client_msg.into())).await {
                            debug!(error = %e, "Client send error — closing");
                            break;
                        }
                    }
                }
                Ok(tungstenite::Message::Binary(_)) => {
                    // Binary from Azure = TTS audio (if voice feature enabled)
                    // We skip it for now — client can use separate TTS endpoint
                }
                Ok(tungstenite::Message::Close(_)) => break,
                Err(e) => {
                    debug!(error = %e, "Upstream WebSocket error");
                    break;
                }
                _ => {}
            }
        }
        let _ = client_sink.close().await;
    };

    // Run both directions concurrently — when either ends, the other is dropped
    tokio::select! {
        _ = client_to_upstream => {
            debug!(user = %user_ctx.user_id, "Client→upstream direction ended");
        }
        _ = upstream_to_client => {
            debug!(user = %user_ctx.user_id, "Upstream→client direction ended");
        }
    }

    info!(user = %user_ctx.user_id, "WebSocket translate session ended");
}

// ═══ Azure Speech Protocol helpers ═══

fn timestamp() -> String {
    chrono::Utc::now().format("%Y-%m-%dT%H:%M:%S%.6fZ").to_string()
}

/// Build `speech.config` text message.
fn build_speech_config(request_id: &str) -> String {
    let body = json!({
        "context": {
            "system": {
                "version": "1.0.0",
                "name": "SpeechSDK",
                "build": "TrueFluentPro-Gateway"
            },
            "os": {
                "name": "Linux",
                "version": "1.0",
                "platform": "Linux"
            },
            "audio": {
                "source": {
                    "type": "Microphones",
                    "samplerate": "16000",
                    "bitspersample": "16",
                    "channelcount": "1"
                }
            }
        }
    });

    format!(
        "Path: speech.config\r\nX-RequestId: {request_id}\r\nX-Timestamp: {ts}\r\nContent-Type: application/json; charset=utf-8\r\n\r\n{body}",
        ts = timestamp(),
        body = body,
    )
}

/// Build `speech.context` text message.
fn build_speech_context(request_id: &str, req: &LiveTranslateRequest) -> String {
    let mut context = json!({
        "phraseDetection": {
            "mode": "CONVERSATION",
            "conversation": {
                "segmentation": {
                    "mode": "Semantic"
                }
            },
            "onSuccess": { "action": "Translate" },
            "onInterim": { "action": "Translate" }
        },
        "translation": {
            "targetLanguages": req.target_langs,
            "output": {
                "includePassThroughResults": true,
                "interimResults": { "mode": "Always" }
            },
            "onSuccess": { "action": "None" },
            "onPassthrough": { "action": "None" }
        },
        "phraseOutput": {
            "interimResults": { "resultType": "None" },
            "phraseResults": { "resultType": "None" }
        },
        "audio": { "streams": { "1": serde_json::Value::Null } }
    });

    // Add language auto-detection if no source language specified
    if req.source_lang.is_empty() && !req.auto_detect_languages.is_empty() {
        if let Some(obj) = context.as_object_mut() {
            // Note: "Priority" uses PascalCase intentionally — this matches Azure's wire format
            // as documented in the Speech SDK source code.
            obj.insert("languageId".to_string(), json!({
                "languages": req.auto_detect_languages,
                "onSuccess": { "action": "Recognize" },
                "onUnknown": { "action": "None" },
                "mode": "DetectContinuous",
                "Priority": "PrioritizeLatency"
            }));
        }
    }

    format!(
        "Path: speech.context\r\nX-RequestId: {request_id}\r\nX-Timestamp: {ts}\r\nContent-Type: application/json; charset=utf-8\r\n\r\n{body}",
        ts = timestamp(),
        body = context,
    )
}

/// Build a binary audio frame in Azure Speech Protocol format.
///
/// Format: [uint16-BE: header_length][header_bytes][pcm_audio_bytes]
fn build_audio_frame(request_id: &str, pcm: &[u8]) -> Vec<u8> {
    let header = format!(
        "Path: audio\r\nX-RequestId: {request_id}\r\nX-Timestamp: {ts}\r\nContent-Type: audio/x-wav\r\n",
        ts = timestamp(),
    );
    let header_bytes = header.as_bytes();
    let header_len = header_bytes.len() as u16;

    let mut frame = Vec::with_capacity(2 + header_bytes.len() + pcm.len());
    frame.extend_from_slice(&header_len.to_be_bytes());
    frame.extend_from_slice(header_bytes);
    frame.extend_from_slice(pcm);
    frame
}

/// Build a 44-byte RIFF/WAV header for streaming 16 kHz / 16-bit / mono PCM.
fn wav_header() -> Vec<u8> {
    let sample_rate: u32 = 16000;
    let channels: u16 = 1;
    let bits_per_sample: u16 = 16;
    let byte_rate: u32 = sample_rate * channels as u32 * bits_per_sample as u32 / 8;
    let block_align: u16 = channels * bits_per_sample / 8;

    let mut header = Vec::with_capacity(44);
    header.extend_from_slice(b"RIFF");
    header.extend_from_slice(&0u32.to_le_bytes()); // file size = 0 for streaming
    header.extend_from_slice(b"WAVE");
    header.extend_from_slice(b"fmt ");
    header.extend_from_slice(&16u32.to_le_bytes()); // fmt chunk size
    header.extend_from_slice(&1u16.to_le_bytes());  // PCM format
    header.extend_from_slice(&channels.to_le_bytes());
    header.extend_from_slice(&sample_rate.to_le_bytes());
    header.extend_from_slice(&byte_rate.to_le_bytes());
    header.extend_from_slice(&block_align.to_le_bytes());
    header.extend_from_slice(&bits_per_sample.to_le_bytes());
    header.extend_from_slice(b"data");
    header.extend_from_slice(&0u32.to_le_bytes()); // data size = 0 for streaming
    header
}

/// Parse an upstream Azure Speech Protocol text message and extract translation data.
///
/// Returns a JSON string to send to the client, or None if the message should be skipped.
fn parse_upstream_text(raw: &str) -> Option<String> {
    // Split header from body at \r\n\r\n
    let (header_part, body) = raw.split_once("\r\n\r\n")?;

    // Extract the Path
    let path = header_part.lines()
        .find(|l| l.to_lowercase().starts_with("path:"))?
        .split_once(':')?
        .1
        .trim();

    match path {
        "translation.response" => {
            let data: serde_json::Value = serde_json::from_str(body).ok()?;

            // Check for SpeechPhrase (final) or SpeechHypothesis (interim)
            let is_final = data.get("SpeechPhrase").is_some();
            let phrase = data.get("SpeechPhrase")
                .or_else(|| data.get("SpeechHypothesis"));

            let phrase = phrase?;
            let recognition_status = phrase.get("RecognitionStatus")
                .and_then(|v| v.as_str())
                .unwrap_or("Unknown");

            let display_text = phrase.get("DisplayText")
                .or_else(|| phrase.get("Text"))
                .and_then(|v| v.as_str())
                .unwrap_or("");

            let source_lang = phrase.get("PrimaryLanguage")
                .and_then(|p| p.get("Language"))
                .and_then(|v| v.as_str())
                .unwrap_or("");

            // Translations are at ROOT level, not inside SpeechPhrase
            let translations = data.get("Translations")
                .and_then(|t| t.as_array())
                .map(|arr| {
                    arr.iter().map(|t| json!({
                        "language": t.get("Language").and_then(|v| v.as_str()).unwrap_or(""),
                        "text": t.get("DisplayText").and_then(|v| v.as_str()).unwrap_or(""),
                    })).collect::<Vec<_>>()
                })
                .unwrap_or_default();

            let msg = json!({
                "type": if is_final { "final" } else { "interim" },
                "recognition_status": recognition_status,
                "text": display_text,
                "source_lang": source_lang,
                "translations": translations,
            });

            Some(msg.to_string())
        }
        "speech.startDetected" => {
            Some(json!({"type": "speech_start"}).to_string())
        }
        "speech.endDetected" => {
            Some(json!({"type": "speech_end"}).to_string())
        }
        "turn.start" => {
            Some(json!({"type": "turn_start"}).to_string())
        }
        "turn.end" => {
            Some(json!({"type": "turn_end"}).to_string())
        }
        _ => {
            debug!(path = %path, "Skipping upstream message");
            None
        }
    }
}

/// Validate a JWT token from query parameter (same logic as middleware but standalone).
fn validate_ws_token(state: &AppState, token: &str) -> Result<UserContext, ApiError> {
    use jsonwebtoken::{decode, Algorithm, DecodingKey, Validation};
    use domain::auth::LocalClaims;

    match state.config.auth.mode.as_str() {
        "local" => {
            let key = DecodingKey::from_secret(state.jwt_secret.as_bytes());
            let mut validation = Validation::new(Algorithm::HS256);
            validation.validate_exp = true;
            validation.leeway = 30;

            let data = decode::<LocalClaims>(token, &key, &validation)
                .map_err(|e| ApiError::Unauthorized(format!("invalid token: {e}")))?;

            let claims = data.claims;
            Ok(UserContext {
                user_id: claims.sub,
                tenant_id: "default".into(),
                role: claims.role.parse().unwrap_or(UserRole::User),
                display_name: claims.display_name,
                email: None,
            })
        }
        "aad" => {
            use domain::auth::AadClaims;

            let mut validation = Validation::new(Algorithm::RS256);
            validation.validate_exp = true;
            validation.leeway = 30;
            if !state.config.auth.aad.audience.is_empty() {
                validation.set_audience(&[&state.config.auth.aad.audience]);
            }
            validation.insecure_disable_signature_validation();

            let key = DecodingKey::from_secret(&[]);
            let data = decode::<AadClaims>(token, &key, &validation)
                .map_err(|e| ApiError::Unauthorized(format!("invalid AAD token: {e}")))?;

            let claims = data.claims;
            let role = claims.roles.as_ref()
                .and_then(|roles| roles.iter().find(|r| *r == "admin"))
                .map(|_| UserRole::Admin)
                .unwrap_or(UserRole::User);

            Ok(UserContext {
                user_id: claims.oid,
                tenant_id: claims.tid.unwrap_or_else(|| "default".into()),
                role,
                display_name: claims.name,
                email: claims.email.or(claims.preferred_username),
            })
        }
        other => Err(ApiError::Internal(format!("unknown auth mode: {other}"))),
    }
}
