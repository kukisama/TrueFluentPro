#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use eframe::egui;
use icon_core::{ico_helper, pe_icon_extractor};
use std::collections::{HashMap, HashSet};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::{mpsc, Arc};
use std::time::Instant;

static TEXTURE_COUNTER: AtomicUsize = AtomicUsize::new(0);
fn next_texture_id(prefix: &str) -> String {
    let id = TEXTURE_COUNTER.fetch_add(1, Ordering::Relaxed);
    format!("{}_{}", prefix, id)
}

/// 卡片固定尺寸常量
const CARD_WIDTH: f32 = 152.0;
const CARD_INNER_WIDTH: f32 = 136.0;
const CARD_INNER_HEIGHT: f32 = 140.0;
const ICON_SIZE: f32 = 56.0;
const TEXT_MAX_WIDTH: f32 = 130.0;

const TREE_PANEL_WIDTH: f32 = 260.0;

fn main() -> eframe::Result<()> {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let initial_path = args.iter().find(|a| *a != "--elevated").cloned();
    let is_elevated_flag = args.iter().any(|a| a == "--elevated");

    let options = eframe::NativeOptions {
        viewport: egui::ViewportBuilder::default()
            .with_inner_size([1100.0, 700.0])
            .with_min_inner_size([800.0, 500.0])
            .with_title("IconTool UI — 目录图标管理"),
        ..Default::default()
    };

    eframe::run_native(
        "IconTool UI",
        options,
        Box::new(move |cc| {
            cc.egui_ctx.set_visuals(egui::Visuals::light());
            configure_chinese_fonts(&cc.egui_ctx);
            Ok(Box::new(IconToolApp::new(
                initial_path,
                is_elevated_flag,
                cc.egui_ctx.clone(),
            )))
        }),
    )
}

/// 加载系统中文字体
fn configure_chinese_fonts(ctx: &egui::Context) {
    let mut fonts = egui::FontDefinitions::default();
    let font_paths = [
        "C:\\Windows\\Fonts\\msyh.ttc",
        "C:\\Windows\\Fonts\\msyhbd.ttc",
        "C:\\Windows\\Fonts\\simsun.ttc",
    ];

    for font_path in &font_paths {
        if let Ok(font_data) = std::fs::read(font_path) {
            fonts.font_data.insert(
                "chinese_font".to_owned(),
                egui::FontData::from_owned(font_data).into(),
            );
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
            break;
        }
    }

    ctx.set_fonts(fonts);
}

// ─── 目录树 ───

struct TreeNode {
    path: PathBuf,
    name: String,
    expanded: bool,
    children: Option<Vec<TreeNode>>,
}

impl TreeNode {
    fn new(path: PathBuf, name: String) -> Self {
        Self {
            path,
            name,
            expanded: false,
            children: None,
        }
    }

    fn ensure_children_loaded(&mut self) {
        if self.children.is_some() {
            return;
        }
        let mut children = Vec::new();
        if let Ok(entries) = std::fs::read_dir(&self.path) {
            for entry in entries.flatten() {
                let path = entry.path();
                if path.is_dir() {
                    let name = path
                        .file_name()
                        .unwrap_or_default()
                        .to_string_lossy()
                        .to_string();
                    if name.starts_with('.') || name.starts_with('$') {
                        continue;
                    }
                    children.push(TreeNode::new(path, name));
                }
            }
        }
        children.sort_by(|a, b| a.name.to_lowercase().cmp(&b.name.to_lowercase()));
        self.children = Some(children);
    }
}

fn get_drive_roots() -> Vec<TreeNode> {
    let mut drives = Vec::new();
    #[cfg(windows)]
    {
        for letter in b'A'..=b'Z' {
            let drive_str = format!("{}:\\", letter as char);
            let path = PathBuf::from(&drive_str);
            if path.exists() {
                let name = format!("{}:", letter as char);
                drives.push(TreeNode::new(path, name));
            }
        }
    }
    #[cfg(not(windows))]
    {
        drives.push(TreeNode::new(PathBuf::from("/"), "/".to_string()));
    }
    drives
}

// ─── Shell 图标后台提取（纯 GDI，不碰 GL 上下文）───

struct ShellIconData {
    rgba: Vec<u8>,
    width: usize,
    height: usize,
}

#[cfg(windows)]
fn extract_shell_icon_data(path: &Path) -> Option<ShellIconData> {
    use std::ffi::OsStr;
    use std::os::windows::ffi::OsStrExt;
    use windows::Win32::Foundation::HWND;
    use windows::Win32::Graphics::Gdi::*;
    use windows::Win32::UI::Shell::*;
    use windows::Win32::UI::WindowsAndMessaging::*;
    use windows::core::PCWSTR;

    let path_wide: Vec<u16> = OsStr::new(path)
        .encode_wide()
        .chain(std::iter::once(0))
        .collect();

    unsafe {
        let mut shfi = SHFILEINFOW::default();
        let cb = std::mem::size_of::<SHFILEINFOW>() as u32;
        let result = SHGetFileInfoW(
            PCWSTR(path_wide.as_ptr()),
            windows::Win32::Storage::FileSystem::FILE_ATTRIBUTE_DIRECTORY,
            Some(&mut shfi),
            cb,
            SHGFI_ICON | SHGFI_SMALLICON,
        );
        if result == 0 || shfi.hIcon.is_invalid() {
            return None;
        }

        let hicon = shfi.hIcon;
        let size: i32 = 16;

        let hdc_screen = GetDC(HWND::default());
        let hdc_mem = CreateCompatibleDC(hdc_screen);

        let bmi = BITMAPINFO {
            bmiHeader: BITMAPINFOHEADER {
                biSize: std::mem::size_of::<BITMAPINFOHEADER>() as u32,
                biWidth: size,
                biHeight: -size,
                biPlanes: 1,
                biBitCount: 32,
                biCompression: 0,
                ..Default::default()
            },
            ..Default::default()
        };

        let mut bits: *mut std::ffi::c_void = std::ptr::null_mut();
        let hbmp = CreateDIBSection(hdc_mem, &bmi, DIB_RGB_COLORS, &mut bits, None, 0);
        let hbmp = match hbmp {
            Ok(b) => b,
            Err(_) => {
                let _ = DeleteDC(hdc_mem);
                ReleaseDC(HWND::default(), hdc_screen);
                let _ = DestroyIcon(hicon);
                return None;
            }
        };

        let old_obj = SelectObject(hdc_mem, HGDIOBJ(hbmp.0));

        let pixel_count = (size * size) as usize;
        let bg_ptr = bits as *mut u8;
        for i in 0..pixel_count {
            *bg_ptr.add(i * 4) = 255;
            *bg_ptr.add(i * 4 + 1) = 255;
            *bg_ptr.add(i * 4 + 2) = 255;
            *bg_ptr.add(i * 4 + 3) = 0;
        }

        let _ = DrawIconEx(hdc_mem, 0, 0, hicon, size, size, 0, None, DI_NORMAL);

        let slice = std::slice::from_raw_parts(bits as *const u8, pixel_count * 4);
        let mut rgba = Vec::with_capacity(pixel_count * 4);
        let mut any_non_white = false;
        let mut any_alpha = false;
        for i in 0..pixel_count {
            let b = slice[i * 4];
            let g = slice[i * 4 + 1];
            let r = slice[i * 4 + 2];
            let a = slice[i * 4 + 3];
            if a > 0 {
                any_alpha = true;
            }
            if r != 255 || g != 255 || b != 255 {
                any_non_white = true;
            }
            rgba.push(r);
            rgba.push(g);
            rgba.push(b);
            rgba.push(a);
        }

        SelectObject(hdc_mem, old_obj);
        let _ = DeleteObject(HGDIOBJ(hbmp.0));
        let _ = DeleteDC(hdc_mem);
        ReleaseDC(HWND::default(), hdc_screen);
        let _ = DestroyIcon(hicon);

        if !any_non_white && !any_alpha {
            return None;
        }

        // 将透明像素预合成到白色底色上，避免渲染时露出面板背景
        for i in 0..pixel_count {
            let a = rgba[i * 4 + 3] as f32 / 255.0;
            if a < 1.0 {
                rgba[i * 4]     = (rgba[i * 4]     as f32 * a + 255.0 * (1.0 - a)) as u8;
                rgba[i * 4 + 1] = (rgba[i * 4 + 1] as f32 * a + 255.0 * (1.0 - a)) as u8;
                rgba[i * 4 + 2] = (rgba[i * 4 + 2] as f32 * a + 255.0 * (1.0 - a)) as u8;
                rgba[i * 4 + 3] = 255;
            }
        }
        Some(ShellIconData {
            rgba,
            width: size as usize,
            height: size as usize,
        })
    }
}

#[cfg(not(windows))]
fn extract_shell_icon_data(_path: &Path) -> Option<ShellIconData> {
    None
}

/// 后台线程：接收路径请求，提取 Shell 图标像素数据，发回主线程
fn spawn_shell_icon_worker(
    request_rx: mpsc::Receiver<PathBuf>,
    result_tx: mpsc::Sender<(PathBuf, Option<ShellIconData>)>,
    repaint_ctx: egui::Context,
) {
    std::thread::spawn(move || {
        // SHGetFileInfoW 要求在调用前初始化 COM
        #[cfg(windows)]
        unsafe {
            use windows::Win32::System::Com::*;
            let _ = CoInitializeEx(None, COINIT_APARTMENTTHREADED);
        }

        while let Ok(path) = request_rx.recv() {
            let data = extract_shell_icon_data(&path);
            let _ = result_tx.send((path, data));
            repaint_ctx.request_repaint();
        }

        #[cfg(windows)]
        unsafe {
            windows::Win32::System::Com::CoUninitialize();
        }
    });
}

/// 渲染目录树节点，返回用户新选中的路径
fn render_tree_node(
    ui: &mut egui::Ui,
    node: &mut TreeNode,
    depth: usize,
    selected_path: &str,
    icon_cache: &HashMap<PathBuf, Option<egui::TextureHandle>>,
    icon_pending: &mut HashSet<PathBuf>,
    icon_request_tx: &mpsc::Sender<PathBuf>,
) -> Option<String> {
    let mut new_selection: Option<String> = None;

    let indent = depth as f32 * 18.0;
    let node_path_str = node.path.to_string_lossy().to_string();
    let is_selected = node_path_str == selected_path;

    ui.horizontal(|ui| {
        ui.add_space(indent);

        let arrow_text = if node.expanded { "▼" } else { "▶" };
        let arrow_btn = ui.add(
            egui::Button::new(egui::RichText::new(arrow_text).size(10.0))
                .frame(false)
                .min_size(egui::vec2(16.0, 16.0)),
        );
        if arrow_btn.clicked() {
            node.expanded = !node.expanded;
            if node.expanded {
                node.ensure_children_loaded();
            }
        }

        // 从缓存中查找图标（主线程已创建好纹理，这里只做引用）
        if let Some(maybe_tex) = icon_cache.get(&node.path) {
            match maybe_tex {
                Some(tex) => {
                    ui.image(egui::load::SizedTexture::new(tex.id(), [16.0, 16.0]));
                }
                None => {
                    ui.label("📁");
                }
            }
        } else if !icon_pending.contains(&node.path) {
            // 未请求过 → 发送到后台线程
            let _ = icon_request_tx.send(node.path.clone());
            icon_pending.insert(node.path.clone());
            ui.label("📁");
        } else {
            // 正在加载中
            ui.label("📁");
        }

        let label_text = if is_selected {
            egui::RichText::new(&node.name)
                .strong()
                .color(egui::Color32::from_rgb(0, 120, 212))
        } else {
            egui::RichText::new(&node.name)
        };

        let label_resp = ui.selectable_label(is_selected, label_text);
        if label_resp.clicked() {
            new_selection = Some(node_path_str.clone());
            if !node.expanded {
                node.expanded = true;
                node.ensure_children_loaded();
            }
        }
        if label_resp.double_clicked() {
            node.expanded = !node.expanded;
            if node.expanded {
                node.ensure_children_loaded();
            }
            new_selection = Some(node_path_str.clone());
        }
    });

    if node.expanded {
        if let Some(children) = &mut node.children {
            for child in children.iter_mut() {
                if let Some(sel) = render_tree_node(
                    ui,
                    child,
                    depth + 1,
                    selected_path,
                    icon_cache,
                    icon_pending,
                    icon_request_tx,
                ) {
                    new_selection = Some(sel);
                }
            }
        }
    }

    new_selection
}

// ─── 图标数据 ───

struct IconItem {
    exe_file_name: String,
    group_id: i32,
    size_description: String,
    max_size: u32,
    ico_data: Vec<u8>,
    texture: Option<egui::TextureHandle>,
}

// ─── 后台增量扫描 ───

fn scan_directory_incremental(
    dir: &Path,
    base: &Path,
    sender: &mpsc::Sender<IconItem>,
    cancel: &AtomicBool,
    ctx: &egui::Context,
) {
    if cancel.load(Ordering::Relaxed) {
        return;
    }
    let Ok(entries) = std::fs::read_dir(dir) else {
        return;
    };

    for entry in entries.flatten() {
        if cancel.load(Ordering::Relaxed) {
            return;
        }

        let path = entry.path();
        if path.is_dir() {
            scan_directory_incremental(&path, base, sender, cancel, ctx);
        } else if path
            .extension()
            .map_or(false, |e| e.to_ascii_lowercase() == "exe")
        {
            if let Ok(groups) = pe_icon_extractor::extract_from_file(&path) {
                let relative = path
                    .strip_prefix(base)
                    .map(|p| p.to_string_lossy().to_string())
                    .unwrap_or_else(|_| {
                        path.file_name()
                            .unwrap_or_default()
                            .to_string_lossy()
                            .to_string()
                    });

                for group in groups {
                    if cancel.load(Ordering::Relaxed) {
                        return;
                    }
                    let item = IconItem {
                        exe_file_name: relative.clone(),
                        group_id: group.group_id,
                        size_description: ico_helper::describe_sizes(&group.ico_data),
                        max_size: ico_helper::get_max_size(&group.ico_data),
                        ico_data: group.ico_data,
                        texture: None,
                    };
                    if sender.send(item).is_err() {
                        return;
                    }
                    ctx.request_repaint();
                }
            }
        }
    }
}

// ─── App ───

struct Toast {
    message: String,
    is_success: bool,
    created_at: Instant,
}

struct IconToolApp {
    tree_roots: Vec<TreeNode>,
    path: String,
    preview_mode: bool,
    icons: Vec<IconItem>,
    selected_index: Option<usize>,
    icon_receiver: Option<mpsc::Receiver<IconItem>>,
    cancel_flag: Arc<AtomicBool>,
    loading: bool,
    status: String,
    is_elevated: bool,
    toast: Option<Toast>,

    // Shell 图标缓存（后台线程提取，主线程创建纹理）
    shell_icon_cache: HashMap<PathBuf, Option<egui::TextureHandle>>,
    shell_icon_pending: HashSet<PathBuf>,
    shell_icon_request_tx: mpsc::Sender<PathBuf>,
    shell_icon_result_rx: mpsc::Receiver<(PathBuf, Option<ShellIconData>)>,
}

impl IconToolApp {
    fn new(initial_path: Option<String>, is_elevated: bool, repaint_ctx: egui::Context) -> Self {
        let path = initial_path.as_deref().unwrap_or_default().to_string();
        let tree_roots = get_drive_roots();

        // 创建 Shell 图标后台 worker
        let (req_tx, req_rx) = mpsc::channel();
        let (res_tx, res_rx) = mpsc::channel();
        spawn_shell_icon_worker(req_rx, res_tx, repaint_ctx);

        Self {
            tree_roots,
            path,
            preview_mode: false,
            icons: Vec::new(),
            selected_index: None,
            icon_receiver: None,
            cancel_flag: Arc::new(AtomicBool::new(false)),
            loading: false,
            status: if is_elevated {
                "就绪（管理员模式）".to_string()
            } else {
                "就绪".to_string()
            },
            is_elevated,
            toast: None,
            shell_icon_cache: HashMap::new(),
            shell_icon_pending: HashSet::new(),
            shell_icon_request_tx: req_tx,
            shell_icon_result_rx: res_rx,
        }
    }

    /// 主线程：接收后台线程的 RGBA 数据，创建 egui 纹理
    fn drain_shell_icon_results(&mut self, ctx: &egui::Context) {
        while let Ok((path, data)) = self.shell_icon_result_rx.try_recv() {
            self.shell_icon_pending.remove(&path);
            let texture = data.and_then(|d| {
                let pixels: Vec<egui::Color32> = d
                    .rgba
                    .chunks_exact(4)
                    .map(|c| egui::Color32::from_rgba_unmultiplied(c[0], c[1], c[2], c[3]))
                    .collect();
                let image = egui::ColorImage {
                    size: [d.width, d.height],
                    pixels,
                };
                Some(ctx.load_texture(
                    next_texture_id("shell"),
                    image,
                    egui::TextureOptions::LINEAR,
                ))
            });
            self.shell_icon_cache.insert(path, texture);
        }
    }

    fn start_loading_icons(&mut self, ctx: &egui::Context) {
        self.cancel_flag.store(true, Ordering::SeqCst);

        self.icons.clear();
        self.selected_index = None;

        let dir = PathBuf::from(&self.path);
        if !dir.is_dir() {
            self.status = format!("❌ 目录不存在: {}", self.path);
            self.loading = false;
            return;
        }

        let cancel = Arc::new(AtomicBool::new(false));
        self.cancel_flag = cancel.clone();

        let (tx, rx) = mpsc::channel();
        self.icon_receiver = Some(rx);
        self.loading = true;
        self.status = format!("正在扫描 {} ...", self.path);

        let ctx_clone = ctx.clone();
        let base = dir.clone();

        std::thread::spawn(move || {
            scan_directory_incremental(&base, &base, &tx, &cancel, &ctx_clone);
        });
    }

    fn stop_loading(&mut self) {
        self.cancel_flag.store(true, Ordering::SeqCst);
        self.icon_receiver = None;
        self.loading = false;
    }

    fn drain_icon_receiver(&mut self) {
        if let Some(rx) = &self.icon_receiver {
            let mut batch = 0;
            loop {
                match rx.try_recv() {
                    Ok(item) => {
                        self.icons.push(item);
                        batch += 1;
                        if batch >= 50 {
                            break;
                        }
                    }
                    Err(mpsc::TryRecvError::Empty) => break,
                    Err(mpsc::TryRecvError::Disconnected) => {
                        if self.icons.is_empty() {
                            self.status = "未找到包含图标的 EXE 文件".to_string();
                        } else {
                            self.status = format!("找到 {} 个图标", self.icons.len());
                        }
                        self.loading = false;
                        self.icon_receiver = None;
                        return;
                    }
                }
            }
            if self.loading && !self.icons.is_empty() {
                self.status = format!("正在扫描... 已找到 {} 个图标", self.icons.len());
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
                    let msg = format!("✅ 已设为目录图标 → {}", ico_file_name);
                    self.status = msg.clone();
                    self.toast = Some(Toast {
                        message: msg,
                        is_success: true,
                        created_at: Instant::now(),
                    });
                    // 清除该路径的图标缓存，下一帧自动重新请求
                    let target = PathBuf::from(&self.path);
                    self.shell_icon_cache.remove(&target);
                    self.shell_icon_pending.remove(&target);
                }
                Err(e) => {
                    let err_msg = e.to_string();
                    if is_permission_error(&err_msg) && !self.is_elevated {
                        self.status = "🔒 权限不足，正在请求管理员权限...".to_string();
                        if restart_as_admin(&self.path) {
                            std::process::exit(0);
                        } else {
                            let msg = "❌ 用户取消了管理员权限请求".to_string();
                            self.status = msg.clone();
                            self.toast = Some(Toast {
                                message: msg,
                                is_success: false,
                                created_at: Instant::now(),
                            });
                        }
                    } else {
                        let msg = format!("❌ 设置失败: {}", e);
                        self.status = msg.clone();
                        self.toast = Some(Toast {
                            message: msg,
                            is_success: false,
                            created_at: Instant::now(),
                        });
                    }
                }
            }
        }

        #[cfg(not(windows))]
        {
            let dest = std::path::Path::new(&self.path).join(&ico_file_name);
            match std::fs::write(&dest, &icon.ico_data) {
                Ok(_) => {
                    let msg = format!("✅ 已保存图标 → {}", ico_file_name);
                    self.status = msg.clone();
                    self.toast = Some(Toast {
                        message: msg,
                        is_success: true,
                        created_at: Instant::now(),
                    });
                }
                Err(e) => {
                    let msg = format!("❌ 保存失败: {}", e);
                    self.status = msg.clone();
                    self.toast = Some(Toast {
                        message: msg,
                        is_success: false,
                        created_at: Instant::now(),
                    });
                }
            }
        }
    }

    fn load_texture(ctx: &egui::Context, ico_data: &[u8]) -> Option<egui::TextureHandle> {
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
        Some(ctx.load_texture(
            next_texture_id("ico"),
            color_image,
            egui::TextureOptions::LINEAR,
        ))
    }

    fn on_directory_selected(&mut self, new_path: String, ctx: &egui::Context) {
        self.path = new_path;
        if self.preview_mode {
            self.start_loading_icons(ctx);
        } else {
            self.status = format!("当前目录: {}", self.path);
        }
    }
}

impl eframe::App for IconToolApp {
    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        // 帧首：接收后台 Shell 图标数据，在 GL 渲染前创建纹理（主线程 → 安全）
        self.drain_shell_icon_results(ctx);
        self.drain_icon_receiver();

        // ── 顶部：地址栏 ──
        egui::TopBottomPanel::top("top_panel").show(ctx, |ui| {
            ui.add_space(4.0);
            ui.horizontal(|ui| {
                ui.label("📁");
                let resp = ui.add(
                    egui::TextEdit::singleline(&mut self.path)
                        .desired_width(ui.available_width() - 8.0),
                );
                if resp.lost_focus() && ui.input(|i| i.key_pressed(egui::Key::Enter)) {
                    let ctx_clone = ctx.clone();
                    if self.preview_mode {
                        self.start_loading_icons(&ctx_clone);
                    }
                }
            });
            ui.add_space(2.0);
        });

        // ── 底部：状态栏 + 操作按钮 ──
        egui::TopBottomPanel::bottom("bottom_panel")
            .min_height(36.0)
            .show(ctx, |ui| {
                ui.horizontal(|ui| {
                    ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                        let enabled = self.selected_index.is_some();
                        let btn = egui::Button::new(
                            egui::RichText::new("📌 设为目录图标").size(15.0),
                        )
                        .min_size(egui::vec2(160.0, 30.0));
                        if ui.add_enabled(enabled, btn).clicked() {
                            self.set_as_directory_icon();
                        }
                        ui.with_layout(egui::Layout::left_to_right(egui::Align::Center), |ui| {
                            if self.loading {
                                ui.spinner();
                            }
                            ui.label(&self.status);
                        });
                    });
                });
            });

        // ── 左侧：目录树 ──
        let mut new_selection: Option<String> = None;

        egui::SidePanel::left("tree_panel")
            .default_width(TREE_PANEL_WIDTH)
            .min_width(180.0)
            .max_width(450.0)
            .resizable(true)
            .show(ctx, |ui| {
                ui.horizontal(|ui| {
                    let (btn_text, btn_color) = if self.preview_mode {
                        ("隐藏图标", egui::Color32::from_rgb(140, 140, 140))
                    } else {
                        ("显示图标", egui::Color32::from_rgb(46, 160, 67))
                    };
                    let btn = egui::Button::new(
                        egui::RichText::new(btn_text)
                            .color(egui::Color32::WHITE)
                            .strong(),
                    )
                    .fill(btn_color)
                    .corner_radius(4.0);
                    if ui.add(btn).clicked() {
                        self.preview_mode = !self.preview_mode;
                        if self.preview_mode && !self.path.is_empty() {
                            new_selection = Some(self.path.clone());
                        } else if !self.preview_mode {
                            self.stop_loading();
                            self.icons.clear();
                            self.selected_index = None;
                            self.status = format!("{}", self.path);
                        }
                    }
                });

                ui.separator();

                egui::ScrollArea::both()
                    .id_salt("tree_scroll")
                    .auto_shrink([false, false])
                    .show(ui, |ui| {
                        let selected = self.path.clone();
                        let cache = &self.shell_icon_cache;
                        let pending = &mut self.shell_icon_pending;
                        let tx = &self.shell_icon_request_tx;
                        for root in self.tree_roots.iter_mut() {
                            if let Some(sel) =
                                render_tree_node(ui, root, 0, &selected, cache, pending, tx)
                            {
                                new_selection = Some(sel);
                            }
                        }
                    });
            });

        if let Some(sel) = new_selection {
            let ctx_clone = ctx.clone();
            self.on_directory_selected(sel, &ctx_clone);
        }

        // ── 右侧：图标网格 ──
        let mut clicked_idx: Option<usize> = None;
        let mut double_clicked_idx: Option<usize> = None;

        egui::CentralPanel::default().show(ctx, |ui| {
            if !self.preview_mode {
                ui.centered_and_justified(|ui| {
                    ui.label(
                        egui::RichText::new("点击「显示图标」预览当前目录")
                            .size(15.0)
                            .color(egui::Color32::GRAY),
                    );
                });
                return;
            }

            if self.icons.is_empty() && !self.loading {
                ui.centered_and_justified(|ui| {
                    let hint = if self.path.is_empty() {
                        "选择一个目录"
                    } else {
                        "无可用图标"
                    };
                    ui.label(
                        egui::RichText::new(hint)
                            .size(15.0)
                            .color(egui::Color32::GRAY),
                    );
                });
                return;
            }

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

                                        let display_name =
                                            std::path::Path::new(&icon.exe_file_name)
                                                .file_name()
                                                .unwrap_or_default()
                                                .to_string_lossy();
                                        let full_name =
                                            format!("{} #{}", display_name, icon.group_id);
                                        let name_label = egui::Label::new(
                                            egui::RichText::new(&full_name).strong().size(12.0),
                                        )
                                        .truncate()
                                        .sense(egui::Sense::hover());
                                        let r = ui.add_sized([TEXT_MAX_WIDTH, 16.0], name_label);
                                        r.on_hover_text(&full_name);

                                        let hd = if icon.max_size >= 256 { " ★HD" } else { "" };
                                        let count =
                                            ico_helper::parse_entries(&icon.ico_data).len();
                                        let size_text = format!(
                                            "{} 张 ({}){hd}",
                                            count, icon.size_description,
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

        // ── Toast 浮窗 ──
        let mut clear_toast = false;
        if let Some(toast) = &self.toast {
            let elapsed = toast.created_at.elapsed().as_secs_f32();
            if elapsed > 2.0 {
                clear_toast = true;
            } else {
                let alpha = if elapsed > 1.5 {
                    ((2.0 - elapsed) / 0.5).clamp(0.0, 1.0)
                } else {
                    1.0
                };
                let bg_color = if toast.is_success {
                    egui::Color32::from_rgba_unmultiplied(30, 120, 50, (230.0 * alpha) as u8)
                } else {
                    egui::Color32::from_rgba_unmultiplied(200, 40, 40, (230.0 * alpha) as u8)
                };
                let text_alpha = (255.0 * alpha) as u8;

                egui::Area::new(egui::Id::new("toast_overlay"))
                    .anchor(egui::Align2::LEFT_BOTTOM, [8.0, -44.0])
                    .interactable(false)
                    .show(ctx, |ui| {
                        egui::Frame::default()
                            .fill(bg_color)
                            .corner_radius(6.0)
                            .inner_margin(egui::Margin::symmetric(16, 10))
                            .show(ui, |ui| {
                                ui.label(
                                    egui::RichText::new(&toast.message)
                                        .color(egui::Color32::from_rgba_unmultiplied(
                                            255, 255, 255, text_alpha,
                                        ))
                                        .size(14.0)
                                        .strong(),
                                );
                            });
                    });
                ctx.request_repaint();
            }
        }
        if clear_toast {
            self.toast = None;
        }
    }
}

// ─── 工具函数 ───

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

fn is_permission_error(err_msg: &str) -> bool {
    let lower = err_msg.to_lowercase();
    lower.contains("permission denied")
        || lower.contains("access is denied")
        || lower.contains("拒绝访问")
        || lower.contains("权限")
        || lower.contains("os error 5")
}

#[cfg(windows)]
fn restart_as_admin(current_path: &str) -> bool {
    use std::ffi::OsStr;
    use std::os::windows::ffi::OsStrExt;

    let exe = std::env::current_exe().unwrap_or_default();
    let exe_wide: Vec<u16> = OsStr::new(&exe)
        .encode_wide()
        .chain(std::iter::once(0))
        .collect();

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

        result.0 as usize > 32
    }
}

#[cfg(not(windows))]
fn restart_as_admin(_current_path: &str) -> bool {
    false
}
