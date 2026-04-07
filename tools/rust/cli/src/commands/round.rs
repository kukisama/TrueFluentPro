use crate::utils;
use image::Rgba;
use std::path::Path;

pub fn run(file: &str, output: Option<&str>, radius: Option<u32>, circle: bool) -> i32 {
    if !Path::new(file).exists() {
        eprintln!("错误: 文件不存在: {}", file);
        return 1;
    }

    let output_path = output
        .map(String::from)
        .unwrap_or_else(|| utils::default_output_path(file, "_round", "png"));

    match image::open(file) {
        Ok(img) => {
            let mut rgba = img.to_rgba8();
            let w = rgba.width();
            let h = rgba.height();
            let side = w.min(h);

            let r = if circle {
                side / 2
            } else {
                radius.unwrap_or((side as f64 * 0.15) as u32)
            };

            println!("── 圆角裁剪 ──");
            println!("  输入: {} ({}x{})", file, w, h);
            println!("  模式: {}", if circle { "圆形".to_string() } else { format!("圆角 r={}", r) });

            apply_rounded_corners_mask(&mut rgba, r);

            if let Some(parent) = Path::new(&output_path).parent() {
                let _ = std::fs::create_dir_all(parent);
            }

            match rgba.save(&output_path) {
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

fn apply_rounded_corners_mask(image: &mut image::RgbaImage, radius: u32) {
    let w = image.width();
    let h = image.height();
    if radius == 0 {
        return;
    }
    let r = radius.min(w.min(h) / 2);

    for y in 0..h {
        for x in 0..w {
            let alpha = get_rounded_rect_alpha(x, y, w, h, r);
            if alpha < 1.0 {
                let pixel = *image.get_pixel(x, y);
                let new_a = (pixel[3] as f32 * alpha) as u8;
                image.put_pixel(x, y, Rgba([pixel[0], pixel[1], pixel[2], new_a]));
            }
        }
    }
}

fn get_rounded_rect_alpha(x: u32, y: u32, w: u32, h: u32, r: u32) -> f32 {
    let (cx, cy);

    if x < r && y < r {
        cx = r as f32;
        cy = r as f32;
    } else if x >= w - r && y < r {
        cx = (w - r) as f32;
        cy = r as f32;
    } else if x < r && y >= h - r {
        cx = r as f32;
        cy = (h - r) as f32;
    } else if x >= w - r && y >= h - r {
        cx = (w - r) as f32;
        cy = (h - r) as f32;
    } else {
        return 1.0;
    }

    let dx = x as f32 - cx + 0.5;
    let dy = y as f32 - cy + 0.5;
    let dist = (dx * dx + dy * dy).sqrt();

    if dist <= r as f32 - 0.5 {
        1.0
    } else if dist >= r as f32 + 0.5 {
        0.0
    } else {
        r as f32 + 0.5 - dist
    }
}
