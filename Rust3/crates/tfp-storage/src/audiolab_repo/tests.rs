use crate::Database;
use tfp_core::{AudioFile, AudioTranscript, AudioSegment, AudioStagePreset};

fn make_audio_file(id: &str) -> AudioFile {
    AudioFile {
        id: id.to_string(),
        display_name: format!("{id}.wav"),
        source_path: format!("/audio/{id}.wav"),
        mp3_path: None,
        sample_rate: 16000,
        channels: 1,
        duration_ms: 60000,
        file_size_bytes: 960000,
        sha256: "abc123".into(),
        imported_at: "2026-01-01T00:00:00Z".into(),
        last_opened_at: None,
        is_legacy_import: false,
        legacy_source_path: None,
        import_batch_id: None,
        session_id: None,
    }
}

#[tokio::test]
async fn test_audiolab_file_crud() {
    let db = Database::open_in_memory().unwrap();

    let f = make_audio_file("f1");
    db.audiolab_insert_file(&f).await.unwrap();

    // List
    let list = db.audiolab_list_files(50, 0, None, None).await.unwrap();
    assert_eq!(list.len(), 1);
    assert_eq!(list[0].id, "f1");

    // Get
    let got = db.audiolab_get_file("f1").await.unwrap();
    assert!(got.is_some());
    assert_eq!(got.unwrap().display_name, "f1.wav");

    // Remove
    db.audiolab_remove_file("f1").await.unwrap();
    let list2 = db.audiolab_list_files(50, 0, None, None).await.unwrap();
    assert!(list2.is_empty());
}

#[tokio::test]
async fn test_audiolab_transcript_and_segments() {
    let db = Database::open_in_memory().unwrap();

    let f = make_audio_file("f2");
    db.audiolab_insert_file(&f).await.unwrap();

    // We need a session_id for transcript, but the table doesn't enforce FK.
    // Use a dummy session_id.
    let transcript = AudioTranscript {
        id: "tr1".into(),
        session_id: "sess-dummy".into(),
        audio_file_id: "f2".into(),
        language: "zh-Hans".into(),
        raw_json: Some(r#"{"text":"hello"}"#.into()),
        parser_kind: "whisper".into(),
        created_at: "2026-01-01T00:00:00Z".into(),
    };
    db.audiolab_insert_transcript(&transcript).await.unwrap();

    // Get transcript
    let got = db.audiolab_get_transcript("sess-dummy").await.unwrap();
    assert!(got.is_some());
    let got = got.unwrap();
    assert_eq!(got.id, "tr1");
    assert_eq!(got.language, "zh-Hans");

    // Insert segments
    let segments = vec![
        AudioSegment {
            id: "seg1".into(),
            transcript_id: "tr1".into(),
            sequence: 1,
            speaker: "Speaker 1".into(),
            speaker_index: 0,
            start_ms: 0,
            end_ms: 5000,
            text: "Hello world".into(),
            confidence: Some(0.95),
        },
        AudioSegment {
            id: "seg2".into(),
            transcript_id: "tr1".into(),
            sequence: 2,
            speaker: "Speaker 2".into(),
            speaker_index: 1,
            start_ms: 5000,
            end_ms: 10000,
            text: "Goodbye".into(),
            confidence: Some(0.88),
        },
    ];
    db.audiolab_insert_segments(&segments).await.unwrap();

    // Get segments
    let segs = db.audiolab_get_segments("tr1").await.unwrap();
    assert_eq!(segs.len(), 2);
    assert_eq!(segs[0].sequence, 1);
    assert_eq!(segs[0].text, "Hello world");
    assert_eq!(segs[1].sequence, 2);
    assert_eq!(segs[1].speaker, "Speaker 2");
}

#[tokio::test]
async fn test_audiolab_stage_presets() {
    let db = Database::open_in_memory().unwrap();

    let preset = AudioStagePreset {
        id: "p1".into(),
        stage: "custom_summary".into(),
        display_name: "Custom Summary".into(),
        system_prompt: "Summarize the audio".into(),
        show_in_tab: true,
        include_in_batch: false,
        is_enabled: true,
        display_mode: "markdown".into(),
        sort_order: 10,
    };
    db.audiolab_upsert_stage_preset(&preset).await.unwrap();

    // List
    let list = db.audiolab_list_stage_presets().await.unwrap();
    assert_eq!(list.len(), 1);
    assert_eq!(list[0].display_name, "Custom Summary");
    assert!(list[0].show_in_tab);

    // Upsert same stage (should update, not duplicate)
    let updated = AudioStagePreset {
        id: "p1-updated".into(),
        stage: "custom_summary".into(),
        display_name: "Updated Summary".into(),
        system_prompt: "New prompt".into(),
        show_in_tab: false,
        include_in_batch: true,
        is_enabled: true,
        display_mode: "markdown".into(),
        sort_order: 5,
    };
    db.audiolab_upsert_stage_preset(&updated).await.unwrap();

    let list2 = db.audiolab_list_stage_presets().await.unwrap();
    assert_eq!(list2.len(), 1, "upsert should not create duplicate");
    assert_eq!(list2[0].display_name, "Updated Summary");
    assert!(!list2[0].show_in_tab);
    assert!(list2[0].include_in_batch);
    assert_eq!(list2[0].sort_order, 5);

    // Delete
    db.audiolab_delete_stage_preset("custom_summary").await.unwrap();
    let list3 = db.audiolab_list_stage_presets().await.unwrap();
    assert!(list3.is_empty());
}
