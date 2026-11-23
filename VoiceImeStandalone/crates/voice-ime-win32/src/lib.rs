//! Windows 底层系统集成封装层（与业务逻辑彻底解耦）。
//!
//! 模块职责：
//! - [`paths`]：原生 64 位 `ProgramFilesDir` 解析、本机数据目录、防重解析点（Symlink/Junction）路径守卫与当前进程 SID/Session 身份；
//! - [`locks`]：`owner.lock` 单实例独占锁、`microphone.lock` 麦克风锁、进程回执与 `maintenance.lock` 100ms 门禁监控；
//! - [`pipe_server`]：支持当前用户与 AppContainer 沙盒连接的异步命名管道服务端及客户端 PID/前台焦点校验；
//! - [`toolbar_ffi`]：专用 COM STA 线程动态加载 `VoiceImeStandalone.dll` 驱动原生 GDI+ 悬浮条；
//! - [`installer`]：多架构（`win-x64` / `win-x86` / `win-arm64x`）安装、修复、卸载与 UAC 同用户提权执行器。

pub mod installer;
pub mod locks;
pub mod paths;
pub mod pipe_server;
pub mod toolbar_ffi;

pub use installer::{
    execute_elevated_worker_from_cli, InstallOperationResult, InstallStateSummary,
    RegistrationStateKind, VoiceImeInstaller,
};
pub use locks::{ExclusiveFileLock, HostReceipt, MaintenanceGate};
pub use paths::{
    current_process_identity, guard_local_path, install_root_dir, maintenance_lock_path,
    standalone_local_data_dir, ProcessIdentity,
};
pub use pipe_server::{
    accept_Windows_or_test_pipe_placeholder, ClientIdentityVerifier, PipePeerVerifier,
    Win32PeerVerifier,
};
pub use toolbar_ffi::{NoopToolbarDriver, ToolbarAction, ToolbarDriver, Win32NativeToolbarDriver};
