//! PR-1.7: 任务事件总线 — 基于 tokio::sync::broadcast
//! 对标 C# TaskEventBus.cs

use serde::{Deserialize, Serialize};
use tokio::sync::broadcast;

/// 任务事件类型 — 对齐 C# TaskStatusChangedEvent / TaskProgressEvent
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

/// 任务事件总线单例
pub struct TaskEventBus {
    tx: broadcast::Sender<TaskBusEvent>,
}

impl TaskEventBus {
    pub fn new() -> Self {
        let (tx, _) = broadcast::channel(256);
        Self { tx }
    }

    /// 发布事件
    pub fn publish(&self, event: TaskBusEvent) {
        // 忽略无接收者的情况
        let _ = self.tx.send(event);
    }

    /// 订阅事件流（SSE / WebSocket 等场景保留）
    #[allow(dead_code)]
    pub fn subscribe(&self) -> broadcast::Receiver<TaskBusEvent> {
        self.tx.subscribe()
    }
}
