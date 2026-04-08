use crate::utils;
use std::path::Path;

pub fn run(files: &[String], output: Option<&str>) -> i32 {
    if files.is_empty() {
        eprintln!("错误: 至少需要一个图片文件。");
        return 1;
    }

    let output_path = output.map(String::from).unwrap_or_else(|| {
        Path::new(&files[0]).parent().unwrap_or(Path::new(".")).join("composed.ico").to_string_lossy().to_string()
    });

    println!("── 多图拼合成 ICO ──");
    println!("  输入: {} 个文件", files.len());

    let mut ico_images: Vec<(u32, Vec<u8>)> = Vec::new();

    for f in files {
        if !Path::new(f).exists() {
            println!("  ⚠ 跳过不存在的文件: {}", f);
            continue;
        }

        match image::open(f) {
            Ok(img) => {
                let rgba = img.to_rgba8();
                let size = rgba.width();
                let png_bytes = utils::encode_png(&rgba);
                ico_images.push((size, png_bytes));
                println!("  [{}] {} → {}x{}", ico_images.len(), Path::new(f).file_name().unwrap_or_default().to_string_lossy(), size, rgba.height());
            }
            Err(e) => {
                println!("  ⚠ 跳过无法加载的文件 {}: {}", f, e);
            }
        }
    }

    if ico_images.is_empty() {
        eprintln!("错误: 没有有效的图片文件。");
        return 1;
    }

    ico_images.sort_by_key(|(s, _)| *s);

    if let Some(parent) = Path::new(&output_path).parent() {
        let _ = std::fs::create_dir_all(parent);
    }

    utils::write_ico_file(&output_path, &ico_images);

    let file_size = std::fs::metadata(&output_path).map(|m| m.len()).unwrap_or(0);
    println!("\n  输出: {} ({}, {} 张)", output_path, utils::format_file_size(file_size), ico_images.len());
    println!("✓ 完成。");
    0
}
