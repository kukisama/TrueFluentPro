use crate::models::IconEntryInfo;

/// ICO 文件格式解析与操作工具。

/// 解析 ICO 文件字节，返回所有图像条目的元信息。
pub fn parse_entries(ico_data: &[u8]) -> Vec<IconEntryInfo> {
    let mut entries = Vec::new();
    if ico_data.len() < 6 {
        return entries;
    }

    let count = u16::from_le_bytes([ico_data[4], ico_data[5]]) as usize;
    for i in 0..count {
        let entry_offset = 6 + i * 16;
        if entry_offset + 16 > ico_data.len() {
            break;
        }

        let w = if ico_data[entry_offset] == 0 { 256 } else { ico_data[entry_offset] as u32 };
        let h = if ico_data[entry_offset + 1] == 0 { 256 } else { ico_data[entry_offset + 1] as u32 };
        let bit_count = u16::from_le_bytes([
            ico_data[entry_offset + 6],
            ico_data[entry_offset + 7],
        ]) as u32;
        let data_size = u32::from_le_bytes([
            ico_data[entry_offset + 8],
            ico_data[entry_offset + 9],
            ico_data[entry_offset + 10],
            ico_data[entry_offset + 11],
        ]);

        entries.push(IconEntryInfo {
            width: w,
            height: h,
            bit_count,
            data_size,
        });
    }

    entries
}

/// 获取 ICO 中最大尺寸条目的索引。
pub fn get_largest_entry_index(ico_data: &[u8]) -> Option<usize> {
    let entries = parse_entries(ico_data);
    if entries.is_empty() {
        return None;
    }

    let mut max_index = 0;
    let mut max_pixels: u64 = 0;
    for (i, entry) in entries.iter().enumerate() {
        let pixels = entry.width as u64 * entry.height as u64;
        if pixels > max_pixels {
            max_pixels = pixels;
            max_index = i;
        }
    }
    Some(max_index)
}

/// 描述 ICO 文件中包含的尺寸列表。
pub fn describe_sizes(ico_data: &[u8]) -> String {
    let entries = parse_entries(ico_data);
    if entries.is_empty() {
        return "?".to_string();
    }

    let sizes: Vec<String> = entries
        .iter()
        .map(|e| {
            if e.width == e.height {
                format!("{}", e.width)
            } else {
                format!("{}x{}", e.width, e.height)
            }
        })
        .collect();

    format!("{} 张 ({})", entries.len(), sizes.join("/"))
}

/// 获取 ICO 中最大尺寸。
pub fn get_max_size(ico_data: &[u8]) -> u32 {
    let entries = parse_entries(ico_data);
    entries
        .iter()
        .map(|e| e.width.max(e.height))
        .max()
        .unwrap_or(0)
}

/// 从 ICO 文件字节中提取指定条目的原始图像数据。
/// 返回的数据可能是 PNG（以 0x89504E47 开头）或 BMP DIB 数据。
pub fn extract_entry_image_data(ico_data: &[u8], entry_index: usize) -> Option<Vec<u8>> {
    if ico_data.len() < 6 {
        return None;
    }

    let count = u16::from_le_bytes([ico_data[4], ico_data[5]]) as usize;
    if entry_index >= count {
        return None;
    }

    let dir_entry_offset = 6 + entry_index * 16;
    if dir_entry_offset + 16 > ico_data.len() {
        return None;
    }

    let data_size = u32::from_le_bytes([
        ico_data[dir_entry_offset + 8],
        ico_data[dir_entry_offset + 9],
        ico_data[dir_entry_offset + 10],
        ico_data[dir_entry_offset + 11],
    ]) as usize;
    let data_offset = u32::from_le_bytes([
        ico_data[dir_entry_offset + 12],
        ico_data[dir_entry_offset + 13],
        ico_data[dir_entry_offset + 14],
        ico_data[dir_entry_offset + 15],
    ]) as usize;

    if data_offset + data_size > ico_data.len() || data_size == 0 {
        return None;
    }

    Some(ico_data[data_offset..data_offset + data_size].to_vec())
}

/// 从 ICO 数据中提取最大尺寸的图像数据（PNG 或 BMP DIB）。
pub fn extract_largest_image_data(ico_data: &[u8]) -> Option<Vec<u8>> {
    let idx = get_largest_entry_index(ico_data)?;
    extract_entry_image_data(ico_data, idx)
}

/// 判断字节数据是否为 PNG 格式。
pub fn is_png(data: &[u8]) -> bool {
    data.len() >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47
}
