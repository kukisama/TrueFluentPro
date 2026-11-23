//! 独立 COM STA 线程动态加载阶段 1 `VoiceImeStandalone.dll` 并驱动原生 GDI+ 悬浮条（对齐 C# `VoiceImeToolbar`）。

use std::path::PathBuf;
use std::sync::{Arc, Mutex};

use tokio::sync::{mpsc, oneshot};

/// 原生悬浮条向守护进程 Actor 回调的用户交互动作（与 `ToolbarBridge.cpp` 1..=6 严格对应）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ToolbarAction {
    /// `1`：点击悬浮条麦克风切换开始/停止录音。
    ToggleRecording,
    /// `2`：TSF 键盘输入法激活状态变化（`true`=切到本输入法，`false`=切走或进入受抑制窗口）。
    ProfileSelected(bool),
    /// `3`：点击悬浮条齿轮图标 → 打开「设置」页签。
    OpenSettings,
    /// `4`：用户在隐私确认框中确认开启/关闭原文历史记录。
    SetHistoryConsent(bool),
    /// `5`：点击悬浮条柱状图图标 → 打开「用量」页签。
    OpenUsage,
    /// `6`：点击悬浮条「言」字图标 → 打开「语言与翻译」页签。
    OpenLanguage,
}

impl ToolbarAction {
    pub fn from_raw(kind: i32, value: i32) -> Option<Self> {
        match (kind, value) {
            (1, _) => Some(ToolbarAction::ToggleRecording),
            (2, 1) => Some(ToolbarAction::ProfileSelected(true)),
            (2, 0) => Some(ToolbarAction::ProfileSelected(false)),
            (3, _) => Some(ToolbarAction::OpenSettings),
            (4, 1) => Some(ToolbarAction::SetHistoryConsent(true)),
            (4, 0) => Some(ToolbarAction::SetHistoryConsent(false)),
            (5, _) => Some(ToolbarAction::OpenUsage),
            (6, _) => Some(ToolbarAction::OpenLanguage),
            _ => None,
        }
    }
}

/// 抽象悬浮条驱动接口（生产环境使用 Win32 STA 原生桥接，单元测试使用无头实现）。
pub trait ToolbarDriver: Send + Sync {
    fn update(&self, visible: bool, state: i32, stopped: bool, error: bool);
    fn update_history(&self, enabled: bool, available: bool);
    fn close(&self);
}

/// 单元测试与无头环境使用的空悬浮条驱动。
#[derive(Default)]
pub struct NoopToolbarDriver;

impl ToolbarDriver for NoopToolbarDriver {
    fn update(&self, _visible: bool, _state: i32, _stopped: bool, _error: bool) {}
    fn update_history(&self, _enabled: bool, _available: bool) {}
    fn close(&self) {}
}

/// Windows 生产环境原生悬浮条驱动：独立 STA 线程加载 `VoiceImeStandalone.dll` 并在退出后安全 `FreeLibrary`。
pub struct Win32NativeToolbarDriver {
    inner: Arc<Mutex<BridgeState>>,
    completion_rx: Mutex<Option<oneshot::Receiver<()>>>,
}

struct BridgeState {
    closing: bool,
    handle: usize,
    latest: (bool, i32, bool, bool),
    history: (bool, bool),
    #[cfg(windows)]
    update_fn: Option<unsafe extern "system" fn(*mut std::ffi::c_void, i32, i32, i32, i32)>,
    #[cfg(windows)]
    history_fn: Option<unsafe extern "system" fn(*mut std::ffi::c_void, i32, i32)>,
    #[cfg(windows)]
    close_fn: Option<unsafe extern "system" fn(*mut std::ffi::c_void)>,
}

impl Win32NativeToolbarDriver {
    /// 启动专用 COM STA 线程创建原生悬浮条，等待就绪后返回驱动句柄。
    pub async fn spawn(
        dll_path: PathBuf,
        action_tx: mpsc::UnboundedSender<ToolbarAction>,
    ) -> Result<Self, String> {
        crate::paths::guard_local_path(&dll_path)?;
        let inner = Arc::new(Mutex::new(BridgeState {
            closing: false,
            handle: 0,
            latest: (false, 0, false, false),
            history: (false, false),
            #[cfg(windows)]
            update_fn: None,
            #[cfg(windows)]
            history_fn: None,
            #[cfg(windows)]
            close_fn: None,
        }));

        let (ready_tx, ready_rx) = oneshot::channel::<Result<(), String>>();
        let (done_tx, done_rx) = oneshot::channel::<()>();

        #[cfg(windows)]
        {
            let thread_inner = Arc::clone(&inner);
            std::thread::Builder::new()
                .name("voice-ime-toolbar-sta".into())
                .spawn(move || {
                    sta_thread_main(dll_path, action_tx, thread_inner, ready_tx);
                    let _ = done_tx.send(());
                })
                .map_err(|_| "无法启动原生悬浮条 STA 线程。".to_string())?;
        }

        #[cfg(not(windows))]
        {
            let _ = (dll_path, action_tx);
            let _ = ready_tx.send(Ok(()));
            let _ = done_tx.send(());
        }

        ready_rx
            .await
            .map_err(|_| "原生悬浮条初始化通道异常关闭。".to_string())??;

        Ok(Self {
            inner,
            completion_rx: Mutex::new(Some(done_rx)),
        })
    }

    pub async fn shutdown(&self) {
        self.close();
        let rx_opt = self.completion_rx.lock().unwrap().take();
        if let Some(rx) = rx_opt {
            let _ = rx.await;
        }
    }
}

impl ToolbarDriver for Win32NativeToolbarDriver {
    fn update(&self, visible: bool, state: i32, stopped: bool, error: bool) {
        let mut guard = self.inner.lock().unwrap();
        guard.latest = (visible, state, stopped, error);
        #[cfg(windows)]
        if !guard.closing && guard.handle != 0 {
            if let Some(f) = guard.update_fn {
                unsafe {
                    f(
                        guard.handle as *mut _,
                        i32::from(visible),
                        state,
                        i32::from(stopped),
                        i32::from(error),
                    );
                }
            }
        }
    }

    fn update_history(&self, enabled: bool, available: bool) {
        let mut guard = self.inner.lock().unwrap();
        guard.history = (enabled, available);
        #[cfg(windows)]
        if !guard.closing && guard.handle != 0 {
            if let Some(f) = guard.history_fn {
                unsafe {
                    f(
                        guard.handle as *mut _,
                        i32::from(enabled),
                        i32::from(available),
                    );
                }
            }
        }
    }

    fn close(&self) {
        let mut guard = self.inner.lock().unwrap();
        if guard.closing {
            return;
        }
        guard.closing = true;
        #[cfg(windows)]
        if guard.handle != 0 {
            if let Some(f) = guard.close_fn {
                unsafe {
                    f(guard.handle as *mut _);
                }
            }
        }
    }
}

#[cfg(windows)]
fn sta_thread_main(
    dll_path: PathBuf,
    action_tx: mpsc::UnboundedSender<ToolbarAction>,
    state: Arc<Mutex<BridgeState>>,
    ready_tx: oneshot::Sender<Result<(), String>>,
) {
    use std::ffi::c_void;
    use std::os::windows::ffi::OsStrExt;
    use std::ptr::{null, null_mut};
    use windows_sys::Win32::Foundation::FreeLibrary;
    use windows_sys::Win32::System::Com::{
        CoInitializeEx, CoUninitialize, COINIT_APARTMENTTHREADED,
    };
    use windows_sys::Win32::System::LibraryLoader::{
        GetProcAddress, LoadLibraryExW, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR,
        LOAD_LIBRARY_SEARCH_SYSTEM32,
    };

    type CreateCallback = unsafe extern "system" fn(i32, i32, *mut c_void);
    type CreateFn = unsafe extern "system" fn(CreateCallback, *mut c_void) -> *mut c_void;
    type UpdateFn = unsafe extern "system" fn(*mut c_void, i32, i32, i32, i32);
    type SetHistoryFn = unsafe extern "system" fn(*mut c_void, i32, i32);
    type RunFn = unsafe extern "system" fn(*mut c_void) -> i32;
    type CloseFn = unsafe extern "system" fn(*mut c_void);

    unsafe extern "system" fn on_toolbar_callback(kind: i32, value: i32, ctx: *mut c_void) {
        if ctx.is_null() {
            return;
        }
        let sender = &*(ctx as *const mpsc::UnboundedSender<ToolbarAction>);
        if let Some(action) = ToolbarAction::from_raw(kind, value) {
            let _ = sender.send(action);
        }
    }

    unsafe {
        let hr = CoInitializeEx(null(), COINIT_APARTMENTTHREADED as u32);
        if hr < 0 {
            let _ = ready_tx.send(Err("初始化悬浮条 COM STA 单元失败。".into()));
            return;
        }

        let wide: Vec<u16> = dll_path
            .as_os_str()
            .encode_wide()
            .chain(std::iter::once(0))
            .collect();
        let module = LoadLibraryExW(
            wide.as_ptr(),
            null_mut(),
            LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32,
        );
        if module.is_null() {
            CoUninitialize();
            let _ = ready_tx.send(Err(format!(
                "无法加载原生输入法组件 {}，请检查构建产物是否完整。",
                dll_path.display()
            )));
            return;
        }

        macro_rules! resolve_sym {
            ($name:literal, $ty:ty) => {{
                let p = GetProcAddress(module, concat!($name, "\0").as_ptr());
                match p {
                    Some(f) => std::mem::transmute::<_, $ty>(f),
                    None => {
                        FreeLibrary(module);
                        CoUninitialize();
                        let _ = ready_tx.send(Err(format!("原生组件缺少导出函数 {}", $name)));
                        return;
                    }
                }
            }};
        }

        let create_fn = resolve_sym!("VoiceImeToolbarCreate", CreateFn);
        let update_fn = resolve_sym!("VoiceImeToolbarUpdate", UpdateFn);
        let history_fn = resolve_sym!("VoiceImeToolbarSetHistory", SetHistoryFn);
        let run_fn = resolve_sym!("VoiceImeToolbarRun", RunFn);
        let close_fn = resolve_sym!("VoiceImeToolbarClose", CloseFn);

        let boxed_tx = Box::new(action_tx);
        let ctx_ptr = &*boxed_tx as *const mpsc::UnboundedSender<ToolbarAction> as *mut c_void;

        let handle = create_fn(on_toolbar_callback, ctx_ptr);
        if handle.is_null() {
            drop(boxed_tx);
            FreeLibrary(module);
            CoUninitialize();
            let _ = ready_tx.send(Err("创建原生悬浮条实例失败。".into()));
            return;
        }

        {
            let mut guard = state.lock().unwrap();
            guard.handle = handle as usize;
            guard.update_fn = Some(update_fn);
            guard.history_fn = Some(history_fn);
            guard.close_fn = Some(close_fn);
            if guard.closing {
                close_fn(handle);
            } else {
                history_fn(
                    handle,
                    i32::from(guard.history.0),
                    i32::from(guard.history.1),
                );
                update_fn(
                    handle,
                    i32::from(guard.latest.0),
                    guard.latest.1,
                    i32::from(guard.latest.2),
                    i32::from(guard.latest.3),
                );
            }
        }

        let _ = ready_tx.send(Ok(()));
        // 阻塞泵送原生消息循环，直到收到 VoiceImeToolbarClose
        let _ = run_fn(handle);

        {
            let mut guard = state.lock().unwrap();
            guard.closing = true;
            guard.handle = 0;
        }
        drop(boxed_tx);
        CoUninitialize();
        FreeLibrary(module);
    }
}
