use crate::utils;
use std::path::Path;

pub fn run(files: &[String], output: Option<&str>, json: Option<&str>, cell_size: Option<u32>) -> i32 {
    if files.is_empty() {
        eprintln!("错误: 至少需要一个图片文件或目录。");
        return 1;
    }

    // Expand directories
    let mut expanded = Vec::new();
    for f in files {
        let p = Path::new(f);
        if p.is_dir() {
            if let Ok(entries) = std::fs::read_dir(p) {
                let mut dir_files: Vec<String> = entries
                    .flatten()
                    .filter_map(|e| {
                        let path = e.path();
                        let ext = path.extension()?.to_str()?.to_lowercase();
                        if ext == "png" || ext == "bmp" || ext == "ico" {
                            Some(path.to_string_lossy().to_string())
                        } else {
                            None
                        }
                    })
                    .collect();
                dir_files.sort();
                expanded.extend(dir_files);
            }
        } else {
            expanded.push(f.clone());
        }
    }

    if expanded.is_empty() {
        eprintln!("错误: 展开目录后没有找到任何图片文件。");
        return 1;
    }

    let output_path = output.map(String::from).unwrap_or_else(|| {
        Path::new(&expanded[0]).parent().unwrap_or(Path::new(".")).join("spritesheet.png").to_string_lossy().to_string()
    });

    println!("── Sprite Sheet 合并 ──");
    println!("  输入: {} 个文件", expanded.len());

    // Load all images
    let mut images: Vec<(String, image::RgbaImage)> = Vec::new();
    for f in &expanded {
        if !Path::new(f).exists() {
            println!("  ⚠ 跳过不存在的文件: {}", f);
            continue;
        }
        match image::open(f) {
            Ok(img) => {
                let name = Path::new(f).file_stem().unwrap_or_default().to_string_lossy().to_string();
                images.push((name, img.to_rgba8()));
            }
            Err(e) => {
                println!("  ⚠ 跳过无法加载的文件 {}: {}", f, e);
            }
        }
    }

    if images.is_empty() {
        eprintln!("错误: 没有有效的图片文件。");
        return 1;
    }

    let cs = cell_size.unwrap_or_else(|| {
        images.iter().map(|(_, img)| img.width().max(img.height())).max().unwrap_or(64)
    });

    let cols = (images.len() as f64).sqrt().ceil() as u32;
    let rows = ((images.len() as f64) / cols as f64).ceil() as u32;
    let sheet_w = cols * cs;
    let sheet_h = rows * cs;

    println!("  单元格: {}x{}", cs, cs);
    println!("  网格: {}x{} ({}x{})", cols, rows, sheet_w, sheet_h);

    let mut sheet = image::RgbaImage::new(sheet_w, sheet_h);
    let mut sprite_entries = Vec::new();

    for (idx, (name, img)) in images.iter().enumerate() {
        let col = idx as u32 % cols;
        let row = idx as u32 / cols;
        let x = col * cs;
        let y = row * cs;

        let draw_x = x + (cs.saturating_sub(img.width())) / 2;
        let draw_y = y + (cs.saturating_sub(img.height())) / 2;

        image::imageops::overlay(&mut sheet, img, draw_x as i64, draw_y as i64);

        sprite_entries.push(format!(
            "    {{ \"name\": \"{}\", \"x\": {}, \"y\": {}, \"width\": {}, \"height\": {}, \"sourceWidth\": {}, \"sourceHeight\": {} }}",
            name, x, y, cs, cs, img.width(), img.height()
        ));

        println!("  [{}] {} ({}x{}) → ({},{})", idx + 1, name, img.width(), img.height(), col, row);
    }

    if let Some(parent) = Path::new(&output_path).parent() {
        let _ = std::fs::create_dir_all(parent);
    }

    if let Err(e) = sheet.save(&output_path) {
        eprintln!("错误: 保存失败: {}", e);
        return 1;
    }

    let file_size = std::fs::metadata(&output_path).map(|m| m.len()).unwrap_or(0);
    println!("\n  输出: {} ({})", output_path, utils::format_file_size(file_size));

    // JSON
    let json_path = json.map(String::from).unwrap_or_else(|| {
        Path::new(&output_path).with_extension("json").to_string_lossy().to_string()
    });
    let json_content = format!(
        "{{\n  \"image\": \"{}\",\n  \"cellSize\": {},\n  \"cols\": {},\n  \"rows\": {},\n  \"sprites\": [\n{}\n  ]\n}}",
        Path::new(&output_path).file_name().unwrap_or_default().to_string_lossy(),
        cs,
        cols,
        rows,
        sprite_entries.join(",\n")
    );
    let _ = std::fs::write(&json_path, &json_content);
    println!("  坐标: {}", json_path);

    // CSS
    let css_path = Path::new(&output_path).with_extension("css").to_string_lossy().to_string();
    let mut css = format!(".sprite {{ display: inline-block; background-image: url('{}'); background-repeat: no-repeat; width: {}px; height: {}px; }}\n",
        Path::new(&output_path).file_name().unwrap_or_default().to_string_lossy(), cs, cs);

    for (idx, (name, _)) in images.iter().enumerate() {
        let col = idx as u32 % cols;
        let row = idx as u32 / cols;
        css.push_str(&format!(".sprite-{} {{ background-position: -{}px -{}px; }}\n", name, col * cs, row * cs));
    }
    let _ = std::fs::write(&css_path, &css);
    println!("  CSS: {}", css_path);

    println!("\n✓ 共合并 {} 个图标。", images.len());
    0
}
