mod commands;
mod utils;

use clap::{Parser, Subcommand};
use std::process;

#[derive(Parser)]
#[command(name = "icon-tool", about = "IconTool — 图标与图像多功能命令行工具")]
struct Cli {
    #[command(subcommand)]
    command: Option<Commands>,
}

#[derive(Subcommand)]
enum Commands {
    /// 检测 PNG/ICO 是否包含透明通道
    Check {
        /// 文件路径
        file: String,
    },
    /// 将图片背景透明化并输出为 PNG
    Transparent {
        /// 文件路径
        file: String,
        /// 输出路径
        #[arg(short, long)]
        output: Option<String>,
        /// 目标颜色 (white/black/#RRGGBB)
        #[arg(short, long, default_value = "white")]
        color: String,
        /// 颜色容差 (0-255)
        #[arg(short, long, default_value = "30")]
        threshold: u8,
        /// 使用连通填充模式
        #[arg(long)]
        flood: bool,
    },
    /// 居中裁剪缩放到指定尺寸
    Crop {
        /// 文件路径
        file: String,
        /// 输出路径
        #[arg(short, long)]
        output: Option<String>,
        /// 目标尺寸
        #[arg(short, long, default_value = "512")]
        size: u32,
    },
    /// 从 EXE/DLL 中提取图标资源
    Extract {
        /// EXE/DLL 路径
        file: String,
        /// 输出目录
        #[arg(short, long)]
        output: Option<String>,
    },
    /// 设置目录的自定义图标
    Seticon {
        /// ICO 文件路径
        ico: String,
        /// 目录路径
        dir: String,
    },
    /// 格式互转 (PNG/ICO/BMP)
    Convert {
        /// 文件路径
        file: String,
        /// 输出路径
        #[arg(short, long)]
        output: Option<String>,
        /// 目标格式 (png/ico/bmp)
        #[arg(short, long)]
        format: Option<String>,
        /// ICO 尺寸列表
        #[arg(short, long, default_value = "16,32,48,256")]
        sizes: String,
    },
    /// 批量生成多尺寸图标
    Resize {
        /// 文件路径
        file: String,
        /// 输出目录
        #[arg(short, long)]
        output: Option<String>,
        /// 尺寸列表
        #[arg(short, long, default_value = "16,20,24,32,40,48,64,128,256")]
        sizes: String,
        /// 格式 (png/bmp)
        #[arg(short, long, default_value = "png")]
        format: String,
    },
    /// 查看图像元信息
    Info {
        /// 文件路径
        file: String,
    },
    /// 加边距/画布扩展
    Pad {
        /// 文件路径
        file: String,
        /// 输出路径
        #[arg(short, long)]
        output: Option<String>,
        /// 绝对像素边距
        #[arg(short, long)]
        padding: Option<u32>,
        /// 百分比边距 (0-50)
        #[arg(long, default_value = "10")]
        percent: u32,
        /// 背景颜色
        #[arg(short, long, default_value = "transparent")]
        color: String,
    },
    /// 圆角/圆形裁剪
    Round {
        /// 文件路径
        file: String,
        /// 输出路径
        #[arg(short, long)]
        output: Option<String>,
        /// 圆角半径
        #[arg(short, long)]
        radius: Option<u32>,
        /// 圆形裁剪
        #[arg(long)]
        circle: bool,
    },
    /// 添加外阴影
    Shadow {
        /// 文件路径
        file: String,
        /// 输出路径
        #[arg(short, long)]
        output: Option<String>,
        /// 模糊半径
        #[arg(short, long, default_value = "20")]
        blur: u32,
        /// 偏移 (x,y)
        #[arg(long, default_value = "4,4")]
        offset: String,
        /// 阴影颜色
        #[arg(short, long, default_value = "black")]
        color: String,
    },
    /// 角标/水印叠加
    Overlay {
        /// 底图路径
        base: String,
        /// 叠加图路径
        overlay_img: String,
        /// 输出路径
        #[arg(short, long)]
        output: Option<String>,
        /// 位置 (tl/tr/bl/br/c)
        #[arg(short, long, default_value = "br")]
        position: String,
        /// 缩放比例 (5-100)
        #[arg(short, long, default_value = "30")]
        scale: u32,
        /// 边距百分比 (0-50)
        #[arg(short, long, default_value = "2")]
        margin: u32,
    },
    /// 多图拼合成 ICO
    Compose {
        /// 图片文件列表
        files: Vec<String>,
        /// 输出路径
        #[arg(short, long)]
        output: Option<String>,
    },
    /// 一键生成 Web 全套 favicon
    Favicon {
        /// 文件路径
        file: String,
        /// 输出目录
        #[arg(short, long)]
        output: Option<String>,
    },
    /// 图标合并为 sprite sheet
    Sheet {
        /// 文件列表或目录
        files: Vec<String>,
        /// 输出路径
        #[arg(short, long)]
        output: Option<String>,
        /// JSON 坐标映射路径
        #[arg(long)]
        json: Option<String>,
        /// 单元格尺寸
        #[arg(short, long)]
        size: Option<u32>,
    },
    /// 浏览目录中 EXE 的图标并设置为目录图标
    Browseicons {
        /// 目录路径
        dir: Option<String>,
    },
}

fn main() {
    let cli = Cli::parse();

    let code = match cli.command {
        None => commands::auto_process::run(),
        Some(cmd) => match cmd {
            Commands::Check { file } => commands::check::run(&file),
            Commands::Transparent {
                file,
                output,
                color,
                threshold,
                flood,
            } => commands::transparent::run(&file, output.as_deref(), &color, threshold, flood),
            Commands::Crop { file, output, size } => {
                commands::crop::run(&file, output.as_deref(), size)
            }
            Commands::Extract { file, output } => {
                commands::extract::run(&file, output.as_deref())
            }
            Commands::Seticon { ico, dir } => commands::seticon::run(&ico, &dir),
            Commands::Convert {
                file,
                output,
                format,
                sizes,
            } => commands::convert::run(&file, output.as_deref(), format.as_deref(), &sizes),
            Commands::Resize {
                file,
                output,
                sizes,
                format,
            } => commands::resize::run(&file, output.as_deref(), &sizes, &format),
            Commands::Info { file } => commands::info::run(&file),
            Commands::Pad {
                file,
                output,
                padding,
                percent,
                color,
            } => commands::pad::run(&file, output.as_deref(), padding, percent, &color),
            Commands::Round {
                file,
                output,
                radius,
                circle,
            } => commands::round::run(&file, output.as_deref(), radius, circle),
            Commands::Shadow {
                file,
                output,
                blur,
                offset,
                color,
            } => commands::shadow::run(&file, output.as_deref(), blur, &offset, &color),
            Commands::Overlay {
                base,
                overlay_img,
                output,
                position,
                scale,
                margin,
            } => commands::overlay::run(
                &base,
                &overlay_img,
                output.as_deref(),
                &position,
                scale,
                margin,
            ),
            Commands::Compose { files, output } => {
                commands::compose::run(&files, output.as_deref())
            }
            Commands::Favicon { file, output } => {
                commands::favicon::run(&file, output.as_deref())
            }
            Commands::Sheet {
                files,
                output,
                json,
                size,
            } => commands::sheet::run(&files, output.as_deref(), json.as_deref(), size),
            Commands::Browseicons { dir } => commands::browseicons::run(dir.as_deref()),
        },
    };

    process::exit(code);
}
