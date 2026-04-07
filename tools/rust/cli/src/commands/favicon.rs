use crate::utils;
use std::path::Path;

pub fn run(file: &str, output: Option<&str>) -> i32 {
    if !Path::new(file).exists() {
        eprintln!("错误: 文件不存在: {}", file);
        return 1;
    }

    let output_dir = output.map(String::from).unwrap_or_else(|| {
        Path::new(file).parent().unwrap_or(Path::new(".")).join("favicon").to_string_lossy().to_string()
    });
    let _ = std::fs::create_dir_all(&output_dir);

    println!("── 生成 Web Favicon 全套 ──");
    println!("  输入: {}", file);
    println!("  输出目录: {}", output_dir);
    println!();

    match image::open(file) {
        Ok(img) => {
            let rgba = img.to_rgba8();
            let square = utils::pad_to_square(&rgba);

            // favicon.ico
            utils::build_and_save_ico(
                &square,
                &[16, 32, 48],
                &Path::new(&output_dir).join("favicon.ico").to_string_lossy(),
            );
            println!("  ✓ favicon.ico (16/32/48)");

            // PNG various sizes
            let png_sizes: &[(u32, &str)] = &[
                (16, "favicon-16x16.png"),
                (32, "favicon-32x32.png"),
                (48, "favicon-48x48.png"),
                (64, "favicon-64x64.png"),
                (96, "favicon-96x96.png"),
                (128, "favicon-128x128.png"),
                (180, "apple-touch-icon.png"),
                (192, "android-chrome-192x192.png"),
                (256, "favicon-256x256.png"),
                (512, "android-chrome-512x512.png"),
            ];

            for &(size, name) in png_sizes {
                let resized = image::imageops::resize(&square, size, size, image::imageops::FilterType::Lanczos3);
                let out_path = Path::new(&output_dir).join(name);
                if let Err(e) = resized.save(&out_path) {
                    eprintln!("  ✗ {} 失败: {}", name, e);
                    continue;
                }
                println!("  ✓ {}", name);
            }

            // site.webmanifest
            let manifest = r##"{
  "name": "",
  "short_name": "",
  "icons": [
    { "src": "/android-chrome-192x192.png", "sizes": "192x192", "type": "image/png" },
    { "src": "/android-chrome-512x512.png", "sizes": "512x512", "type": "image/png" }
  ],
  "theme_color": "#ffffff",
  "background_color": "#ffffff",
  "display": "standalone"
}"##;
            let _ = std::fs::write(Path::new(&output_dir).join("site.webmanifest"), manifest);
            println!("  ✓ site.webmanifest");

            // HTML snippet
            let html = r#"<!-- Favicon -->
<link rel="icon" type="image/x-icon" href="/favicon.ico">
<link rel="icon" type="image/png" sizes="32x32" href="/favicon-32x32.png">
<link rel="icon" type="image/png" sizes="16x16" href="/favicon-16x16.png">
<link rel="apple-touch-icon" sizes="180x180" href="/apple-touch-icon.png">
<link rel="manifest" href="/site.webmanifest">"#;
            let _ = std::fs::write(Path::new(&output_dir).join("_head.html"), html);
            println!("  ✓ _head.html (HTML link 标签片段)");

            println!();
            println!("✓ 共生成 {} 个文件。", png_sizes.len() + 3);
            0
        }
        Err(e) => {
            eprintln!("错误: 处理失败: {}", e);
            1
        }
    }
}
