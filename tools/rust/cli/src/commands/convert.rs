use crate::utils;
use std::path::Path;

pub fn run(file: &str, output: Option<&str>, format: Option<&str>, sizes: &str) -> i32 {
    if !Path::new(file).exists() {
        eprintln!("错误: 文件不存在: {}", file);
        return 1;
    }

    let src_ext = Path::new(file)
        .extension()
        .and_then(|e| e.to_str())
        .unwrap_or("")
        .to_lowercase();

    let fmt = format.unwrap_or_else(|| match src_ext.as_str() {
        "png" => "ico",
        "ico" => "png",
        "bmp" => "png",
        _ => "png",
    });

    if !["png", "ico", "bmp"].contains(&fmt) {
        eprintln!("错误: 不支持的目标格式: {}。支持 png, ico, bmp。", fmt);
        return 1;
    }

    let output_path = output.map(String::from).unwrap_or_else(|| {
        utils::default_output_path(file, "", fmt)
    });

    println!("── 格式转换 ──");
    println!("  输入: {} (.{})", file, src_ext);
    println!("  输出: {} (.{})", output_path, fmt);

    match convert_impl(file, &src_ext, &output_path, fmt, sizes) {
        Ok(_) => {
            let file_size = std::fs::metadata(&output_path).map(|m| m.len()).unwrap_or(0);
            println!("  输出大小: {}", utils::format_file_size(file_size));
            println!("✓ 转换完成。");
            0
        }
        Err(e) => {
            eprintln!("错误: 转换失败: {}", e);
            1
        }
    }
}

fn convert_impl(file: &str, src_ext: &str, output_path: &str, fmt: &str, sizes: &str) -> Result<(), String> {
    if src_ext == "ico" && fmt == "png" {
        let ico_bytes = std::fs::read(file).map_err(|e| e.to_string())?;
        let largest = utils::extract_largest_image_from_ico(&ico_bytes)
            .ok_or("无法从 ICO 中提取图像")?;
        if let Some(parent) = Path::new(output_path).parent() {
            let _ = std::fs::create_dir_all(parent);
        }
        std::fs::write(output_path, largest).map_err(|e| e.to_string())?;
    } else if fmt == "ico" {
        let size_list = utils::parse_size_list(sizes);
        println!("  ICO 尺寸: {:?}", size_list);
        let img = image::open(file).map_err(|e| e.to_string())?;
        let rgba = img.to_rgba8();
        let square = utils::pad_to_square(&rgba);
        utils::build_and_save_ico(&square, &size_list, output_path);
    } else if fmt == "bmp" {
        let img = image::open(file).map_err(|e| e.to_string())?;
        img.save(output_path).map_err(|e| e.to_string())?;
    } else {
        // png
        let img = image::open(file).map_err(|e| e.to_string())?;
        let rgba = img.to_rgba8();
        rgba.save(output_path).map_err(|e| e.to_string())?;
    }

    Ok(())
}
