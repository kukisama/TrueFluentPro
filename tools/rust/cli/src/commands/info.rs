use crate::utils;
use std::path::Path;

pub fn run(file: &str) -> i32 {
    if !Path::new(file).exists() {
        eprintln!("错误: 文件不存在: {}", file);
        return 1;
    }

    let ext = Path::new(file)
        .extension()
        .and_then(|e| e.to_str())
        .unwrap_or("")
        .to_lowercase();

    let file_size = std::fs::metadata(file).map(|m| m.len()).unwrap_or(0);

    println!("── 图像信息: {} ──", Path::new(file).file_name().unwrap_or_default().to_string_lossy());
    println!("  路径: {}", std::fs::canonicalize(file).unwrap_or_else(|_| Path::new(file).to_path_buf()).display());
    println!("  文件大小: {}", utils::format_file_size(file_size));
    println!("  格式: {}", ext);

    if ext == "ico" {
        return info_ico(file);
    }

    match image::open(file) {
        Ok(img) => {
            let rgba = img.to_rgba8();
            let w = rgba.width();
            let h = rgba.height();

            println!("  尺寸: {}x{}", w, h);
            println!("  像素数: {}", w as u64 * h as u64);
            println!("  宽高比: {:.3}", w as f64 / h as f64);

            // Transparency stats
            let mut transparent = 0u64;
            let mut semi_transparent = 0u64;
            let total = w as u64 * h as u64;

            for pixel in rgba.pixels() {
                if pixel[3] == 0 {
                    transparent += 1;
                } else if pixel[3] < 255 {
                    semi_transparent += 1;
                }
            }

            let has_alpha = transparent + semi_transparent > 0;
            println!("  包含Alpha: {}", if has_alpha { "是" } else { "否" });
            if has_alpha {
                println!("    完全透明: {} ({:.1}%)", transparent, transparent as f64 / total as f64 * 100.0);
                println!("    半透明: {} ({:.1}%)", semi_transparent, semi_transparent as f64 / total as f64 * 100.0);
            }
            0
        }
        Err(e) => {
            eprintln!("错误: 读取失败: {}", e);
            1
        }
    }
}

fn info_ico(file: &str) -> i32 {
    match std::fs::read(file) {
        Ok(ico_bytes) => {
            if ico_bytes.len() < 6 {
                eprintln!("错误: ICO 文件太小。");
                return 1;
            }
            let count = u16::from_le_bytes([ico_bytes[4], ico_bytes[5]]);
            println!("  包含图像: {} 张", count);

            for i in 0..count as usize {
                let off = 6 + i * 16;
                if off + 16 > ico_bytes.len() {
                    break;
                }
                let w = if ico_bytes[off] == 0 { 256u32 } else { ico_bytes[off] as u32 };
                let h = if ico_bytes[off + 1] == 0 { 256u32 } else { ico_bytes[off + 1] as u32 };
                let bpp = u16::from_le_bytes([ico_bytes[off + 6], ico_bytes[off + 7]]);
                let data_size = u32::from_le_bytes([ico_bytes[off + 8], ico_bytes[off + 9], ico_bytes[off + 10], ico_bytes[off + 11]]);
                println!("    [{}] {}x{}, {}bpp, {}", i + 1, w, h, bpp, utils::format_file_size(data_size as u64));
            }
            0
        }
        Err(e) => {
            eprintln!("错误: 读取失败: {}", e);
            1
        }
    }
}
