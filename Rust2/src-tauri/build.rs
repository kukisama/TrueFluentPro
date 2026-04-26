fn main() {
    tauri_build::build();

    // 将 Speech SDK 的 DLL 复制到与输出二进制文件同目录，确保运行时能加载
    copy_speech_sdk_dlls();
}

fn copy_speech_sdk_dlls() {
    let out_dir = std::env::var("OUT_DIR").unwrap_or_default();
    if out_dir.is_empty() {
        return;
    }

    // Speech SDK 的 build 输出目录在 target/<profile>/build/speech-sdk-<hash>/out/sdk_output
    // OUT_DIR 是 target/<profile>/build/truefluent-pro-<hash>/out
    // 我们需要遍历 build 目录找到 speech-sdk 的输出
    let build_dir = std::path::Path::new(&out_dir)
        .parent() // out
        .and_then(|p| p.parent()) // truefluent-pro-<hash>
        .and_then(|p| p.parent()); // build

    let build_dir = match build_dir {
        Some(d) => d,
        None => return,
    };

    // 找到 speech-sdk 的输出目录
    let sdk_dll_dir = find_speech_sdk_dlls(build_dir);
    if let Some(dll_dir) = sdk_dll_dir {
        // 目标目录: target/<profile>/
        let target_dir = build_dir.parent().unwrap();
        for entry in std::fs::read_dir(&dll_dir).unwrap() {
            let entry = entry.unwrap();
            let path = entry.path();
            if path.extension().map(|e| e == "dll").unwrap_or(false) {
                let dest = target_dir.join(path.file_name().unwrap());
                let _ = std::fs::copy(&path, &dest);
            }
        }
    }
}

fn find_speech_sdk_dlls(build_dir: &std::path::Path) -> Option<std::path::PathBuf> {
    for entry in std::fs::read_dir(build_dir).ok()? {
        let entry = entry.ok()?;
        let name = entry.file_name();
        let name_str = name.to_string_lossy();
        if name_str.starts_with("speech-sdk-") {
            #[cfg(target_arch = "x86_64")]
            let candidate = entry.path().join("out/sdk_output/runtimes/win-x64/native");
            #[cfg(target_arch = "aarch64")]
            let candidate = entry.path().join("out/sdk_output/runtimes/win-arm64/native");
            #[cfg(not(any(target_arch = "x86_64", target_arch = "aarch64")))]
            let candidate = entry.path().join("out/sdk_output/runtimes/win-x86/native");

            if candidate.exists() {
                return Some(candidate);
            }
        }
    }
    None
}
