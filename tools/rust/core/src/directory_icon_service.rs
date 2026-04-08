#![cfg(windows)]

use std::fs;
use std::io;
use std::path::Path;

/// 将指定的 ICO 文件设置为目标目录的图标。
pub fn set_directory_icon<P: AsRef<Path>, Q: AsRef<Path>>(
    ico_file_path: P,
    target_directory: Q,
) -> io::Result<()> {
    let ico_path = ico_file_path.as_ref();
    let target_dir = target_directory.as_ref();

    if !ico_path.exists() {
        return Err(io::Error::new(
            io::ErrorKind::NotFound,
            format!("ICO 文件不存在: {}", ico_path.display()),
        ));
    }

    let ext = ico_path
        .extension()
        .and_then(|e| e.to_str())
        .unwrap_or("")
        .to_lowercase();
    if ext != "ico" {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "图标文件必须是 .ico 格式。",
        ));
    }

    let target_dir = fs::canonicalize(target_dir)?;
    if !target_dir.exists() {
        return Err(io::Error::new(
            io::ErrorKind::NotFound,
            format!("目录不存在: {}", target_dir.display()),
        ));
    }

    let ico_file_name = ico_path
        .file_name()
        .unwrap_or_default()
        .to_string_lossy()
        .to_string();
    let dest_ico_path = target_dir.join(&ico_file_name);

    // Remove hidden/system attrs if exists
    if dest_ico_path.exists() {
        set_normal_attributes(&dest_ico_path);
    }

    let source_full = fs::canonicalize(ico_path)?;
    if source_full != dest_ico_path {
        fs::copy(&source_full, &dest_ico_path)?;
    }

    set_hidden_system_attributes(&dest_ico_path);
    apply_folder_icon(&target_dir, &ico_file_name)?;

    Ok(())
}

/// 将 ICO 字节数据保存到目标目录并设置为目录图标。
pub fn set_directory_icon_from_bytes<P: AsRef<Path>>(
    ico_data: &[u8],
    ico_file_name: &str,
    target_directory: P,
) -> io::Result<()> {
    let target_dir = fs::canonicalize(target_directory.as_ref())?;
    if !target_dir.exists() {
        return Err(io::Error::new(
            io::ErrorKind::NotFound,
            format!("目录不存在: {}", target_dir.display()),
        ));
    }

    let mut name = ico_file_name.to_string();
    if !name.to_lowercase().ends_with(".ico") {
        name.push_str(".ico");
    }

    let dest_ico_path = target_dir.join(&name);

    if dest_ico_path.exists() {
        set_normal_attributes(&dest_ico_path);
    }

    fs::write(&dest_ico_path, ico_data)?;
    set_hidden_system_attributes(&dest_ico_path);
    apply_folder_icon(&target_dir, &name)?;

    Ok(())
}

fn apply_folder_icon(target_directory: &Path, ico_file_name: &str) -> io::Result<()> {
    // Step 1: Try Shell API
    let hr = shell_set_folder_icon(target_directory, ico_file_name);

    if hr < 0 {
        // Fallback: manually write desktop.ini
        write_desktop_ini(target_directory, ico_file_name)?;
    }

    // Step 2: Write IconResource entry
    shell_write_ini_entry(target_directory, "IconResource", &format!("{},0", ico_file_name));

    // Step 3: Ensure desktop.ini attributes
    let desktop_ini = target_directory.join("desktop.ini");
    if desktop_ini.exists() {
        set_hidden_system_attributes(&desktop_ini);
    }

    // Step 4: Make system folder
    shell_make_system_folder(target_directory);

    // Step 5: Notify Shell
    notify_shell_icon_changed(target_directory);

    Ok(())
}

fn write_desktop_ini(target_directory: &Path, ico_file_name: &str) -> io::Result<()> {
    let desktop_ini = target_directory.join("desktop.ini");

    if desktop_ini.exists() {
        set_normal_attributes(&desktop_ini);
    }

    let content = format!("[.ShellClassInfo]\r\nIconResource={},0\r\n", ico_file_name);
    // Write as UTF-16 LE with BOM
    let mut bytes = vec![0xFF, 0xFE]; // BOM
    for c in content.encode_utf16() {
        bytes.extend_from_slice(&c.to_le_bytes());
    }
    fs::write(&desktop_ini, bytes)?;

    set_hidden_system_attributes(&desktop_ini);
    Ok(())
}

fn notify_shell_icon_changed(directory_path: &Path) {
    let desktop_ini = directory_path.join("desktop.ini");

    // Layer 1: Notify desktop.ini update
    shell_notify_update_item(&desktop_ini);

    // Layer 2: Notify directory update
    shell_notify_update_dir(directory_path);

    // Layer 3: Notify parent directory
    if let Some(parent) = directory_path.parent() {
        shell_notify_update_dir(parent);
    }

    // Layer 4: Global association change
    shell_notify_assoc_changed();

    // Layer 5: Reinitialize icon cache
    shell_reinitialize_icon_cache();

    // Layer 6: Broadcast WM_SETTINGCHANGE
    shell_broadcast_setting_change();
}

// ─── Windows API wrappers ───

fn set_normal_attributes(path: &Path) {
    use windows::Win32::Storage::FileSystem::*;
    let wide: Vec<u16> = path
        .to_string_lossy()
        .encode_utf16()
        .chain(std::iter::once(0))
        .collect();
    let pstr = windows::core::PCWSTR::from_raw(wide.as_ptr());
    unsafe {
        let _ = SetFileAttributesW(pstr, FILE_ATTRIBUTE_NORMAL);
    }
}

fn set_hidden_system_attributes(path: &Path) {
    use windows::Win32::Storage::FileSystem::*;
    let wide: Vec<u16> = path
        .to_string_lossy()
        .encode_utf16()
        .chain(std::iter::once(0))
        .collect();
    let pstr = windows::core::PCWSTR::from_raw(wide.as_ptr());
    unsafe {
        let _ = SetFileAttributesW(
            pstr,
            FILE_ATTRIBUTE_HIDDEN | FILE_ATTRIBUTE_SYSTEM,
        );
    }
}

fn shell_set_folder_icon(folder_path: &Path, ico_file_name: &str) -> i32 {
    let result1 = write_private_profile_string(
        folder_path,
        "IconFile",
        ico_file_name,
    );
    let result2 = write_private_profile_string(
        folder_path,
        "IconIndex",
        "0",
    );

    if result1 && result2 { 0 } else { -1 }
}

fn shell_write_ini_entry(folder_path: &Path, key: &str, value: &str) {
    write_private_profile_string(folder_path, key, value);
}

/// 封装 WritePrivateProfileStringW 调用，将 &str 正确转为 PCWSTR。
fn write_private_profile_string(folder_path: &Path, key: &str, value: &str) -> bool {
    use std::ffi::OsStr;
    use std::os::windows::ffi::OsStrExt;
    use windows::Win32::System::WindowsProgramming::WritePrivateProfileStringW;
    use windows::core::PCWSTR;

    let section: Vec<u16> = OsStr::new(".ShellClassInfo")
        .encode_wide().chain(std::iter::once(0)).collect();
    let key_wide: Vec<u16> = OsStr::new(key)
        .encode_wide().chain(std::iter::once(0)).collect();
    let value_wide: Vec<u16> = OsStr::new(value)
        .encode_wide().chain(std::iter::once(0)).collect();
    let ini_path = folder_path.join("desktop.ini");
    let ini_wide: Vec<u16> = OsStr::new(&ini_path.to_string_lossy().to_string())
        .encode_wide().chain(std::iter::once(0)).collect();

    unsafe {
        WritePrivateProfileStringW(
            PCWSTR::from_raw(section.as_ptr()),
            PCWSTR::from_raw(key_wide.as_ptr()),
            PCWSTR::from_raw(value_wide.as_ptr()),
            PCWSTR::from_raw(ini_wide.as_ptr()),
        ).is_ok()
    }
}

fn shell_make_system_folder(directory_path: &Path) {
    use std::ffi::OsStr;
    use std::os::windows::ffi::OsStrExt;
    use windows::Win32::UI::Shell::PathMakeSystemFolderW;
    use windows::core::PCWSTR;

    let path_wide: Vec<u16> = OsStr::new(&directory_path.to_string_lossy().to_string())
        .encode_wide()
        .chain(std::iter::once(0))
        .collect();

    unsafe {
        let _ = PathMakeSystemFolderW(PCWSTR::from_raw(path_wide.as_ptr()));
    }
}

fn shell_notify_update_item(file_path: &Path) {
    shell_change_notify(0x00002000, 0x0005 | 0x1000, Some(file_path), None);
}

fn shell_notify_update_dir(directory_path: &Path) {
    shell_change_notify(0x00001000, 0x0005 | 0x1000, Some(directory_path), None);
}

fn shell_notify_assoc_changed() {
    shell_change_notify(0x08000000, 0x0000 | 0x3000, None, None);
}

fn shell_change_notify(
    event_id: i32,
    flags: i32,
    item1: Option<&Path>,
    _item2: Option<&Path>,
) {
    use windows::Win32::UI::Shell::{SHChangeNotify, SHCNE_ID, SHCNF_FLAGS};
    use std::ffi::OsStr;
    use std::os::windows::ffi::OsStrExt;

    let item1_wide: Option<Vec<u16>> = item1.map(|p| {
        OsStr::new(&p.to_string_lossy().to_string())
            .encode_wide()
            .chain(std::iter::once(0))
            .collect()
    });

    unsafe {
        match item1_wide {
            Some(ref w) => {
                SHChangeNotify(
                    SHCNE_ID(event_id as u32),
                    SHCNF_FLAGS(flags as u32),
                    Some(w.as_ptr() as *const std::ffi::c_void),
                    None,
                );
            }
            None => {
                SHChangeNotify(SHCNE_ID(event_id as u32), SHCNF_FLAGS(flags as u32), None, None);
            }
        }
    }
}

/// 调用 shell32.dll 未文档化的 FileIconInit (ordinal #660) 重置系统图标缓存。
/// 该函数未在 Windows SDK 中公开，只能通过 ordinal 动态加载。
fn shell_reinitialize_icon_cache() {
    unsafe {
        use windows::Win32::System::LibraryLoader::{GetModuleHandleW, GetProcAddress};

        // shell32.dll 在 GUI 进程中始终已加载，用 GetModuleHandleW 获取句柄（不增加引用计数）
        let Ok(shell32) = GetModuleHandleW(windows::core::w!("shell32.dll")) else {
            return;
        };

        let Some(func) = GetProcAddress(
            shell32,
            windows::core::PCSTR::from_raw(b"#660\0".as_ptr()),
        ) else {
            return;
        };

        let file_icon_init: unsafe extern "system" fn(i32) -> i32 =
            std::mem::transmute(func);
        file_icon_init(0); // 清除缓存
        file_icon_init(1); // 重新初始化
    }
}

fn shell_broadcast_setting_change() {
    use windows::Win32::UI::WindowsAndMessaging::*;
    use std::ffi::OsStr;
    use std::os::windows::ffi::OsStrExt;

    let env_str: Vec<u16> = OsStr::new("Environment")
        .encode_wide()
        .chain(std::iter::once(0))
        .collect();

    unsafe {
        let _ = SendMessageTimeoutW(
            HWND_BROADCAST,
            WM_SETTINGCHANGE,
            windows::Win32::Foundation::WPARAM(0),
            windows::Win32::Foundation::LPARAM(env_str.as_ptr() as isize),
            SMTO_ABORTIFHUNG,
            3000,
            None,
        );
    }
}
