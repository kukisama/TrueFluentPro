use std::sync::Arc;
use std::sync::atomic::{AtomicI64, Ordering};

use tfp_core::{RealtimeEvent, RecognitionSettings, TranslationSegment};

use crate::text_filter::filter_modal_particles;

/// Extract a final segment from a RealtimeEvent (Recognized / Translated only).
///
/// Returns `None` for non-final events (SessionStarted, Recognizing, etc.) or
/// when the text is empty after modal-particle filtering.
pub fn extract_final_segment(
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

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::HashMap;
    use std::sync::Arc;
    use std::sync::atomic::AtomicI64;

    #[test]
    fn test_extract_final_segment_recognized() {
        let counter = Arc::new(AtomicI64::new(0));
        let recognition = RecognitionSettings {
            filter_modal_particles: false,
            ..Default::default()
        };
        let event = RealtimeEvent::Recognized {
            text: "hello".into(),
            duration_ms: 1000,
        };
        let seg = extract_final_segment(&event, "sess-1", &counter, &recognition);
        assert!(seg.is_some());
        let seg = seg.unwrap();
        assert_eq!(seg.original_text, "hello");
        assert_eq!(seg.sequence, 1);
        assert_eq!(seg.session_id, "sess-1");
        assert_eq!(seg.translated_text, "");
    }

    #[test]
    fn test_extract_final_segment_translated() {
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
        let seg = extract_final_segment(&event, "sess-1", &counter, &recognition);
        assert!(seg.is_some());
        let seg = seg.unwrap();
        assert_eq!(seg.original_text, "你好");
        assert_eq!(seg.translated_text, "hello");
        assert_eq!(seg.target_lang, "en");
    }

    #[test]
    fn test_extract_final_segment_empty_returns_none() {
        let counter = Arc::new(AtomicI64::new(0));
        let recognition = RecognitionSettings {
            filter_modal_particles: false,
            ..Default::default()
        };
        let event = RealtimeEvent::Recognized {
            text: "".into(),
            duration_ms: 0,
        };
        assert!(extract_final_segment(&event, "sess-1", &counter, &recognition).is_none());
    }

    #[test]
    fn test_extract_final_segment_ignores_other_events() {
        let counter = Arc::new(AtomicI64::new(0));
        let recognition = RecognitionSettings::default();
        let events = vec![
            RealtimeEvent::SessionStarted {
                session_id: "s1".into(),
            },
            RealtimeEvent::Recognizing {
                text: "partial".into(),
                offset_ms: 0,
            },
            RealtimeEvent::SessionStopped {
                session_id: "s1".into(),
            },
            RealtimeEvent::Error {
                message: "err".into(),
            },
        ];
        for event in &events {
            assert!(
                extract_final_segment(event, "sess-1", &counter, &recognition).is_none(),
                "expected None for event {:?}",
                event,
            );
        }
    }
}
