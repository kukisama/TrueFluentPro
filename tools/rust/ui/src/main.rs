use eframe::egui;
use icon_core::{ico_helper, pe_icon_extractor};
use std::path::PathBuf;

fn main() -> eframe::Result<()> {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let initial_path = args.first().cloned();

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
        Box::new(move |_cc| Ok(Box::new(IconToolApp::new(initial_path)))),
    )
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
}

impl IconToolApp {
    fn new(initial_path: Option<String>) -> Self {
        let mut app = Self {
            path: initial_path.unwrap_or_default(),
            icons: Vec::new(),
            selected_index: None,
            status: "就绪 — 选择一个目录以浏览其中 EXE 文件的图标".to_string(),
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

        self.path = path.to_string_lossy().to_string();
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
                    self.status = format!("❌ 设置失败: {}", e);
                }
            }
        }

        #[cfg(not(windows))]
        {
            // On non-Windows, just save the ICO file
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
        // Top panel
        egui::TopBottomPanel::top("top_panel").show(ctx, |ui| {
            ui.horizontal(|ui| {
                ui.label("📁 目录");
                let response = ui.text_edit_singleline(&mut self.path);
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

        // Bottom panel
        egui::TopBottomPanel::bottom("bottom_panel").show(ctx, |ui| {
            ui.horizontal(|ui| {
                ui.label(&self.status);
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    let enabled = self.selected_index.is_some();
                    if ui
                        .add_enabled(enabled, egui::Button::new("✓ 设为目录图标"))
                        .clicked()
                    {
                        self.set_as_directory_icon();
                    }
                });
            });
        });

        // Central panel - icon grid
        let mut clicked_idx: Option<usize> = None;
        let mut double_clicked_idx: Option<usize> = None;

        egui::CentralPanel::default().show(ctx, |ui| {
            egui::ScrollArea::vertical().show(ui, |ui| {
                let available_width = ui.available_width();
                let card_width = 152.0;
                let cols = ((available_width / card_width) as usize).max(1);

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
                                    egui::Stroke::new(1.0, egui::Color32::from_rgb(0, 120, 212))
                                } else {
                                    egui::Stroke::new(1.0, egui::Color32::from_gray(210))
                                });

                            let response = frame
                                .show(ui, |ui| {
                                    ui.set_width(136.0);
                                    ui.set_height(114.0);

                                    ui.vertical_centered(|ui| {
                                        // Icon preview
                                        if icon.texture.is_none() {
                                            icon.texture =
                                                Self::load_texture(ctx, &icon.ico_data);
                                        }

                                        if let Some(tex) = &icon.texture {
                                            ui.image(egui::load::SizedTexture::new(
                                                tex.id(),
                                                [56.0, 56.0],
                                            ));
                                        } else {
                                            ui.allocate_space(egui::vec2(56.0, 56.0));
                                        }

                                        // File name
                                        let display_name =
                                            std::path::Path::new(&icon.exe_file_name)
                                                .file_name()
                                                .unwrap_or_default()
                                                .to_string_lossy();
                                        ui.strong(format!("{} #{}", display_name, icon.group_id));

                                        // Size info
                                        let hd = if icon.max_size >= 256 { " ★HD" } else { "" };
                                        ui.colored_label(
                                            egui::Color32::GRAY,
                                            format!("{}{}", icon.size_description, hd),
                                        );

                                        // File size
                                        ui.colored_label(
                                            egui::Color32::from_gray(160),
                                            format_file_size(icon.ico_data.len() as u64),
                                        );
                                    });
                                })
                                .response;

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
