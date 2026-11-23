//! 多架构系统安装/修复/卸载事务引擎与同 SID UAC 提权执行器（阶段 2 Rust 版 `VoiceImeLifecycle`）。
//!
//! 职责：
//! 1. 探测打包目录中存在的 `win-x64` / `win-x86` / `win-arm64x` 原生组件；
//! 2. 查询每个架构 `VoiceImeRegister.exe --status <dll>` 与 `--probe`；
//! 3. 管理员模式下执行：置 `maintenance.lock` → 等待后台宿主安静退出 → Staged 部署至内容寻址目录
//!    `%ProgramFiles%\TrueFluentVoiceIme\<64hex>\<arch>\VoiceImeStandalone.dll` → 调用原生注册助手 →
//!    后置校验 → 移除 `maintenance.lock`；保留历史包以避免删除仍被应用加载的 DLL。
//! 4. 普通权限 UI 发起安装/卸载时，使用同账户 UAC 维护进程并检查退出状态。

use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::time::Duration;

use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use voice_ime_protocol::StandaloneIdentityContract;

use crate::locks::{ExclusiveFileLock, HostReceipt};
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
        self.component_dir(arch).join(StandaloneIdentityContract::DLL_FILE_NAME)
    }

    fn component_dir(&self, arch: &str) -> PathBuf {
        let deployed = self.app_base_dir.parent().is_some_and(|parent| {
            install_root_dir().is_ok_and(|root| root == parent)
        });
        if deployed {
            self.app_base_dir.join(arch)
        } else {
            self.app_base_dir.join("voice-ime").join(arch)
        }
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

    fn manifest_path() -> Result<PathBuf, String> {
        Ok(install_root_dir()?.join("install-manifest.json"))
    }

    fn read_manifest() -> Result<InstallManifest, String> {
        let path = Self::manifest_path()?;
        guard_local_path(&path)?;
        match fs::read_to_string(&path) {
            Ok(s) => {
                let manifest: InstallManifest = serde_json::from_str(&s)
                    .map_err(|_| "安装记录损坏；已停止维护，未修改系统登记。".to_string())?;
                validate_manifest(&manifest, path.parent().unwrap())?;
                Ok(manifest)
            }
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(InstallManifest::default()),
            Err(e) => Err(format!("无法读取安装记录：{e}")),
        }
    }

    fn write_manifest(manifest: &InstallManifest) -> Result<(), String> {
        let path = Self::manifest_path()?;
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent).map_err(|e| e.to_string())?;
        }
        use std::io::Write;
        guard_local_path(&path)?;
        let raw = serde_json::to_vec_pretty(manifest).map_err(|e| e.to_string())?;
        let tmp = path.with_file_name(format!("install-{}.tmp", uuid::Uuid::new_v4()));
        let result = (|| -> std::io::Result<()> {
            let mut file = fs::OpenOptions::new().write(true).create_new(true).open(&tmp)?;
            file.write_all(&raw)?;
            file.sync_all()?;
            drop(file);
            fs::rename(&tmp, &path)
        })();
        if result.is_err() {
            let _ = fs::remove_file(tmp);
        }
        result.map_err(|e| format!("写入安装记录失败：{e}"))
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

        let Ok(manifest) = Self::read_manifest() else {
            return status_unavailable(ver, "无法读取或校验安装记录，已停止查询。".into());
        };
        let Ok(required) = required_architectures() else {
            return status_unavailable(ver, "当前 Windows 架构不在此测试包的支持范围内。".into());
        };
        let archs = if !manifest.entries.is_empty() {
            manifest.entries.clone()
        } else {
            required.iter().copied()
                .map(|arch| {
                    let dir = self.component_dir(arch);
                    ArchEntry {
                        architecture: arch.into(),
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
        } else if required.iter().all(|arch| registered_archs.iter().any(|r| r == arch))
            && !any_unregistered && !any_partial && !maintenance {
            RegistrationStateKind::Registered
        } else if registered_archs.is_empty() && !any_partial {
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
        if !is_current_process_elevated() {
            return fail_op("系统注册操作需要当前账户管理员授权。".into());
        }
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

        match self.maintain(&root, &gate_file, install).await {
            Ok(message) => InstallOperationResult {
                succeeded: true, requires_elevation: false, cleanup_pending_reboot: false, message,
            },
            Err(message) => fail_op(message),
        }
    }

    async fn maintain(&self, root: &Path, gate: &Path, install: bool) -> Result<String, String> {
        let _transaction = ExclusiveFileLock::try_acquire(&root.join("installer.lock"))?
            .ok_or_else(|| "另一项输入法维护操作正在进行。".to_string())?;
        let old = Self::read_manifest()?;
        let sid = current_process_identity()?.sid;
        if !old.owner_sid.is_empty() && !old.owner_sid.eq_ignore_ascii_case(&sid) {
            return Err("当前系统登记属于另一 Windows 账户，请使用原安装账户维护。".into());
        }
        if old.entries.is_empty() && !install {
            if self.query_status().state != RegistrationStateKind::Unregistered {
                return Err("没有可确认的安装记录，未尝试卸载现有登记。".into());
            }
            if old.phase == "removed" && gate.exists() {
                guard_local_path(gate)?;
                fs::remove_file(gate).map_err(|e| format!("无法清除已卸载输入法的维护门禁：{e}"))?;
            }
            return Ok("此输入法未注册，无需卸载。".into());
        }
        let mut next = if install {
            self.stage_package(root, &sid)?
        } else {
            InstallManifest { version: 1, phase: "removed".into(), ..Default::default() }
        };
        guard_local_path(gate)?;
        let gate_was_active = gate.is_file();
        fs::write(gate, b"").map_err(|e| format!("无法设置维护门禁：{e}"))?;
        let _mic_guard = match HostReceipt::wait_for_all_exited(&standalone_local_data_dir(), Duration::from_secs(8)).await {
            Ok(held) => held,
            Err(msg) => {
                if !gate_was_active {
                    fs::remove_file(gate).map_err(|e| format!("{msg}；无法清除维护门禁：{e}"))?;
                }
                return Err(msg);
            }
        };

        // 同包修复不拆除已正确登记的部分；换包前保留旧记录直到旧登记全部移除。
        if !install || old.package_hash != next.package_hash || old.host_path != next.host_path {
            for entry in &old.entries {
                execute_helper(entry, &["--unregister", &entry.dll_path])?;
            }
        }
        if install {
            // 在任何新登记之前持久化恢复记录。中断后可从同一包修复，或按原路径卸载。
            Self::write_manifest(&next)?;
            for entry in &next.entries {
                execute_helper(entry, &["--register", &entry.dll_path, &next.host_path])?;
            }
            for entry in &next.entries {
                execute_helper(entry, &["--probe"])?;
                execute_helper(entry, &["--status", &entry.dll_path])?;
            }
            next.phase = "installed".into();
        }
        Self::write_manifest(&next)?;
        fs::remove_file(gate).map_err(|e| format!("无法清除维护门禁，请点击修复重试：{e}"))?;
        Ok(if install {
            "已注册独立语音输入法。切换到「译见pro语音输入法」后按 F8 听写。".into()
        } else {
            "已卸载独立输入法的 Windows 登记。程序文件、个人配置及用量统计保留。".into()
        })
    }

    fn stage_package(&self, root: &Path, sid: &str) -> Result<InstallManifest, String> {
        let archs = required_architectures()?;
        let mut files = Vec::new();
        for name in [StandaloneIdentityContract::HOST_EXE_NAME, StandaloneIdentityContract::TOOLS_EXE_NAME] {
            files.push((self.app_base_dir.join(name), PathBuf::from(name)));
        }
        for arch in archs.iter().copied() {
            let src = self.component_dir(arch);
            for name in [StandaloneIdentityContract::DLL_FILE_NAME, StandaloneIdentityContract::HELPER_FILE_NAME, "libIME2.dll"] {
                let path = src.join(name);
                if name != "libIME2.dll" || path.exists() {
                    files.push((path, PathBuf::from(arch).join(name)));
                }
            }
        }
        let planned = files.into_iter().map(|(src, rel)| {
            guard_local_path(&src)?;
            let digest = sha256_file(&src)?;
            Ok((src, rel, digest))
        }).collect::<Result<Vec<_>, String>>()?;
        let mut digest = Sha256::new();
        for (_, rel, hash) in &planned {
            digest.update(format!("{}:{hash}\n", rel.display()));
        }
        let hash = hex::encode(digest.finalize());
        let package = root.join(&hash);
        for (src, rel, expected) in planned {
            let dst = package.join(rel);
            guard_local_path(&dst)?;
            if sha256_file(&dst).is_ok_and(|actual| actual == expected) {
                continue;
            }
            if dst.exists() {
                return Err(format!("已部署组件的摘要不正确，已停止覆盖：{}", dst.display()));
            }
            let bytes = fs::read(&src).map_err(|e| format!("无法读取 {}：{e}", src.display()))?;
            if hex::encode(Sha256::digest(&bytes)) != expected {
                return Err("源包在维护期间发生变更，请重新解压后重试。".into());
            }
            fs::create_dir_all(dst.parent().unwrap()).map_err(|e| format!("无法创建部署目录：{e}"))?;
            let temp = dst.with_extension(format!("{}.tmp", uuid::Uuid::new_v4()));
            let result = fs::write(&temp, bytes).and_then(|()| fs::rename(&temp, &dst));
            if let Err(e) = result {
                let _ = fs::remove_file(temp);
                return Err(format!("无法部署 {}：{e}", dst.display()));
            }
        }
        Ok(InstallManifest {
            version: 1,
            phase: "installing".into(),
            package_hash: hash,
            owner_sid: sid.into(),
            host_path: package.join(StandaloneIdentityContract::HOST_EXE_NAME).to_string_lossy().into_owned(),
            entries: archs.iter().map(|arch| {
                let dir = package.join(arch);
                ArchEntry {
                    architecture: (*arch).into(),
                    dll_path: dir.join(StandaloneIdentityContract::DLL_FILE_NAME).to_string_lossy().into_owned(),
                    helper_path: dir.join(StandaloneIdentityContract::HELPER_FILE_NAME).to_string_lossy().into_owned(),
                }
            }).collect(),
            ..Default::default()
        })
    }

    fn resolve_usable_helper(&self, entry: &ArchEntry) -> PathBuf {
        let pkg_helper = self.component_dir(&entry.architecture)
            .join(StandaloneIdentityContract::HELPER_FILE_NAME);
        let recorded = PathBuf::from(&entry.helper_path);
        if recorded.is_file() { recorded } else { pkg_helper }
    }

    async fn run_via_uac_worker(&self, install: bool) -> InstallOperationResult {
        #[cfg(windows)]
        {
            let Ok(id) = current_process_identity() else {
                return fail_op("无法确认当前登录账户身份，已取消管理员提权。".into());
            };
            let host_exe = self.app_base_dir.join(StandaloneIdentityContract::HOST_EXE_NAME);
            if !host_exe.is_file() || guard_local_path(&host_exe).is_err() {
                return fail_op("未找到 TrueFluentVoiceImeHost.exe 维护宿主。".into());
            }
            let op_arg = if install { "install" } else { "uninstall" };
            let params = format!(
                "--maintenance-worker {op_arg} {} {}",
                id.pid, id.start_ticks_100ns
            );

            let launch_res = tokio::task::spawn_blocking(move || {
                launch_runas_and_wait(&host_exe, &params)
            })
            .await
            .unwrap_or_else(|_| Err("等待管理员维护进程异常中断。".into()));

            match launch_res {
                Ok(0) => InstallOperationResult {
                    succeeded: true, requires_elevation: false, cleanup_pending_reboot: false,
                    message: if install { "安装完成，请切换到独立输入法后按 F8。".into() }
                        else { "Windows 输入法登记已卸载；程序文件及个人资料保留。".into() },
                },
                Ok(3 | 4) => fail_op("维护进程未通过同账户、同会话的身份验证，未修改系统登记。".into()),
                Ok(code) => fail_op(format!("管理员维护未完成（退出码 {code}），请刷新状态并重试修复。")),
                Err(msg) => fail_op(msg),
            }
        }
        #[cfg(not(windows))]
        {
            let _ = install;
            fail_op("非 Windows 平台不支持 UAC 提权。".into())
        }
    }
}

/// 供 `TrueFluentVoiceImeHost.exe --maintenance-worker <install|uninstall> <caller_pid> <caller_start>` 调用。
pub async fn execute_elevated_worker_from_cli(args: &[String]) -> i32 {
    if args.len() != 4 {
        return 2;
    }
    let install = match args[1].as_str() {
        "install" => true,
        "uninstall" => false,
        _ => return 2,
    };
    if !is_current_process_elevated() {
        return 4;
    }
    if !verify_worker_caller(&args[2], &args[3]) {
        return 3;
    }
    let installer = VoiceImeInstaller::from_current_exe();
    let res = installer.execute_elevated_transaction(install).await;
    if res.succeeded { 0 } else { 1 }
}

#[cfg(windows)]
fn verify_worker_caller(pid: &str, start: &str) -> bool {
    let (Ok(pid), Ok(start), Ok(me)) = (pid.parse(), start.parse::<u64>(), current_process_identity()) else {
        return false;
    };
    let Ok(caller) = crate::paths::query_process_identity_by_pid(pid) else { return false; };
    let tools = me.exe_path.with_file_name(StandaloneIdentityContract::TOOLS_EXE_NAME);
    start != 0 && caller.start_ticks_100ns == start && caller.session_id == me.session_id
        && caller.sid.eq_ignore_ascii_case(&me.sid)
        && caller.exe_path.to_string_lossy().eq_ignore_ascii_case(&tools.to_string_lossy())
        && guard_local_path(&tools).is_ok()
}

#[cfg(not(windows))]
fn verify_worker_caller(_pid: &str, _start: &str) -> bool { false }

fn status_unavailable(version_text: String, status_text: String) -> InstallStateSummary {
    InstallStateSummary {
        is_windows: cfg!(windows), state: RegistrationStateKind::Unknown, status_text,
        requires_admin: true, maintenance_active: false, installed_architectures: Vec::new(), version_text,
    }
}

fn fail_op(message: String) -> InstallOperationResult {
    InstallOperationResult {
        succeeded: false,
        requires_elevation: false,
        cleanup_pending_reboot: false,
        message,
    }
}

#[cfg(windows)]
fn required_architectures() -> Result<&'static [&'static str], String> {
    use windows_sys::Win32::System::SystemInformation::{IMAGE_FILE_MACHINE_AMD64, IMAGE_FILE_MACHINE_ARM64};
    use windows_sys::Win32::System::Threading::{GetCurrentProcess, IsWow64Process2};
    let (mut process, mut native) = (0, 0);
    if unsafe { IsWow64Process2(GetCurrentProcess(), &mut process, &mut native) } == 0 {
        return Err("无法识别 Windows 原生架构，未修改系统登记。".into());
    }
    match native {
        IMAGE_FILE_MACHINE_AMD64 => Ok(&["win-x64", "win-x86"]),
        IMAGE_FILE_MACHINE_ARM64 => Ok(&["win-arm64x", "win-x86"]),
        _ => Err("此包仅支持 64 位 Windows。".into()),
    }
}

#[cfg(not(windows))]
fn required_architectures() -> Result<&'static [&'static str], String> { Err("仅支持 Windows。".into()) }

fn validate_manifest(manifest: &InstallManifest, root: &Path) -> Result<(), String> {
    let invalid = || "安装记录与本程序的受控部署路径不符，已停止维护。".to_string();
    if manifest.version != 1 {
        return Err(invalid());
    }
    if manifest.entries.is_empty() && manifest.phase == "removed" && manifest.package_hash.is_empty()
        && manifest.host_path.is_empty() {
        return Ok(());
    }
    if manifest.entries.is_empty() || manifest.owner_sid.is_empty()
        || !matches!(manifest.phase.as_str(), "installing" | "installed")
        || manifest.package_hash.len() != 64 || !manifest.package_hash.bytes().all(|b| b.is_ascii_hexdigit()) {
        return Err(invalid());
    }
    let package = root.join(&manifest.package_hash);
    if Path::new(&manifest.host_path) != package.join(StandaloneIdentityContract::HOST_EXE_NAME) {
        return Err(invalid());
    }
    let mut seen = std::collections::HashSet::new();
    for entry in &manifest.entries {
        if !matches!(entry.architecture.as_str(), "win-x64" | "win-x86" | "win-arm64x")
            || !seen.insert(&entry.architecture) {
            return Err(invalid());
        }
        let dir = package.join(&entry.architecture);
        if Path::new(&entry.dll_path) != dir.join(StandaloneIdentityContract::DLL_FILE_NAME)
            || Path::new(&entry.helper_path) != dir.join(StandaloneIdentityContract::HELPER_FILE_NAME) {
            return Err(invalid());
        }
    }
    Ok(())
}

fn sha256_file(path: &Path) -> Result<String, String> {
    let bytes = fs::read(path).map_err(|_| format!("无法读取组件文件 {}", path.display()))?;
    let mut h = Sha256::new();
    h.update(&bytes);
    Ok(hex::encode(h.finalize()))
}

fn run_helper(helper: &Path, args: &[&str]) -> Result<(i32, String), String> {
    guard_local_path(helper)?;
    let mut cmd = Command::new(helper);
    cmd.args(args);
    if let Some(dir) = helper.parent() { cmd.current_dir(dir); }
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        use windows_sys::Win32::System::Threading::CREATE_NO_WINDOW;
        cmd.creation_flags(CREATE_NO_WINDOW);
    }
    let output = cmd.output()
        .map_err(|e| format!("无法启动注册助手 {}：{e}", helper.display()))?;
    let code = output.status.code().unwrap_or(6);
    let stdout = String::from_utf8_lossy(&output.stdout).to_string();
    Ok((code, stdout))
}

fn execute_helper(entry: &ArchEntry, args: &[&str]) -> Result<(), String> {
    let (code, output) = run_helper(Path::new(&entry.helper_path), args)?;
    if code != 0 {
        return Err(format!("{} {} 失败（{code}）：{}", entry.architecture, args[0], output.trim()));
    }
    Ok(())
}

#[cfg(windows)]
fn launch_runas_and_wait(exe: &Path, params: &str) -> Result<u32, String> {
    use std::os::windows::ffi::OsStrExt;
    use windows_sys::Win32::Foundation::{CloseHandle, WAIT_OBJECT_0};
    use windows_sys::Win32::System::Threading::{GetExitCodeProcess, WaitForSingleObject, INFINITE};
    use windows_sys::Win32::UI::Shell::{
        ShellExecuteExW, SEE_MASK_NOCLOSEPROCESS, SHELLEXECUTEINFOW,
    };
    use windows_sys::Win32::UI::WindowsAndMessaging::SW_HIDE;

    let verb: Vec<u16> = "runas\0".encode_utf16().collect();
    let file: Vec<u16> = exe.as_os_str().encode_wide().chain(std::iter::once(0)).collect();
    let directory: Vec<u16> = exe.parent().unwrap().as_os_str().encode_wide().chain(std::iter::once(0)).collect();
    let wide_params: Vec<u16> = params.encode_utf16().chain(std::iter::once(0)).collect();

    unsafe {
        let mut info: SHELLEXECUTEINFOW = std::mem::zeroed();
        info.cbSize = std::mem::size_of::<SHELLEXECUTEINFOW>() as u32;
        info.fMask = SEE_MASK_NOCLOSEPROCESS;
        info.lpVerb = verb.as_ptr();
        info.lpFile = file.as_ptr();
        info.lpParameters = wide_params.as_ptr();
        info.lpDirectory = directory.as_ptr();
        info.nShow = SW_HIDE;

        if ShellExecuteExW(&mut info) == 0 {
            return Err("已取消 Windows 管理员授权，未修改输入法系统登记。".into());
        }
        if info.hProcess.is_null() {
            return Err("无法获取管理员维护进程句柄。".into());
        }
        let mut code = 0;
        let completed = WaitForSingleObject(info.hProcess, INFINITE) == WAIT_OBJECT_0
            && GetExitCodeProcess(info.hProcess, &mut code) != 0;
        CloseHandle(info.hProcess);
        if completed { Ok(code) } else { Err("无法确认管理员维护结果。".into()) }
    }
}
