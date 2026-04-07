use crate::utils;
use image::Rgba;
use std::path::Path;

pub fn run(file: &str, output: Option<&str>, padding: Option<u32>, percent: u32, color: &str) -> i32 {
    if !Path::new(file).exists() {
        eprintln!("错误: 文件不存在: {}", file);
        return 1;
    }

    let output_path = output
        .map(String::from)
        .unwrap_or_else(|| utils::default_output_path(file, "_padded", "png"));

    match image::open(file) {
        Ok(img) => {
            let rgba = img.to_rgba8();
            let pad = padding.unwrap_or_else(|| {
                (rgba.width().max(rgba.height()) as f64 * percent as f64 / 100.0) as u32
            });

            let new_w = rgba.width() + pad * 2;
            let new_h = rgba.height() + pad * 2;

            let bg = if color.eq_ignore_ascii_case("transparent") {
                Rgba([0, 0, 0, 0])
            } else {
                match utils::parse_color(color) {
                    Some(c) => c,
                    None => {
                        eprintln!("错误: 无法解析颜色: {}", color);
                        return 1;
                    }
                }
            };

            println!("── 加边距 ──");
            println!("  输入: {} ({}x{})", file, rgba.width(), rgba.height());
            println!("  边距: {}px", pad);
            println!("  输出尺寸: {}x{}", new_w, new_h);

            let mut result = image::RgbaImage::from_pixel(new_w, new_h, bg);
            image::imageops::overlay(&mut result, &rgba, pad as i64, pad as i64);

            if let Some(parent) = Path::new(&output_path).parent() {
                let _ = std::fs::create_dir_all(parent);
            }

            match result.save(&output_path) {
                Ok(_) => {
                    let file_size = std::fs::metadata(&output_path).map(|m| m.len()).unwrap_or(0);
                    println!("  输出: {} ({})", output_path, utils::format_file_size(file_size));
                    println!("✓ 完成。");
                    0
                }
                Err(e) => {
                    eprintln!("错误: 处理失败: {}", e);
                    1
                }
            }
        }
        Err(e) => {
            eprintln!("错误: 处理失败: {}", e);
            1
        }
    }
}
