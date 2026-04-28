use tauri::{Emitter, State};
use tfp_core::{SupportedLanguage, TranslationSegment, TranslationSession};

use crate::state::AppState;

#[tauri::command]
pub async fn live_get_active_session(
    state: State<'_, AppState>,
) -> Result<Option<TranslationSession>, String> {
    state
        .db
        .live_get_active_session()
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn live_get_recent_segments(
    state: State<'_, AppState>,
    session_id: String,
    limit: Option<u32>,
) -> Result<Vec<TranslationSegment>, String> {
    state
        .db
        .live_get_recent_segments(&session_id, limit.unwrap_or(200))
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn live_bookmark_segment(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    segment_id: String,
    note: Option<String>,
) -> Result<(), String> {
    state
        .db
        .live_bookmark_segment(&segment_id, note.as_deref())
        .await
        .map_err(|e| e.to_string())?;
    let _ = app.emit("segment-updated", &segment_id);
    Ok(())
}

#[tauri::command]
pub async fn live_unbookmark_segment(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    segment_id: String,
) -> Result<(), String> {
    state
        .db
        .live_unbookmark_segment(&segment_id)
        .await
        .map_err(|e| e.to_string())?;
    let _ = app.emit("segment-updated", &segment_id);
    Ok(())
}

#[tauri::command]
pub async fn live_list_supported_languages(
    provider: String,
) -> Result<Vec<SupportedLanguage>, String> {
    Ok(build_language_list(&provider))
}

#[tauri::command]
pub async fn live_list_sessions(
    state: State<'_, AppState>,
    limit: Option<u32>,
    offset: Option<u32>,
) -> Result<Vec<TranslationSession>, String> {
    state
        .db
        .live_list_sessions(limit.unwrap_or(50), offset.unwrap_or(0))
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn live_get_session_segments(
    state: State<'_, AppState>,
    session_id: String,
) -> Result<Vec<TranslationSegment>, String> {
    state
        .db
        .live_get_recent_segments(&session_id, 99999)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn live_export_subtitles(
    state: State<'_, AppState>,
    session_id: String,
    format: String,
    include_translation: bool,
    output_path: String,
) -> Result<String, String> {
    let segments = state
        .db
        .live_get_recent_segments(&session_id, 99999)
        .await
        .map_err(|e| e.to_string())?;

    let content = match format.to_lowercase().as_str() {
        "vtt" => build_vtt(&segments, include_translation),
        _ => build_srt(&segments, include_translation),
    };

    tokio::fs::write(&output_path, content.as_bytes())
        .await
        .map_err(|e| format!("Failed to write subtitle file: {e}"))?;

    Ok(output_path)
}

#[tauri::command]
pub async fn live_clear_session_segments(
    state: State<'_, AppState>,
    session_id: String,
) -> Result<(), String> {
    state
        .db
        .live_clear_session_segments(&session_id)
        .await
        .map_err(|e| e.to_string())
}

// ── Subtitle formatting ──

fn build_srt(segments: &[TranslationSegment], include_translation: bool) -> String {
    let mut out = String::new();
    for (i, seg) in segments.iter().enumerate() {
        let idx = i + 1;
        let start = segment_timestamp_srt(seg.started_at.as_deref());
        let end = segment_timestamp_srt(seg.ended_at.as_deref());
        out.push_str(&format!("{idx}\r\n"));
        out.push_str(&format!("{start} --> {end}\r\n"));
        if include_translation {
            out.push_str(&format!("{}\r\n", seg.translated_text));
        } else {
            out.push_str(&format!("{}\r\n", seg.original_text));
        }
        out.push_str("\r\n");
    }
    out
}

fn build_vtt(segments: &[TranslationSegment], include_translation: bool) -> String {
    let mut out = String::from("WEBVTT\r\n\r\n");
    for seg in segments {
        let start = segment_timestamp_vtt(seg.started_at.as_deref());
        let end = segment_timestamp_vtt(seg.ended_at.as_deref());
        out.push_str(&format!("{start} --> {end}\r\n"));
        if include_translation {
            out.push_str(&format!("{}\r\n", seg.translated_text));
        } else {
            out.push_str(&format!("{}\r\n", seg.original_text));
        }
        out.push_str("\r\n");
    }
    out
}

fn segment_timestamp_srt(ts: Option<&str>) -> String {
    // Expect ISO 8601 or fallback to 00:00:00,000
    match ts {
        Some(s) if s.len() >= 19 => {
            // "YYYY-MM-DDTHH:MM:SSZ" → "HH:MM:SS,000"
            let time_part = &s[11..19];
            format!("{},000", time_part)
        }
        _ => "00:00:00,000".into(),
    }
}

fn segment_timestamp_vtt(ts: Option<&str>) -> String {
    match ts {
        Some(s) if s.len() >= 19 => {
            let time_part = &s[11..19];
            format!("{}.000", time_part)
        }
        _ => "00:00:00.000".into(),
    }
}

// ── Language lists ──

fn build_language_list(provider: &str) -> Vec<SupportedLanguage> {
    match provider {
        "openai_realtime" => openai_languages(),
        _ => azure_speech_languages(),
    }
}

fn openai_languages() -> Vec<SupportedLanguage> {
    vec![
        lang("auto", "Auto Detect", "source"),
        lang("zh", "Chinese", "both"),
        lang("en", "English", "both"),
        lang("ja", "Japanese", "both"),
        lang("ko", "Korean", "both"),
    ]
}

fn azure_speech_languages() -> Vec<SupportedLanguage> {
    vec![
        lang("auto", "Auto Detect", "source"),
        lang("zh-Hans", "Chinese (Simplified)", "both"),
        lang("zh-Hant", "Chinese (Traditional)", "both"),
        lang("en", "English", "both"),
        lang("ja", "Japanese", "both"),
        lang("ko", "Korean", "both"),
    ]
}

fn lang(code: &str, label: &str, kind: &str) -> SupportedLanguage {
    SupportedLanguage {
        code: code.into(),
        label: label.into(),
        kind: kind.into(),
    }
}
