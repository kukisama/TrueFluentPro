//! tfp-engine — 任务引擎（零 Tauri 耦合）

pub mod task_event_bus;
pub mod task_engine;
pub mod batch_coordinator;

pub use task_event_bus::TaskEventBus;
pub use task_engine::{TaskEngine, TaskEngineDeps};
pub use batch_coordinator::{BatchCoordinator, parse_batch_package_id, parse_batch_queue_item_id};
