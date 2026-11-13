//! 多架构系统安装/修复/卸载事务引擎与同 SID UAC 提权执行器（阶段 2 Rust 版 `VoiceImeLifecycle`）。
//!
//! 职责：
//! 1. 探测打包目录中存在的 `win-x64` / `win-x86` / `win-arm64x` 原生组件；
//! 2. 查询每个架构 `VoiceImeRegister.exe --status <dll>` 与 `--probe`；
//! 3. 管理员模式下执行：置 `maintenance.lock` → 等待后台宿主安静退出 → Staged 部署至内容寻址目录
//!    `%ProgramFiles%\TrueFluentVoiceIme\<64hex>\<arch>\VoiceImeStandalone.dll` → 调用原生注册助手 →
//!    后置校验 → 移除 `maintenance.lock` → 清理旧版或排入 `MoveFileExW` 重启删除队列；
//! 4. 普通权限 UI 发起安装/卸载时，自动拉起同可执行文件的 UAC 提权子任务并回传结构化 JSON 结果。

use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::time::Duration;

use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use voice_ime_protocol::StandaloneIdentityContract;

use crate::locks::HostReceipt;
use crate::paths::{
    current_process_identity, guard_local_path, install_root_dir, is_current_process_elevated,
    maintenance_lock_path, standalone_local_data_dir,
};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum RegistrationStateKind {
    Registered,
    Unregistered,
    Partial,
    DifferentPath,
    LegacyUser,
    NotSupportedPlatform,
    Unknown,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct InstallStateSummary {
    pub is_windows: bool,
    pub state: RegistrationStateKind,
    pub status_text: String,
    pub requires_admin: bool,
    pub maintenance_active: bool,
    pub installed_architectures: Vec<String>,
    pub version_text: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct InstallOperationResult {
    pub succeeded: bool,
    pub requires_elevation: bool,
    pub cleanup_pending_reboot: bool,
    pub message: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
struct InstallManifest {
    version: u32,
    phase: String,
    package_hash: String,
    owner_sid: String,
    host_path: String,
    entries: Vec<ArchEntry>,
    pending_reboot_cleanup: Vec<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct ArchEntry {
    architecture: String,
    dll_path: String,
    helper_path: String,
}

#[derive(Deserialize)]
struct HelperStatusJson {
    registered: bool,
    state: String,
    #[serde(rename = "requiresAdmin")]
    requires_admin: bool,
    #[serde(default)]
    maintenance: bool,
}

pub struct VoiceImeInstaller {
    app_base_dir: PathBuf,
}

impl VoiceImeInstaller {
    pub fn new(app_base_dir: impl Into<PathBuf>) -> Self {
        Self {
            app_base_dir: app_base_dir.into(),
        }
    }

    pub fn from_current_exe() -> Self {
        let base = std::env::current_exe()
            .ok()
            .and_then(|p| p.parent().map(|d| d.to_path_buf()))
            .unwrap_or_else(|| PathBuf::from("."));
        Self::new(base)
    }

    /// 定位当前应用包随附的指定架构 `VoiceImeStandalone.dll`（供守护进程悬浮条与安装器使用）。
    pub fn packaged_native_dll(&self, arch: &str) -> PathBuf {
        self.app_base_dir
            .join("voice-ime")
            .join(arch)
            .join(StandaloneIdentityContract::DLL_FILE_NAME)
    }

    pub fn current_arch_folder() -> &'static str {
        #[cfg(target_arch = "aarch64")]
        {
            "win-arm64x"
        }
        #[cfg(target_arch = "x86")]
        {
            "win-x86"
        }
        #[cfg(not(any(target_arch = "aarch64", target_arch = "x86")))]
        {
            "win-x64"
        }
    }

    fn expected_architectures(&self) -> Vec<String> {
        let mut out = Vec::new();
        for cand in ["win-arm64x", "win-x64", "win-x86"] {
            let dir = self.app_base_dir.join("voice-ime").join(cand);
            if dir.join(StandaloneIdentityContract::DLL_FILE_NAME).exists()
                && dir.join(StandaloneIdentityContract::HELPER_FILE_NAME).exists()
            {
                // ARM64X 与 x64 共享 64 位槽位，优先使用已构建的架构目录
                if cand == "win-x64" && out.iter().any(|x| x == "win-arm64x") {
                    continue;
                }
                out.push(cand.to_string());
            }
        }
        if out.is_empty() {
            out.push("win-x64".into());
            out.push("win-x86".into());
        }
        out
    }

    fn manifest_path() -> Result<PathBuf, String> {
        Ok(install_root_dir()?.join("install-manifest.json"))
    }

    fn read_manifest() -> InstallManifest {
        let Ok(path) = Self::manifest_path() else {
            return InstallManifest::default();
        };
        fs::read_to_string(path)
            .ok()
            .and_then(|s| serde_json::from_str(&s).ok())
            .unwrap_or_default()
    }

    fn write_manifest(manifest: &InstallManifest) -> Result<(), String> {
        let path = Self::manifest_path()?;
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent).map_err(|e| e.to_string())?;
        }
        let raw = serde_json::to_vec_pretty(manifest).map_err(|e| e.to_string())?;
        fs::write(&path, raw).map_err(|e| format!("写入安装清单失败：{e}"))
    }

    /// 只读查询当前系统安装登记状态（绝不弹 UAC，绝不写盘）。
    pub fn query_status(&self) -> InstallStateSummary {
        let ver = format!("独立版 v{}", env!("CARGO_PKG_VERSION"));
        if !cfg!(windows) {
            return InstallStateSummary {
                is_windows: false,
                state: RegistrationStateKind::NotSupportedPlatform,
                status_text: "当前非 Windows 环境（系统 TSF 注册仅在 Windows 上启用）。".into(),
                requires_admin: false,
                maintenance_active: false,
                installed_architectures: Vec::new(),
                version_text: ver,
            };
        }

        let manifest = Self::read_manifest();
        let archs = if !manifest.entries.is_empty() {
            manifest.entries.clone()
        } else {
            self.expected_architectures()
                .into_iter()
                .map(|arch| {
                    let dir = self.app_base_dir.join("voice-ime").join(&arch);
                    ArchEntry {
                        architecture: arch,
                        dll_path: dir
                            .join(StandaloneIdentityContract::DLL_FILE_NAME)
                            .to_string_lossy()
                            .to_string(),
                        helper_path: dir
                            .join(StandaloneIdentityContract::HELPER_FILE_NAME)
                            .to_string_lossy()
                            .to_string(),
                    }
                })
                .collect()
        };

        let mut registered_archs = Vec::new();
        let mut any_unregistered = false;
        let mut any_partial = false;
        let mut any_conflict = false;
        let mut maintenance = false;

        for entry in &archs {
            let helper = self.resolve_usable_helper(entry);
            if !helper.exists() {
                return InstallStateSummary {
                    is_windows: true,
                    state: RegistrationStateKind::Unknown,
                    status_text: format!(
                        "尚未发现 {} 原生组件产物，请先执行构建脚本编译原生层。",
                        entry.architecture
                    ),
                    requires_admin: true,
                    maintenance_active: false,
                    installed_architectures: Vec::new(),
                    version_text: ver,
                };
            }
            match run_helper(&helper, &["--status", &entry.dll_path]) {
                Ok((code, stdout)) if code == 0 || code == 1 => {
                    if let Ok(parsed) = serde_json::from_str::<HelperStatusJson>(stdout.trim()) {
                        maintenance |= parsed.maintenance;
                        match parsed.state.as_str() {
                            "registered" if parsed.registered => {
                                registered_archs.push(entry.architecture.clone());
                            }
                            "unregistered" => any_unregistered = true,
                            "different-path" => any_conflict = true,
                            _ => any_partial = true,
                        }
                    } else {
                        any_partial = true;
                    }
                }
                _ => {
                    return InstallStateSummary {
                        is_windows: true,
                        state: RegistrationStateKind::Unknown,
                        status_text: "原生注册助手返回了无法识别的状态，请检查组件版本。".into(),
                        requires_admin: true,
                        maintenance_active: false,
                        installed_architectures: Vec::new(),
                        version_text: ver,
                    };
                }
            }
        }

        let state = if any_conflict {
            RegistrationStateKind::DifferentPath
        } else if !registered_archs.is_empty() && !any_unregistered && !any_partial && !maintenance {
            RegistrationStateKind::Registered
        } else if registered_archs.is_empty() && !any_partial && !maintenance {
            RegistrationStateKind::Unregistered
        } else {
            RegistrationStateKind::Partial
        };

        let status_text = match state {
            RegistrationStateKind::Registered => format!(
                "已完整安装并启用（架构：{}）· 按 F8 即可开始/停止语音输入。",
                registered_archs.join(" + ")
            ),
            RegistrationStateKind::Unregistered => {
                "当前未注册到 Windows 输入法列表。点击「安装 / 修复输入法」即可一键启用。".into()
            }
            RegistrationStateKind::DifferentPath => {
                "检测到其他路径的同名组件登记，点击「安装 / 修复输入法」可在校验所有权后迁移更新。".into()
            }
            RegistrationStateKind::Partial => {
                if maintenance {
                    "当前处于组件维护门禁状态，请点击「安装 / 修复输入法」或「卸载输入法」完成收敛。".into()
                } else {
                    "检测到部分架构或残缺登记，点击「安装 / 修复输入法」可自动修复补齐。".into()
                }
            }
            _ => "状态未知".into(),
        };

        InstallStateSummary {
            is_windows: true,
            state,
            status_text,
            requires_admin: true,
            maintenance_active: maintenance,
            installed_architectures: registered_archs,
            version_text: ver,
        }
    }

    /// 执行安装/修复或卸载：若当前未提权且请求 `allow_uac = true`，通过同 SID UAC 提权执行。
    pub async fn manage(&self, install: bool, allow_uac: bool) -> InstallOperationResult {
        if !cfg!(windows) {
            return InstallOperationResult {
                succeeded: false,
                requires_elevation: false,
                cleanup_pending_reboot: false,
                message: "系统 TSF 输入法注册仅支持在 Windows 上执行。".into(),
            };
        }

        if !is_current_process_elevated() {
            if !allow_uac {
                return InstallOperationResult {
                    succeeded: false,
                    requires_elevation: true,
                    cleanup_pending_reboot: false,
                    message: "输入法安装/卸载需要管理员权限，请确认后在 Windows UAC 提示中使用当前账户授权。日常 F8 听写无需管理员。".into(),
                };
            }
            return self.run_via_uac_worker(install).await;
        }

        self.execute_elevated_transaction(install).await
    }

    pub async fn execute_elevated_transaction(&self, install: bool) -> InstallOperationResult {
        let root = match install_root_dir() {
            Ok(r) => r,
            Err(msg) => return fail_op(msg),
        };
        let gate_file = match maintenance_lock_path() {
            Ok(p) => p,
            Err(msg) => return fail_op(msg),
        };
        if let Err(msg) = guard_local_path(&root) {
            return fail_op(msg);
        }
        if let Err(e) = fs::create_dir_all(&root) {
            return fail_op(format!("无法创建安装根目录：{e}"));
        }

        // 1. 置全局维护门禁 maintenance.lock
        if let Err(e) = fs::write(&gate_file, b"") {
            return fail_op(format!("无法写入维护门禁文件：{e}"));
        }

        // 2. 等待所有已启动的守护进程释放麦克风并优雅退出
        let data_dir = standalone_local_data_dir();
        let _mic_guard = match HostReceipt::wait_for_all_exited(&data_dir, Duration::from_secs(6)).await {
            Ok(g) => g,
            Err(msg) => return fail_op(msg),
        };

        let mut manifest = Self::read_manifest();
        if let Ok(id) = current_process_identity() {
            manifest.owner_sid = id.sid;
        }

        // 3. 若已有旧登记，先按记录精确反注册
        for old in &manifest.entries {
            let helper = self.resolve_usable_helper(old);
            if helper.exists() {
                if let Ok((code, out)) = run_helper(&helper, &["--unregister", &old.dll_path]) {
                    if code != 0 {
                        return fail_op(format!("移除旧版输入法登记失败 (code={code})：{out}"));
                    }
                }
            }
        }

        if !install {
            let pending = cleanup_old_files(&manifest.entries, &[]);
            manifest.entries.clear();
            manifest.pending_reboot_cleanup = pending.clone();
            manifest.phase = if pending.is_empty() {
                "removed".into()
            } else {
                "pending-reboot".into()
            };
            let _ = Self::write_manifest(&manifest);
            let _ = fs::remove_file(&gate_file);
            return InstallOperationResult {
                succeeded: true,
                requires_elevation: false,
                cleanup_pending_reboot: !pending.is_empty(),
                message: if pending.is_empty() {
                    "语音输入法已完整卸载：多架构 TSF 入口与自有组件文件已移除；个人配置与统计保留。".into()
                } else {
                    "语音输入法系统入口已卸载并停用；部分仍被目标应用加载的旧 DLL 已排入重启后自动清理队列。".into()
                },
            };
        }

        // 4. 计算包内容 SHA-256 并复制新包文件至 `%ProgramFiles%\TrueFluentVoiceIme\<64hex>\<arch>`
        let archs = self.expected_architectures();
        let host_exe = self.app_base_dir.join(StandaloneIdentityContract::HOST_EXE_NAME);
        if !host_exe.exists() {
            return fail_op(format!(
                "未找到守护进程可执行文件 {}，请先完成完整构建。",
                host_exe.display()
            ));
        }

        let pkg_hash = match compute_package_sha256(&self.app_base_dir, &archs) {
            Ok(h) => h,
            Err(msg) => return fail_op(msg),
        };

        let mut new_entries = Vec::new();
        for arch in &archs {
            let src_dir = self.app_base_dir.join("voice-ime").join(arch);
            let src_dll = src_dir.join(StandaloneIdentityContract::DLL_FILE_NAME);
            let src_helper = src_dir.join(StandaloneIdentityContract::HELPER_FILE_NAME);

            let dst_dir = root.join(&pkg_hash).join(arch);
            if let Err(e) = fs::create_dir_all(&dst_dir) {
                return fail_op(format!("创建架构目录失败：{e}"));
            }
            let dst_dll = dst_dir.join(StandaloneIdentityContract::DLL_FILE_NAME);
            let dst_helper = dst_dir.join(StandaloneIdentityContract::HELPER_FILE_NAME);

            if let Err(e) = fs::copy(&src_dll, &dst_dll) {
                return fail_op(format!("部署 {} DLL 失败：{e}", arch));
            }
            if let Err(e) = fs::copy(&src_helper, &dst_helper) {
                return fail_op(format!("部署 {} 注册助手失败：{e}", arch));
            }
            // 若同目录存在动态编译的 libIME2.dll，一并同步部署
            let src_libime = src_dir.join("libIME2.dll");
            if src_libime.exists() {
                let _ = fs::copy(&src_libime, dst_dir.join("libIME2.dll"));
            }

            new_entries.push(ArchEntry {
                architecture: arch.clone(),
                dll_path: dst_dll.to_string_lossy().to_string(),
                helper_path: dst_helper.to_string_lossy().to_string(),
            });
        }

        // 5. 逐架构调用原生注册助手写入 COM + TSF Profile/Category + 当前用户启用
        let host_str = host_exe.to_string_lossy().to_string();
        for entry in &new_entries {
            let helper = PathBuf::from(&entry.helper_path);
            match run_helper(&helper, &["--register", &entry.dll_path, &host_str]) {
                Ok((0, _)) => {}
                Ok((code, out)) => {
                    return fail_op(format!(
                        "{} 注册失败 (退出码 {code})：{}",
                        entry.architecture,
                        out.trim()
                    ));
                }
                Err(msg) => return fail_op(msg),
            }
            match run_helper(&helper, &["--probe"]) {
                Ok((0, _)) => {}
                _ => {
                    return fail_op(format!(
                        "{} 后置 TSF 枚举探测未通过，已保留维护门禁供重试。",
                        entry.architecture
                    ));
                }
            }
        }

        let pending = cleanup_old_files(&manifest.entries, &new_entries);
        manifest.version = 1;
        manifest.phase = "installed".into();
        manifest.package_hash = pkg_hash;
        manifest.host_path = host_str;
        manifest.entries = new_entries;
        manifest.pending_reboot_cleanup = pending.clone();
        let _ = Self::write_manifest(&manifest);
        let _ = fs::remove_file(&gate_file);

        InstallOperationResult {
            succeeded: true,
            requires_elevation: false,
            cleanup_pending_reboot: !pending.is_empty(),
            message: "独立语音输入法安装/修复完成（未篡改系统默认输入法；切换到「译见pro语音输入法」后按 F8 即可听写）。".into(),
        }
    }

    fn resolve_usable_helper(&self, entry: &ArchEntry) -> PathBuf {
        let pkg_helper = self
            .app_base_dir
            .join("voice-ime")
            .join(&entry.architecture)
            .join(StandaloneIdentityContract::HELPER_FILE_NAME);
        if pkg_helper.exists() {
            pkg_helper
        } else {
            PathBuf::from(&entry.helper_path)
        }
    }

    async fn run_via_uac_worker(&self, install: bool) -> InstallOperationResult {
        #[cfg(windows)]
        {
            let Ok(id) = current_process_identity() else {
                return fail_op("无法确认当前登录账户身份，已取消管理员提权。".into());
            };
            let host_exe = self.app_base_dir.join(StandaloneIdentityContract::HOST_EXE_NAME);
            if !host_exe.exists() {
                return fail_op("未找到 TrueFluentVoiceImeHost.exe 维护宿主。".into());
            }
            let data_dir = standalone_local_data_dir();
            let _ = fs::create_dir_all(&data_dir);
            let result_file = data_dir.join(format!(
                "uac-result-{}.json",
                uuid::Uuid::new_v4().simple()
            ));
            let op_arg = if install { "install" } else { "uninstall" };
            let params = format!(
                "--maintenance-worker {} \"{}\" \"{}\"",
                op_arg,
                id.sid,
                result_file.display()
            );

            let launch_res = tokio::task::spawn_blocking(move || {
                launch_runas_and_wait(&host_exe, &params)
            })
            .await
            .unwrap_or_else(|_| Err("等待管理员维护进程异常中断。".into()));

            let out = match launch_res {
                Ok(()) => fs::read_to_string(&result_file)
                    .ok()
                    .and_then(|s| serde_json::from_str::<InstallOperationResult>(&s).ok())
                    .unwrap_or_else(|| fail_op("管理员维护子进程未返回有效结果，请刷新状态确认。".into())),
                Err(msg) => fail_op(msg),
            };
            let _ = fs::remove_file(&result_file);
            out
        }
        #[cfg(not(windows))]
        {
            let _ = install;
            fail_op("非 Windows 平台不支持 UAC 提权。".into())
        }
    }
}

/// 供 `TrueFluentVoiceImeHost.exe --maintenance-worker <install|uninstall> <expected_sid> <result_json>` 调用。
pub async fn execute_elevated_worker_from_cli(args: &[String]) -> i32 {
    if args.len() != 4 {
        return 2;
    }
    let install = match args[1].as_str() {
        "install" => true,
        "uninstall" => false,
        _ => return 2,
    };
    let expected_sid = &args[2];
    let result_path = PathBuf::from(&args[3]);

    let Ok(current) = current_process_identity() else {
        return 3;
    };
    if !current.sid.eq_ignore_ascii_case(expected_sid) {
        let _ = write_worker_result(
            &result_path,
            &fail_op("UAC 授权使用了不同的管理员账户；为保障当前用户词库与输入法表隔离，请使用当前登录账户授权。".into()),
        );
        return 3;
    }
    if !is_current_process_elevated() {
        let _ = write_worker_result(&result_path, &fail_op("维护进程未获得管理员令牌。".into()));
        return 4;
    }

    let installer = VoiceImeInstaller::from_current_exe();
    let res = installer.execute_elevated_transaction(install).await;
    let code = if res.succeeded { 0 } else { 1 };
    let _ = write_worker_result(&result_path, &res);
    code
}

fn write_worker_result(path: &Path, res: &InstallOperationResult) -> Result<(), String> {
    if guard_local_path(path).is_err() {
        return Err("结果文件路径校验失败".into());
    }
    let json = serde_json::to_vec_pretty(res).map_err(|e| e.to_string())?;
    fs::write(path, json).map_err(|e| e.to_string())
}

fn fail_op(message: String) -> InstallOperationResult {
    InstallOperationResult {
        succeeded: false,
        requires_elevation: false,
        cleanup_pending_reboot: false,
        message,
    }
}

fn compute_package_sha256(base_dir: &Path, archs: &[String]) -> Result<String, String> {
    let mut parts = Vec::new();
    for arch in archs {
        let dir = base_dir.join("voice-ime").join(arch);
        let dll = dir.join(StandaloneIdentityContract::DLL_FILE_NAME);
        let helper = dir.join(StandaloneIdentityContract::HELPER_FILE_NAME);
        let dll_hash = sha256_file(&dll)?;
        let helper_hash = sha256_file(&helper)?;
        parts.push(format!("{arch}:{dll_hash}:{helper_hash}"));
    }
    let mut h = Sha256::new();
    h.update(parts.join("\n").as_bytes());
    Ok(hex::encode(h.finalize()))
}

fn sha256_file(path: &Path) -> Result<String, String> {
    let bytes = fs::read(path).map_err(|_| format!("无法读取组件文件 {}", path.display()))?;
    let mut h = Sha256::new();
    h.update(&bytes);
    Ok(hex::encode(h.finalize()))
}

fn run_helper(helper: &Path, args: &[&str]) -> Result<(i32, String), String> {
    let output = Command::new(helper)
        .args(args)
        .output()
        .map_err(|e| format!("无法启动注册助手 {}：{e}", helper.display()))?;
    let code = output.status.code().unwrap_or(6);
    let stdout = String::from_utf8_lossy(&output.stdout).to_string();
    Ok((code, stdout))
}

fn cleanup_old_files(old_entries: &[ArchEntry], keep_entries: &[ArchEntry]) -> Vec<String> {
    let mut pending = Vec::new();
    for old in old_entries {
        if keep_entries
            .iter()
            .any(|k| k.dll_path.eq_ignore_ascii_case(&old.dll_path))
        {
            continue;
        }
        for file_str in [&old.dll_path, &old.helper_path] {
            let p = Path::new(file_str);
            if p.exists() && fs::remove_file(p).is_err() {
                schedule_delete_on_reboot(p);
                pending.push(file_str.clone());
            }
        }
    }
    pending
}

#[cfg(windows)]
fn schedule_delete_on_reboot(path: &Path) {
    use std::os::windows::ffi::OsStrExt;
    use std::ptr::null;
    use windows_sys::Win32::Storage::FileSystem::{MoveFileExW, MOVEFILE_DELAY_UNTIL_REBOOT};
    let wide: Vec<u16> = path
        .as_os_str()
        .encode_wide()
        .chain(std::iter::once(0))
        .collect();
    unsafe {
        let _ = MoveFileExW(wide.as_ptr(), null(), MOVEFILE_DELAY_UNTIL_REBOOT);
    }
}

#[cfg(not(windows))]
fn schedule_delete_on_reboot(_path: &Path) {}

#[cfg(windows)]
fn launch_runas_and_wait(exe: &Path, params: &str) -> Result<(), String> {
    use std::os::windows::ffi::OsStrExt;
    use std::ptr::{null, null_mut};
    use windows_sys::Win32::Foundation::CloseHandle;
    use windows_sys::Win32::System::Threading::WaitForSingleObject;
    use windows_sys::Win32::UI::Shell::{
        ShellExecuteExW, SEE_MASK_NOCLOSEPROCESS, SHELLEXECUTEINFOW,
    };
    use windows_sys::Win32::UI::WindowsAndMessaging::SW_HIDE;

    let verb: Vec<u16> = "runas\0".encode_utf16().collect();
    let file: Vec<u16> = exe.as_os_str().encode_wide().chain(std::iter::once(0)).collect();
    let wide_params: Vec<u16> = params.encode_utf16().chain(std::iter::once(0)).collect();

    unsafe {
        let mut info: SHELLEXECUTEINFOW = std::mem::zeroed();
        info.cbSize = std::mem::size_of::<SHELLEXECUTEINFOW>() as u32;
        info.fMask = SEE_MASK_NOCLOSEPROCESS;
        info.lpVerb = verb.as_ptr();
        info.lpFile = file.as_ptr();
        info.lpParameters = wide_params.as_ptr();
        info.lpDirectory = null();
        info.nShow = SW_HIDE;

        if ShellExecuteExW(&mut info) == 0 {
            return Err("已取消 Windows 管理员授权，未修改输入法系统登记。".into());
        }
        if !info.hProcess.is_null() {
            let _ = WaitForSingleObject(info.hProcess, 120_000);
            CloseHandle(info.hProcess);
        }
    }
    Ok(())
}
