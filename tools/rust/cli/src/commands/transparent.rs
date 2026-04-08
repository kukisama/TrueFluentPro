use crate::utils;
use image::{Rgba, RgbaImage};
use std::path::Path;

pub fn run(file: &str, output: Option<&str>, color: &str, threshold: u8, flood: bool) -> i32 {
    if !Path::new(file).exists() {
        eprintln!("错误: 文件不存在: {}", file);
        return 1;
    }

    let output_path = output
        .map(String::from)
        .unwrap_or_else(|| utils::default_output_path(file, "_transparent", "png"));

    let target_color = match utils::parse_color(color) {
        Some(c) => c,
        None => {
            eprintln!("错误: 无法解析颜色: {}。支持 white, black, #RRGGBB, #RRGGBBAA。", color);
            return 1;
        }
    };

    println!("── 透明化处理 ──");
    println!("  输入: {}", file);
    println!("  去除背景色: {} (R={}, G={}, B={})", color, target_color[0], target_color[1], target_color[2]);
    println!("  颜色容差: {}", threshold);
    println!("  模式: {}", if flood { "连通填充（仅从边缘可达区域）" } else { "全局匹配" });
    println!("  输出: {}", output_path);
    println!();

    match image::open(file) {
        Ok(img) => {
            let mut image = img.to_rgba8();
            println!("  图像尺寸: {}x{}", image.width(), image.height());

            let total_pixels = (image.width() * image.height()) as usize;
            let removed_count;

            if flood {
                removed_count = utils::flood_fill_transparent(&mut image, &target_color, threshold);
            } else {
                removed_count = global_transparent(&mut image, &target_color, threshold);
            }

            let ratio = removed_count as f64 / total_pixels as f64 * 100.0;
            println!("  已透明化像素: {}/{} ({:.2}%)", removed_count, total_pixels, ratio);

            if let Some(parent) = Path::new(&output_path).parent() {
                let _ = std::fs::create_dir_all(parent);
            }

            match image.save(&output_path) {
                Ok(_) => {
                    let file_size = std::fs::metadata(&output_path).map(|m| m.len()).unwrap_or(0);
                    println!("  输出文件大小: {}", utils::format_file_size(file_size));
                    println!();

                    println!("── 输出文件透明度检测 ──");
                    println!();
                    let report = utils::analyze_transparency(&image);
                    utils::print_transparency_report(&report, image.width(), image.height(), "");
                    println!();
                    println!("✓ 透明化处理完成。");
                    0
                }
                Err(e) => {
                    eprintln!("错误: 保存失败: {}", e);
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

fn global_transparent(image: &mut RgbaImage, target_color: &Rgba<u8>, threshold: u8) -> usize {
    let mut removed = 0;
    let w = image.width();
    let h = image.height();

    for y in 0..h {
        for x in 0..w {
            let pixel = *image.get_pixel(x, y);
            if utils::is_color_match(&pixel, target_color, threshold) {
                image.put_pixel(x, y, Rgba([0, 0, 0, 0]));
                removed += 1;
            } else if pixel[3] == 255 {
                let distance = utils::color_distance(&pixel, target_color);
                if distance < threshold as f64 * 2.0 {
                    let alpha = (255.0 * distance / (threshold as f64 * 2.0)).clamp(0.0, 255.0) as u8;
                    image.put_pixel(x, y, Rgba([pixel[0], pixel[1], pixel[2], alpha]));
                    if alpha == 0 {
                        removed += 1;
                    }
                }
            }
        }
    }

    removed
}
