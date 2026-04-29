use tauri::Emitter;
use tfp_core::{EventSink, TaskBusEvent, TaskFrontendEvent};

/// Tauri-specific implementation of EventSink.
///
/// Bridges the framework-agnostic engine events to Tauri's `app.emit()` IPC.
pub struct TauriEventSink {
    app_handle: tauri::AppHandle,
}

impl TauriEventSink {
    pub fn new(app_handle: tauri::AppHandle) -> Self {
        Self { app_handle }
    }
}

impl EventSink for TauriEventSink {
    fn emit_task_bus_event(&self, _event: TaskBusEvent) {
        // Bus events are consumed by internal subscribers, not directly emitted to Tauri.
        // The engine calls notify_monitor() which uses emit_task_event + emit_monitor_refresh.
    }

    fn emit_task_event(&self, event: TaskFrontendEvent) {
        let _ = self.app_handle.emit("task-event", serde_json::json!({
            "type": event.event_type,
            "payload": event.payload,
        }));
    }

    fn emit_monitor_refresh(&self) {
        let _ = self.app_handle.emit("monitor-snapshot-update", serde_json::Value::Null);
    }

    fn emit_json(&self, event_name: &str, payload: serde_json::Value) {
        let _ = self.app_handle.emit(event_name, payload);
    }
}
