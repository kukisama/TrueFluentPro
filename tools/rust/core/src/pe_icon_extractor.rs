use crate::ico_helper;
use crate::models::{ExeIconInfo, IconGroupInfo};
use std::collections::HashMap;
use std::fs;
use std::io;
use std::path::Path;

/// 从 PE 文件路径提取所有图标组。
pub fn extract_from_file<P: AsRef<Path>>(pe_file_path: P) -> io::Result<Vec<IconGroupInfo>> {
    let path = pe_file_path.as_ref();
    if !path.exists() {
        return Err(io::Error::new(
            io::ErrorKind::NotFound,
            format!("文件不存在: {}", path.display()),
        ));
    }

    let ext = path
        .extension()
        .and_then(|e| e.to_str())
        .unwrap_or("")
        .to_lowercase();
    if ext != "exe" && ext != "dll" {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            format!("不支持的文件格式: .{}。仅支持 .exe 和 .dll。", ext),
        ));
    }

    let pe_bytes = fs::read(path)?;
    extract_from_bytes(&pe_bytes)
}

/// 从 PE 文件字节提取所有图标组。每个图标组为一个完整的 ICO 文件。
pub fn extract_from_bytes(pe: &[u8]) -> io::Result<Vec<IconGroupInfo>> {
    let raw_icons = extract_icons_from_pe(pe)?;
    let mut result = Vec::new();

    for (index, ico_data) in raw_icons {
        let entries = ico_helper::parse_entries(&ico_data);
        result.push(IconGroupInfo {
            group_id: index,
            ico_data,
            entries,
        });
    }

    Ok(result)
}

/// 扫描目录中的所有 EXE 文件并提取图标。
pub fn scan_directory<P: AsRef<Path>>(directory_path: P) -> io::Result<Vec<ExeIconInfo>> {
    let dir = directory_path.as_ref();
    if !dir.exists() {
        return Err(io::Error::new(
            io::ErrorKind::NotFound,
            format!("目录不存在: {}", dir.display()),
        ));
    }

    let mut result = Vec::new();

    fn visit_dir(dir: &Path, base: &Path, result: &mut Vec<ExeIconInfo>) {
        if let Ok(entries) = fs::read_dir(dir) {
            for entry in entries.flatten() {
                let path = entry.path();
                if path.is_dir() {
                    visit_dir(&path, base, result);
                } else if let Some(ext) = path.extension() {
                    if ext.to_ascii_lowercase() == "exe" {
                        if let Ok(groups) = extract_from_file(&path) {
                            if !groups.is_empty() {
                                let relative = path
                                    .strip_prefix(base)
                                    .map(|p| p.to_string_lossy().to_string())
                                    .unwrap_or_else(|_| {
                                        path.file_name()
                                            .unwrap_or_default()
                                            .to_string_lossy()
                                            .to_string()
                                    });
                                result.push(ExeIconInfo {
                                    exe_file_path: path.to_string_lossy().to_string(),
                                    exe_file_name: relative,
                                    icon_groups: groups,
                                });
                            }
                        }
                    }
                }
            }
        }
    }

    visit_dir(dir, dir, &mut result);
    Ok(result)
}

// ─── PE 解析核心 ───

fn read_u16_le(data: &[u8], offset: usize) -> u16 {
    u16::from_le_bytes([data[offset], data[offset + 1]])
}

fn read_i32_le(data: &[u8], offset: usize) -> i32 {
    i32::from_le_bytes([
        data[offset],
        data[offset + 1],
        data[offset + 2],
        data[offset + 3],
    ])
}

fn read_u32_le(data: &[u8], offset: usize) -> u32 {
    u32::from_le_bytes([
        data[offset],
        data[offset + 1],
        data[offset + 2],
        data[offset + 3],
    ])
}

fn extract_icons_from_pe(pe: &[u8]) -> io::Result<Vec<(i32, Vec<u8>)>> {
    let mut result = Vec::new();

    // PE 头解析
    if pe.len() < 64 || pe[0] != b'M' || pe[1] != b'Z' {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "不是有效的 PE 文件（缺少 MZ 签名）。",
        ));
    }

    let pe_offset = read_i32_le(pe, 0x3C) as usize;
    if pe_offset + 4 > pe.len() || pe[pe_offset] != b'P' || pe[pe_offset + 1] != b'E' {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "不是有效的 PE 文件（缺少 PE 签名）。",
        ));
    }

    let coff_offset = pe_offset + 4;
    let number_of_sections = read_u16_le(pe, coff_offset + 2) as usize;
    let size_of_optional_header = read_u16_le(pe, coff_offset + 16) as usize;

    let optional_header_offset = coff_offset + 20;
    let magic = read_u16_le(pe, optional_header_offset);
    let is_pe32_plus = magic == 0x20B;

    // 获取资源目录 RVA 和 Size (Data Directory index 2 = Resource)
    let data_directory_offset = optional_header_offset + if is_pe32_plus { 112 } else { 96 };
    let resource_dir_rva = read_i32_le(pe, data_directory_offset + 2 * 8) as usize;
    let resource_dir_size = read_i32_le(pe, data_directory_offset + 2 * 8 + 4) as usize;

    if resource_dir_rva == 0 || resource_dir_size == 0 {
        return Ok(result);
    }

    // 查找 .rsrc Section
    let section_table_offset = optional_header_offset + size_of_optional_header;
    let mut rsrc_file_offset: Option<usize> = None;
    let mut rsrc_virtual_address: usize = 0;

    for i in 0..number_of_sections {
        let sec_offset = section_table_offset + i * 40;
        let sec_va = read_i32_le(pe, sec_offset + 12) as usize;
        let sec_raw_size = read_i32_le(pe, sec_offset + 16) as usize;
        let sec_raw_ptr = read_i32_le(pe, sec_offset + 20) as usize;

        if resource_dir_rva >= sec_va && resource_dir_rva < sec_va + sec_raw_size {
            rsrc_file_offset = Some(sec_raw_ptr + (resource_dir_rva - sec_va));
            rsrc_virtual_address = sec_va;
            break;
        }
    }

    let rsrc_file_offset = match rsrc_file_offset {
        Some(v) => v,
        None => return Ok(result),
    };

    // 解析资源目录树
    let mut icon_entries: HashMap<i32, Vec<u8>> = HashMap::new();
    let mut group_entries: Vec<(i32, Vec<u8>)> = Vec::new();

    let (top_named, top_id) = read_res_dir(pe, rsrc_file_offset);
    let top_entry_count = top_named as usize + top_id as usize;

    for t in 0..top_entry_count {
        let entry_off = rsrc_file_offset + 16 + t * 8;
        if entry_off + 8 > pe.len() {
            break;
        }
        let type_id = read_i32_le(pe, entry_off);
        let offset_or_dir = read_u32_le(pe, entry_off + 4);

        let is_dir = (offset_or_dir & 0x80000000) != 0;
        if !is_dir {
            continue;
        }

        let sub_dir_offset = rsrc_file_offset + (offset_or_dir & 0x7FFFFFFF) as usize;
        let is_icon = (type_id & 0x7FFFFFFF) == 3;
        let is_group_icon = (type_id & 0x7FFFFFFF) == 14;

        if !is_icon && !is_group_icon {
            continue;
        }

        let (l1_named, l1_id) = read_res_dir(pe, sub_dir_offset);
        let l1_count = l1_named as usize + l1_id as usize;

        for n in 0..l1_count {
            let l1_entry_off = sub_dir_offset + 16 + n * 8;
            if l1_entry_off + 8 > pe.len() {
                break;
            }
            let name_id = read_i32_le(pe, l1_entry_off) & 0x7FFFFFFF;
            let l1_offset_or_dir = read_u32_le(pe, l1_entry_off + 4);

            let l1_is_dir = (l1_offset_or_dir & 0x80000000) != 0;
            if !l1_is_dir {
                let data_entry_offset =
                    rsrc_file_offset + (l1_offset_or_dir & 0x7FFFFFFF) as usize;
                if let Some(data) =
                    read_resource_data_entry(pe, data_entry_offset, rsrc_virtual_address)
                {
                    if is_icon {
                        icon_entries.insert(name_id, data);
                    } else if is_group_icon {
                        group_entries.push((name_id, data));
                    }
                }
                continue;
            }

            let l2_dir_offset = rsrc_file_offset + (l1_offset_or_dir & 0x7FFFFFFF) as usize;
            let (l2_named, l2_id) = read_res_dir(pe, l2_dir_offset);
            let l2_count = l2_named as usize + l2_id as usize;
            if l2_count == 0 {
                continue;
            }

            let l2_entry_off = l2_dir_offset + 16;
            if l2_entry_off + 8 > pe.len() {
                continue;
            }
            let l2_offset_or_dir = read_u32_le(pe, l2_entry_off + 4);

            let l2_is_dir = (l2_offset_or_dir & 0x80000000) != 0;
            if l2_is_dir {
                continue;
            }

            let data_entry_offset = rsrc_file_offset + l2_offset_or_dir as usize;
            if let Some(data) =
                read_resource_data_entry(pe, data_entry_offset, rsrc_virtual_address)
            {
                if is_icon {
                    icon_entries.insert(name_id, data);
                } else if is_group_icon {
                    group_entries.push((name_id, data));
                }
            }
        }
    }

    // 将 RT_GROUP_ICON + RT_ICON 组合为标准 ICO
    for (group_id, group_data) in &group_entries {
        if group_data.len() < 6 {
            continue;
        }

        let ico_type = read_u16_le(group_data, 2);
        let count = read_u16_le(group_data, 4) as usize;

        if ico_type != 1 || count == 0 {
            continue;
        }

        let mut entries: Vec<([u8; 12], Vec<u8>)> = Vec::new();

        for i in 0..count {
            let grp_entry_off = 6 + i * 14;
            if grp_entry_off + 14 > group_data.len() {
                break;
            }

            let icon_id = read_u16_le(group_data, grp_entry_off + 12) as i32;

            if let Some(icon_data) = icon_entries.get(&icon_id) {
                let mut header = [0u8; 12];
                header.copy_from_slice(&group_data[grp_entry_off..grp_entry_off + 12]);
                // Fix actual size
                let actual_size = (icon_data.len() as u32).to_le_bytes();
                header[8..12].copy_from_slice(&actual_size);
                entries.push((header, icon_data.clone()));
            }
        }

        if entries.is_empty() {
            continue;
        }

        let mut ico_data = Vec::new();

        // ICONDIR
        ico_data.extend_from_slice(&0u16.to_le_bytes()); // reserved
        ico_data.extend_from_slice(&1u16.to_le_bytes()); // type = ICO
        ico_data.extend_from_slice(&(entries.len() as u16).to_le_bytes()); // count

        let mut data_offset = 6 + entries.len() * 16;

        for (header, data) in &entries {
            ico_data.extend_from_slice(header);
            ico_data.extend_from_slice(&(data_offset as u32).to_le_bytes());
            data_offset += data.len();
        }

        for (_, data) in &entries {
            ico_data.extend_from_slice(data);
        }

        result.push((*group_id, ico_data));
    }

    Ok(result)
}

fn read_res_dir(data: &[u8], offset: usize) -> (u16, u16) {
    if offset + 16 > data.len() {
        return (0, 0);
    }
    let named = read_u16_le(data, offset + 12);
    let id = read_u16_le(data, offset + 14);
    (named, id)
}

fn read_resource_data_entry(
    pe: &[u8],
    entry_file_offset: usize,
    rsrc_virtual_address: usize,
) -> Option<Vec<u8>> {
    if entry_file_offset + 16 > pe.len() {
        return None;
    }

    let data_rva = read_i32_le(pe, entry_file_offset) as usize;
    let data_size = read_i32_le(pe, entry_file_offset + 4) as usize;

    if data_rva == 0 || data_size == 0 {
        return None;
    }

    let data_file_offset = rva_to_file_offset(pe, data_rva, rsrc_virtual_address)?;
    if data_file_offset + data_size > pe.len() {
        return None;
    }

    Some(pe[data_file_offset..data_file_offset + data_size].to_vec())
}

fn rva_to_file_offset(pe: &[u8], rva: usize, _rsrc_va: usize) -> Option<usize> {
    let pe_offset = read_i32_le(pe, 0x3C) as usize;
    let coff_offset = pe_offset + 4;
    let number_of_sections = read_u16_le(pe, coff_offset + 2) as usize;
    let size_of_optional_header = read_u16_le(pe, coff_offset + 16) as usize;
    let section_table_offset = coff_offset + 20 + size_of_optional_header;

    for i in 0..number_of_sections {
        let sec_offset = section_table_offset + i * 40;
        if sec_offset + 40 > pe.len() {
            break;
        }
        let sec_va = read_i32_le(pe, sec_offset + 12) as usize;
        let sec_raw_size = read_i32_le(pe, sec_offset + 16) as usize;
        let sec_raw_ptr = read_i32_le(pe, sec_offset + 20) as usize;
        let sec_virt_size = read_i32_le(pe, sec_offset + 8) as usize;
        let effective_size = sec_raw_size.max(sec_virt_size);

        if rva >= sec_va && rva < sec_va + effective_size {
            return Some(sec_raw_ptr + (rva - sec_va));
        }
    }

    None
}
