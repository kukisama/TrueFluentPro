//! 输入法安装管理控制器（只读状态查询、安装/修复、卸载及 UAC 引导）。

use voice_ime_win32::{InstallOperationResult, InstallStateSummary, VoiceImeInstaller};

pub fn query_installation_status(installer: &VoiceImeInstaller) -> InstallStateSummary {
    installer.query_status()
}

pub async fn run_installation_action(
    installer: &VoiceImeInstaller,
    install: bool,
    confirmed_elevation: bool,
) -> InstallOperationResult {
    installer.manage(install, confirmed_elevation).await
}
