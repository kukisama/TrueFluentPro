// build.rs — 将 speech-sdk 下载的原生库复制到 exe 同目录，
// 否则运行时找不到 Microsoft.CognitiveServices.Speech.core 原生库。
//
// 支持桌面三平台：
//   - Windows：复制 runtimes/win-x64/native/*.dll
//   - Linux：复制 lib/<arch>/*.so*，并设置 rpath=$ORIGIN
//   - macOS：复制 MicrosoftCognitiveServicesSpeech.framework，并设置 rpath=@executable_path

use std::fs;
use std::path::{Path, PathBuf};

fn main() {
    println!("cargo:rerun-if-changed=build.rs");

    let target_os = std::env::var("CARGO_CFG_TARGET_OS").unwrap_or_default();
    let target_arch = std::env::var("CARGO_CFG_TARGET_ARCH").unwrap_or_default();

    let out_dir = std::env::var("OUT_DIR").unwrap();
    // OUT_DIR = target/<profile>/build/speech-test-ui-HASH/out
    // 向上 3 级到 profile 目录（exe 所在目录）
    let profile_dir = match Path::new(&out_dir).ancestors().nth(3) {
        Some(p) => p.to_path_buf(),
        None => {
            println!("cargo:warning=无法定位 profile 目录，跳过原生库复制");
            return;
        }
    };

    let build_dir = profile_dir.join("build");
    if !build_dir.exists() {
        println!("cargo:warning=未找到 build 目录，跳过原生库复制");
        return;
    }

    // 定位 speech-sdk-* 构建产物里的 sdk_output 目录
    let sdk_output = match find_sdk_output(&build_dir) {
        Some(d) => d,
        None => {
            println!("cargo:warning=未找到 speech-sdk 原生产物，请先构建 speech-sdk");
            return;
        }
    };

    match target_os.as_str() {
        "windows" => copy_windows(&sdk_output, &profile_dir),
        "linux" => copy_linux(&sdk_output, &profile_dir, &target_arch),
        "macos" => copy_macos(&sdk_output, &profile_dir),
        other => {
            println!("cargo:warning=未支持的目标平台 {other}，跳过原生库复制");
        }
    }
}

/// 在 build_dir 下查找含 sdk_output 的 speech-sdk-* 产物目录。
fn find_sdk_output(build_dir: &Path) -> Option<PathBuf> {
    let entries = fs::read_dir(build_dir).ok()?;
    for e in entries.flatten() {
        let name = e.file_name();
        if name.to_string_lossy().starts_with("speech-sdk-") {
            let candidate = e.path().join("out").join("sdk_output");
            if candidate.exists() {
                return Some(candidate);
            }
        }
    }
    None
}

/// 复制目录下匹配判定的文件到目标目录，返回复制数量。
fn copy_matching(dir: &Path, dst_dir: &Path, pred: impl Fn(&Path) -> bool) -> usize {
    let mut n = 0;
    if let Ok(entries) = fs::read_dir(dir) {
        for e in entries.flatten() {
            let p = e.path();
            if p.is_file() && pred(&p) {
                if let Some(name) = p.file_name() {
                    if fs::copy(&p, dst_dir.join(name)).is_ok() {
                        n += 1;
                    }
                }
            }
        }
    }
    n
}

fn copy_windows(sdk_output: &Path, profile_dir: &Path) {
    let native = sdk_output.join("runtimes").join("win-x64").join("native");
    let n = copy_matching(&native, profile_dir, |p| {
        p.extension().and_then(|s| s.to_str()) == Some("dll")
    });
    if n == 0 {
        println!("cargo:warning=未在 {} 找到原生 DLL", native.display());
    }
}

fn copy_linux(sdk_output: &Path, profile_dir: &Path, arch: &str) {
    let sub = match arch {
        "aarch64" => "arm64",
        _ => "x64",
    };
    let native = sdk_output.join("lib").join(sub);
    let n = copy_matching(&native, profile_dir, |p| {
        p.file_name()
            .and_then(|s| s.to_str())
            .map(|s| s.contains(".so"))
            .unwrap_or(false)
    });
    if n == 0 {
        println!("cargo:warning=未在 {} 找到原生 .so", native.display());
    }
    // 让运行时从 exe 同目录查找 .so
    println!("cargo:rustc-link-arg=-Wl,-rpath,$ORIGIN");
}

fn copy_macos(sdk_output: &Path, profile_dir: &Path) {
    // macOS 为 xcframework，复制整个 .framework 到 exe 同目录
    let framework_src = sdk_output
        .join("MicrosoftCognitiveServicesSpeech.xcframework")
        .join("macos-arm64_x86_64")
        .join("MicrosoftCognitiveServicesSpeech.framework");
    if !framework_src.exists() {
        println!("cargo:warning=未在 {} 找到 framework", framework_src.display());
        return;
    }
    let framework_dst = profile_dir.join("MicrosoftCognitiveServicesSpeech.framework");
    if let Err(e) = copy_dir_recursive(&framework_src, &framework_dst) {
        println!("cargo:warning=复制 framework 失败：{e}");
    }
    // 让运行时从 exe 同目录查找 framework
    println!("cargo:rustc-link-arg=-Wl,-rpath,@executable_path");
}

/// 递归复制目录。
fn copy_dir_recursive(src: &Path, dst: &Path) -> std::io::Result<()> {
    fs::create_dir_all(dst)?;
    for entry in fs::read_dir(src)? {
        let entry = entry?;
        let from = entry.path();
        let to = dst.join(entry.file_name());
        if from.is_dir() {
            copy_dir_recursive(&from, &to)?;
        } else {
            fs::copy(&from, &to)?;
        }
    }
    Ok(())
}
