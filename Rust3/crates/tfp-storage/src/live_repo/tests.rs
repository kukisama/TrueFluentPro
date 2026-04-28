use tfp_core::{TranslationSegment, TranslationSession};

use crate::Database;

#[tokio::test]
async fn test_live_session_crud() {
    let db = Database::open_in_memory().unwrap();

    let session = TranslationSession {
        id: "sess-001".into(),
        started_at: "2026-04-28T10:00:00Z".into(),
        stopped_at: None,
        source_lang: "zh-Hans".into(),
        target_langs: r#"["en","ja"]"#.into(),
        provider: "azure_speech".into(),
        status: "active".into(),
    };
    db.live_create_session(&session).await.unwrap();

    // Query active session
    let active = db.live_get_active_session().await.unwrap();
    assert!(active.is_some());
    let active = active.unwrap();
    assert_eq!(active.id, "sess-001");
    assert_eq!(active.status, "active");

    // List sessions
    let all = db.live_list_sessions(50, 0).await.unwrap();
    assert_eq!(all.len(), 1);

    // Stop session
    db.live_stop_session("sess-001", "2026-04-28T11:00:00Z")
        .await
        .unwrap();

    // No more active sessions
    let active2 = db.live_get_active_session().await.unwrap();
    assert!(active2.is_none());

    // Stopped session is still in the list
    let all2 = db.live_list_sessions(50, 0).await.unwrap();
    assert_eq!(all2.len(), 1);
    assert_eq!(all2[0].status, "stopped");
    assert_eq!(all2[0].stopped_at.as_deref(), Some("2026-04-28T11:00:00Z"));
}

#[tokio::test]
async fn test_live_segment_crud() {
    let db = Database::open_in_memory().unwrap();

    let session = TranslationSession {
        id: "sess-002".into(),
        started_at: "2026-04-28T10:00:00Z".into(),
        stopped_at: None,
        source_lang: "zh-Hans".into(),
        target_langs: r#"["en"]"#.into(),
        provider: "azure_speech".into(),
        status: "active".into(),
    };
    db.live_create_session(&session).await.unwrap();

    // Insert segments out of order
    for i in [3, 1, 2] {
        let seg = TranslationSegment {
            id: format!("seg-{i:03}"),
            session_id: "sess-002".into(),
            sequence: i,
            original_text: format!("text-{i}"),
            translated_text: format!("translated-{i}"),
            target_lang: "en".into(),
            started_at: None,
            ended_at: None,
            is_bookmarked: false,
            bookmark_note: None,
            audio_path: None,
            raw_event_json: None,
        };
        db.live_insert_segment(&seg).await.unwrap();
    }

    // Max sequence
    let max = db.live_get_max_sequence("sess-002").await.unwrap();
    assert_eq!(max, 3);

    // get_recent_segments returns in ascending order (after internal reverse)
    let recent = db.live_get_recent_segments("sess-002", 10).await.unwrap();
    assert_eq!(recent.len(), 3);
    assert_eq!(recent[0].sequence, 1);
    assert_eq!(recent[1].sequence, 2);
    assert_eq!(recent[2].sequence, 3);

    // Clear segments
    db.live_clear_session_segments("sess-002").await.unwrap();
    let after_clear = db.live_get_recent_segments("sess-002", 10).await.unwrap();
    assert!(after_clear.is_empty());
}

#[tokio::test]
async fn test_live_bookmark() {
    let db = Database::open_in_memory().unwrap();

    let session = TranslationSession {
        id: "sess-003".into(),
        started_at: "2026-04-28T10:00:00Z".into(),
        stopped_at: None,
        source_lang: "zh-Hans".into(),
        target_langs: r#"["en"]"#.into(),
        provider: "azure_speech".into(),
        status: "active".into(),
    };
    db.live_create_session(&session).await.unwrap();

    let seg = TranslationSegment {
        id: "seg-bm-001".into(),
        session_id: "sess-003".into(),
        sequence: 1,
        original_text: "hello".into(),
        translated_text: "你好".into(),
        target_lang: "zh-Hans".into(),
        started_at: None,
        ended_at: None,
        is_bookmarked: false,
        bookmark_note: None,
        audio_path: None,
        raw_event_json: None,
    };
    db.live_insert_segment(&seg).await.unwrap();

    // Bookmark
    db.live_bookmark_segment("seg-bm-001", Some("important"))
        .await
        .unwrap();
    let segs = db.live_get_recent_segments("sess-003", 10).await.unwrap();
    assert!(segs[0].is_bookmarked);
    assert_eq!(segs[0].bookmark_note.as_deref(), Some("important"));

    // Unbookmark
    db.live_unbookmark_segment("seg-bm-001").await.unwrap();
    let segs2 = db.live_get_recent_segments("sess-003", 10).await.unwrap();
    assert!(!segs2[0].is_bookmarked);
    assert!(segs2[0].bookmark_note.is_none());
}
