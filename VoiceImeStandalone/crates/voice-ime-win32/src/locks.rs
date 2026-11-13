//! 独占文件锁（`owner.lock` / `microphone.lock`）、运行进程回执与 `maintenance.lock` 100ms 监控。

use std::fs;
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant};

use serde::{Deserialize, Serialize};
use tokio::sync::watch;

use crate::paths::{current_process_identity, guard_local_path};

/// 跨进程独占文件锁（进程退出或崩溃时由操作系统内核立即自动释放句柄）。
pub struct ExclusiveFileLock {
    #[cfg(windows)]
    handle: windows_sys::Win32::Foundation::HANDLE,
    #[cfg(not(windows))]
    _file: fs::File,
    path: PathBuf,
}

unsafe impl Send for ExclusiveFileLock {}
unsafe impl Sync for ExclusiveFileLock {}

impl ExclusiveFileLock {
    pub fn try_acquire(path: &Path) -> Result<Option<Self>, String> {
        guard_local_path(path)?;
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent).map_err(|e| format!("无法创建锁目录：{e}"))?;
        }
        #[cfg(windows)]
        {
            use std::os::windows::ffi::OsStrExt;
            use std::ptr::null;
            use windows_sys::Win32::Foundation::{
                GetLastError, ERROR_LOCK_VIOLATION, ERROR_SHARING_VIOLATION, GENERIC_READ,
                GENERIC_WRITE, INVALID_HANDLE_VALUE,
            };
            use windows_sys::Win32::Storage::FileSystem::{
                CreateFileW, FILE_ATTRIBUTE_NORMAL, OPEN_ALWAYS,
            };

            let wide: Vec<u16> = path
                .as_os_str()
                .encode_wide()
                .chain(std::iter::once(0))
                .collect();
            unsafe {
                // dwShareMode = 0 => FILE_SHARE_NONE 严格进程独占
                let h = CreateFileW(
                    wide.as_ptr(),
                    GENERIC_READ | GENERIC_WRITE,
                    0,
                    null(),
                    OPEN_ALWAYS,
                    FILE_ATTRIBUTE_NORMAL,
                    std::ptr::null_mut(),
                );
                if h == INVALID_HANDLE_VALUE {
                    let err = GetLastError();
                    if err == ERROR_SHARING_VIOLATION || err == ERROR_LOCK_VIOLATION {
                        return Ok(None);
                    }
                    return Err(format!("获取独占锁失败 (Win32 error {err})。"));
                }
                Ok(Some(Self {
                    handle: h,
                    path: path.to_path_buf(),
                }))
            }
        }
        #[cfg(not(windows))]
        {
            let file = fs::OpenOptions::new()
                .create(true)
                .read(true)
                .write(true)
                .truncate(false)
                .open(path)
                .map_err(|e| e.to_string())?;
            Ok(Some(Self {
                _file: file,
                path: path.to_path_buf(),
            }))
        }
    }

    pub fn path(&self) -> &Path {
        &self.path
    }
}

#[cfg(windows)]
impl Drop for ExclusiveFileLock {
    fn drop(&mut self) {
        unsafe {
            windows_sys::Win32::Foundation::CloseHandle(self.handle);
        }
    }
}

/// 守护进程存活回执（供维护/卸载流程精确等待自己启动的守护进程退出，绝不按名字盲杀进程）。
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct HostReceipt {
    pub pid: u32,
    pub start_ticks: u64,
    pub host_exe: String,
}

impl HostReceipt {
    pub fn record_current(data_dir: &Path) -> Result<PathBuf, String> {
        guard_local_path(data_dir)?;
        fs::create_dir_all(data_dir).map_err(|e| e.to_string())?;
        let id = current_process_identity()?;
        let receipt = Self {
            pid: id.pid,
            start_ticks: id.start_ticks_100ns,
            host_exe: id.exe_path.to_string_lossy().to_string(),
        };
        let file_path = data_dir.join(format!("host-{}-{}.json", receipt.pid, receipt.start_ticks));
        guard_local_path(&file_path)?;
        let bytes = serde_json::to_vec(&receipt).map_err(|e| e.to_string())?;
        fs::write(&file_path, bytes).map_err(|e| e.to_string())?;
        Ok(file_path)
    }

    /// 等待所有已记录的守护进程退出并占用 `microphone.lock`，确保维护阶段零采音、零进程残留。
    pub async fn wait_for_all_exited(data_dir: &Path, timeout: Duration) -> Result<ExclusiveFileLock, String> {
        let started = Instant::now();
        let mic_path = data_dir.join("microphone.lock");
        loop {
            if let Some(held) = ExclusiveFileLock::try_acquire(&mic_path)? {
                let mut any_alive = false;
                if data_dir.exists() {
                    if let Ok(entries) = fs::read_dir(data_dir) {
                        for entry in entries.flatten().take(64) {
                            let p = entry.path();
                            let name = p.file_name().and_then(|s| s.to_str()).unwrap_or("");
                            if !name.starts_with("host-") || !name.ends_with(".json") {
                                continue;
                            }
                            if is_receipt_alive(&p) {
                                any_alive = true;
                            } else {
                                let _ = fs::remove_file(&p);
                            }
                        }
                    }
                }
                if !any_alive {
                    return Ok(held);
                }
                drop(held);
            }
            if started.elapsed() >= timeout {
                return Err("输入法后台守护进程仍存活或麦克风尚未释放，请稍后重试。".into());
            }
            tokio::time::sleep(Duration::from_millis(100)).await;
        }
    }
}

fn is_receipt_alive(path: &Path) -> bool {
    let Ok(raw) = fs::read_to_string(path) else {
        return false;
    };
    let Ok(receipt) = serde_json::from_str::<HostReceipt>(&raw) else {
        return false;
    };
    #[cfg(windows)]
    {
        if let Ok(actual) = crate::paths::query_process_identity_by_pid(receipt.pid) {
            return actual.start_ticks_100ns == receipt.start_ticks
                && actual
                    .exe_path
                    .to_string_lossy()
                    .eq_ignore_ascii_case(&receipt.host_exe);
        }
        false
    }
    #[cfg(not(windows))]
    {
        let _ = receipt;
        false
    }
}

/// 维护门禁文件（`maintenance.lock`）检查与 100ms Fail-Closed 监控器。
#[derive(Clone)]
pub struct MaintenanceGate {
    gate_path: PathBuf,
}

impl MaintenanceGate {
    pub fn new(gate_path: impl Into<PathBuf>) -> Self {
        Self {
            gate_path: gate_path.into(),
        }
    }

    pub fn path(&self) -> &Path {
        &self.gate_path
    }

    /// 门禁文件存在或状态异常时返回 `true`（Fail-Closed 立即阻止新录音并停止当前宿主）。
    pub fn is_active(&self) -> bool {
        #[cfg(windows)]
        {
            use std::os::windows::ffi::OsStrExt;
            use windows_sys::Win32::Foundation::{
                GetLastError, ERROR_FILE_NOT_FOUND, ERROR_PATH_NOT_FOUND,
            };
            use windows_sys::Win32::Storage::FileSystem::{
                GetFileAttributesW, INVALID_FILE_ATTRIBUTES,
            };

            let wide: Vec<u16> = self
                .gate_path
                .as_os_str()
                .encode_wide()
                .chain(std::iter::once(0))
                .collect();
            unsafe {
                let attrs = GetFileAttributesW(wide.as_ptr());
                if attrs != INVALID_FILE_ATTRIBUTES {
                    return true;
                }
                let err = GetLastError();
                err != ERROR_FILE_NOT_FOUND && err != ERROR_PATH_NOT_FOUND
            }
        }
        #[cfg(not(windows))]
        {
            self.gate_path.exists()
        }
    }

    /// 启动 100ms 周期监控任务；一旦检测到 `maintenance.lock` 立即通过 `watch` 发出取消信号。
    pub fn spawn_monitor(&self) -> (watch::Receiver<bool>, tokio::task::JoinHandle<()>) {
        let (tx, rx) = watch::channel(self.is_active());
        let gate = self.clone();
        let handle = tokio::spawn(async move {
            let mut ticker = tokio::time::interval(Duration::from_millis(100));
            loop {
                ticker.tick().await;
                if gate.is_active() {
                    let _ = tx.send(true);
                    return;
                }
            }
        });
        (rx, handle)
    }
}
