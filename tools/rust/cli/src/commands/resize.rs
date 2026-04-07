use crate::utils;
use std::path::Path;

pub fn run(file: &str, output: Option<&str>, sizes: &str, format: &str) -> i32 {
    if !Path::new(file).exists() {
        eprintln!("错误: 文件不存在: {}", file);
        return 1;
    }

    let output_dir = output
        .map(String::from)
        .unwrap_or_else(|| {
            Path::new(file).parent().unwrap_or(Path::new(".")).to_string_lossy().to_string()
        });
    let _ = std::fs::create_dir_all(&output_dir);

    let size_list = utils::parse_size_list(sizes);
    let base_name = Path::new(file).file_stem().unwrap_or_default().to_string_lossy().to_string();

    println!("── 批量生成多尺寸 ──");
    println!("  输入: {}", file);
    println!("  尺寸: {:?}", size_list);
    println!("  格式: {}", format);
    println!();

    match image::open(file) {
        Ok(img) => {
            let rgba = img.to_rgba8();
            let square = utils::pad_to_square(&rgba);

            for &size in &size_list {
                let resized = image::imageops::resize(&square, size, size, image::imageops::FilterType::Lanczos3);
                let out_name = format!("{}_{size}x{size}.{format}", base_name);
                let out_path = Path::new(&output_dir).join(&out_name);

                if let Err(e) = resized.save(&out_path) {
                    eprintln!("  保存失败: {}: {}", out_name, e);
                    continue;
                }

                let file_size = std::fs::metadata(&out_path).map(|m| m.len()).unwrap_or(0);
                println!("  {size}x{size} → {} ({})", out_name, utils::format_file_size(file_size));
            }

            println!();
            println!("✓ 共生成 {} 个文件。", size_list.len());
            0
        }
        Err(e) => {
            eprintln!("错误: 处理失败: {}", e);
            1
        }
    }
}
