use serde::{Deserialize, Serialize};
use tokio::sync::broadcast;

/// Task event types — aligned with C# TaskStatusChangedEvent / TaskProgressEvent
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "type", content = "data")]
pub enum TaskBusEvent {
    Submitted {
        task_id: String,
        stage: String,
        task_type: String,
    },
    Started {
        task_id: String,
        stage: String,
    },
    ProgressChanged {
        task_id: String,
        progress: f64,
        progress_message: Option<String>,
    },
    Completed {
        task_id: String,
        stage: String,
    },
    Failed {
        task_id: String,
        error: String,
    },
    Cancelled {
        task_id: String,
        reason: String,
    },
    Timeout {
        task_id: String,
    },
}

/// Task event bus singleton
pub struct TaskEventBus {
    tx: broadcast::Sender<TaskBusEvent>,
}

impl TaskEventBus {
    pub fn new() -> Self {
        let (tx, _) = broadcast::channel(256);
        Self { tx }
    }

    /// Publish an event
    pub fn publish(&self, event: TaskBusEvent) {
        let _ = self.tx.send(event);
    }

    /// Subscribe to the event stream (reserved for SSE / WebSocket)
    #[allow(dead_code)]
    pub fn subscribe(&self) -> broadcast::Receiver<TaskBusEvent> {
        self.tx.subscribe()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_publish_subscribe_roundtrip() {
        let bus = TaskEventBus::new();
        let mut rx = bus.subscribe();

        bus.publish(TaskBusEvent::Submitted {
            task_id: "t-001".into(),
            stage: "queued".into(),
            task_type: "translation".into(),
        });

        let event = rx.try_recv().expect("should receive event");
        match event {
            TaskBusEvent::Submitted { task_id, stage, task_type } => {
                assert_eq!(task_id, "t-001");
                assert_eq!(stage, "queued");
                assert_eq!(task_type, "translation");
            }
            other => panic!("unexpected event variant: {other:?}"),
        }
    }

    #[test]
    fn test_publish_without_subscriber_no_panic() {
        let bus = TaskEventBus::new();
        // No subscriber — should not panic
        bus.publish(TaskBusEvent::Started {
            task_id: "t-002".into(),
            stage: "running".into(),
        });
    }

    // ── T-001: TaskBusEvent serde contract tests ──

    #[test]
    fn test_submitted_serde() {
        let evt = TaskBusEvent::Submitted {
            task_id: "t1".into(),
            stage: "Transcribed".into(),
            task_type: "Transcription".into(),
        };
        let json: serde_json::Value = serde_json::to_value(&evt).unwrap();
        assert_eq!(json["type"], "Submitted");
        assert_eq!(json["data"]["task_id"], "t1");
        assert_eq!(json["data"]["stage"], "Transcribed");
        assert_eq!(json["data"]["task_type"], "Transcription");
        // Only "type" and "data" at top level
        let obj = json.as_object().unwrap();
        assert_eq!(obj.len(), 2);
        assert!(obj.contains_key("type"));
        assert!(obj.contains_key("data"));
    }

    #[test]
    fn test_progress_changed_serde() {
        let evt = TaskBusEvent::ProgressChanged {
            task_id: "t2".into(),
            progress: 0.75,
            progress_message: Some("Processing...".into()),
        };
        let json: serde_json::Value = serde_json::to_value(&evt).unwrap();
        assert_eq!(json["type"], "ProgressChanged");
        assert_eq!(json["data"]["progress"], 0.75);
        assert_eq!(json["data"]["progress_message"], "Processing...");
    }

    #[test]
    fn test_timeout_serde() {
        let evt = TaskBusEvent::Timeout {
            task_id: "t3".into(),
        };
        let json: serde_json::Value = serde_json::to_value(&evt).unwrap();
        assert_eq!(json["type"], "Timeout");
        assert_eq!(json["data"]["task_id"], "t3");
    }

    #[test]
    fn test_bus_event_roundtrip() {
        let variants: Vec<TaskBusEvent> = vec![
            TaskBusEvent::Submitted { task_id: "r1".into(), stage: "s".into(), task_type: "t".into() },
            TaskBusEvent::Started { task_id: "r2".into(), stage: "s".into() },
            TaskBusEvent::ProgressChanged { task_id: "r3".into(), progress: 0.5, progress_message: None },
            TaskBusEvent::Completed { task_id: "r4".into(), stage: "done".into() },
            TaskBusEvent::Failed { task_id: "r5".into(), error: "err".into() },
            TaskBusEvent::Cancelled { task_id: "r6".into(), reason: "user".into() },
            TaskBusEvent::Timeout { task_id: "r7".into() },
        ];
        let expected_ids = ["r1", "r2", "r3", "r4", "r5", "r6", "r7"];

        for (evt, expected_id) in variants.into_iter().zip(expected_ids.iter()) {
            let json_str = serde_json::to_string(&evt).unwrap();
            let restored: TaskBusEvent = serde_json::from_str(&json_str).unwrap();
            let restored_id = match &restored {
                TaskBusEvent::Submitted { task_id, .. } => task_id.as_str(),
                TaskBusEvent::Started { task_id, .. } => task_id.as_str(),
                TaskBusEvent::ProgressChanged { task_id, .. } => task_id.as_str(),
                TaskBusEvent::Completed { task_id, .. } => task_id.as_str(),
                TaskBusEvent::Failed { task_id, .. } => task_id.as_str(),
                TaskBusEvent::Cancelled { task_id, .. } => task_id.as_str(),
                TaskBusEvent::Timeout { task_id } => task_id.as_str(),
            };
            assert_eq!(restored_id, *expected_id);
        }
    }

    #[test]
    fn test_bus_publish_subscribe() {
        let bus = TaskEventBus::new();
        let mut rx = bus.subscribe();

        bus.publish(TaskBusEvent::Submitted {
            task_id: "sub-test".into(),
            stage: "init".into(),
            task_type: "Translation".into(),
        });

        let received = rx.try_recv().expect("should receive published event");
        match received {
            TaskBusEvent::Submitted { task_id, .. } => assert_eq!(task_id, "sub-test"),
            other => panic!("wrong variant: {other:?}"),
        }
    }
}
