use std::sync::Arc;
use std::sync::atomic::AtomicI64;

use tauri::{Emitter, State};
use tfp_core::{
    LanguageInfo, RealtimeEvent, RealtimeSessionConfig, RecognitionSettings,
    TranslateRequest, TranslateResponse, TranslationSegment, TranslationSession,
};
use tfp_providers::ProviderCapability;

use crate::state::AppState;

#[tauri::command]
pub async fn translate_text(
    state: State<'_, AppState>,
    request: TranslateRequest,
) -> Result<TranslateResponse, String> {
    let providers = state.providers.read().await;

    let provider_id = match request.endpoint_id.as_deref() {
        Some(id) if !id.is_empty() => id.to_string(),
        _ => {
            // Pick the first provider that has TextTranslation capability
            let list = providers.list_providers();
            list.iter()
                .find(|p| p.capabilities.contains(&ProviderCapability::TextTranslation))
                .map(|p| p.id.clone())
                .ok_or_else(|| "No text translation provider available".to_string())?
        }
    };

    let provider = providers
        .get_text_translation(&provider_id)
        .ok_or_else(|| format!("Text translation provider not found: {provider_id}"))?;

    provider
        .translate(&request)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn get_supported_languages() -> Result<Vec<LanguageInfo>, String> {
    Ok(tfp_speech::languages::built_in_languages())
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  Realtime speech translation
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

#[tauri::command]
pub async fn start_realtime_translation(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    mut config: RealtimeSessionConfig,
) -> Result<String, String> {
    // Fill default timeout values from global RecognitionSettings
    {
        let app_config = state.config.read().await;
        if config.initial_silence_timeout_seconds.is_none() {
            config.initial_silence_timeout_seconds =
                Some(app_config.recognition.initial_silence_timeout_seconds);
        }
        if config.end_silence_timeout_seconds.is_none() {
            config.end_silence_timeout_seconds =
                Some(app_config.recognition.end_silence_timeout_seconds);
        }
    }

    let providers = state.providers.read().await;
    let provider = providers
        .get_realtime_speech(&config.endpoint_id)
        .ok_or_else(|| {
            format!(
                "Realtime speech provider not found: {}. Please add an Azure Speech endpoint in settings.",
                config.endpoint_id
            )
        })?;

    let (mut rx, handle) = provider
        .create_session(&config)
        .await
        .map_err(|e| e.to_string())?;

    let session_id = uuid::Uuid::new_v4().to_string();
    let sid = session_id.clone();

    // Persist translation session record
    let db = state.db.clone();
    let target_langs_json = serde_json::to_string(&config.target_langs).unwrap_or_default();
    let ts_session = TranslationSession {
        id: sid.clone(),
        started_at: chrono::Utc::now().format("%Y-%m-%d %H:%M:%S").to_string(),
        stopped_at: None,
        source_lang: config.source_lang.clone(),
        target_langs: target_langs_json,
        provider: config.endpoint_id.clone(),
        status: "active".to_string(),
    };
    db.live_create_session(&ts_session)
        .await
        .map_err(|e| e.to_string())?;

    {
        let mut sessions = state.active_speech_sessions.write().await;
        sessions.insert(sid.clone(), handle);
    }

    // Current max sequence for this session
    let max_seq = db.live_get_max_sequence(&sid).await.unwrap_or(0);
    let sequence_counter = Arc::new(AtomicI64::new(max_seq));

    // Recognition settings (for modal particle filtering)
    let recognition = {
        let cfg = state.config.read().await;
        cfg.recognition.clone()
    };

    // no_response_restart configuration
    let no_response_restart_enabled = recognition.enable_no_response_restart;
    let no_response_restart_secs = recognition.no_response_restart_seconds as u64;

    let db_for_spawn = db.clone();
    let sid_for_spawn = sid.clone();
    let app_for_subtitle = app.clone();
    tauri::async_runtime::spawn(async move {
        let timeout_duration = std::time::Duration::from_secs(
            if no_response_restart_enabled && no_response_restart_secs > 0 {
                no_response_restart_secs
            } else {
                u64::MAX / 2
            },
        );

        loop {
            let event = if no_response_restart_enabled {
                match tokio::time::timeout(timeout_duration, rx.recv()).await {
                    Ok(Some(event)) => event,
                    Ok(None) => break,
                    Err(_) => {
                        // Timeout — emit reconnect marker to frontend
                        let _ = app.emit(
                            "realtime-event",
                            &RealtimeEvent::SessionStopped {
                                session_id: format!("no_response_restart:{}", sid_for_spawn),
                            },
                        );
                        continue;
                    }
                }
            } else {
                match rx.recv().await {
                    Some(event) => event,
                    None => break,
                }
            };

            // Persist final events (Recognized / Translated) to database
            if let Some(seg) = extract_final_segment(
                &event,
                &sid_for_spawn,
                &sequence_counter,
                &recognition,
            ) {
                super::floating::emit_subtitle_update(
                    &app_for_subtitle,
                    &seg.original_text,
                    &seg.translated_text,
                );
                let _ = db_for_spawn.live_insert_segment(&seg).await;
            }
            let _ = app.emit("realtime-event", &event);
        }
    });

    Ok(sid)
}

#[tauri::command]
pub async fn stop_realtime_translation(
    state: State<'_, AppState>,
    session_id: String,
) -> Result<(), String> {
    let handle = {
        let mut sessions = state.active_speech_sessions.write().await;
        sessions.remove(&session_id)
    };

    if let Some(h) = handle {
        h.stop().await.map_err(|e| e.to_string())?;
    }

    let now = chrono::Utc::now()
        .format("%Y-%m-%d %H:%M:%S")
        .to_string();
    state
        .db
        .live_stop_session(&session_id, &now)
        .await
        .map_err(|e| e.to_string())?;

    Ok(())
}

#[tauri::command]
pub async fn push_realtime_audio(
    state: State<'_, AppState>,
    session_id: String,
    audio_base64: String,
) -> Result<(), String> {
    use base64::Engine;
    let pcm_data = base64::engine::general_purpose::STANDARD
        .decode(&audio_base64)
        .map_err(|e| format!("Invalid base64 audio: {e}"))?;

    let sessions = state.active_speech_sessions.read().await;
    let handle = sessions
        .get(&session_id)
        .ok_or_else(|| format!("Session not found: {session_id}"))?;

    handle
        .push_audio(&pcm_data)
        .await
        .map_err(|e| e.to_string())
}

/// Extract a final segment from a RealtimeEvent — delegates to tfp_speech::segment
fn extract_final_segment(
    event: &RealtimeEvent,
    session_id: &str,
    sequence_counter: &Arc<AtomicI64>,
    recognition: &RecognitionSettings,
) -> Option<TranslationSegment> {
    tfp_speech::segment::extract_final_segment(event, session_id, sequence_counter, recognition)
}

#[cfg(test)]
mod tests {
    use std::collections::HashMap;
    use std::sync::Arc;
    use std::sync::atomic::AtomicI64;
    use tfp_core::{RealtimeEvent, RecognitionSettings};

    #[test]
    fn test_built_in_languages() {
        let langs = tfp_speech::languages::built_in_languages();
        assert_eq!(langs.len(), 15);
        assert_eq!(langs[0].code, "zh-Hans");
    }

    #[test]
    fn test_filter_delegates() {
        assert_eq!(tfp_speech::text_filter::filter_modal_particles("啊，很好"), "很好");
        assert_eq!(tfp_speech::text_filter::filter_modal_particles("好的吧"), "好的");
    }

    #[test]
    fn test_extract_delegates_recognized() {
        let counter = Arc::new(AtomicI64::new(0));
        let recognition = RecognitionSettings {
            filter_modal_particles: false,
            ..Default::default()
        };
        let event = RealtimeEvent::Recognized {
            text: "hello".into(),
            duration_ms: 1000,
        };
        let seg = tfp_speech::segment::extract_final_segment(&event, "sess-1", &counter, &recognition);
        assert!(seg.is_some());
        assert_eq!(seg.unwrap().original_text, "hello");
    }

    #[test]
    fn test_extract_delegates_translated() {
        let counter = Arc::new(AtomicI64::new(0));
        let recognition = RecognitionSettings {
            filter_modal_particles: false,
            ..Default::default()
        };
        let mut translations = HashMap::new();
        translations.insert("en".to_string(), "hello".to_string());
        let event = RealtimeEvent::Translated {
            source_text: "你好".into(),
            translations,
        };
        let seg = tfp_speech::segment::extract_final_segment(&event, "sess-1", &counter, &recognition);
        assert!(seg.is_some());
        let seg = seg.unwrap();
        assert_eq!(seg.original_text, "你好");
        assert_eq!(seg.translated_text, "hello");
    }
}
