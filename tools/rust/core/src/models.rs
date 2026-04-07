/// 表示从 PE 文件中提取的一个图标组（对应一个 RT_GROUP_ICON 资源）。
/// ico_data 是完整的 ICO 文件字节，可直接写入文件或加载为图像。
#[derive(Debug, Clone)]
pub struct IconGroupInfo {
    pub group_id: i32,
    pub ico_data: Vec<u8>,
    pub entries: Vec<IconEntryInfo>,
}

/// ICO 文件中单个图像条目的元信息。
#[derive(Debug, Clone)]
pub struct IconEntryInfo {
    pub width: u32,
    pub height: u32,
    pub bit_count: u32,
    pub data_size: u32,
}

/// 扫描目录时，描述一个 EXE 文件及其包含的所有图标组。
#[derive(Debug, Clone)]
pub struct ExeIconInfo {
    pub exe_file_path: String,
    pub exe_file_name: String,
    pub icon_groups: Vec<IconGroupInfo>,
}
