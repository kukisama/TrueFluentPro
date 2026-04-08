use crate::utils;
use std::path::Path;

pub fn run(base: &str, overlay_img: &str, output: Option<&str>, position: &str, scale: u32, margin: u32) -> i32 {
    if !Path::new(base).exists() {
        eprintln!("错误: 底图不存在: {}", base);
        return 1;
    }
    if !Path::new(overlay_img).exists() {
        eprintln!("错误: 叠加图不存在: {}", overlay_img);
        return 1;
    }

    let output_path = output
        .map(String::from)
        .unwrap_or_else(|| utils::default_output_path(base, "_overlay", "png"));

    match (image::open(base), image::open(overlay_img)) {
        (Ok(base_img), Ok(overlay)) => {
            let mut base_rgba = base_img.to_rgba8();
            let overlay_rgba = overlay.to_rgba8();

            let bw = base_rgba.width();
            let bh = base_rgba.height();
            let target_w = (bw as f64 * scale as f64 / 100.0) as u32;
            let target_h = (overlay_rgba.height() as f64 * (target_w as f64 / overlay_rgba.width() as f64)) as u32;
            let margin_px = (bw as f64 * margin as f64 / 100.0) as u32;

            let resized = image::imageops::resize(&overlay_rgba, target_w, target_h, image::imageops::FilterType::Lanczos3);

            let (x, y) = match position {
                "tl" => (margin_px, margin_px),
                "tr" => (bw - target_w - margin_px, margin_px),
                "bl" => (margin_px, bh - target_h - margin_px),
                "br" => (bw - target_w - margin_px, bh - target_h - margin_px),
                "c" | "center" => ((bw - target_w) / 2, (bh - target_h) / 2),
                _ => (bw - target_w - margin_px, bh - target_h - margin_px),
            };

            println!("── 叠加角标 ──");
            println!("  底图: {} ({}x{})", base, bw, bh);
            println!("  叠加: {} → {}x{}", overlay_img, target_w, target_h);
            println!("  位置: {}, 偏移: ({},{})", position, x, y);

            image::imageops::overlay(&mut base_rgba, &resized, x as i64, y as i64);

            if let Some(parent) = Path::new(&output_path).parent() {
                let _ = std::fs::create_dir_all(parent);
            }

            match base_rgba.save(&output_path) {
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
        (Err(e), _) | (_, Err(e)) => {
            eprintln!("错误: 加载图片失败: {}", e);
            1
        }
    }
}
