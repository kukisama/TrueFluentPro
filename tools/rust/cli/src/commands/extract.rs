use crate::utils;
use icon_core::ico_helper;
use std::path::Path;

pub fn run(file: &str, output: Option<&str>) -> i32 {
    if !Path::new(file).exists() {
        eprintln!("错误: 文件不存在: {}", file);
        return 1;
    }

    let output_dir = output
        .map(String::from)
        .unwrap_or_else(|| std::env::current_dir().unwrap_or_default().to_string_lossy().to_string());

    let _ = std::fs::create_dir_all(&output_dir);

    println!("── 从 PE 文件提取图标 ──");
    println!("  输入: {}", file);
    println!("  输出目录: {}", std::fs::canonicalize(&output_dir).unwrap_or_else(|_| Path::new(&output_dir).to_path_buf()).display());
    println!();

    match icon_core::pe_icon_extractor::extract_from_file(file) {
        Ok(groups) => {
            if groups.is_empty() {
                println!("  未找到图标资源。");
                return 0;
            }

            let base_name = Path::new(file)
                .file_stem()
                .unwrap_or_default()
                .to_string_lossy();
            let mut saved = 0;

            for group in &groups {
                let size_info = ico_helper::describe_sizes(&group.ico_data);
                let out_name = format!("{}_icon{}.ico", base_name, group.group_id);
                let out_path = Path::new(&output_dir).join(&out_name);

                if let Err(e) = std::fs::write(&out_path, &group.ico_data) {
                    eprintln!("  写入失败: {}: {}", out_name, e);
                    continue;
                }

                let file_size = std::fs::metadata(&out_path).map(|m| m.len()).unwrap_or(0);
                println!("  [图标 {}] {} → {} ({})", group.group_id, size_info, out_name, utils::format_file_size(file_size));
                saved += 1;
            }

            println!();
            println!("✓ 共提取 {} 个图标文件。", saved);
            0
        }
        Err(e) => {
            eprintln!("错误: 提取失败: {}", e);
            1
        }
    }
}
