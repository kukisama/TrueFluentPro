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
    use std::ffi::OsStr;
    use std::os::windows::ffi::OsStrExt;

    let folder_wide: Vec<u16> = OsStr::new(&folder_path.to_string_lossy().to_string())
        .encode_wide()
        .chain(std::iter::once(0))
        .collect();

    let ico_wide: Vec<u16> = OsStr::new(ico_file_name)
        .encode_wide()
        .chain(std::iter::once(0))
        .collect();

    // Use WritePrivateProfileString as primary approach
    let section: Vec<u16> = OsStr::new(".ShellClassInfo")
        .encode_wide()
        .chain(std::iter::once(0))
        .collect();
    let key_icon_file: Vec<u16> = OsStr::new("IconFile")
        .encode_wide()
        .chain(std::iter::once(0))
        .collect();
    let key_icon_index: Vec<u16> = OsStr::new("IconIndex")
        .encode_wide()
        .chain(std::iter::once(0))
        .collect();
    let zero_str: Vec<u16> = OsStr::new("0")
        .encode_wide()
        .chain(std::iter::once(0))
        .collect();

    let ini_path = folder_path.join("desktop.ini");
    let ini_wide: Vec<u16> = OsStr::new(&ini_path.to_string_lossy().to_string())
        .encode_wide()
        .chain(std::iter::once(0))
        .collect();

    unsafe {
        use windows::core::PCWSTR;
        // WritePrivateProfileStringW
        type WriteProfileFn = unsafe extern "system" fn(
            PCWSTR,
            PCWSTR,
            PCWSTR,
            PCWSTR,
        ) -> i32;

        let kernel32 = windows::Win32::System::LibraryLoader::LoadLibraryW(
            PCWSTR::from_raw(
                OsStr::new("kernel32.dll")
                    .encode_wide()
                    .chain(std::iter::once(0))
                    .collect::<Vec<u16>>()
                    .as_ptr(),
            ),
        );
        if let Ok(lib) = kernel32 {
            let func = windows::Win32::System::LibraryLoader::GetProcAddress(
                lib,
                windows::core::PCSTR::from_raw(b"WritePrivateProfileStringW\0".as_ptr()),
            );
            if let Some(f) = func {
                let write_fn: WriteProfileFn = std::mem::transmute(f);
                write_fn(
                    PCWSTR::from_raw(section.as_ptr()),
                    PCWSTR::from_raw(key_icon_file.as_ptr()),
                    PCWSTR::from_raw(ico_wide.as_ptr()),
                    PCWSTR::from_raw(ini_wide.as_ptr()),
                );
                write_fn(
                    PCWSTR::from_raw(section.as_ptr()),
                    PCWSTR::from_raw(key_icon_index.as_ptr()),
                    PCWSTR::from_raw(zero_str.as_ptr()),
                    PCWSTR::from_raw(ini_wide.as_ptr()),
                );
            }
            let _ = windows::Win32::Foundation::FreeLibrary(lib);
        }
    }
    0
}

fn shell_write_ini_entry(folder_path: &Path, key: &str, value: &str) {
    use std::ffi::OsStr;
    use std::os::windows::ffi::OsStrExt;

    let section: Vec<u16> = OsStr::new(".ShellClassInfo")
        .encode_wide()
        .chain(std::iter::once(0))
        .collect();
    let key_wide: Vec<u16> = OsStr::new(key)
        .encode_wide()
        .chain(std::iter::once(0))
        .collect();
    let value_wide: Vec<u16> = OsStr::new(value)
        .encode_wide()
        .chain(std::iter::once(0))
        .collect();
    let ini_path = folder_path.join("desktop.ini");
    let ini_wide: Vec<u16> = OsStr::new(&ini_path.to_string_lossy().to_string())
        .encode_wide()
        .chain(std::iter::once(0))
        .collect();

    unsafe {
        use windows::core::PCWSTR;
        type WriteProfileFn =
            unsafe extern "system" fn(PCWSTR, PCWSTR, PCWSTR, PCWSTR) -> i32;

        let kernel32 = windows::Win32::System::LibraryLoader::LoadLibraryW(PCWSTR::from_raw(
            OsStr::new("kernel32.dll")
                .encode_wide()
                .chain(std::iter::once(0))
                .collect::<Vec<u16>>()
                .as_ptr(),
        ));
        if let Ok(lib) = kernel32 {
            let func = windows::Win32::System::LibraryLoader::GetProcAddress(
                lib,
                windows::core::PCSTR::from_raw(b"WritePrivateProfileStringW\0".as_ptr()),
            );
            if let Some(f) = func {
                let write_fn: WriteProfileFn = std::mem::transmute(f);
                write_fn(
                    PCWSTR::from_raw(section.as_ptr()),
                    PCWSTR::from_raw(key_wide.as_ptr()),
                    PCWSTR::from_raw(value_wide.as_ptr()),
                    PCWSTR::from_raw(ini_wide.as_ptr()),
                );
            }
            let _ = windows::Win32::Foundation::FreeLibrary(lib);
        }
    }
}

fn shell_make_system_folder(directory_path: &Path) {
    use std::ffi::OsStr;
    use std::os::windows::ffi::OsStrExt;

    let path_wide: Vec<u16> = OsStr::new(&directory_path.to_string_lossy().to_string())
        .encode_wide()
        .chain(std::iter::once(0))
        .collect();

    unsafe {
        use windows::core::PCWSTR;
        let shlwapi = windows::Win32::System::LibraryLoader::LoadLibraryW(PCWSTR::from_raw(
            OsStr::new("shlwapi.dll")
                .encode_wide()
                .chain(std::iter::once(0))
                .collect::<Vec<u16>>()
                .as_ptr(),
        ));
        if let Ok(lib) = shlwapi {
            let func = windows::Win32::System::LibraryLoader::GetProcAddress(
                lib,
                windows::core::PCSTR::from_raw(b"PathMakeSystemFolderW\0".as_ptr()),
            );
            if let Some(f) = func {
                let make_sys: unsafe extern "system" fn(PCWSTR) -> i32 =
                    std::mem::transmute(f);
                make_sys(PCWSTR::from_raw(path_wide.as_ptr()));
            }
            let _ = windows::Win32::Foundation::FreeLibrary(lib);
        }
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
    use windows::Win32::UI::Shell::SHChangeNotify;
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
                    event_id,
                    flags as u32,
                    Some(w.as_ptr() as *const std::ffi::c_void),
                    None,
                );
            }
            None => {
                SHChangeNotify(event_id, flags as u32, None, None);
            }
        }
    }
}

fn shell_reinitialize_icon_cache() {
    use std::ffi::OsStr;
    use std::os::windows::ffi::OsStrExt;

    unsafe {
        use windows::core::PCWSTR;
        let shell32 = windows::Win32::System::LibraryLoader::LoadLibraryW(PCWSTR::from_raw(
            OsStr::new("shell32.dll")
                .encode_wide()
                .chain(std::iter::once(0))
                .collect::<Vec<u16>>()
                .as_ptr(),
        ));
        if let Ok(lib) = shell32 {
            let func = windows::Win32::System::LibraryLoader::GetProcAddress(
                lib,
                windows::core::PCSTR::from_raw(b"#660\0".as_ptr()),
            );
            if let Some(f) = func {
                let file_icon_init: unsafe extern "system" fn(i32) -> i32 =
                    std::mem::transmute(f);
                file_icon_init(0);
                file_icon_init(1);
            }
            let _ = windows::Win32::Foundation::FreeLibrary(lib);
        }
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
