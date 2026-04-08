use image::codecs::png::PngEncoder;
use image::{ExtendedColorType, ImageEncoder, RgbaImage};
use sha2::{Digest, Sha256};
use std::path::Path;
use std::process;

fn main() {
    let args: Vec<String> = std::env::args().collect();

    if args.len() < 3 {
        eprintln!("Usage: icon-gen <input.png> <output.ico>");
        process::exit(2);
    }

    let input_png = &args[1];
    let output_ico = &args[2];

    // Ensure output directory exists
    if let Some(parent) = Path::new(output_ico).parent() {
        let _ = std::fs::create_dir_all(parent);
    }

    if !Path::new(input_png).exists() {
        eprintln!("Input PNG not found: {}", input_png);
        process::exit(3);
    }

    let png_bytes = match std::fs::read(input_png) {
        Ok(b) => b,
        Err(e) => {
            eprintln!("Failed to read input: {}", e);
            process::exit(3);
        }
    };

    if png_bytes.len() < 24 {
        eprintln!("Input is too small to be a valid PNG.");
        process::exit(4);
    }

    // Read dimensions from PNG header
    let width = read_be_u32(&png_bytes, 16);
    let height = read_be_u32(&png_bytes, 20);

    let hash = sha256_hex(&png_bytes);
    println!(
        "[IconGen] Input: {} ({}x{}), sha256={}",
        input_png, width, height, hash
    );

    // Load and process
    let src = match image::open(input_png) {
        Ok(img) => img.to_rgba8(),
        Err(e) => {
            eprintln!("Failed to load image: {}", e);
            process::exit(4);
        }
    };

    let square = pad_to_square(&src);

    let sizes = [16u32, 32, 48, 256];
    let mut ico_images: Vec<(u32, Vec<u8>)> = Vec::new();

    for &size in &sizes {
        let resized = image::imageops::resize(
            &square,
            size,
            size,
            image::imageops::FilterType::Nearest,
        );
        ico_images.push((size, encode_png(&resized)));
    }

    // Write ICO
    let temp_output = format!("{}.tmp", output_ico);
    write_ico(&temp_output, &ico_images);

    match std::fs::rename(&temp_output, output_ico) {
        Ok(_) => {}
        Err(e) => {
            eprintln!(
                "[IconGen] Failed to replace output ICO (file may be locked): {}",
                output_ico
            );
            eprintln!("{}", e);
            let _ = std::fs::remove_file(&temp_output);
            process::exit(10);
        }
    }

    let output_size = std::fs::metadata(output_ico)
        .map(|m| m.len())
        .unwrap_or(0);
    println!(
        "[IconGen] Output: {} bytes={}",
        output_ico, output_size
    );
}

fn read_be_u32(bytes: &[u8], offset: usize) -> u32 {
    u32::from_be_bytes([
        bytes[offset],
        bytes[offset + 1],
        bytes[offset + 2],
        bytes[offset + 3],
    ])
}

fn sha256_hex(bytes: &[u8]) -> String {
    let mut hasher = Sha256::new();
    hasher.update(bytes);
    let result = hasher.finalize();
    hex_encode(&result)
}

fn hex_encode(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{:02X}", b)).collect()
}

fn pad_to_square(source: &RgbaImage) -> RgbaImage {
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

fn encode_png(image: &RgbaImage) -> Vec<u8> {
    let mut buf = Vec::new();
    let encoder = PngEncoder::new(&mut buf);
    ImageEncoder::write_image(
        encoder,
        image.as_raw(),
        image.width(),
        image.height(),
        ExtendedColorType::Rgba8,
    )
    .expect("Failed to encode PNG");
    buf
}

fn write_ico(path: &str, images: &[(u32, Vec<u8>)]) {
    let mut data = Vec::new();

    // ICONDIR
    data.extend_from_slice(&0u16.to_le_bytes());
    data.extend_from_slice(&1u16.to_le_bytes());
    data.extend_from_slice(&(images.len() as u16).to_le_bytes());

    let mut offset = 6 + images.len() * 16;
    for (size, png) in images {
        let s = if *size >= 256 { 0u8 } else { *size as u8 };
        data.push(s);
        data.push(s);
        data.push(0);
        data.push(0);
        data.extend_from_slice(&1u16.to_le_bytes());
        data.extend_from_slice(&32u16.to_le_bytes());
        data.extend_from_slice(&(png.len() as u32).to_le_bytes());
        data.extend_from_slice(&(offset as u32).to_le_bytes());
        offset += png.len();
    }

    for (_, png) in images {
        data.extend_from_slice(png);
    }

    std::fs::write(path, data).expect("Failed to write ICO");
}
