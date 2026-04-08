# tools/

TrueFluentPro 主项目的辅助工具集。全部独立于主项目编译（`TrueFluentPro.csproj` 已排除 `tools/**`）。

---

## 目录总览

| 目录 / 文件 | 语言 | 类型 | 用途 |
|---|---|---|---|
| **IconTool/** | C# (.NET 10) | CLI | 多功能图标 & 图像处理命令行工具（16+ 子命令） |
| **IconTool.Core/** | C# (.NET 10) | 类库 | PE 图标提取 / ICO 解析 / 目录图标设置的共享核心 |
| **IconToolUI/** | C# (.NET 10) + WinForms | 桌面应用 | 可视化文件夹图标管理（扫描 EXE → 选图标 → 一键设置） |
| **IconGen/** | C# (.NET 10) | CLI | PNG → ICO 转换器，可集成到 MSBuild 构建步骤 |
| **Updater/** | C# (.NET 10) | 桌面应用 | 通用外部更新器（zip 解压覆盖 + 进程控制 + 回滚） |
| **TestFrameExtract/** | C# (.NET 10) | 控制台 | 视频帧提取诊断工具（MediaFoundation + SkiaSharp） |
| **rust/** | Rust | Workspace | .NET 图标工具套件的 Rust 重写（4 个 crate） |
| **dbcheck.cs** | C# 脚本 | 脚本 | SQLite 数据库会话查询诊断 |
| **Add-ApimV1CompatRoutes.ps1** | PowerShell | 脚本 | Azure APIM v1 兼容路由批量配置 |
| **reference/** | Markdown | 文档 | APIM / Sora 集成参考资料 |

---

## .NET 图标工具套件

### 架构关系

```
IconTool (CLI)           IconToolUI (GUI)
    ↓                        ↓
    └───→ IconTool.Core ←────┘
           ├─ PeIconExtractor        纯字节操作，从 PE (EXE/DLL) 提取图标
           ├─ DirectoryIconService   Shell API + desktop.ini + 6 层缓存刷新
           ├─ IcoHelper              ICO 格式解析
           └─ Models                 IconGroupInfo / IconEntryInfo / ExeIconInfo

IconGen ──── 独立，不依赖 Core（使用 ImageSharp 处理 PNG→ICO）
```

### IconTool — CLI 工具

```
dotnet run --project tools/IconTool -- <命令> [参数]
```

主要子命令：

| 命令 | 说明 |
|---|---|
| `check` | 检测 PNG/ICO 是否包含透明通道 |
| `transparent` | 背景透明化（白/黑自动检测或指定颜色） |
| `crop` | 居中裁剪到指定尺寸 |
| `extract` | 从 EXE/DLL 提取图标资源 |
| `seticon` | 设置目录自定义图标 |
| `convert` | 格式互转（PNG/ICO/BMP） |
| `resize` | 批量生成多尺寸图标 |
| `info` | 查看图像元信息 |
| `pad` | 画布扩展 / 添加边距 |
| `round` | 圆角 / 圆形裁剪 |
| `shadow` | 外阴影效果 |
| `overlay` | 角标 / 水印叠加 |
| `compose` | 多图合成 ICO |
| `favicon` | 一键生成 Web 全套 favicon |
| `sheet` | 合并 sprite sheet |
| `browseicons` | 浏览 EXE 图标并设为目录图标 |

**依赖**：SixLabors.ImageSharp 3.1  
**发布**：`dotnet publish -c Release`（单文件，win-x64）

### IconTool.Core — 共享核心库

零第三方依赖，纯托管 PE 解析。同时被 IconTool CLI 和 IconToolUI 引用。

### IconToolUI — GUI 工具

WinForms 桌面应用。扫描目录内的 EXE → 列出所有图标 → 双击设置为文件夹图标。权限不足时自动 UAC 提权。详见 [IconToolUI/README.md](IconToolUI/README.md)。

### IconGen — PNG→ICO 生成器

```
dotnet run --project tools/IconGen -- <input.png> <output.ico>
```

自动正方形画布转换 + SHA256 校验和输出。可嵌入 MSBuild 构建步骤。

---

## Rust 图标工具套件

.NET 图标工具的 **Rust 平行重写**，4 个 crate 组成 Workspace：

```
rust/
├── core/       icon-core      共享库（PE 提取、ICO 解析、目录图标设置）
├── cli/        icon-tool      CLI（clap 子命令，对标 .NET IconTool）
├── icongen/    icon-gen       PNG→ICO（对标 .NET IconGen）
└── ui/         icon-ui        egui 桌面应用（对标 .NET IconToolUI）
```

### 构建

```powershell
cd tools/rust
cargo build --release
# 输出：target/release/icon-tool.exe, icon-gen.exe, icon-ui.exe
```

### 关键依赖

| crate | 用途 |
|---|---|
| `windows` | Win32 Shell / PE API |
| `image` | 图像格式读写 |
| `clap` | 命令行解析 |
| `eframe` / `egui` | GUI 框架 |
| `rfd` | 原生文件对话框 |

详见 [rust/README.md](rust/README.md)。

---

## 其他工具

### Updater — 通用更新器

```
Updater.exe --zip <zipPath> --target <appDir> --exe <exeName> --pid <pid>
```

工作流：等待主进程退出 → 备份 → 解压覆盖 → 清理 → 重启主程序。失败自动回滚。零第三方依赖，FDD 单文件发布。

### TestFrameExtract — 视频帧诊断

验证 Vortice.MediaFoundation SourceReader + SkiaSharp 的视频帧提取流程。硬编码测试路径，仅供开发诊断用。

### dbcheck.cs

C# 脚本，查询本地 `truefluentpro.db` 中 `media-center-v2` 会话统计（总数、任务数、资源数等）。

### Add-ApimV1CompatRoutes.ps1

Azure APIM v1 API 兼容路由批量配置脚本。支持 `--DryRun` 预览模式。

### reference/

| 文件 | 内容 |
|---|---|
| `APIM针对image-1.5编辑图片的特殊处理.md` | OpenAI images/edits 通过 APIM 对接 Azure OpenAI 的方案 |
| `sora与sora2相关配置对比.md` | Sora 1.0 (Jobs) vs Sora 2.0 (Videos) API 差异对比 |

---

## .NET vs Rust 对照

| 功能 | .NET 实现 | Rust 实现 |
|---|---|---|
| 核心库 | IconTool.Core | icon-core |
| CLI 工具 | IconTool (16+ 命令) | icon-tool |
| PNG→ICO | IconGen | icon-gen |
| GUI 工具 | IconToolUI (WinForms) | icon-ui (egui) |

两套实现功能对标，可独立使用。Rust 版本产物更小、无需 .NET 运行时。
