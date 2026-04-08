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

    match ext.as_str() {
        "ico" => check_ico(file),
        "png" => check_png(file),
        _ => {
            eprintln!("错误: 不支持的文件格式: .{}。仅支持 .png 和 .ico。", ext);
            1
        }
    }
}

fn check_png(file: &str) -> i32 {
    println!("── 检测 PNG: {} ──", Path::new(file).file_name().unwrap_or_default().to_string_lossy());
    println!();

    match image::open(file) {
        Ok(img) => {
            let rgba = img.to_rgba8();
            let report = utils::analyze_transparency(&rgba);
            utils::print_transparency_report(&report, rgba.width(), rgba.height(), "");
            0
        }
        Err(e) => {
            eprintln!("错误: 读取 PNG 失败: {}", e);
            1
        }
    }
}

fn check_ico(file: &str) -> i32 {
    println!("── 检测 ICO: {} ──", Path::new(file).file_name().unwrap_or_default().to_string_lossy());
    println!();

    match std::fs::read(file) {
        Ok(ico_bytes) => {
            if ico_bytes.len() < 6 {
                eprintln!("错误: ICO 文件太小，不是有效的 ICO 格式。");
                return 1;
            }

            let reserved = u16::from_le_bytes([ico_bytes[0], ico_bytes[1]]);
            let ico_type = u16::from_le_bytes([ico_bytes[2], ico_bytes[3]]);
            let count = u16::from_le_bytes([ico_bytes[4], ico_bytes[5]]);

            if reserved != 0 || ico_type != 1 {
                eprintln!("错误: 不是有效的 ICO 文件（头部标志不正确）。");
                return 1;
            }

            println!("ICO 包含 {} 张嵌入图像", count);
            println!();

            for i in 0..count as usize {
                let entry_offset = 6 + i * 16;
                if entry_offset + 16 > ico_bytes.len() {
                    println!("  [图像 {}] 目录项超出文件范围，跳过。", i + 1);
                    continue;
                }

                let w = if ico_bytes[entry_offset] == 0 { 256 } else { ico_bytes[entry_offset] as u32 };
                let h = if ico_bytes[entry_offset + 1] == 0 { 256 } else { ico_bytes[entry_offset + 1] as u32 };
                let data_size = u32::from_le_bytes([
                    ico_bytes[entry_offset + 8],
                    ico_bytes[entry_offset + 9],
                    ico_bytes[entry_offset + 10],
                    ico_bytes[entry_offset + 11],
                ]) as usize;
                let data_offset = u32::from_le_bytes([
                    ico_bytes[entry_offset + 12],
                    ico_bytes[entry_offset + 13],
                    ico_bytes[entry_offset + 14],
                    ico_bytes[entry_offset + 15],
                ]) as usize;

                println!("  [图像 {}] {}x{}, 数据大小={} 字节", i + 1, w, h, data_size);

                if data_offset + data_size > ico_bytes.len() {
                    println!("    ⚠ 数据偏移超出文件范围，跳过。");
                    println!();
                    continue;
                }

                let image_data = &ico_bytes[data_offset..data_offset + data_size];
                match image::load_from_memory(image_data) {
                    Ok(img) => {
                        let rgba = img.to_rgba8();
                        let report = utils::analyze_transparency(&rgba);
                        utils::print_transparency_report(&report, rgba.width(), rgba.height(), "    ");
                    }
                    Err(_) => {
                        println!("    ⚠ 无法解码嵌入图像（可能是旧式 BMP 格式），跳过透明度分析。");
                    }
                }
                println!();
            }

            0
        }
        Err(e) => {
            eprintln!("错误: 读取 ICO 失败: {}", e);
            1
        }
    }
}
