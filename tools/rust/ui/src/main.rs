#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use eframe::egui;
use icon_core::{ico_helper, pe_icon_extractor};
use std::path::PathBuf;

/// 卡片固定尺寸常量
const CARD_WIDTH: f32 = 152.0;
const CARD_INNER_WIDTH: f32 = 136.0;
const CARD_INNER_HEIGHT: f32 = 140.0;
const ICON_SIZE: f32 = 56.0;
const TEXT_MAX_WIDTH: f32 = 130.0;

fn main() -> eframe::Result<()> {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let initial_path = args.first().cloned();

    // 如果带了 --elevated 参数，说明已经提权过了，不再重复提权
    let is_elevated_flag = args.iter().any(|a| a == "--elevated");

    let options = eframe::NativeOptions {
        viewport: egui::ViewportBuilder::default()
            .with_inner_size([920.0, 660.0])
            .with_min_inner_size([640.0, 480.0])
            .with_title("IconTool UI — 目录图标管理"),
        ..Default::default()
    };

    eframe::run_native(
        "IconTool UI",
        options,
        Box::new(move |cc| {
            configure_chinese_fonts(&cc.egui_ctx);
            Ok(Box::new(IconToolApp::new(initial_path, is_elevated_flag)))
        }),
    )
}

/// 加载系统中文字体，确保 UI 中文正常显示
fn configure_chinese_fonts(ctx: &egui::Context) {
    let mut fonts = egui::FontDefinitions::default();

    // 尝试加载 Windows 系统中文字体（微软雅黑）
    let font_paths = [
        "C:\\Windows\\Fonts\\msyh.ttc",
        "C:\\Windows\\Fonts\\msyhbd.ttc",
        "C:\\Windows\\Fonts\\simsun.ttc",
    ];

    let mut loaded = false;
    for font_path in &font_paths {
        if let Ok(font_data) = std::fs::read(font_path) {
            fonts.font_data.insert(
                "chinese_font".to_owned(),
                egui::FontData::from_owned(font_data).into(),
            );

            // 在所有字体族最前面插入中文字体，保证中文优先匹配
            fonts
                .families
                .entry(egui::FontFamily::Proportional)
                .or_default()
                .insert(0, "chinese_font".to_owned());
            fonts
                .families
                .entry(egui::FontFamily::Monospace)
                .or_default()
                .insert(0, "chinese_font".to_owned());

            loaded = true;
            break;
        }
    }

    if !loaded {
        eprintln!("警告: 未找到系统中文字体，中文可能显示异常");
    }

    ctx.set_fonts(fonts);
}

struct IconItem {
    exe_file_name: String,
    group_id: i32,
    size_description: String,
    max_size: u32,
    ico_data: Vec<u8>,
    texture: Option<egui::TextureHandle>,
}

struct IconToolApp {
    path: String,
    icons: Vec<IconItem>,
    selected_index: Option<usize>,
    status: String,
    is_elevated: bool,
}

impl IconToolApp {
    fn new(initial_path: Option<String>, is_elevated: bool) -> Self {
        let path = initial_path.as_deref().unwrap_or_default();
        // 过滤掉 --elevated 标记作为路径
        let path = if path == "--elevated" { "" } else { path };
        let mut app = Self {
            path: path.to_string(),
            icons: Vec::new(),
            selected_index: None,
            status: if is_elevated {
                "就绪（管理员模式）— 选择一个目录以浏览其中 EXE 文件的图标".to_string()
            } else {
                "就绪 — 选择一个目录以浏览其中 EXE 文件的图标".to_string()
            },
            is_elevated,
        };

        if !app.path.is_empty() && std::path::Path::new(&app.path).is_dir() {
            app.load_icons();
        }

        app
    }

    fn load_icons(&mut self) {
        self.icons.clear();
        self.selected_index = None;

        let path = std::fs::canonicalize(&self.path)
            .unwrap_or_else(|_| PathBuf::from(&self.path));

        if !path.is_dir() {
            self.status = format!("❌ 目录不存在: {}", path.display());
            return;
        }

        // 去掉 Windows canonicalize 产生的 \\?\ 前缀
        let path_str = path.to_string_lossy().to_string();
        self.path = path_str.strip_prefix(r"\\?\").unwrap_or(&path_str).to_string();
        self.status = "正在扫描 EXE 文件...".to_string();

        match pe_icon_extractor::scan_directory(&path) {
            Ok(exe_icons) => {
                if exe_icons.is_empty() {
                    self.status = "未找到包含图标的 EXE 文件".to_string();
                    return;
                }

                let mut total = 0;
                for exe in &exe_icons {
                    for group in &exe.icon_groups {
                        self.icons.push(IconItem {
                            exe_file_name: exe.exe_file_name.clone(),
                            group_id: group.group_id,
                            size_description: ico_helper::describe_sizes(&group.ico_data),
                            max_size: ico_helper::get_max_size(&group.ico_data),
                            ico_data: group.ico_data.clone(),
                            texture: None,
                        });
                        total += 1;
                    }
                }

                self.status = format!(
                    "找到 {} 个图标（来自 {} 个 EXE 文件）",
                    total,
                    exe_icons.len()
                );
            }
            Err(e) => {
                self.status = format!("❌ 扫描失败: {}", e);
            }
        }
    }

    fn set_as_directory_icon(&mut self) {
        let Some(idx) = self.selected_index else {
            return;
        };
        if self.path.is_empty() {
            return;
        }

        let icon = &self.icons[idx];
        let stem = std::path::Path::new(&icon.exe_file_name)
            .file_stem()
            .unwrap_or_default()
            .to_string_lossy()
            .to_string();
        let ico_file_name = format!("{}_icon{}.ico", sanitize_filename(&stem), icon.group_id);

        #[cfg(windows)]
        {
            match icon_core::directory_icon_service::set_directory_icon_from_bytes(
                &icon.ico_data,
                &ico_file_name,
                &self.path,
            ) {
                Ok(_) => {
                    self.status = format!("✅ 已将图标设为目录图标 → {}", ico_file_name);
                }
                Err(e) => {
                    let err_msg = e.to_string();
                    if is_permission_error(&err_msg) && !self.is_elevated {
                        self.status = "🔒 权限不足，正在请求管理员权限...".to_string();
                        if restart_as_admin(&self.path) {
                            std::process::exit(0);
                        } else {
                            self.status = "❌ 用户取消了管理员权限请求".to_string();
                        }
                    } else {
                        self.status = format!("❌ 设置失败: {}", e);
                    }
                }
            }
        }

        #[cfg(not(windows))]
        {
            let dest = std::path::Path::new(&self.path).join(&ico_file_name);
            match std::fs::write(&dest, &icon.ico_data) {
                Ok(_) => {
                    self.status = format!("✅ 已保存图标 → {}", ico_file_name);
                }
                Err(e) => {
                    self.status = format!("❌ 保存失败: {}", e);
                }
            }
        }
    }

    fn load_texture(ctx: &egui::Context, ico_data: &[u8]) -> Option<egui::TextureHandle> {
        // Try to extract the largest PNG from the ICO and load as texture
        let image_data = ico_helper::extract_largest_image_data(ico_data)?;

        let img = if ico_helper::is_png(&image_data) {
            image::load_from_memory(&image_data).ok()?
        } else {
            image::load_from_memory(ico_data).ok()?
        };

        let rgba = img.to_rgba8();
        let size = [rgba.width() as usize, rgba.height() as usize];
        let pixels: Vec<egui::Color32> = rgba
            .pixels()
            .map(|p| egui::Color32::from_rgba_unmultiplied(p[0], p[1], p[2], p[3]))
            .collect();

        let color_image = egui::ColorImage { size, pixels };
        Some(ctx.load_texture("icon", color_image, egui::TextureOptions::LINEAR))
    }
}

impl eframe::App for IconToolApp {
    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        // Top panel — 目录输入
        egui::TopBottomPanel::top("top_panel").show(ctx, |ui| {
            ui.horizontal(|ui| {
                ui.label("📁 目录");
                let response = ui.add(
                    egui::TextEdit::singleline(&mut self.path).desired_width(ui.available_width() - 130.0),
                );
                if response.lost_focus() && ui.input(|i| i.key_pressed(egui::Key::Enter)) {
                    self.load_icons();
                }
                if ui.button("浏览").clicked() {
                    if let Some(folder) = rfd::FileDialog::new().pick_folder() {
                        self.path = folder.to_string_lossy().to_string();
                        self.load_icons();
                    }
                }
                if ui.button("加载").clicked() {
                    self.load_icons();
                }
            });
        });

        // Bottom panel — 状态栏 + 操作按钮（分左右布局，互不挤占）
        egui::TopBottomPanel::bottom("bottom_panel").show(ctx, |ui| {
            ui.horizontal(|ui| {
                // 右侧按钮先占位
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    let enabled = self.selected_index.is_some();
                    if ui
                        .add_enabled(enabled, egui::Button::new("📌 设为目录图标"))
                        .clicked()
                    {
                        self.set_as_directory_icon();
                    }
                    // 左侧状态文字（在右到左布局里，剩余空间用 label）
                    ui.with_layout(egui::Layout::left_to_right(egui::Align::Center), |ui| {
                        ui.label(&self.status);
                    });
                });
            });
        });

        // Central panel - icon grid
        let mut clicked_idx: Option<usize> = None;
        let mut double_clicked_idx: Option<usize> = None;

        egui::CentralPanel::default().show(ctx, |ui| {
            egui::ScrollArea::vertical().show(ui, |ui| {
                let available_width = ui.available_width();
                let cols = ((available_width / (CARD_WIDTH + 8.0)) as usize).max(1);

                egui::Grid::new("icon_grid")
                    .num_columns(cols)
                    .spacing([8.0, 8.0])
                    .show(ui, |ui| {
                        for (idx, icon) in self.icons.iter_mut().enumerate() {
                            let is_selected = self.selected_index == Some(idx);

                            let frame = egui::Frame::default()
                                .inner_margin(8.0)
                                .corner_radius(6.0)
                                .fill(if is_selected {
                                    egui::Color32::from_rgb(232, 242, 255)
                                } else {
                                    egui::Color32::WHITE
                                })
                                .stroke(if is_selected {
                                    egui::Stroke::new(2.0, egui::Color32::from_rgb(0, 120, 212))
                                } else {
                                    egui::Stroke::new(1.0, egui::Color32::from_gray(210))
                                });

                            let response = frame
                                .show(ui, |ui| {
                                    ui.set_width(CARD_INNER_WIDTH);
                                    ui.set_height(CARD_INNER_HEIGHT);

                                    ui.vertical_centered(|ui| {
                                        // 图标预览 — 固定尺寸
                                        if icon.texture.is_none() {
                                            icon.texture =
                                                Self::load_texture(ctx, &icon.ico_data);
                                        }

                                        if let Some(tex) = &icon.texture {
                                            ui.image(egui::load::SizedTexture::new(
                                                tex.id(),
                                                [ICON_SIZE, ICON_SIZE],
                                            ));
                                        } else {
                                            ui.allocate_space(egui::vec2(ICON_SIZE, ICON_SIZE));
                                        }

                                        ui.add_space(4.0);

                                        // 文件名 — 截断 + tooltip
                                        let display_name =
                                            std::path::Path::new(&icon.exe_file_name)
                                                .file_name()
                                                .unwrap_or_default()
                                                .to_string_lossy();
                                        let full_name = format!("{} #{}", display_name, icon.group_id);
                                        let name_label = egui::Label::new(
                                            egui::RichText::new(&full_name).strong().size(12.0),
                                        )
                                        .truncate()
                                        .sense(egui::Sense::hover());
                                        let r = ui.add_sized([TEXT_MAX_WIDTH, 16.0], name_label);
                                        r.on_hover_text(&full_name);

                                        // 尺寸信息 — 截断 + tooltip
                                        let hd = if icon.max_size >= 256 { " ★HD" } else { "" };
                                        let count = ico_helper::parse_entries(&icon.ico_data).len();
                                        let size_text = format!(
                                            "{} 张 ({}){hd}",
                                            count,
                                            icon.size_description,
                                        );
                                        let size_label = egui::Label::new(
                                            egui::RichText::new(&size_text)
                                                .color(egui::Color32::GRAY)
                                                .size(11.0),
                                        )
                                        .truncate()
                                        .sense(egui::Sense::hover());
                                        let r = ui.add_sized([TEXT_MAX_WIDTH, 14.0], size_label);
                                        r.on_hover_text(&size_text);

                                        // 文件大小
                                        ui.colored_label(
                                            egui::Color32::from_gray(140),
                                            format_file_size(icon.ico_data.len() as u64),
                                        );
                                    });
                                })
                                .response
                                .interact(egui::Sense::click());

                            if response.clicked() {
                                clicked_idx = Some(idx);
                            }

                            if response.double_clicked() {
                                double_clicked_idx = Some(idx);
                            }

                            if (idx + 1) % cols == 0 {
                                ui.end_row();
                            }
                        }
                    });
            });
        });

        // Handle click/double-click outside the borrow scope
        if let Some(idx) = clicked_idx {
            self.selected_index = Some(idx);
            let icon = &self.icons[idx];
            let display_name = std::path::Path::new(&icon.exe_file_name)
                .file_name()
                .unwrap_or_default()
                .to_string_lossy()
                .to_string();
            let hd = if icon.max_size >= 256 { " ★HD" } else { "" };
            self.status = format!(
                "已选中: {} #{} ({}{})",
                display_name, icon.group_id, icon.size_description, hd
            );
        }

        if let Some(idx) = double_clicked_idx {
            self.selected_index = Some(idx);
            self.set_as_directory_icon();
        }
    }
}

fn format_file_size(bytes: u64) -> String {
    if bytes < 1024 {
        format!("{} B", bytes)
    } else if bytes < 1024 * 1024 {
        format!("{:.1} KB", bytes as f64 / 1024.0)
    } else {
        format!("{:.1} MB", bytes as f64 / (1024.0 * 1024.0))
    }
}

fn sanitize_filename(name: &str) -> String {
    let invalid = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];
    let sanitized: String = name.chars().filter(|c| !invalid.contains(c)).collect();
    if sanitized.trim().is_empty() {
        "icon".to_string()
    } else {
        sanitized
    }
}

/// 判断错误是否为权限不足
fn is_permission_error(err_msg: &str) -> bool {
    let lower = err_msg.to_lowercase();
    lower.contains("permission denied")
        || lower.contains("access is denied")
        || lower.contains("拒绝访问")
        || lower.contains("权限")
        || lower.contains("os error 5")
}

/// 以管理员身份重启当前程序，传入当前目录路径
#[cfg(windows)]
fn restart_as_admin(current_path: &str) -> bool {
    use std::ffi::OsStr;
    use std::os::windows::ffi::OsStrExt;

    let exe = std::env::current_exe().unwrap_or_default();
    let exe_wide: Vec<u16> = OsStr::new(&exe)
        .encode_wide()
        .chain(std::iter::once(0))
        .collect();

    // 构造参数：传入当前路径 + --elevated 标记
    let params = if current_path.is_empty() {
        "--elevated".to_string()
    } else {
        format!("\"{}\" --elevated", current_path)
    };
    let params_wide: Vec<u16> = OsStr::new(&params)
        .encode_wide()
        .chain(std::iter::once(0))
        .collect();

    let verb: Vec<u16> = OsStr::new("runas")
        .encode_wide()
        .chain(std::iter::once(0))
        .collect();

    unsafe {
        use windows::Win32::UI::Shell::ShellExecuteW;
        use windows::core::PCWSTR;

        let result = ShellExecuteW(
            None,
            PCWSTR(verb.as_ptr()),
            PCWSTR(exe_wide.as_ptr()),
            PCWSTR(params_wide.as_ptr()),
            PCWSTR::null(),
            windows::Win32::UI::WindowsAndMessaging::SW_SHOWNORMAL,
        );

        // ShellExecuteW 返回值 > 32 表示成功
        result.0 as usize > 32
    }
}

#[cfg(not(windows))]
fn restart_as_admin(_current_path: &str) -> bool {
    false
}
