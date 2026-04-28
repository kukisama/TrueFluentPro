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
