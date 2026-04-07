use crate::utils;
use image::Rgba;
use std::path::Path;

pub fn run(file: &str, output: Option<&str>, blur: u32, offset: &str, color: &str) -> i32 {
    if !Path::new(file).exists() {
        eprintln!("错误: 文件不存在: {}", file);
        return 1;
    }

    let output_path = output
        .map(String::from)
        .unwrap_or_else(|| utils::default_output_path(file, "_shadow", "png"));

    let (offset_x, offset_y) = parse_offset(offset).unwrap_or((4, 4));

    let s_color = match utils::parse_color(color) {
        Some(c) => c,
        None => {
            eprintln!("错误: 无法解析颜色: {}", color);
            return 1;
        }
    };

    match image::open(file) {
        Ok(img) => {
            let rgba = img.to_rgba8();
            let w = rgba.width();
            let h = rgba.height();

            let expand = blur * 2 + (offset_x.unsigned_abs().max(offset_y.unsigned_abs())) as u32;
            let canvas_w = w + expand * 2;
            let canvas_h = h + expand * 2;

            println!("── 添加阴影 ──");
            println!("  输入: {} ({}x{})", file, w, h);
            println!("  模糊半径: {}, 偏移: ({},{})", blur, offset_x, offset_y);
            println!("  画布: {}x{}", canvas_w, canvas_h);

            // Create shadow layer
            let mut shadow_layer = image::RgbaImage::new(canvas_w, canvas_h);
            let shadow_draw_x = (expand as i32 + offset_x) as u32;
            let shadow_draw_y = (expand as i32 + offset_y) as u32;

            for y in 0..h {
                for x in 0..w {
                    let src = rgba.get_pixel(x, y);
                    if src[3] > 0 {
                        let sx = shadow_draw_x + x;
                        let sy = shadow_draw_y + y;
                        if sx < canvas_w && sy < canvas_h {
                            let shadow_a = (src[3] as f32 * 0.5) as u8;
                            shadow_layer.put_pixel(sx, sy, Rgba([s_color[0], s_color[1], s_color[2], shadow_a]));
                        }
                    }
                }
            }

            // Gaussian blur
            if blur > 0 {
                shadow_layer = image::imageops::blur(&shadow_layer, blur as f32);
            }

            // Draw original on top
            image::imageops::overlay(&mut shadow_layer, &rgba, expand as i64, expand as i64);

            if let Some(parent) = Path::new(&output_path).parent() {
                let _ = std::fs::create_dir_all(parent);
            }

            match shadow_layer.save(&output_path) {
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

fn parse_offset(s: &str) -> Option<(i32, i32)> {
    let parts: Vec<&str> = s.split(',').collect();
    if parts.len() == 2 {
        let x = parts[0].trim().parse().ok()?;
        let y = parts[1].trim().parse().ok()?;
        Some((x, y))
    } else {
        None
    }
}
