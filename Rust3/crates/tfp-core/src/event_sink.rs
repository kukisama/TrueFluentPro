use serde::{Deserialize, Serialize};

/// Task lifecycle events — the engine publishes these, the shell listens.
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

/// Task-level frontend event (emitted as "task-event" to the shell).
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct TaskFrontendEvent {
    #[serde(rename = "type")]
    pub event_type: String,
    #[serde(flatten)]
    pub payload: serde_json::Value,
}

/// Abstract event output — the engine pushes events through this trait,
/// without knowing whether the listener is Tauri, axum SSE, or a test harness.
pub trait EventSink: Send + Sync {
    /// Emit a task lifecycle event to the bus (for internal subscribers).
    fn emit_task_bus_event(&self, event: TaskBusEvent);

    /// Emit a task frontend event (e.g. "TaskStarted", "TaskCompleted", "TaskFailed").
    fn emit_task_event(&self, event: TaskFrontendEvent);

    /// Emit a monitor snapshot refresh hint (throttled by implementation).
    fn emit_monitor_refresh(&self);

    /// Emit a generic JSON event (used by domain services for domain-specific events
    /// such as "studio-message-delta", "studio-task-update", "video-progress", etc.).
    fn emit_json(&self, event_name: &str, payload: serde_json::Value);
}
