use crate::Database;
use tfp_core::{AudioLibraryItem, AudioLifecycleRow, AudioTaskRow};

fn make_audio_item(id: &str) -> AudioLibraryItem {
    AudioLibraryItem {
        id: id.to_string(),
        file_name: format!("{id}.wav"),
        file_path: format!("/audio/{id}.wav"),
        duration_ms: 30000,
        sample_rate: 16000,
        channels: 1,
        source_lang: "zh-Hans".into(),
        created_at: "2026-01-01T00:00:00Z".into(),
        updated_at: "2026-01-01T00:00:00Z".into(),
    }
}

fn make_task(id: &str, audio_item_id: &str, status: &str) -> AudioTaskRow {
    AudioTaskRow {
        id: id.to_string(),
        audio_item_id: audio_item_id.to_string(),
        stage: "Transcribed".into(),
        task_type: "transcribe".into(),
        status: status.to_string(),
        priority: 10,
        retry_count: 0,
        max_retries: 3,
        progress: 0.0,
        prompt_text: None,
        result_text: None,
        error: None,
        submitted_at: "2026-01-01T00:00:00Z".into(),
        started_at: None,
        completed_at: None,
    }
}

#[tokio::test]
async fn test_audio_item_crud() {
    let db = Database::open_in_memory().unwrap();

    // Add
    let item = make_audio_item("a1");
    db.add_audio_item(&item).await.unwrap();

    // List
    let list = db.list_audio_items().await.unwrap();
    assert_eq!(list.len(), 1);
    assert_eq!(list[0].id, "a1");
    assert_eq!(list[0].file_name, "a1.wav");

    // Get
    let got = db.get_audio_item("a1").await.unwrap();
    assert!(got.is_some());
    assert_eq!(got.unwrap().duration_ms, 30000);

    // Delete
    db.delete_audio_item("a1").await.unwrap();
    let list2 = db.list_audio_items().await.unwrap();
    assert!(list2.is_empty());
}

#[tokio::test]
async fn test_lifecycle_init_and_upsert() {
    let db = Database::open_in_memory().unwrap();

    let item = make_audio_item("a2");
    db.add_audio_item(&item).await.unwrap();

    // Init lifecycle stages
    db.init_lifecycle_stages("a2").await.unwrap();

    // Query lifecycle — should have 8 stages
    let lc = db.get_audio_lifecycle("a2").await.unwrap();
    assert_eq!(lc.len(), 8);
    assert!(lc.iter().all(|r| r.status == "Pending"));

    // Upsert one stage to change status
    let updated = AudioLifecycleRow {
        id: "a2-Transcribed".into(),
        audio_item_id: "a2".into(),
        stage: "Transcribed".into(),
        status: "Completed".into(),
        result_text: Some("Transcript text".into()),
        result_json: None,
        model_id: Some("whisper-large-v3".into()),
        token_used: Some(500),
        error: None,
        started_at: Some("2026-01-01T00:01:00Z".into()),
        completed_at: Some("2026-01-01T00:02:00Z".into()),
    };
    db.upsert_lifecycle(&updated).await.unwrap();

    // Verify the update
    let lc2 = db.get_audio_lifecycle("a2").await.unwrap();
    let transcribed = lc2.iter().find(|r| r.stage == "Transcribed").unwrap();
    assert_eq!(transcribed.status, "Completed");
    assert_eq!(transcribed.result_text.as_deref(), Some("Transcript text"));
    assert_eq!(transcribed.model_id.as_deref(), Some("whisper-large-v3"));
    assert_eq!(transcribed.token_used, Some(500));
}

#[tokio::test]
async fn test_task_submit_and_stats() {
    let db = Database::open_in_memory().unwrap();

    let item = make_audio_item("a3");
    db.add_audio_item(&item).await.unwrap();

    // Submit 2 tasks: one Queued, one to be Completed
    let t1 = make_task("t1", "a3", "Queued");
    db.submit_task(&t1).await.unwrap();

    let t2 = make_task("t2", "a3", "Queued");
    db.submit_task(&t2).await.unwrap();

    // List tasks
    let tasks = db.list_tasks(None, 100).await.unwrap();
    assert_eq!(tasks.len(), 2);

    // Stats — both Queued
    let stats = db.get_task_stats().await.unwrap();
    assert_eq!(stats.queued, 2);

    // Complete t2
    db.update_task_status_new("t2", "Completed", None).await.unwrap();

    // Verify stats after completion
    let stats2 = db.get_task_stats().await.unwrap();
    assert_eq!(stats2.queued, 1);
    assert_eq!(stats2.completed, 1);
}
