//! 按需拉起 Tauri 2 工具窗进程（`TrueFluentVoiceImeTools.exe --tab <settings|usage|language>`）。
//!
//! 关键架构改进：不再像旧版那样在后台 24 小时常驻一个 102 MiB 的 Avalonia UI 预热进程；
//! 仅在用户点击悬浮条图标时按需拉起轻量 WebView2 工具窗，并授予前台窗口焦点权限（`AllowSetForegroundWindow`）。

use std::path::PathBuf;
use std::process::Command;
use voice_ime_protocol::StandaloneIdentityContract;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ToolsTabRequest {
    Settings,
    Usage,
    Language,
}

impl ToolsTabRequest {
    pub fn as_cli_arg(self) -> &'static str {
        match self {
            ToolsTabRequest::Settings => "settings",
            ToolsTabRequest::Usage => "usage",
            ToolsTabRequest::Language => "language",
        }
    }
}

pub trait ToolsWindowLauncher: Send + Sync {
    fn open_tab(&self, tab: ToolsTabRequest);
}

#[derive(Default)]
pub struct NoopToolsLauncher;

impl ToolsWindowLauncher for NoopToolsLauncher {
    fn open_tab(&self, _tab: ToolsTabRequest) {}
}

pub struct OnDemandTauriToolsLauncher {
    tools_exe: PathBuf,
}

impl OnDemandTauriToolsLauncher {
    pub fn from_current_exe() -> Self {
        let base = std::env::current_exe()
            .ok()
            .and_then(|p| p.parent().map(|d| d.to_path_buf()))
            .unwrap_or_else(|| PathBuf::from("."));
        Self {
            tools_exe: base.join(StandaloneIdentityContract::TOOLS_EXE_NAME),
        }
    }

    pub fn new(tools_exe: PathBuf) -> Self {
        Self { tools_exe }
    }
}

impl ToolsWindowLauncher for OnDemandTauriToolsLauncher {
    fn open_tab(&self, tab: ToolsTabRequest) {
        if !self.tools_exe.exists() {
            tracing::warn!(
                path = %self.tools_exe.display(),
                "未找到按需工具窗可执行文件 TrueFluentVoiceImeTools.exe"
            );
            return;
        }
        let mut cmd = Command::new(&self.tools_exe);
        cmd.arg("--tab").arg(tab.as_cli_arg());
        if let Some(dir) = self.tools_exe.parent() {
            cmd.current_dir(dir);
        }
        #[cfg(windows)]
        {
            use std::os::windows::process::CommandExt;
            const CREATE_NO_WINDOW: u32 = 0x0800_0000;
            cmd.creation_flags(CREATE_NO_WINDOW);
        }
        if let Ok(child) = cmd.spawn() {
            #[cfg(windows)]
            unsafe {
                windows_sys::Win32::UI::WindowsAndMessaging::AllowSetForegroundWindow(child.id());
            }
            let _ = child;
        }
    }
}
