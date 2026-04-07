use image::{Rgba, RgbaImage};
use std::path::Path;

/// 解析颜色字符串 (white/black/#RRGGBB/#RRGGBBAA/transparent)
pub fn parse_color(value: &str) -> Option<Rgba<u8>> {
    match value.to_lowercase().as_str() {
        "white" => Some(Rgba([255, 255, 255, 255])),
        "black" => Some(Rgba([0, 0, 0, 255])),
        "transparent" => Some(Rgba([0, 0, 0, 0])),
        s if s.starts_with('#') && (s.len() == 7 || s.len() == 9) => {
            let hex = &s[1..];
            let r = u8::from_str_radix(&hex[0..2], 16).ok()?;
            let g = u8::from_str_radix(&hex[2..4], 16).ok()?;
            let b = u8::from_str_radix(&hex[4..6], 16).ok()?;
            let a = if hex.len() == 8 {
                u8::from_str_radix(&hex[6..8], 16).ok()?
            } else {
                255
            };
            Some(Rgba([r, g, b, a]))
        }
        _ => None,
    }
}

/// 格式化文件大小
pub fn format_file_size(bytes: u64) -> String {
    if bytes < 1024 {
        format!("{} B", bytes)
    } else if bytes < 1024 * 1024 {
        format!("{:.1} KB", bytes as f64 / 1024.0)
    } else {
        format!("{:.1} MB", bytes as f64 / (1024.0 * 1024.0))
    }
}

/// 解析尺寸列表字符串
pub fn parse_size_list(s: &str) -> Vec<u32> {
    let sizes: Vec<u32> = s
        .split(',')
        .filter_map(|p| p.trim().parse::<u32>().ok())
        .filter(|&v| v > 0 && v <= 512)
        .collect();
    if sizes.is_empty() {
        vec![16, 32, 48, 256]
    } else {
        sizes
    }
}

/// 补齐为正方形
pub fn pad_to_square(source: &RgbaImage) -> RgbaImage {
    let size = source.width().max(source.height());
    if source.width() == size && source.height() == size {
        return source.clone();
    }

    let mut dest = RgbaImage::new(size, size);
    let offset_x = (size - source.width()) / 2;
    let offset_y = (size - source.height()) / 2;

    image::imageops::overlay(&mut dest, source, offset_x as i64, offset_y as i64);
    dest
}

/// 编码为 PNG 字节
pub fn encode_png(image: &RgbaImage) -> Vec<u8> {
    let mut buf = Vec::new();
    let encoder = image::codecs::png::PngEncoder::new(&mut buf);
    image::ImageEncoder::write_image(
        encoder,
        image.as_raw(),
        image.width(),
        image.height(),
        image::ExtendedColorType::Rgba8,
    )
    .expect("Failed to encode PNG");
    buf
}

/// 构建并保存 ICO 文件
pub fn build_and_save_ico(square: &RgbaImage, sizes: &[u32], out_path: &str) {
    let mut ico_images: Vec<(u32, Vec<u8>)> = Vec::new();

    for &size in sizes {
        let resized = image::imageops::resize(
            square,
            size,
            size,
            image::imageops::FilterType::Lanczos3,
        );
        ico_images.push((size, encode_png(&resized)));
    }

    let temp_path = format!("{}.tmp", out_path);
    write_ico_file(&temp_path, &ico_images);
    let _ = std::fs::rename(&temp_path, out_path);
}

/// 写 ICO 文件
pub fn write_ico_file(path: &str, images: &[(u32, Vec<u8>)]) {
    let mut data = Vec::new();

    // ICONDIR
    data.extend_from_slice(&0u16.to_le_bytes()); // reserved
    data.extend_from_slice(&1u16.to_le_bytes()); // type = ICO
    data.extend_from_slice(&(images.len() as u16).to_le_bytes()); // count

    let mut offset = 6 + images.len() * 16;

    for (size, png) in images {
        let s = if *size >= 256 { 0u8 } else { *size as u8 };
        data.push(s); // width
        data.push(s); // height
        data.push(0); // color count
        data.push(0); // reserved
        data.extend_from_slice(&1u16.to_le_bytes()); // planes
        data.extend_from_slice(&32u16.to_le_bytes()); // bit count
        data.extend_from_slice(&(png.len() as u32).to_le_bytes()); // size
        data.extend_from_slice(&(offset as u32).to_le_bytes()); // offset
        offset += png.len();
    }

    for (_, png) in images {
        data.extend_from_slice(png);
    }

    std::fs::write(path, data).expect("Failed to write ICO file");
}

/// 居中裁剪并缩放到目标正方形尺寸
pub fn center_crop_and_resize(source: &RgbaImage, target_size: u32) -> RgbaImage {
    let w = source.width();
    let h = source.height();
    let side = w.min(h);

    let crop_x = (w - side) / 2;
    let crop_y = (h - side) / 2;

    let cropped = image::imageops::crop_imm(source, crop_x, crop_y, side, side).to_image();
    image::imageops::resize(&cropped, target_size, target_size, image::imageops::FilterType::Lanczos3)
}

/// 从 ICO 数据提取最大的图像并解码为 PNG 字节
pub fn extract_largest_image_from_ico(ico: &[u8]) -> Option<Vec<u8>> {
    if ico.len() < 6 {
        return None;
    }
    let count = u16::from_le_bytes([ico[4], ico[5]]) as usize;
    let mut best_size: u64 = 0;
    let mut best_data: Option<Vec<u8>> = None;

    for i in 0..count {
        let off = 6 + i * 16;
        if off + 16 > ico.len() {
            break;
        }
        let w = if ico[off] == 0 { 256u64 } else { ico[off] as u64 };
        let h = if ico[off + 1] == 0 {
            256u64
        } else {
            ico[off + 1] as u64
        };
        let data_size = u32::from_le_bytes([ico[off + 8], ico[off + 9], ico[off + 10], ico[off + 11]]) as usize;
        let data_offset = u32::from_le_bytes([ico[off + 12], ico[off + 13], ico[off + 14], ico[off + 15]]) as usize;

        if w * h > best_size && data_offset + data_size <= ico.len() {
            best_size = w * h;
            best_data = Some(ico[data_offset..data_offset + data_size].to_vec());
        }
    }

    let data = best_data?;
    // Try to decode and re-encode as PNG
    if let Ok(img) = image::load_from_memory(&data) {
        let rgba = img.to_rgba8();
        Some(encode_png(&rgba))
    } else {
        Some(data)
    }
}

/// 获取默认输出路径
pub fn default_output_path(file_path: &str, suffix: &str, ext: &str) -> String {
    let path = Path::new(file_path);
    let dir = path.parent().unwrap_or(Path::new("."));
    let stem = path.file_stem().unwrap_or_default().to_string_lossy();
    dir.join(format!("{}{}.{}", stem, suffix, ext))
        .to_string_lossy()
        .to_string()
}

/// 透明度分析报告
pub struct TransparencyReport {
    pub total_pixels: usize,
    pub transparent_pixels: usize,
    pub semi_transparent_pixels: usize,
    pub corners_transparent: bool,
    pub near_corners_transparent: bool,
    pub corner_samples: Vec<PixelSample>,
    pub center_samples: Vec<PixelSample>,
}

pub struct PixelSample {
    pub x: u32,
    pub y: u32,
    pub a: u8,
    pub r: u8,
    pub g: u8,
    pub b: u8,
}

/// 分析图像透明度
pub fn analyze_transparency(image: &RgbaImage) -> TransparencyReport {
    let w = image.width();
    let h = image.height();
    let total_pixels = (w * h) as usize;
    let mut transparent = 0usize;
    let mut semi_transparent = 0usize;

    for pixel in image.pixels() {
        if pixel[3] == 0 {
            transparent += 1;
        } else if pixel[3] < 255 {
            semi_transparent += 1;
        }
    }

    let corner_points = [
        (0, 0),
        (w - 1, 0),
        (0, h - 1),
        (w - 1, h - 1),
    ];

    let offset = 20.min(w.min(h) / 10);
    let near_corners = [
        (offset, offset),
        (w - 1 - offset, offset),
        (offset, h - 1 - offset),
        (w - 1 - offset, h - 1 - offset),
    ];

    let center_points = [
        (w / 2, h / 2),
        (w / 4, h / 4),
        (w * 3 / 4, h / 4),
        (w / 4, h * 3 / 4),
        (w * 3 / 4, h * 3 / 4),
    ];

    let sample = |x: u32, y: u32| -> PixelSample {
        let px = image.get_pixel(x, y);
        PixelSample {
            x,
            y,
            a: px[3],
            r: px[0],
            g: px[1],
            b: px[2],
        }
    };

    let mut corner_samples: Vec<PixelSample> = corner_points.iter().map(|&(x, y)| sample(x, y)).collect();
    corner_samples.extend(near_corners.iter().map(|&(x, y)| sample(x, y)));

    let center_samples: Vec<PixelSample> = center_points.iter().map(|&(x, y)| sample(x, y)).collect();

    let corners_transparent = corner_samples[0].a == 0
        && corner_samples[1].a == 0
        && corner_samples[2].a == 0
        && corner_samples[3].a == 0;

    let near_corners_transparent = corner_samples[4].a == 0
        && corner_samples[5].a == 0
        && corner_samples[6].a == 0
        && corner_samples[7].a == 0;

    TransparencyReport {
        total_pixels,
        transparent_pixels: transparent,
        semi_transparent_pixels: semi_transparent,
        corners_transparent,
        near_corners_transparent,
        corner_samples,
        center_samples,
    }
}

/// 打印透明度报告
pub fn print_transparency_report(r: &TransparencyReport, width: u32, height: u32, indent: &str) {
    let trans_ratio = r.transparent_pixels as f64 / r.total_pixels as f64 * 100.0;
    let semi_ratio = r.semi_transparent_pixels as f64 / r.total_pixels as f64 * 100.0;
    let has_any = r.transparent_pixels + r.semi_transparent_pixels > 0;

    println!("{}尺寸: {}x{}", indent, width, height);
    println!("{}总像素: {}", indent, r.total_pixels);
    println!(
        "{}完全透明像素 (A=0): {} ({:.2}%)",
        indent, r.transparent_pixels, trans_ratio
    );
    println!(
        "{}半透明像素 (0<A<255): {} ({:.2}%)",
        indent, r.semi_transparent_pixels, semi_ratio
    );
    println!(
        "{}包含透明通道: {}",
        indent,
        if has_any { "是" } else { "否" }
    );
    println!();

    println!("{}■ 角点检测:", indent);
    let labels = [
        "左上", "右上", "左下", "右下", "左上(内)", "右上(内)", "左下(内)", "右下(内)",
    ];
    for (i, s) in r.corner_samples.iter().enumerate() {
        let status = if s.a == 0 {
            "透明".to_string()
        } else if s.a == 255 {
            "不透明".to_string()
        } else {
            format!("半透明(A={})", s.a)
        };
        println!(
            "{}  {:<8} ({:>4},{:>4}) A={:>3} RGB=({},{},{}) → {}",
            indent, labels[i], s.x, s.y, s.a, s.r, s.g, s.b, status
        );
    }
    println!();

    println!("{}■ 主体区域采样:", indent);
    for s in &r.center_samples {
        let status = if s.a == 255 {
            "不透明"
        } else if s.a == 0 {
            "透明"
        } else {
            "半透明"
        };
        println!(
            "{}  ({:>4},{:>4}) A={:>3} RGB=({},{},{}) → {}",
            indent, s.x, s.y, s.a, s.r, s.g, s.b, status
        );
    }
    println!();

    println!("{}■ 判定结果:", indent);
    if !has_any {
        println!("{}  ✗ 不透明图片 — 没有任何透明像素。", indent);
    } else if r.corners_transparent && r.near_corners_transparent {
        let center_opaque = r.center_samples.iter().all(|s| s.a == 255);
        if center_opaque {
            println!("{}  ✓ 圆角透明图标 — 四角透明且主体不透明。", indent);
        } else {
            println!("{}  △ 含透明通道 — 四角透明，但主体区域存在透明/半透明像素。", indent);
        }
    } else {
        println!(
            "{}  △ 含透明通道 — 但四角不全是透明 (可能不是圆角图标)。",
            indent
        );
    }
}

/// 颜色匹配
pub fn is_color_match(pixel: &Rgba<u8>, target: &Rgba<u8>, threshold: u8) -> bool {
    (pixel[0] as i16 - target[0] as i16).unsigned_abs() <= threshold as u16
        && (pixel[1] as i16 - target[1] as i16).unsigned_abs() <= threshold as u16
        && (pixel[2] as i16 - target[2] as i16).unsigned_abs() <= threshold as u16
}

/// 颜色距离
pub fn color_distance(a: &Rgba<u8>, b: &Rgba<u8>) -> f64 {
    let dr = a[0] as f64 - b[0] as f64;
    let dg = a[1] as f64 - b[1] as f64;
    let db = a[2] as f64 - b[2] as f64;
    (dr * dr + dg * dg + db * db).sqrt()
}

/// 检查背景候选
pub fn is_background_candidate(pixel: &Rgba<u8>, target: &Rgba<u8>, threshold: u8) -> bool {
    if pixel[3] <= 8 {
        return true;
    }
    is_color_match(pixel, target, threshold)
}

/// 四向边缘扫描 flood fill 透明化
pub fn flood_fill_transparent(image: &mut RgbaImage, bg_color: &Rgba<u8>, threshold: u8) -> usize {
    let w = image.width() as usize;
    let h = image.height() as usize;
    let mut vertical_bg = vec![false; w * h];
    let mut horizontal_bg = vec![false; w * h];

    // Top to bottom
    for x in 0..w {
        for y in 0..h {
            if is_background_candidate(image.get_pixel(x as u32, y as u32), bg_color, threshold) {
                vertical_bg[y * w + x] = true;
            } else {
                break;
            }
        }
    }

    // Bottom to top
    for x in 0..w {
        for y in (0..h).rev() {
            if is_background_candidate(image.get_pixel(x as u32, y as u32), bg_color, threshold) {
                vertical_bg[y * w + x] = true;
            } else {
                break;
            }
        }
    }

    // Left to right
    for y in 0..h {
        for x in 0..w {
            if is_background_candidate(image.get_pixel(x as u32, y as u32), bg_color, threshold) {
                horizontal_bg[y * w + x] = true;
            } else {
                break;
            }
        }
    }

    // Right to left
    for y in 0..h {
        for x in (0..w).rev() {
            if is_background_candidate(image.get_pixel(x as u32, y as u32), bg_color, threshold) {
                horizontal_bg[y * w + x] = true;
            } else {
                break;
            }
        }
    }

    // Intersection
    let mut is_background = vec![false; w * h];
    let mut removed_count = 0;
    for y in 0..h {
        for x in 0..w {
            let idx = y * w + x;
            if vertical_bg[idx] && horizontal_bg[idx] {
                is_background[idx] = true;
                image.put_pixel(x as u32, y as u32, Rgba([0, 0, 0, 0]));
                removed_count += 1;
            }
        }
    }

    // Anti-alias border
    for y in 0..h {
        for x in 0..w {
            let idx = y * w + x;
            if is_background[idx] {
                continue;
            }
            let pixel = *image.get_pixel(x as u32, y as u32);
            if pixel[3] == 0 {
                continue;
            }

            let mut adjacent = false;
            for dy in -1i32..=1 {
                for dx in -1i32..=1 {
                    if dx == 0 && dy == 0 {
                        continue;
                    }
                    let nx = x as i32 + dx;
                    let ny = y as i32 + dy;
                    if nx >= 0 && nx < w as i32 && ny >= 0 && ny < h as i32 {
                        if is_background[ny as usize * w + nx as usize] {
                            adjacent = true;
                        }
                    }
                }
            }

            if adjacent {
                let distance = color_distance(&pixel, bg_color);
                if distance < threshold as f64 * 3.0 {
                    let alpha = (255.0 * distance / (threshold as f64 * 3.0))
                        .clamp(0.0, 255.0) as u8;
                    image.put_pixel(x as u32, y as u32, Rgba([pixel[0], pixel[1], pixel[2], alpha]));
                    if alpha == 0 {
                        removed_count += 1;
                    }
                }
            }
        }
    }

    removed_count
}

/// 自动 flood fill (尝试多个阈值)
pub fn auto_flood_fill_transparent(
    image: &mut RgbaImage,
    bg_color: &Rgba<u8>,
) -> (usize, u8) {
    let thresholds = [3u8, 6, 10, 14];
    let total_pixels = (image.width() * image.height()) as usize;

    let mut best_removed = 0;
    let mut best_threshold = thresholds[0];
    let mut best_result: Option<RgbaImage> = None;

    for &t in &thresholds {
        let mut candidate = image.clone();
        let removed = flood_fill_transparent(&mut candidate, bg_color, t);
        if removed == 0 {
            continue;
        }

        let ratio = removed as f64 / total_pixels as f64;
        if ratio > 0.55 {
            continue;
        }

        if removed > best_removed {
            best_removed = removed;
            best_threshold = t;
            best_result = Some(candidate);
        }
    }

    if let Some(result) = best_result {
        *image = result;
    }

    (best_removed, best_threshold)
}

/// 检测边框颜色
pub fn detect_border_color(image: &RgbaImage) -> Option<(Rgba<u8>, String)> {
    let w = image.width() as usize;
    let h = image.height() as usize;

    let region_size = 4.max(w.min(h) * 5 / 100);
    let step = 1.max(region_size / 10);

    let mut corner_pixels = Vec::new();

    // Four corners
    let regions = [
        (0..region_size, 0..region_size),
        (w - region_size..w, 0..region_size),
        (0..region_size, h - region_size..h),
        (w - region_size..w, h - region_size..h),
    ];

    for (x_range, y_range) in &regions {
        let mut y = y_range.start;
        while y < y_range.end {
            let mut x = x_range.start;
            while x < x_range.end {
                corner_pixels.push(*image.get_pixel(x as u32, y as u32));
                x += step;
            }
            y += step;
        }
    }

    if corner_pixels.is_empty() {
        return None;
    }

    let tolerance = 30i16;
    let mut white_count = 0;
    let mut black_count = 0;

    for px in &corner_pixels {
        if px[3] < 128 {
            continue;
        }
        if px[0] as i16 >= 255 - tolerance
            && px[1] as i16 >= 255 - tolerance
            && px[2] as i16 >= 255 - tolerance
        {
            white_count += 1;
        } else if (px[0] as i16) <= tolerance
            && (px[1] as i16) <= tolerance
            && (px[2] as i16) <= tolerance
        {
            black_count += 1;
        }
    }

    let total = corner_pixels.len() as f64;
    if white_count as f64 / total >= 0.80 {
        Some((Rgba([255, 255, 255, 255]), "white".to_string()))
    } else if black_count as f64 / total >= 0.80 {
        Some((Rgba([0, 0, 0, 255]), "black".to_string()))
    } else {
        None
    }
}
