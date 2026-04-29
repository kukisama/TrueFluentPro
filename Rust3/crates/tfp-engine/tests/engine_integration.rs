//! Integration test for task engine event bus and lifecycle.
//!
//! Run with: cargo test -p tfp-engine --test engine_integration

use std::sync::Arc;

use tfp_core::{EventSink, TaskBusEvent, TaskFrontendEvent};
use tfp_engine::TaskEventBus;
use tfp_storage::Database;

/// Minimal EventSink for test assertions.
struct TestSink {
    events: std::sync::Mutex<Vec<String>>,
}

impl TestSink {
    fn new() -> Self {
        Self { events: std::sync::Mutex::new(Vec::new()) }
    }
    fn event_count(&self) -> usize {
        self.events.lock().unwrap().len()
    }
}

impl EventSink for TestSink {
    fn emit_task_bus_event(&self, _event: TaskBusEvent) {}
    fn emit_task_event(&self, event: TaskFrontendEvent) {
        self.events.lock().unwrap().push(event.event_type);
    }
    fn emit_monitor_refresh(&self) {}
    fn emit_json(&self, _event_name: &str, _payload: serde_json::Value) {}
}

#[tokio::test]
async fn test_task_event_bus_publish_subscribe() {
    let bus = TaskEventBus::new();
    let mut rx = bus.subscribe();

    // Publish a Submitted event
    bus.publish(TaskBusEvent::Submitted {
        task_id: "task-integration-001".into(),
        stage: "Transcribed".into(),
        task_type: "Transcription".into(),
    });

    // Should receive it
    let event = rx.try_recv().expect("should receive submitted event");
    match event {
        TaskBusEvent::Submitted { task_id, stage, task_type } => {
            assert_eq!(task_id, "task-integration-001");
            assert_eq!(stage, "Transcribed");
            assert_eq!(task_type, "Transcription");
        }
        other => panic!("unexpected event: {other:?}"),
    }

    // Publish lifecycle sequence
    bus.publish(TaskBusEvent::Started {
        task_id: "task-integration-001".into(),
        stage: "Transcribed".into(),
    });
    bus.publish(TaskBusEvent::ProgressChanged {
        task_id: "task-integration-001".into(),
        progress: 0.5,
        progress_message: Some("Halfway done".into()),
    });
    bus.publish(TaskBusEvent::Completed {
        task_id: "task-integration-001".into(),
        stage: "Transcribed".into(),
    });

    let e1 = rx.try_recv().unwrap();
    assert!(matches!(e1, TaskBusEvent::Started { .. }));

    let e2 = rx.try_recv().unwrap();
    match e2 {
        TaskBusEvent::ProgressChanged { progress, progress_message, .. } => {
            assert!((progress - 0.5).abs() < f64::EPSILON);
            assert_eq!(progress_message, Some("Halfway done".into()));
        }
        other => panic!("unexpected: {other:?}"),
    }

    let e3 = rx.try_recv().unwrap();
    assert!(matches!(e3, TaskBusEvent::Completed { .. }));
}

#[tokio::test]
async fn test_task_submit_and_query() {
    // Test that we can submit a task to DB and query it back
    let db = Arc::new(Database::open_in_memory().unwrap());

    let task = tfp_core::AudioTaskRow {
        id: "task-eng-001".into(),
        audio_item_id: "audio-001".into(),
        stage: "Transcribed".into(),
        task_type: "Transcription".into(),
        status: "Queued".into(),
        priority: 5,
        retry_count: 0,
        max_retries: 3,
        progress: 0.0,
        prompt_text: None,
        result_text: None,
        error: None,
        submitted_at: "2026-04-30T00:00:00Z".into(),
        started_at: None,
        completed_at: None,
    };

    db.submit_task(&task).await.unwrap();

    // Query it back
    let tasks = db.list_tasks(Some("Queued"), 10).await.unwrap();
    assert!(!tasks.is_empty(), "Should have at least one queued task");
    let found = tasks.iter().find(|t| t.id == "task-eng-001");
    assert!(found.is_some(), "Should find submitted task");
    assert_eq!(found.unwrap().status, "Queued");

    // Update status
    db.update_task_status_new("task-eng-001", "Executing", None).await.unwrap();
    let tasks = db.list_tasks(Some("Executing"), 10).await.unwrap();
    let found = tasks.iter().find(|t| t.id == "task-eng-001");
    assert!(found.is_some());
    assert_eq!(found.unwrap().status, "Executing");

    // Complete
    db.update_task_status_new("task-eng-001", "Completed", None).await.unwrap();
    let tasks = db.list_tasks(Some("Completed"), 10).await.unwrap();
    let found = tasks.iter().find(|t| t.id == "task-eng-001");
    assert!(found.is_some());
    assert_eq!(found.unwrap().status, "Completed");
}

#[tokio::test]
async fn test_event_sink_receives_events() {
    let sink = Arc::new(TestSink::new());

    // Simulate engine publishing events through the sink
    sink.emit_task_event(TaskFrontendEvent {
        event_type: "TaskStarted".into(),
        payload: serde_json::json!({"task_id": "t-100"}),
    });
    sink.emit_task_event(TaskFrontendEvent {
        event_type: "TaskCompleted".into(),
        payload: serde_json::json!({"task_id": "t-100"}),
    });

    assert_eq!(sink.event_count(), 2);
}

#[tokio::test]
async fn test_multiple_subscribers() {
    let bus = TaskEventBus::new();
    let mut rx1 = bus.subscribe();
    let mut rx2 = bus.subscribe();

    bus.publish(TaskBusEvent::Failed {
        task_id: "t-multi".into(),
        error: "timeout".into(),
    });

    // Both should receive
    assert!(rx1.try_recv().is_ok());
    assert!(rx2.try_recv().is_ok());
}
