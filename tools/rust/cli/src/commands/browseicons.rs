use crate::utils;
use icon_core::ico_helper;
use std::io::{self, Write};
use std::path::Path;

pub fn run(dir: Option<&str>) -> i32 {
    let target_dir = dir
        .map(|d| std::fs::canonicalize(d).unwrap_or_else(|_| Path::new(d).to_path_buf()))
        .unwrap_or_else(|| std::env::current_dir().unwrap_or_default());

    if !target_dir.exists() {
        eprintln!("错误: 目录不存在: {}", target_dir.display());
        return 1;
    }

    println!("── 浏览目录中的 EXE 图标 ──");
    println!("  目录: {}", target_dir.display());
    println!();

    // Scan EXE files
    let exe_files: Vec<_> = std::fs::read_dir(&target_dir)
        .unwrap_or_else(|_| std::fs::read_dir(".").unwrap())
        .flatten()
        .filter(|e| {
            e.path()
                .extension()
                .map(|ext| ext.to_ascii_lowercase() == "exe")
                .unwrap_or(false)
        })
        .map(|e| e.path())
        .collect();

    if exe_files.is_empty() {
        println!("  该目录下没有找到 EXE 文件。");
        return 0;
    }

    // Extract all icons
    let mut all_icons: Vec<(String, i32, Vec<u8>, String)> = Vec::new();

    for exe_path in &exe_files {
        if let Ok(groups) = icon_core::pe_icon_extractor::extract_from_file(exe_path) {
            let exe_name = exe_path.file_name().unwrap_or_default().to_string_lossy().to_string();
            for group in groups {
                let size_info = ico_helper::describe_sizes(&group.ico_data);
                all_icons.push((exe_name.clone(), group.group_id, group.ico_data, size_info));
            }
        }
    }

    if all_icons.is_empty() {
        println!("  未从任何 EXE 中找到图标资源。");
        return 0;
    }

    println!("  找到 {} 个图标:", all_icons.len());
    println!();

    for (i, (exe_name, group_id, ico_data, size_info)) in all_icons.iter().enumerate() {
        let max_size = ico_helper::get_max_size(ico_data);
        let hd_tag = if max_size >= 256 { " ★HD" } else { "" };
        println!(
            "  [{}] {} → 图标组 {}: {}{}  ({})",
            i + 1,
            exe_name,
            group_id,
            size_info,
            hd_tag,
            utils::format_file_size(ico_data.len() as u64)
        );
    }

    println!();
    println!("  输入编号选择图标设为目录图标，输入 0 或 q 退出:");
    print!("  > ");
    io::stdout().flush().unwrap();

    let mut input = String::new();
    if io::stdin().read_line(&mut input).is_err() {
        return 0;
    }
    let input = input.trim();

    if input.is_empty() || input == "0" || input.eq_ignore_ascii_case("q") {
        println!("  已取消。");
        return 0;
    }

    let selection: usize = match input.parse() {
        Ok(n) if n >= 1 && n <= all_icons.len() => n,
        _ => {
            eprintln!("错误: 无效的选择: {}", input);
            return 1;
        }
    };

    let (exe_name, group_id, ico_data, size_info) = &all_icons[selection - 1];
    println!();
    println!("  选中: {} 图标组 {} ({})", exe_name, group_id, size_info);

    let stem = Path::new(exe_name).file_stem().unwrap_or_default().to_string_lossy();
    let ico_file_name = format!("{}_icon{}.ico", stem, group_id);
    let ico_path = target_dir.join(&ico_file_name);

    if let Err(e) = std::fs::write(&ico_path, ico_data) {
        eprintln!("错误: 保存图标失败: {}", e);
        return 1;
    }
    println!("  已保存图标: {}", ico_file_name);

    // Write desktop.ini
    let desktop_ini = target_dir.join("desktop.ini");
    let content = format!("[.ShellClassInfo]\nIconResource={},0\n", ico_file_name);
    let _ = std::fs::write(&desktop_ini, &content);
    println!("  写入: desktop.ini");

    #[cfg(windows)]
    {
        if let Err(e) = icon_core::directory_icon_service::set_directory_icon_from_bytes(
            ico_data,
            &ico_file_name,
            &target_dir,
        ) {
            eprintln!("  ⚠ Shell 通知失败: {}", e);
        } else {
            println!("  已通知资源管理器刷新图标缓存");
        }
    }

    println!();
    println!("✓ 目录图标设置完成。");
    0
}
