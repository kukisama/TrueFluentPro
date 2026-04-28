use crate::Database;
use tfp_core::{AudioLibraryItem, AudioTaskRow};

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
async fn test_monitor_status_counts() {
    let db = Database::open_in_memory().unwrap();

    let item = make_audio_item("a1");
    db.add_audio_item(&item).await.unwrap();

    // Submit tasks with different statuses
    let t1 = make_task("t1", "a1", "Queued");
    db.submit_task(&t1).await.unwrap();

    let t2 = make_task("t2", "a1", "Queued");
    db.submit_task(&t2).await.unwrap();

    // Complete one
    db.update_task_status_new("t2", "Completed", None).await.unwrap();

    // Check monitor status counts
    let counts = db.monitor_get_status_counts().await.unwrap();
    assert_eq!(counts.get("Queued").copied().unwrap_or(0), 1);
    assert_eq!(counts.get("Completed").copied().unwrap_or(0), 1);
}

#[tokio::test]
async fn test_monitor_insert_and_get_execution() {
    let db = Database::open_in_memory().unwrap();

    let item = make_audio_item("a2");
    db.add_audio_item(&item).await.unwrap();

    let task = make_task("t1", "a2", "Queued");
    db.submit_task(&task).await.unwrap();

    // Insert execution via monitor
    db.monitor_insert_execution(
        "exec-1",
        "t1",
        "completed",
        true,
        Some("gpt-4o"),
        Some(100),
        Some(200),
        Some(1500),
        None,
        Some("prompt text"),
        Some("response text"),
        "2026-01-01T00:00:00Z",
        Some("2026-01-01T00:01:00Z"),
    ).await.unwrap();

    // Get executions for task
    let execs = db.monitor_get_executions("t1").await.unwrap();
    assert_eq!(execs.len(), 1);
    assert_eq!(execs[0].id, "exec-1");
    assert_eq!(execs[0].task_id, "t1");
    assert_eq!(execs[0].status, "completed");
    assert!(execs[0].billable);
    assert_eq!(execs[0].model_name.as_deref(), Some("gpt-4o"));
    assert_eq!(execs[0].tokens_in, Some(100));
    assert_eq!(execs[0].tokens_out, Some(200));

    // Get by ID
    let exec = db.monitor_get_execution_by_id("exec-1").await.unwrap();
    assert_eq!(exec.id, "exec-1");
    assert_eq!(exec.debug_prompt.as_deref(), Some("prompt text"));
    assert_eq!(exec.debug_response.as_deref(), Some("response text"));
}
