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
}
