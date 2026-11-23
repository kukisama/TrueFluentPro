//! 独立语音输入法守护进程核心库。
//!
//! - [`actor`]：单线程 Tokio Actor 会话协调器（1:1 承接 `VoiceImeSharedHost` 全部并发与防串窗不变量）；
//! - [`launcher`]：按需启动 Tauri 2 工具窗进程（点击时启动、关窗即退，彻底消灭 100MB+ 常驻 UI 预热进程）。

pub mod actor;
pub mod launcher;

pub use actor::{serve_duplex_peer, SharedDaemonActor, SharedDaemonHandle, ToolsTabRequest};
pub use launcher::{NoopToolsLauncher, OnDemandTauriToolsLauncher, ToolsWindowLauncher};
