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

#[cfg(test)]
mod tests {
    use super::*;

    fn make_tseg(
        original: &str,
        translated: &str,
        started_at: Option<&str>,
        ended_at: Option<&str>,
    ) -> TranslationSegment {
        TranslationSegment {
            id: "seg1".into(),
            session_id: "sess1".into(),
            sequence: 1,
            original_text: original.into(),
            translated_text: translated.into(),
            target_lang: "en".into(),
            started_at: started_at.map(|s| s.into()),
            ended_at: ended_at.map(|s| s.into()),
            is_bookmarked: false,
            bookmark_note: None,
            audio_path: None,
            raw_event_json: None,
        }
    }

    #[test]
    fn test_segment_timestamp_srt_valid() {
        assert_eq!(
            segment_timestamp_srt(Some("2024-01-01T12:34:56Z")),
            "12:34:56,000"
        );
    }

    #[test]
    fn test_segment_timestamp_srt_none() {
        assert_eq!(segment_timestamp_srt(None), "00:00:00,000");
    }

    #[test]
    fn test_segment_timestamp_srt_short() {
        assert_eq!(segment_timestamp_srt(Some("short")), "00:00:00,000");
    }

    #[test]
    fn test_segment_timestamp_vtt_valid() {
        assert_eq!(
            segment_timestamp_vtt(Some("2024-01-01T12:34:56Z")),
            "12:34:56.000"
        );
    }

    #[test]
    fn test_segment_timestamp_vtt_none() {
        assert_eq!(segment_timestamp_vtt(None), "00:00:00.000");
    }

    #[test]
    fn test_build_srt_with_translation() {
        let seg = make_tseg(
            "你好世界",
            "Hello World",
            Some("2024-01-01T00:00:01Z"),
            Some("2024-01-01T00:00:03Z"),
        );
        let out = build_srt(&[seg], true);
        assert!(out.contains("1\r\n"));
        assert!(out.contains("00:00:01,000 --> 00:00:03,000\r\n"));
        assert!(out.contains("Hello World\r\n"));
        assert!(!out.contains("你好世界"));
    }

    #[test]
    fn test_build_vtt_original() {
        let seg = make_tseg(
            "你好世界",
            "Hello World",
            Some("2024-01-01T00:00:01Z"),
            Some("2024-01-01T00:00:03Z"),
        );
        let out = build_vtt(&[seg], false);
        assert!(out.starts_with("WEBVTT\r\n\r\n"));
        assert!(out.contains("你好世界\r\n"));
        assert!(!out.contains("Hello World"));
    }

    #[test]
    fn test_build_language_list_openai() {
        let list = build_language_list("openai_realtime");
        assert_eq!(list.len(), 5);
        assert_eq!(list[0].code, "auto");
    }

    #[test]
    fn test_build_language_list_azure() {
        let list = build_language_list("azure_speech");
        assert_eq!(list.len(), 6);
        assert!(list.iter().any(|l| l.code == "zh-Hans"));
    }

    #[test]
    fn test_build_language_list_default_is_azure() {
        let list = build_language_list("unknown_provider");
        let azure = build_language_list("azure_speech");
        assert_eq!(list.len(), azure.len());
        for (a, b) in list.iter().zip(azure.iter()) {
            assert_eq!(a.code, b.code);
            assert_eq!(a.label, b.label);
            assert_eq!(a.kind, b.kind);
        }
    }
}
