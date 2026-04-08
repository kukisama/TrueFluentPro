use crate::utils;
use std::path::Path;

pub fn run() -> i32 {
    let current_dir = std::env::current_dir().unwrap_or_default();
    println!("══ IconTool 自动处理模式 ══");
    println!("工作目录: {}", current_dir.display());
    println!();

    // Collect png/jpg/jpeg files
    let extensions = ["png", "jpg", "jpeg"];
    let files: Vec<String> = std::fs::read_dir(&current_dir)
        .unwrap_or_else(|_| std::fs::read_dir(".").unwrap())
        .flatten()
        .filter_map(|e| {
            let path = e.path();
            if path.is_file() {
                let ext = path.extension()?.to_str()?.to_lowercase();
                if extensions.contains(&ext.as_str()) {
                    return Some(path.to_string_lossy().to_string());
                }
            }
            None
        })
        .collect();

    if files.is_empty() {
        println!("当前目录没有找到 PNG/JPG/JPEG 文件。");
        return 0;
    }

    println!("找到 {} 个图片文件。", files.len());
    println!();

    // Create backup directory
    let timestamp = chrono_like_timestamp();
    let backup_dir = current_dir.join(format!("backup_{}", timestamp));
    let _ = std::fs::create_dir_all(&backup_dir);

    let mut log_lines = vec![
        "IconTool 自动处理日志".to_string(),
        format!("处理时间: {}", timestamp),
        format!("工作目录: {}", current_dir.display()),
        format!("备份目录: {}", backup_dir.display()),
        format!("待处理文件数: {}", files.len()),
        "─".repeat(60),
    ];

    let mut success_count = 0;
    let mut skip_count = 0;
    let mut fail_count = 0;

    for file_path in &files {
        let file_name = Path::new(file_path).file_name().unwrap_or_default().to_string_lossy().to_string();
        let name_no_ext = Path::new(file_path).file_stem().unwrap_or_default().to_string_lossy().to_string();
        println!("── 处理: {} ──", file_name);
        log_lines.push(String::new());
        log_lines.push(format!("[文件] {}", file_name));

        match image::open(file_path) {
            Ok(img) => {
                let mut rgba = img.to_rgba8();
                log_lines.push(format!("  尺寸: {}x{}", rgba.width(), rgba.height()));

                // 1. Check if already transparent
                let report = utils::analyze_transparency(&rgba);
                let already_transparent = report.transparent_pixels + report.semi_transparent_pixels > 0;

                if already_transparent && report.corners_transparent {
                    println!("  已是透明图片（透明像素 {}），跳过透明化。", report.transparent_pixels);
                    log_lines.push(format!("  结果: 跳过透明化 — 已是透明图片"));

                    // Backup
                    let backup_path = backup_dir.join(&file_name);
                    let _ = std::fs::copy(file_path, &backup_path);

                    // Center crop
                    let cropped = utils::center_crop_and_resize(&rgba, 512);
                    println!("  裁剪缩放: {}x{} → {}x{}", rgba.width(), rgba.height(), cropped.width(), cropped.height());

                    let ext = Path::new(file_path).extension().and_then(|e| e.to_str()).unwrap_or("").to_lowercase();
                    let output_png = if ext == "jpg" || ext == "jpeg" {
                        let p = current_dir.join(format!("{}.png", name_no_ext));
                        let _ = std::fs::remove_file(file_path);
                        p
                    } else {
                        Path::new(file_path).to_path_buf()
                    };

                    let _ = cropped.save(&output_png);
                    generate_ico_for_file(&output_png, &cropped, &backup_dir, &mut log_lines, &current_dir);

                    success_count += 1;
                    println!();
                    continue;
                }

                // 2. Detect border color
                let border = utils::detect_border_color(&rgba);
                if border.is_none() {
                    println!("  四周颜色不一致，不适合自动透明化，跳过。");
                    log_lines.push("  结果: 跳过 — 四周像素颜色不一致".to_string());
                    skip_count += 1;
                    println!();
                    continue;
                }

                let (bg_color, bg_name) = border.unwrap();
                println!("  检测到背景色: {} (R={}, G={}, B={})", bg_name, bg_color[0], bg_color[1], bg_color[2]);

                // 3. Backup
                let backup_path = backup_dir.join(&file_name);
                let _ = std::fs::copy(file_path, &backup_path);

                // 4. Auto flood fill transparent
                let (removed_count, used_threshold) = utils::auto_flood_fill_transparent(&mut rgba, &bg_color);
                let total_pixels = (rgba.width() * rgba.height()) as usize;
                let ratio = removed_count as f64 / total_pixels as f64 * 100.0;

                println!("  自动阈值: {}", used_threshold);
                println!("  透明化: {}/{} 像素 ({:.2}%)", removed_count, total_pixels, ratio);

                // 5. Center crop
                let cropped = utils::center_crop_and_resize(&rgba, 512);
                println!("  裁剪缩放: {}x{} → {}x{}", rgba.width(), rgba.height(), cropped.width(), cropped.height());

                // 6. Save as PNG
                let ext = Path::new(file_path).extension().and_then(|e| e.to_str()).unwrap_or("").to_lowercase();
                let output_png = if ext == "jpg" || ext == "jpeg" {
                    let p = current_dir.join(format!("{}.png", name_no_ext));
                    let _ = std::fs::remove_file(file_path);
                    p
                } else {
                    Path::new(file_path).to_path_buf()
                };

                let _ = cropped.save(&output_png);

                // 7. Generate ICO
                generate_ico_for_file(&output_png, &cropped, &backup_dir, &mut log_lines, &current_dir);

                log_lines.push("  结果: 成功".to_string());
                success_count += 1;
                println!();
            }
            Err(e) => {
                println!("  处理失败: {}", e);
                log_lines.push(format!("  结果: 失败 — {}", e));
                fail_count += 1;
                println!();
            }
        }
    }

    log_lines.push(String::new());
    log_lines.push("═".repeat(60));
    log_lines.push(format!("汇总: 共 {} 个文件, 成功 {}, 跳过 {}, 失败 {}", files.len(), success_count, skip_count, fail_count));

    let log_path = backup_dir.join("处理日志.txt");
    let _ = std::fs::write(&log_path, log_lines.join("\n"));

    println!("{}", "═".repeat(60));
    println!("处理完成: 成功={}, 跳过={}, 失败={}", success_count, skip_count, fail_count);
    println!("备份目录: {}", backup_dir.display());
    println!("处理日志: {}", log_path.display());

    if fail_count > 0 { 1 } else { 0 }
}

fn generate_ico_for_file(
    png_path: &Path,
    source_image: &image::RgbaImage,
    backup_dir: &Path,
    log_lines: &mut Vec<String>,
    work_dir: &Path,
) {
    let name_no_ext = png_path.file_stem().unwrap_or_default().to_string_lossy().to_string();
    let ico_path = work_dir.join(format!("{}.ico", name_no_ext));

    // Backup existing ICO
    if ico_path.exists() {
        let backup_ico = backup_dir.join(format!("{}.ico", name_no_ext));
        let _ = std::fs::copy(&ico_path, &backup_ico);
        println!("  备份已有 ICO: {}.ico", name_no_ext);
    }

    let square = utils::pad_to_square(source_image);
    let sizes = [16u32, 32, 48, 256];
    let mut ico_images: Vec<(u32, Vec<u8>)> = Vec::new();

    for &size in &sizes {
        let resized = image::imageops::resize(
            &square,
            size,
            size,
            image::imageops::FilterType::Nearest,
        );
        ico_images.push((size, utils::encode_png(&resized)));
    }

    utils::write_ico_file(&ico_path.to_string_lossy(), &ico_images);

    let ico_size = std::fs::metadata(&ico_path).map(|m| m.len()).unwrap_or(0);
    let size_str: Vec<String> = sizes.iter().map(|s| s.to_string()).collect();
    println!("  生成 ICO: {}.ico ({}, 尺寸: {})", name_no_ext, utils::format_file_size(ico_size), size_str.join("/"));
    log_lines.push(format!("  生成 ICO: {}.ico ({}, 尺寸: {})", name_no_ext, utils::format_file_size(ico_size), size_str.join("/")));
}

fn chrono_like_timestamp() -> String {
    use std::time::SystemTime;
    let now = SystemTime::now()
        .duration_since(SystemTime::UNIX_EPOCH)
        .unwrap_or_default();
    let secs = now.as_secs();
    // Simple timestamp format
    format!("{}", secs)
}
