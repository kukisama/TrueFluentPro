use std::sync::Arc;
use std::sync::atomic::{AtomicI64, Ordering};

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
    Ok(built_in_languages())
}

fn built_in_languages() -> Vec<LanguageInfo> {
    [
        ("zh-Hans", "Chinese (Simplified)", "中文（简体）"),
        ("zh-Hant", "Chinese (Traditional)", "中文（繁體）"),
        ("en", "English", "English"),
        ("ja", "Japanese", "日本語"),
        ("ko", "Korean", "한국어"),
        ("fr", "French", "Français"),
        ("de", "German", "Deutsch"),
        ("es", "Spanish", "Español"),
        ("ru", "Russian", "Русский"),
        ("pt", "Portuguese", "Português"),
        ("it", "Italian", "Italiano"),
        ("ar", "Arabic", "العربية"),
        ("hi", "Hindi", "हिन्दी"),
        ("th", "Thai", "ภาษาไทย"),
        ("vi", "Vietnamese", "Tiếng Việt"),
    ]
    .into_iter()
    .map(|(code, name, native)| LanguageInfo {
        code: code.into(),
        name: name.into(),
        native_name: native.into(),
    })
    .collect()
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

/// Extract a final segment from a RealtimeEvent (Recognized / Translated only)
fn extract_final_segment(
    event: &RealtimeEvent,
    session_id: &str,
    sequence_counter: &Arc<AtomicI64>,
    recognition: &RecognitionSettings,
) -> Option<TranslationSegment> {
    match event {
        RealtimeEvent::Recognized { text, .. } => {
            let original = text.clone();
            if original.is_empty() {
                return None;
            }
            let original = if recognition.filter_modal_particles {
                filter_modal_particles(&original)
            } else {
                original
            };
            if original.trim().is_empty() {
                return None;
            }
            let seq = sequence_counter.fetch_add(1, Ordering::SeqCst) + 1;
            let now = chrono::Utc::now()
                .format("%Y-%m-%d %H:%M:%S")
                .to_string();
            Some(TranslationSegment {
                id: uuid::Uuid::new_v4().to_string(),
                session_id: session_id.to_string(),
                sequence: seq,
                original_text: original,
                translated_text: String::new(),
                target_lang: String::new(),
                started_at: Some(now.clone()),
                ended_at: Some(now),
                is_bookmarked: false,
                bookmark_note: None,
                audio_path: None,
                raw_event_json: serde_json::to_string(event).ok(),
            })
        }
        RealtimeEvent::Translated {
            source_text,
            translations,
        } => {
            if source_text.is_empty() && translations.is_empty() {
                return None;
            }
            let original = if recognition.filter_modal_particles {
                filter_modal_particles(source_text)
            } else {
                source_text.clone()
            };
            let (target_lang, translated_text) = translations
                .iter()
                .next()
                .map(|(k, v)| (k.clone(), v.clone()))
                .unwrap_or_default();
            if original.trim().is_empty() && translated_text.trim().is_empty() {
                return None;
            }
            let seq = sequence_counter.fetch_add(1, Ordering::SeqCst) + 1;
            let now = chrono::Utc::now()
                .format("%Y-%m-%d %H:%M:%S")
                .to_string();
            Some(TranslationSegment {
                id: uuid::Uuid::new_v4().to_string(),
                session_id: session_id.to_string(),
                sequence: seq,
                original_text: original,
                translated_text,
                target_lang,
                started_at: Some(now.clone()),
                ended_at: Some(now),
                is_bookmarked: false,
                bookmark_note: None,
                audio_path: None,
                raw_event_json: serde_json::to_string(event).ok(),
            })
        }
        _ => None,
    }
}

/// Filter modal particles / filler words from recognized text (aligned with C# ModalParticleFillers)
fn filter_modal_particles(text: &str) -> String {
    static FILLERS: &[&str] = &[
        "\u{554a}", "\u{5440}", "\u{5427}", "\u{5566}", "\u{561b}", "\u{5462}",
        "\u{54e6}", "\u{5450}", "\u{54c8}", "\u{5475}", "\u{55ef}", "\u{5509}",
        "\u{54ce}", "\u{90a3}\u{4e2a}", "\u{8fd9}\u{4e2a}", "\u{5c31}\u{662f}",
        "\u{7136}\u{540e}", "\u{5c31}\u{662f}\u{8bf4}", "\u{600e}\u{4e48}\u{8bf4}",
        "\u{4f60}\u{77e5}\u{9053}", "\u{5bf9}\u{5427}", "\u{662f}\u{5427}",
        "\u{5443}", "\u{989d}", "\u{55ef}\u{55ef}", "\u{554a}\u{554a}",
        "\u{54e6}\u{54e6}",
    ];
    let mut result = text.to_string();
    for filler in FILLERS {
        // Remove filler at start of sentence (followed by comma or entire match)
        let start_pattern = format!("{}\u{ff0c}", filler);
        if result.starts_with(&start_pattern) {
            result = result[start_pattern.len()..].to_string();
        } else if result.starts_with(filler) && result.len() == filler.len() {
            result.clear();
        }
        // Remove filler at end of sentence (preceded by comma or trailing)
        let end_pattern = format!("\u{ff0c}{}", filler);
        if result.ends_with(&end_pattern) {
            let new_len = result.len() - end_pattern.len();
            result.truncate(new_len);
        } else if result.ends_with(filler) {
            let new_len = result.len() - filler.len();
            result.truncate(new_len);
        }
    }
    result.trim().to_string()
}
