// Windows 发布构建隐藏控制台黑框
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use std::sync::Arc;
use tokio::sync::mpsc;

use voice_ime_config::{ConfigStore, RuntimeConfigCache};
use voice_ime_daemon::{
    serve_duplex_peer, OnDemandTauriToolsLauncher, SharedDaemonActor, ToolsWindowLauncher,
};
use voice_ime_protocol::StandaloneIdentityContract;
use voice_ime_speech::UspSpeechEngineFactory;
use voice_ime_storage::{default_usage_db_path, UsageWriterService};
use voice_ime_win32::{
    current_process_identity, execute_elevated_worker_from_cli, maintenance_lock_path,
    standalone_local_data_dir, ExclusiveFileLock, HostReceipt, MaintenanceGate, NoopToolbarDriver,
    ToolbarDriver, VoiceImeInstaller, Win32NativeToolbarDriver, Win32PeerVerifier,
};

#[tokio::main]
async fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();

    // 1. 管理员维护提权子进程分流（不初始化日志/语音/管道，执行完立即退出）
    if args.first().map(|s| s.as_str()) == Some("--maintenance-worker") {
        let code = execute_elevated_worker_from_cli(&args).await;
        std::process::exit(code);
    }

    tracing_subscriber::fmt()
        .with_env_filter(
            tracing_subscriber::EnvFilter::try_from_default_env()
                .unwrap_or_else(|_| "warn,voice_ime_daemon=info".into()),
        )
        .init();

    // 2. 标准共享语音守护进程模式 (`--daemon` 或默认无参启动)
    if !args.is_empty() && !(args.len() == 1 && args[0] == "--daemon") {
        std::process::exit(2);
    }

    let code = run_shared_daemon().await;
    std::process::exit(code);
}

async fn run_shared_daemon() -> i32 {
    let Ok(identity) = current_process_identity() else {
        return 1;
    };
    let Ok(gate_path) = maintenance_lock_path() else {
        return 1;
    };
    let maintenance = MaintenanceGate::new(gate_path);
    if maintenance.is_active() {
        return 0;
    }

    let data_dir = standalone_local_data_dir();
    let owner_lock_path = data_dir.join(format!("owner-{}.lock", identity.session_id));
    let owner_lock = match ExclusiveFileLock::try_acquire(&owner_lock_path) {
        Ok(Some(lock)) => lock,
        Ok(None) => return 0, // 同一会话已有活跃守护进程，直接退出由客户端连接同一确定性管道
        Err(_) => return 1,
    };

    if maintenance.is_active() {
        drop(owner_lock);
        return 0;
    }

    let receipt_path = match HostReceipt::record_current(&data_dir) {
        Ok(path) => path,
        Err(error) => {
            tracing::error!(%error, "无法创建后台回执");
            return 1;
        }
    };
    if maintenance.is_active() {
        let _ = std::fs::remove_file(receipt_path);
        return 0;
    }

    let config_cache = Arc::new(RuntimeConfigCache::new(ConfigStore::default_store()));
    let speech_factory = Arc::new(UspSpeechEngineFactory::default());
    let verifier = Arc::new(Win32PeerVerifier::new(
        identity.session_id,
        identity.sid.clone(),
    ));
    let tools_launcher: Arc<dyn ToolsWindowLauncher> =
        Arc::new(OnDemandTauriToolsLauncher::from_current_exe());

    let usage = Arc::new(UsageWriterService::spawn(
        default_usage_db_path(),
        identity.session_id.to_string(),
    ));

    let (toolbar_tx, toolbar_rx) = mpsc::unbounded_channel();
    let installer = VoiceImeInstaller::from_current_exe();
    let dll_path = installer.packaged_native_dll(VoiceImeInstaller::current_arch_folder());

    let toolbar: Arc<dyn ToolbarDriver> = if dll_path.exists() {
        match Win32NativeToolbarDriver::spawn(dll_path, toolbar_tx).await {
            Ok(driver) => Arc::new(driver),
            Err(e) => {
                tracing::warn!(%e, "原生悬浮条加载失败，回退无窗口后台模式");
                Arc::new(NoopToolbarDriver)
            }
        }
    } else {
        Arc::new(NoopToolbarDriver)
    };

    let (daemon_handle, mut actor_join) = SharedDaemonActor::spawn(
        config_cache,
        speech_factory,
        verifier,
        toolbar,
        tools_launcher,
        Some(usage),
        maintenance,
        Some(data_dir.join("microphone.lock")),
        toolbar_rx,
    );

    let pipe_name = StandaloneIdentityContract::shared_pipe_name(identity.session_id, &identity.sid);
    let mut accept_task = spawn_pipe_accept_loop(pipe_name, identity.sid, daemon_handle.clone());

    tokio::select! {
        _ = &mut actor_join => accept_task.abort(),
        _ = &mut accept_task => {
            daemon_handle.shutdown().await;
            let _ = actor_join.await;
        }
    }

    let _ = std::fs::remove_file(receipt_path);
    drop(owner_lock);
    0
}

#[cfg(windows)]
fn spawn_pipe_accept_loop(
    pipe_name: String,
    user_sid: String,
    daemon: voice_ime_daemon::SharedDaemonHandle,
) -> tokio::task::JoinHandle<()> {
    use voice_ime_win32::pipe_server::windows_pipe::{
        create_secure_pipe_instance, query_pipe_client_pid,
    };

    tokio::spawn(async move {
        let mut first = true;
        loop {
            let Ok(permit) = Arc::clone(daemon.slots()).acquire_owned().await else {
                break;
            };
            let server = match create_secure_pipe_instance(&pipe_name, &user_sid, first) {
                Ok(s) => {
                    first = false;
                    s
                }
                Err(_) => break,
            };
            if server.connect().await.is_err() {
                drop(permit);
                continue;
            }
            let Some(client_pid) = query_pipe_client_pid(&server) else {
                drop(permit);
                continue;
            };
            let (reader, writer) = tokio::io::split(server);
            let h = daemon.clone();
            tokio::spawn(async move {
                serve_duplex_peer(reader, writer, client_pid, h).await;
                drop(permit);
            });
        }
    })
}

#[cfg(not(windows))]
fn spawn_pipe_accept_loop(
    _pipe_name: String,
    _user_sid: String,
    _daemon: voice_ime_daemon::SharedDaemonHandle,
) -> tokio::task::JoinHandle<()> {
    tokio::spawn(async move {
        std::future::pending::<()>().await;
    })
}
