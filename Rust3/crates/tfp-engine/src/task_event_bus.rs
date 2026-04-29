use tokio::sync::broadcast;
use tfp_core::TaskBusEvent;

/// Task event bus — publish/subscribe for task lifecycle events.
///
/// Internal subscribers (e.g. future SSE/WebSocket handlers) can subscribe.
/// The EventSink implementations publish events here.
pub struct TaskEventBus {
    tx: broadcast::Sender<TaskBusEvent>,
}

impl TaskEventBus {
    pub fn new() -> Self {
        let (tx, _) = broadcast::channel(256);
        Self { tx }
    }

    /// Publish an event to all subscribers.
    pub fn publish(&self, event: TaskBusEvent) {
        let _ = self.tx.send(event);
    }

    /// Subscribe to the event stream (for SSE / WebSocket / tests).
    #[allow(dead_code)]
    pub fn subscribe(&self) -> broadcast::Receiver<TaskBusEvent> {
        self.tx.subscribe()
    }
}

impl Default for TaskEventBus {
    fn default() -> Self {
        Self::new()
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
        bus.publish(TaskBusEvent::Started {
            task_id: "t-002".into(),
            stage: "running".into(),
        });
    }

    #[test]
    fn test_bus_event_serde_roundtrip() {
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
}
