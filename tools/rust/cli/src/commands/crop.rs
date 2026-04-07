use crate::utils;
use std::path::Path;

pub fn run(file: &str, output: Option<&str>, size: u32) -> i32 {
    if !Path::new(file).exists() {
        eprintln!("错误: 文件不存在: {}", file);
        return 1;
    }

    let output_path = output
        .map(String::from)
        .unwrap_or_else(|| utils::default_output_path(file, "_cropped", "png"));

    println!("── 居中裁剪缩放 ──");
    println!("  输入: {}", file);
    println!("  目标尺寸: {}x{}", size, size);
    println!("  输出: {}", output_path);
    println!();

    match image::open(file) {
        Ok(img) => {
            let rgba = img.to_rgba8();
            println!("  原始尺寸: {}x{}", rgba.width(), rgba.height());

            let cropped = utils::center_crop_and_resize(&rgba, size);
            println!("  裁剪后尺寸: {}x{}", cropped.width(), cropped.height());

            if let Some(parent) = Path::new(&output_path).parent() {
                let _ = std::fs::create_dir_all(parent);
            }

            match cropped.save(&output_path) {
                Ok(_) => {
                    let file_size = std::fs::metadata(&output_path).map(|m| m.len()).unwrap_or(0);
                    println!("  输出文件大小: {}", utils::format_file_size(file_size));
                    println!();
                    println!("✓ 裁剪完成。");
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
