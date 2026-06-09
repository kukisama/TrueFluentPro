// build.rs — 将 speech-sdk 下载的原生 DLL 复制到 exe 同目录，
// 否则运行时找不到 Microsoft.CognitiveServices.Speech.core.dll。

use std::fs;
use std::path::{Path, PathBuf};

fn main() {
    // 仅 Windows 需要复制原生 DLL
    if std::env::var("CARGO_CFG_TARGET_OS").as_deref() != Ok("windows") {
        return;
    }

    let out_dir = std::env::var("OUT_DIR").unwrap();
    // OUT_DIR = target/<profile>/build/speech-test-ui-HASH/out
    // 向上 3 级到 profile 目录（exe 所在目录）
    let profile_dir = Path::new(&out_dir)
        .ancestors()
        .nth(3)
        .expect("无法定位 profile 目录")
        .to_path_buf();

    let build_dir = profile_dir.join("build");
    if !build_dir.exists() {
        println!("cargo:warning=未找到 build 目录，跳过 DLL 复制");
        return;
    }

    // 查找 speech-sdk-* 构建产物里的原生 DLL
    let mut native_dir: Option<PathBuf> = None;
    if let Ok(entries) = fs::read_dir(&build_dir) {
        for e in entries.flatten() {
            let name = e.file_name();
            let name = name.to_string_lossy();
            if name.starts_with("speech-sdk-") {
                let candidate = e
                    .path()
                    .join("out")
                    .join("sdk_output")
                    .join("runtimes")
                    .join("win-x64")
                    .join("native");
                if candidate.join("Microsoft.CognitiveServices.Speech.core.dll").exists() {
                    native_dir = Some(candidate);
                    break;
                }
            }
        }
    }

    let native_dir = match native_dir {
        Some(d) => d,
        None => {
            println!("cargo:warning=未找到原生 Speech DLL，请先构建 speech-sdk");
            return;
        }
    };

    if let Ok(entries) = fs::read_dir(&native_dir) {
        for e in entries.flatten() {
            let p = e.path();
            if p.extension().and_then(|s| s.to_str()) == Some("dll") {
                let dst = profile_dir.join(p.file_name().unwrap());
                let _ = fs::copy(&p, &dst);
            }
        }
    }

    println!("cargo:rerun-if-changed=build.rs");
}
