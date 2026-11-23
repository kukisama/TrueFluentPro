//! 命名管道对端身份认证与前台窗口资格校验（支持桌面进程与 UWP/AppContainer 沙盒客户端）。

pub trait PipePeerVerifier: Send + Sync {
    /// 验证管道对端客户端进程属于当前会话与同一登录用户 SID，并返回对端 PID。
    fn authenticate_peer(&self, client_pid: u32, declared_thread_id: u32) -> bool;

    /// 验证指定客户端进程当前是否真正拥有 Windows 前台焦点窗口（`GetForegroundWindow`）。
    fn is_foreground(&self, client_pid: u32, declared_thread_id: u32) -> bool;
}

/// 生产环境基于 Win32 内核句柄、SessionId、TokenUser SID 与 `GetForegroundWindow` 的验证器。
#[derive(Clone)]
pub struct Win32PeerVerifier {
    expected_session_id: u32,
    expected_sid: String,
}

impl Win32PeerVerifier {
    pub fn new(expected_session_id: u32, expected_sid: impl Into<String>) -> Self {
        Self {
            expected_session_id,
            expected_sid: expected_sid.into(),
        }
    }
}

impl PipePeerVerifier for Win32PeerVerifier {
    fn authenticate_peer(&self, client_pid: u32, declared_thread_id: u32) -> bool {
        if client_pid == 0 || declared_thread_id == 0 {
            return false;
        }
        #[cfg(windows)]
        {
            if let Ok(info) = crate::paths::query_process_identity_by_pid(client_pid) {
                return info.session_id == self.expected_session_id
                    && info.sid.eq_ignore_ascii_case(&self.expected_sid);
            }
            false
        }
        #[cfg(not(windows))]
        {
            let _ = (self.expected_session_id, &self.expected_sid);
            true
        }
    }

    fn is_foreground(&self, client_pid: u32, declared_thread_id: u32) -> bool {
        if client_pid == 0 || declared_thread_id == 0 {
            return false;
        }
        #[cfg(windows)]
        {
            use windows_sys::Win32::UI::WindowsAndMessaging::{
                GetForegroundWindow, GetWindowThreadProcessId,
            };
            unsafe {
                let fg = GetForegroundWindow();
                if fg.is_null() {
                    return false;
                }
                let mut fg_pid: u32 = 0;
                let _ = GetWindowThreadProcessId(fg, &mut fg_pid);
                fg_pid == client_pid
            }
        }
        #[cfg(not(windows))]
        {
            true
        }
    }
}

pub type ClientIdentityVerifier = dyn PipePeerVerifier;

/// 构建允许「当前登录用户 SID 完全控制 + AppContainer 沙盒连接读写」的命名管道 SDDL 字符串。
pub fn build_pipe_sddl_for_user(user_sid: &str) -> String {
    // SY = LocalSystem, user_sid = 当前登录用户完全控制，
    // AC (S-1-15-2-1) / S-1-15-2-2 = AppContainer 读写同步权限（仍由服务端二次校验 TokenUser SID == user_sid！）
    format!(
        "D:PAI(A;;GA;;;SY)(A;;GA;;;{user_sid})(A;;0x12019f;;;AC)(A;;0x12019f;;;S-1-15-2-2)"
    )
}

#[cfg(windows)]
pub mod windows_pipe {
    use std::ffi::OsStr;
    use std::io;
    use std::os::windows::ffi::OsStrExt;
    use std::os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle};
    use std::ptr::null_mut;

    use tokio::net::windows::named_pipe::NamedPipeServer;
    use windows_sys::Win32::Foundation::INVALID_HANDLE_VALUE;
    use windows_sys::Win32::Security::Authorization::{
        ConvertStringSecurityDescriptorToSecurityDescriptorW, SDDL_REVISION_1,
    };
    use windows_sys::Win32::Security::SECURITY_ATTRIBUTES;
    use windows_sys::Win32::Storage::FileSystem::{
        FILE_FLAG_FIRST_PIPE_INSTANCE, FILE_FLAG_OVERLAPPED, PIPE_ACCESS_DUPLEX,
    };
    use windows_sys::Win32::System::Pipes::{
        GetNamedPipeClientProcessId, PIPE_READMODE_BYTE, PIPE_REJECT_REMOTE_CLIENTS, PIPE_TYPE_BYTE,
        PIPE_UNLIMITED_INSTANCES, PIPE_WAIT,
    };

    use super::build_pipe_sddl_for_user;

    /// 创建绑定自定义安全描述符（支持当前用户及当前用户 AppContainer 沙盒进程连接）的异步命名管道实例。
    pub fn create_secure_pipe_instance(
        pipe_name_without_prefix: &str,
        user_sid: &str,
        first_instance: bool,
    ) -> io::Result<NamedPipeServer> {
        let full_path = format!(r"\\.\pipe\{pipe_name_without_prefix}");
        let wide_path: Vec<u16> = OsStr::new(&full_path)
            .encode_wide()
            .chain(std::iter::once(0))
            .collect();
        let sddl = build_pipe_sddl_for_user(user_sid);
        let wide_sddl: Vec<u16> = OsStr::new(&sddl)
            .encode_wide()
            .chain(std::iter::once(0))
            .collect();

        unsafe {
            let mut sd = null_mut();
            if ConvertStringSecurityDescriptorToSecurityDescriptorW(
                wide_sddl.as_ptr(),
                SDDL_REVISION_1,
                &mut sd,
                null_mut(),
            ) == 0
            {
                return Err(io::Error::last_os_error());
            }

            let sa = SECURITY_ATTRIBUTES {
                nLength: std::mem::size_of::<SECURITY_ATTRIBUTES>() as u32,
                lpSecurityDescriptor: sd,
                bInheritHandle: 0,
            };

            let mut open_mode = PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED;
            if first_instance {
                open_mode |= FILE_FLAG_FIRST_PIPE_INSTANCE;
            }

            let raw_handle = windows_sys::Win32::System::Pipes::CreateNamedPipeW(
                wide_path.as_ptr(),
                open_mode,
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
                PIPE_UNLIMITED_INSTANCES,
                65536,
                65536,
                0,
                &sa,
            );
            if raw_handle == INVALID_HANDLE_VALUE || raw_handle.is_null() {
                let error = io::Error::last_os_error();
                windows_sys::Win32::Foundation::LocalFree(sd);
                return Err(error);
            }
            windows_sys::Win32::Foundation::LocalFree(sd);

            let owned = OwnedHandle::from_raw_handle(raw_handle as _);
            NamedPipeServer::from_raw_handle(owned.as_raw_handle())
                .inspect(|_| std::mem::forget(owned))
        }
    }

    pub fn query_pipe_client_pid(pipe: &NamedPipeServer) -> Option<u32> {
        let raw = pipe.as_raw_handle();
        let mut pid: u32 = 0;
        unsafe {
            if GetNamedPipeClientProcessId(raw as _, &mut pid) != 0 && pid != 0 {
                Some(pid)
            } else {
                None
            }
        }
    }
}

#[cfg(all(test, windows))]
mod tests {
    use super::{windows_pipe::*, PipePeerVerifier, Win32PeerVerifier};
    use std::time::Duration;
    use tokio::io::{AsyncReadExt, AsyncWriteExt};
    use tokio::net::windows::named_pipe::ClientOptions;
    use windows_sys::Win32::System::Threading::GetCurrentThreadId;

    #[tokio::test]
    async fn named_pipe_accepts_same_session_and_reads_bytes() {
        let id = crate::paths::current_process_identity().unwrap();
        let name = format!("TrueFluentVoiceIme.Test.{}", uuid::Uuid::new_v4());
        let mut server = create_secure_pipe_instance(&name, &id.sid, true).unwrap();
        let mut client = ClientOptions::new().open(format!(r"\\.\pipe\{name}")).unwrap();
        tokio::time::timeout(Duration::from_secs(2), server.connect()).await.unwrap().unwrap();
        let actual = query_pipe_client_pid(&server).unwrap();
        assert_eq!(actual, id.pid);
        let tid = unsafe { GetCurrentThreadId() };
        assert!(Win32PeerVerifier::new(id.session_id, &id.sid).authenticate_peer(actual, tid));
        assert!(!Win32PeerVerifier::new(id.session_id.wrapping_add(1), &id.sid).authenticate_peer(actual, tid));
        client.write_all(b"H").await.unwrap();
        assert_eq!(tokio::time::timeout(Duration::from_secs(2), server.read_u8()).await.unwrap().unwrap(), b'H');
    }
}
