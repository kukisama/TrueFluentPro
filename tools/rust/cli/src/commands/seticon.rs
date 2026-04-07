use std::path::Path;

pub fn run(ico: &str, dir: &str) -> i32 {
    if !Path::new(ico).exists() {
        eprintln!("错误: ICO 文件不存在: {}", ico);
        return 1;
    }

    let ext = Path::new(ico)
        .extension()
        .and_then(|e| e.to_str())
        .unwrap_or("")
        .to_lowercase();
    if ext != "ico" {
        eprintln!("错误: 图标文件必须是 .ico 格式。");
        return 1;
    }

    let target_dir = std::fs::canonicalize(dir).unwrap_or_else(|_| Path::new(dir).to_path_buf());
    if !target_dir.exists() {
        eprintln!("错误: 目录不存在: {}", dir);
        return 1;
    }

    println!("── 设置目录图标 ──");
    println!("  图标: {}", std::fs::canonicalize(ico).unwrap_or_else(|_| Path::new(ico).to_path_buf()).display());
    println!("  目录: {}", target_dir.display());
    println!();

    // Copy ICO to target directory
    let ico_file_name = Path::new(ico).file_name().unwrap_or_default().to_string_lossy().to_string();
    let dest_ico = target_dir.join(&ico_file_name);

    let source_full = std::fs::canonicalize(ico).unwrap_or_else(|_| Path::new(ico).to_path_buf());
    if source_full != dest_ico {
        if let Err(e) = std::fs::copy(&source_full, &dest_ico) {
            eprintln!("错误: 复制图标失败: {}", e);
            return 1;
        }
        println!("  复制图标: {} → {}", ico_file_name, target_dir.display());
    }

    // Write desktop.ini
    let desktop_ini = target_dir.join("desktop.ini");
    let content = format!("[.ShellClassInfo]\nIconResource={},0\n", ico_file_name);
    if let Err(e) = std::fs::write(&desktop_ini, &content) {
        eprintln!("错误: 写入 desktop.ini 失败: {}", e);
        return 1;
    }
    println!("  写入: desktop.ini");

    // On Windows, use Shell APIs
    #[cfg(windows)]
    {
        if let Err(e) = icon_core::directory_icon_service::set_directory_icon(ico, dir) {
            eprintln!("错误: 设置失败: {}", e);
            return 1;
        }
        println!("  已通知资源管理器刷新图标缓存");
    }

    println!();
    println!("✓ 目录图标设置完成。");
    println!();
    println!("提示: 如果图标未立即显示，可尝试：");
    println!("  1. 按 F5 刷新资源管理器");
    println!("  2. 关闭并重新打开资源管理器窗口");

    0
}
