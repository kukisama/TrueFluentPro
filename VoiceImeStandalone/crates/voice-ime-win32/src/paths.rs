//! 安全路径解析、防重解析点（Reparse Point）校验与当前用户会话身份读取。

use std::path::{Path, PathBuf};
use voice_ime_protocol::StandaloneIdentityContract;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ProcessIdentity {
    pub pid: u32,
    pub session_id: u32,
    pub sid: String,
    pub start_ticks_100ns: u64,
    pub exe_path: PathBuf,
}

/// `%LocalAppData%\TrueFluentVoiceIme`
pub fn standalone_local_data_dir() -> PathBuf {
    dirs::data_local_dir()
        .or_else(dirs::config_dir)
        .unwrap_or_else(|| PathBuf::from("."))
        .join(StandaloneIdentityContract::INSTALL_SUBDIR)
}

/// 原生 64 位 `ProgramFilesDir\TrueFluentVoiceIme`（与阶段 1 `NativeInstallPath.h` 完全一致）。
pub fn install_root_dir() -> Result<PathBuf, String> {
    #[cfg(windows)]
    {
        let pf = read_native_program_files_dir()?;
        Ok(PathBuf::from(pf).join(StandaloneIdentityContract::INSTALL_SUBDIR))
    }
    #[cfg(not(windows))]
    {
        Ok(standalone_local_data_dir().join("simulated-program-files"))
    }
}

pub fn maintenance_lock_path() -> Result<PathBuf, String> {
    Ok(install_root_dir()?.join("maintenance.lock"))
}

/// 严格校验本机绝对路径，拒绝 UNC、设备前缀、备用数据流（ADS）及任意祖先目录上的重解析点（Symlink/Junction）。
pub fn guard_local_path(path: &Path) -> Result<(), String> {
    let raw = path.to_string_lossy();
    if !path.is_absolute() {
        return Err("输入法组件路径必须是本机绝对路径。".into());
    }
    #[cfg(windows)]
    {
        if raw.starts_with(r"\\") {
            return Err("拒绝使用 UNC 或网络设备路径。".into());
        }
        let chars: Vec<char> = raw.chars().collect();
        if chars.len() < 3 || !chars[0].is_ascii_alphabetic() || chars[1] != ':' {
            return Err("Windows 本机路径必须包含盘符前缀。".into());
        }
        if raw[2..].contains(':') {
            return Err("拒绝包含 NTFS 备用数据流 (ADS) 的路径。".into());
        }
        let mut cursor: Option<&Path> = Some(path);
        while let Some(part) = cursor {
            if is_reparse_point_win32(part)? {
                return Err("输入法路径或其父目录包含重解析点（符号链接/挂载点），已拒绝操作。".into());
            }
            cursor = part.parent();
        }
    }
    #[cfg(not(windows))]
    {
        if raw.is_empty() {
            return Err("路径不能为空。".into());
        }
        let mut cursor: Option<&Path> = Some(path);
        while let Some(part) = cursor {
            if let Ok(meta) = std::fs::symlink_metadata(part) {
                if meta.file_type().is_symlink() {
                    return Err("路径包含符号链接，已拒绝操作。".into());
                }
            }
            cursor = part.parent();
        }
    }
    Ok(())
}

/// 获取当前进程的 PID、SessionId、用户 SID 与可执行文件路径。
pub fn current_process_identity() -> Result<ProcessIdentity, String> {
    let exe_path = std::env::current_exe().map_err(|e| format!("无法获取当前进程路径：{e}"))?;
    #[cfg(windows)]
    {
        query_process_identity_by_handle_current(exe_path)
    }
    #[cfg(not(windows))]
    {
        Ok(ProcessIdentity {
            pid: std::process::id(),
            session_id: 1,
            sid: "S-1-5-21-0-0-0-1000".to_string(),
            start_ticks_100ns: 1,
            exe_path,
        })
    }
}

#[cfg(windows)]
fn read_native_program_files_dir() -> Result<String, String> {
    use std::ptr::null_mut;
    use windows_sys::Win32::System::Registry::{
        RegCloseKey, RegOpenKeyExW, RegQueryValueExW, HKEY, HKEY_LOCAL_MACHINE, KEY_QUERY_VALUE,
        KEY_WOW64_64KEY, REG_SZ,
    };

    let subkey: Vec<u16> = "Software\\Microsoft\\Windows\\CurrentVersion\0"
        .encode_utf16()
        .collect();
    let value_name: Vec<u16> = "ProgramFilesDir\0".encode_utf16().collect();

    unsafe {
        let mut key: HKEY = null_mut();
        let status = RegOpenKeyExW(
            HKEY_LOCAL_MACHINE,
            subkey.as_ptr(),
            0,
            KEY_QUERY_VALUE | KEY_WOW64_64KEY,
            &mut key,
        );
        if status != 0 {
            return Err("无法读取原生 ProgramFilesDir 注册表项。".into());
        }
        let mut buf = vec![0u16; 1024];
        let mut byte_len = (buf.len() * 2) as u32;
        let mut reg_type = 0u32;
        let q = RegQueryValueExW(
            key,
            value_name.as_ptr(),
            null_mut(),
            &mut reg_type,
            buf.as_mut_ptr() as *mut u8,
            &mut byte_len,
        );
        RegCloseKey(key);
        if q != 0 || reg_type != REG_SZ || byte_len < 6 {
            return Err("原生 ProgramFilesDir 数据格式无效。".into());
        }
        let u16_len = (byte_len as usize / 2).saturating_sub(1);
        let s = String::from_utf16_lossy(&buf[..u16_len])
            .trim_end_matches('\\')
            .to_string();
        if s.len() < 3 {
            return Err("原生 ProgramFilesDir 路径为空。".into());
        }
        Ok(s)
    }
}

#[cfg(windows)]
fn is_reparse_point_win32(path: &Path) -> Result<bool, String> {
    use std::os::windows::ffi::OsStrExt;
    use windows_sys::Win32::Foundation::{ERROR_FILE_NOT_FOUND, ERROR_PATH_NOT_FOUND, GetLastError};
    use windows_sys::Win32::Storage::FileSystem::{
        GetFileAttributesW, FILE_ATTRIBUTE_REPARSE_POINT, INVALID_FILE_ATTRIBUTES,
    };

    let wide: Vec<u16> = path.as_os_str().encode_wide().chain(std::iter::once(0)).collect();
    unsafe {
        let attrs = GetFileAttributesW(wide.as_ptr());
        if attrs == INVALID_FILE_ATTRIBUTES {
            let err = GetLastError();
            if err == ERROR_FILE_NOT_FOUND || err == ERROR_PATH_NOT_FOUND {
                return Ok(false);
            }
            return Err(format!("无法校验路径属性 (Win32 error {err})。"));
        }
        Ok((attrs & FILE_ATTRIBUTE_REPARSE_POINT) != 0)
    }
}

#[cfg(windows)]
pub fn query_process_identity_by_pid(pid: u32) -> Result<ProcessIdentity, String> {
    use windows_sys::Win32::Foundation::CloseHandle;
    use windows_sys::Win32::System::Threading::{
        OpenProcess, QueryFullProcessImageNameW, PROCESS_QUERY_LIMITED_INFORMATION,
    };

    unsafe {
        let handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid);
        if handle.is_null() {
            return Err("无法打开目标进程句柄。".into());
        }
        let mut img_buf = vec![0u16; 4096];
        let mut size = img_buf.len() as u32;
        let img_ok = QueryFullProcessImageNameW(handle, 0, img_buf.as_mut_ptr(), &mut size);
        if img_ok == 0 {
            CloseHandle(handle);
            return Err("无法读取进程可执行文件路径。".into());
        }
        let exe = PathBuf::from(String::from_utf16_lossy(&img_buf[..size as usize]));
        let res = extract_handle_identity(handle, pid, exe);
        CloseHandle(handle);
        res
    }
}

#[cfg(windows)]
fn query_process_identity_by_handle_current(exe_path: PathBuf) -> Result<ProcessIdentity, String> {
    use windows_sys::Win32::System::Threading::{GetCurrentProcess, GetCurrentProcessId};
    unsafe {
        let handle = GetCurrentProcess();
        let pid = GetCurrentProcessId();
        extract_handle_identity(handle, pid, exe_path)
    }
}

#[cfg(windows)]
unsafe fn extract_handle_identity(
    process_handle: windows_sys::Win32::Foundation::HANDLE,
    pid: u32,
    exe_path: PathBuf,
) -> Result<ProcessIdentity, String> {
    use std::ptr::null_mut;
    use windows_sys::Win32::Foundation::{CloseHandle, LocalFree, FILETIME};
    use windows_sys::Win32::Security::Authorization::ConvertSidToStringSidW;
    use windows_sys::Win32::Security::{
        GetTokenInformation, TokenSessionId, TokenUser, TOKEN_QUERY, TOKEN_USER,
    };
    use windows_sys::Win32::System::Threading::{GetProcessTimes, OpenProcessToken};

    let mut token = null_mut();
    if OpenProcessToken(process_handle, TOKEN_QUERY, &mut token) == 0 {
        return Err("无法打开进程访问令牌。".into());
    }

    let mut session_id: u32 = 0;
    let mut ret_len: u32 = 0;
    if GetTokenInformation(
        token,
        TokenSessionId,
        &mut session_id as *mut u32 as *mut _,
        std::mem::size_of::<u32>() as u32,
        &mut ret_len,
    ) == 0
    {
        CloseHandle(token);
        return Err("无法读取进程会话 ID。".into());
    }

    let _ = GetTokenInformation(token, TokenUser, null_mut(), 0, &mut ret_len);
    if ret_len == 0 {
        CloseHandle(token);
        return Err("无法获取用户 SID 长度。".into());
    }
    let mut user_buf = vec![0u8; ret_len as usize];
    if GetTokenInformation(
        token,
        TokenUser,
        user_buf.as_mut_ptr() as *mut _,
        ret_len,
        &mut ret_len,
    ) == 0
    {
        CloseHandle(token);
        return Err("无法读取用户 SID。".into());
    }
    let token_user = &*(user_buf.as_ptr() as *const TOKEN_USER);
    let mut sid_wide: *mut u16 = null_mut();
    if ConvertSidToStringSidW(token_user.User.Sid, &mut sid_wide) == 0 || sid_wide.is_null() {
        CloseHandle(token);
        return Err("无法格式化用户 SID。".into());
    }
    let mut len = 0usize;
    while *sid_wide.add(len) != 0 {
        len += 1;
    }
    let sid = String::from_utf16_lossy(std::slice::from_raw_parts(sid_wide, len));
    LocalFree(sid_wide as _);
    CloseHandle(token);

    let mut creation = FILETIME { dwLowDateTime: 0, dwHighDateTime: 0 };
    let mut exit = FILETIME { dwLowDateTime: 0, dwHighDateTime: 0 };
    let mut kernel = FILETIME { dwLowDateTime: 0, dwHighDateTime: 0 };
    let mut user = FILETIME { dwLowDateTime: 0, dwHighDateTime: 0 };
    let _ = GetProcessTimes(process_handle, &mut creation, &mut exit, &mut kernel, &mut user);
    let start_ticks_100ns =
        ((creation.dwHighDateTime as u64) << 32) | (creation.dwLowDateTime as u64);

    Ok(ProcessIdentity {
        pid,
        session_id,
        sid,
        start_ticks_100ns,
        exe_path,
    })
}

#[cfg(windows)]
pub fn is_current_process_elevated() -> bool {
    use std::ptr::null_mut;
    use windows_sys::Win32::Foundation::CloseHandle;
    use windows_sys::Win32::Security::{
        GetTokenInformation, TokenElevation, TOKEN_ELEVATION, TOKEN_QUERY,
    };
    use windows_sys::Win32::System::Threading::{GetCurrentProcess, OpenProcessToken};

    unsafe {
        let mut token = null_mut();
        if OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token) == 0 {
            return false;
        }
        let mut elevation = TOKEN_ELEVATION { TokenIsElevated: 0 };
        let mut size = 0u32;
        let ok = GetTokenInformation(
            token,
            TokenElevation,
            &mut elevation as *mut _ as *mut _,
            std::mem::size_of::<TOKEN_ELEVATION>() as u32,
            &mut size,
        );
        CloseHandle(token);
        ok != 0 && elevation.TokenIsElevated != 0
    }
}

#[cfg(not(windows))]
pub fn is_current_process_elevated() -> bool {
    false
}
