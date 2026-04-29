//! Subtitle export: SRT / VTT formatting with millisecond-precision timestamps.
//!
//! Aligned with C# SpeechTranslationService flow 7.

use tfp_core::TranslationSegment;

/// A single subtitle entry with millisecond timestamps.
#[derive(Debug, Clone)]
pub struct SubtitleEntry {
    pub index: u32,
    pub start_ms: u64,
    pub end_ms: u64,
    pub text: String,
}

/// Format milliseconds as SRT timestamp: HH:MM:SS,mmm
pub fn format_srt_timestamp(ms: u64) -> String {
    let total_secs = ms / 1000;
    let millis = ms % 1000;
    let hours = total_secs / 3600;
    let minutes = (total_secs % 3600) / 60;
    let seconds = total_secs % 60;
    format!("{:02}:{:02}:{:02},{:03}", hours, minutes, seconds, millis)
}

/// Format milliseconds as VTT timestamp: HH:MM:SS.mmm
pub fn format_vtt_timestamp(ms: u64) -> String {
    let total_secs = ms / 1000;
    let millis = ms % 1000;
    let hours = total_secs / 3600;
    let minutes = (total_secs % 3600) / 60;
    let seconds = total_secs % 60;
    format!("{:02}:{:02}:{:02}.{:03}", hours, minutes, seconds, millis)
}

/// Build SRT subtitle content from entries.
pub fn build_srt(entries: &[SubtitleEntry]) -> String {
    let mut out = String::new();
    for entry in entries {
        out.push_str(&format!("{}\r\n", entry.index));
        out.push_str(&format!(
            "{} --> {}\r\n",
            format_srt_timestamp(entry.start_ms),
            format_srt_timestamp(entry.end_ms),
        ));
        out.push_str(&format!("{}\r\n", entry.text));
        out.push_str("\r\n");
    }
    out
}

/// Build VTT subtitle content from entries.
pub fn build_vtt(entries: &[SubtitleEntry]) -> String {
    let mut out = String::from("WEBVTT\r\n\r\n");
    for entry in entries {
        out.push_str(&format!(
            "{} --> {}\r\n",
            format_vtt_timestamp(entry.start_ms),
            format_vtt_timestamp(entry.end_ms),
        ));
        out.push_str(&format!("{}\r\n", entry.text));
        out.push_str("\r\n");
    }
    out
}

/// Convert TranslationSegments to SubtitleEntries using session-relative timestamps.
///
/// If segments have `started_at`/`ended_at` in "YYYY-MM-DD HH:MM:SS" format,
/// we compute milliseconds relative to `session_start_utc`. Otherwise, we
/// estimate based on sequence position.
pub fn segments_to_subtitle_entries(
    segments: &[TranslationSegment],
    session_start_utc: Option<&str>,
) -> Vec<SubtitleEntry> {
    let base_ts = session_start_utc
        .and_then(|s| chrono::NaiveDateTime::parse_from_str(s, "%Y-%m-%d %H:%M:%S").ok());

    let mut entries = Vec::with_capacity(segments.len());
    let mut fallback_offset_ms: u64 = 0;

    for (i, seg) in segments.iter().enumerate() {
        let (start_ms, end_ms) = if let (Some(started), Some(base)) =
            (seg.started_at.as_deref().and_then(|s| {
                chrono::NaiveDateTime::parse_from_str(s, "%Y-%m-%d %H:%M:%S").ok()
            }), base_ts)
        {
            let start = (started - base).num_milliseconds().max(0) as u64;
            let end = seg
                .ended_at
                .as_deref()
                .and_then(|s| chrono::NaiveDateTime::parse_from_str(s, "%Y-%m-%d %H:%M:%S").ok())
                .map(|e| (e - base).num_milliseconds().max(0) as u64)
                .unwrap_or(start + 3000);
            (start, end)
        } else {
            // Fallback: estimate 3 seconds per segment
            let start = fallback_offset_ms;
            let end = start + 3000;
            fallback_offset_ms = end;
            (start, end)
        };

        let text = if seg.translated_text.is_empty() {
            seg.original_text.clone()
        } else {
            format!("{}\r\n{}", seg.original_text, seg.translated_text)
        };

        entries.push(SubtitleEntry {
            index: (i + 1) as u32,
            start_ms,
            end_ms,
            text,
        });
    }
    entries
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_format_srt_timestamp() {
        assert_eq!(format_srt_timestamp(0), "00:00:00,000");
        assert_eq!(format_srt_timestamp(1500), "00:00:01,500");
        assert_eq!(format_srt_timestamp(3661234), "01:01:01,234");
        assert_eq!(format_srt_timestamp(86400000), "24:00:00,000");
    }

    #[test]
    fn test_format_vtt_timestamp() {
        assert_eq!(format_vtt_timestamp(0), "00:00:00.000");
        assert_eq!(format_vtt_timestamp(1500), "00:00:01.500");
        assert_eq!(format_vtt_timestamp(3661234), "01:01:01.234");
    }

    #[test]
    fn test_build_srt() {
        let entries = vec![
            SubtitleEntry { index: 1, start_ms: 0, end_ms: 3000, text: "Hello".into() },
            SubtitleEntry { index: 2, start_ms: 3000, end_ms: 6000, text: "World".into() },
        ];
        let srt = build_srt(&entries);
        assert!(srt.contains("1\r\n00:00:00,000 --> 00:00:03,000\r\nHello\r\n"));
        assert!(srt.contains("2\r\n00:00:03,000 --> 00:00:06,000\r\nWorld\r\n"));
    }

    #[test]
    fn test_build_vtt() {
        let entries = vec![
            SubtitleEntry { index: 1, start_ms: 0, end_ms: 3000, text: "Hello".into() },
        ];
        let vtt = build_vtt(&entries);
        assert!(vtt.starts_with("WEBVTT\r\n\r\n"));
        assert!(vtt.contains("00:00:00.000 --> 00:00:03.000\r\nHello\r\n"));
    }

    #[test]
    fn test_segments_to_entries_with_timestamps() {
        let segments = vec![
            TranslationSegment {
                id: "1".into(),
                session_id: "s".into(),
                sequence: 1,
                original_text: "你好".into(),
                translated_text: "Hello".into(),
                target_lang: "en".into(),
                started_at: Some("2024-01-01 00:00:05".into()),
                ended_at: Some("2024-01-01 00:00:08".into()),
                is_bookmarked: false,
                bookmark_note: None,
                audio_path: None,
                raw_event_json: None,
            },
        ];
        let entries = segments_to_subtitle_entries(&segments, Some("2024-01-01 00:00:00"));
        assert_eq!(entries.len(), 1);
        assert_eq!(entries[0].start_ms, 5000);
        assert_eq!(entries[0].end_ms, 8000);
        assert!(entries[0].text.contains("你好"));
        assert!(entries[0].text.contains("Hello"));
    }

    #[test]
    fn test_segments_to_entries_fallback() {
        let segments = vec![
            TranslationSegment {
                id: "1".into(),
                session_id: "s".into(),
                sequence: 1,
                original_text: "First".into(),
                translated_text: String::new(),
                target_lang: "en".into(),
                started_at: None,
                ended_at: None,
                is_bookmarked: false,
                bookmark_note: None,
                audio_path: None,
                raw_event_json: None,
            },
            TranslationSegment {
                id: "2".into(),
                session_id: "s".into(),
                sequence: 2,
                original_text: "Second".into(),
                translated_text: String::new(),
                target_lang: "en".into(),
                started_at: None,
                ended_at: None,
                is_bookmarked: false,
                bookmark_note: None,
                audio_path: None,
                raw_event_json: None,
            },
        ];
        let entries = segments_to_subtitle_entries(&segments, None);
        assert_eq!(entries.len(), 2);
        assert_eq!(entries[0].start_ms, 0);
        assert_eq!(entries[0].end_ms, 3000);
        assert_eq!(entries[1].start_ms, 3000);
        assert_eq!(entries[1].end_ms, 6000);
        // No translation → text is just original
        assert_eq!(entries[0].text, "First");
    }
}
