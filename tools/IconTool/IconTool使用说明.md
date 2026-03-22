# IconTool 使用说明

> PNG/ICO 图像处理与图标管理命令行工具

---

## 一、项目概述

IconTool 是一个独立的命令行工具，提供 **15 大核心功能**：

| # | 命令 | 功能 |
|---|------|------|
| 1 | **auto**（无参数） | 扫描当前目录图片，自动检测→透明化→裁剪→生成 ICO |
| 2 | **check** | 检测 PNG/ICO 文件是否包含透明通道，输出详细分析报告 |
| 3 | **transparent** | 将图片中的指定背景色去除，输出带透明通道的 PNG |
| 4 | **crop** | 将图片居中裁剪缩放到指定正方形尺寸 |
| 5 | **extract** | 从 EXE/DLL 文件中提取所有嵌入的图标资源 |
| 6 | **seticon** | 将 ICO 图标设为目录的自定义图标（desktop.ini + Shell 通知） |
| 7 | **info** | 查看图片/ICO 文件详细元数据信息 |
| 8 | **convert** | ICO↔PNG↔BMP 格式互转 |
| 9 | **resize** | 批量生成多尺寸图片 |
| 10 | **pad** | 为图片添加透明边距（按像素或百分比） |
| 11 | **round** | 为图片添加圆角效果 |
| 12 | **shadow** | 为图片生成投影效果 |
| 13 | **overlay** | 在底图上叠加角标/水印 |
| 14 | **compose** | 将多张 PNG 合成为多尺寸 ICO 文件 |
| 15 | **favicon** | 一键生成 Web Favicon 全套文件（ICO + PNG + webmanifest + HTML） |
| 16 | **sheet** | 将多张图片合并为 Sprite Sheet（精灵图 + JSON + CSS） |

技术栈：.NET 10 + SixLabors.ImageSharp + SixLabors.ImageSharp.Drawing，支持发布为 Windows 单文件可执行程序（无需安装 .NET 运行时）。

> **注意**：主项目 `TrueFluentPro.csproj` 中的 `GenerateAppIconIco` Target（原 IconGen 自动构建）已禁用。图标生成改为通过 IconTool 手动执行。如需恢复自动构建，移除 csproj 中 Target 的 `Condition="false"`。

---

## 二、编译与打包

### 前置条件

- 已安装 [.NET 10 SDK](https://dotnet.microsoft.com/download)（或更高版本）
- Windows x64 环境（发布产物默认为 win-x64）

### 编译（开发调试）

在 `tools/IconTool` 目录下执行：

```powershell
dotnet build
```

产物位于 `bin/Debug/net10.0/win-x64/`，需要本机安装 .NET 运行时才能运行。

### 发布为单文件可执行程序（推荐）

```powershell
dotnet publish -c Release
```

产物位于 `bin/Release/net10.0/win-x64/publish/`，生成的 `IconTool.exe`（约 15 MB）是自包含的单文件程序，可直接复制到任何 Windows x64 机器上运行，**无需安装 .NET 运行时**。

### 发布说明

项目 `.csproj` 已预配置以下发布参数：

| 参数 | 值 | 说明 |
|------|-----|------|
| `PublishSingleFile` | `true` | 打包为单个 EXE |
| `SelfContained` | `true` | 包含 .NET 运行时，无需目标机器安装 |
| `RuntimeIdentifier` | `win-x64` | 目标平台为 Windows x64 |
| `PublishTrimmed` | `true` | 裁剪未使用的程序集，减小体积 |

如需发布到其他平台（如 linux-x64），可修改 `RuntimeIdentifier` 或在命令行覆盖：

```powershell
dotnet publish -c Release -r linux-x64
```

---

## 三、使用方法

### 查看帮助

```powershell
IconTool help
IconTool --help
IconTool -h
```

---

### 自动模式（无参数）— 一键处理当前目录

#### 用法

直接运行 `IconTool`，不带任何参数：

```powershell
# 将 IconTool.exe 复制到包含图片的目录，然后双击运行或在终端执行：
IconTool
```

#### 处理流程

工具会自动对当前目录中所有 `*.png`、`*.jpg`、`*.jpeg` 文件执行以下流程：

```
扫描当前目录图片
  ↓
逐个文件处理：
  ├─ 已透明？ → 跳过透明化（仍会裁剪 + 生成 ICO）
  ├─ 四周为白色？ → 去白色背景
  ├─ 四周为黑色？ → 去黑色背景
  └─ 四周颜色混杂？ → 跳过（不适合自动处理）
  ↓
透明化后覆盖原文件（JPG 自动转为 PNG）
  ↓
居中裁剪缩放到 512×512
  ↓
生成同名 .ico（16/32/48/256 四尺寸）
  ↓
源文件（含旧 .ico）备份到时间子目录
  ↓
输出详细处理日志
```

#### 背景色自动检测

工具会对图像**四个角落**的矩形区域进行采样（各角区域边长 = 图像短边的 5%，每角约 100 个均匀采样点），统计角落像素的颜色分布：

| 条件 | 判定 |
|------|------|
| ≥80% 角区域像素接近白色 (RGB≥225) | 背景色 = white |
| ≥80% 角区域像素接近黑色 (RGB≤30) | 背景色 = black |
| 其他情况 | 颜色不一致，跳过该文件 |

> **为什么采样角落而不是四条边？**  圆角矩形图标的内容往往延伸到边缘中段，如果沿整条边线采样，会把图标主体像素误当作背景色。角落区域无论图标是正方形还是圆角，都一定是背景色，因此检测更可靠。

#### 自动透明化策略（2026-02 更新）

自动模式的透明化步骤已升级为“稳健优先 + 自适应增强”：

- 仍使用四向边缘扫描交集（AND）保证不误删主体；
- 自动尝试阈值 `3 / 6 / 10 / 14`，选择更合适的方案；
- 扫描时允许穿透已透明/近透明像素（抗锯齿边缘），减少“检测到白边但透明化为 0”的情况。

这使得“近白边框/发光边缘”图标在自动模式下更容易正确透明化，同时保持对深色主体内容的保护。

#### 备份与日志

每次运行会在当前目录创建一个以时间命名的备份子目录：

```
backup_20260220_153045/
  ├── AppIcon.png          ← 原始 PNG 备份
  ├── AppIcon.ico          ← 原始 ICO 备份（如果已存在）
  ├── banner.jpg           ← 原始 JPG 备份
  └── 处理日志.txt          ← 详细处理日志
```

#### 日志示例

```
IconTool 自动处理日志
处理时间: 2026-02-20 15:30:45
工作目录: C:\icons
备份目录: C:\icons\backup_20260220_153045
待处理文件数: 3
────────────────────────────────────────────────────────────

[文件] logo.png
  尺寸: 512x512
  检测到背景色: white (R=255, G=255, B=255)
  备份源文件: logo.png → logo.png
  透明化像素: 45320/262144 (17.29%)
  输出文件: logo.png (198.5 KB)
  备份已有 ICO: logo.ico → logo.ico
  生成 ICO: logo.ico (91.0 KB, 尺寸: 16/32/48/256)
  结果: 成功

[文件] photo.jpg
  尺寸: 800x600
  结果: 跳过 — 四周像素颜色不一致，无法确定单一背景色

[文件] icon.png
  尺寸: 256x256
  结果: 跳过 — 已是透明图片（透明像素=12000, 占比=18.31%）
  生成 ICO: icon.ico (85.2 KB, 尺寸: 16/32/48/256)

════════════════════════════════════════════════════════════
汇总: 共 3 个文件, 成功 1, 跳过 2, 失败 0
```

#### ICO 生成说明

自动模式下生成的 ICO 与原 IconGen 工具逻辑一致：

- 图像先补齐为正方形（透明填充），保持比例
- 生成 4 个尺寸：16×16、32×32、48×48、256×256
- 使用 PNG 格式嵌入 ICO（现代标准）
- 如果目录已有同名 `.ico`，先备份再覆盖

#### 典型使用场景

```powershell
# 场景1：批量处理一批应用图标
cd C:\my-icons
IconTool

# 场景2：把 IconTool.exe 放到 PATH 中，随时在任何目录使用
$env:PATH += ";C:\tools"
cd C:\项目\Assets
IconTool

# 场景3：处理完后检查某个文件的效果
IconTool check logo.png
```

---

### 命令一：`check` — 透明度检测

#### 用法

```
IconTool check <文件路径>
```

支持 `.png` 和 `.ico` 两种格式。

#### 示例

```powershell
# 检测一张 PNG 图片
IconTool check logo.png

# 检测一个 ICO 图标文件
IconTool check "C:\icons\app.ico"
```

#### 输出内容说明

执行后会输出以下详细报告：

**1）基本统计**

```
尺寸: 448x448
总像素: 200,704
完全透明像素 (A=0): 37,245 (18.56%)
半透明像素 (0<A<255): 3,638 (1.81%)
包含透明通道: 是
```

- **完全透明像素 (A=0)**：Alpha 值为 0 的像素数量和占比
- **半透明像素 (0<A<255)**：Alpha 值介于 1~254 的像素数量和占比（常见于边缘抗锯齿）
- **包含透明通道**：只要存在任何非 255 的 Alpha 值，即判定为"是"

**2）角点检测**

```
■ 角点检测:
  左上       (   0,   0) A=  0 RGB=(0,0,0) → 透明
  右上       ( 447,   0) A=  0 RGB=(0,0,0) → 透明
  左下       (   0, 447) A=  0 RGB=(0,0,0) → 透明
  右下       ( 447, 447) A=  0 RGB=(0,0,0) → 透明
  左上(内)   (  20,  20) A=  0 RGB=(0,0,0) → 透明
  右上(内)   ( 427,  20) A=  0 RGB=(0,0,0) → 透明
  左下(内)   (  20, 427) A=  0 RGB=(0,0,0) → 透明
  右下(内)   ( 427, 427) A=  0 RGB=(0,0,0) → 透明
```

- 检测图像四个角落以及内缩的近角点（向内偏移 `min(20px, 图像边长的 10%)`）
- 用于判断图标是否为"圆角透明"样式

**3）主体区域采样**

```
■ 主体区域采样:
  ( 224, 224) A=255 RGB=(209,227,251) → 不透明
  ( 112, 112) A=255 RGB=(121,218,250) → 不透明
  ...
```

- 对图像中心及四个象限的中心点进行采样
- 确认主体图案是否为不透明（避免整图被误判为"透明"）

**4）综合判定**

```
■ 判定结果:
  ✓ 圆角透明图标 — 四角透明且主体不透明。
```

判定结果有三种：

| 结果 | 含义 |
|------|------|
| `✓ 圆角透明图标` | 四角和近角点全部透明，主体区域不透明。标准圆角图标。 |
| `△ 含透明通道` | 有透明像素，但四角不全透明，或主体区域存在半透明。需要进一步检查。 |
| `✗ 不透明图片` | 没有任何透明像素，图片完全不透明。 |

#### ICO 文件特殊行为

对于 `.ico` 文件，工具会解析 ICO 内部结构，**逐一分析每张嵌入图像**：

```
── 检测 ICO: AppIcon.ico ──

ICO 包含 4 张嵌入图像

  [图像 1] 16x16, 数据大小=827 字节
    尺寸: 16x16
    总像素: 256
    ...（完整报告）

  [图像 2] 32x32, 数据大小=2544 字节
    ...

  [图像 3] 48x48, ...
  [图像 4] 256x256, ...
```

每张嵌入图像都会独立输出透明度分析报告和判定结果。

---

### 命令二：`transparent` — 背景透明化

#### 用法

```
IconTool transparent <文件路径> [选项]
```

支持输入 PNG、JPG、JPEG 等 ImageSharp 支持的常见图片格式，输出始终为 PNG。

#### 选项

| 选项 | 简写 | 默认值 | 说明 |
|------|------|--------|------|
| `--output <路径>` | `-o` | `原文件名_transparent.png` | 输出文件路径 |
| `--color <颜色>` | `-c` | `white` | 要去除的背景色 |
| `--threshold <值>` | `-t` | `30` | 颜色匹配容差（0-255） |
| `--flood` | — | 关闭 | 连通填充模式：仅从边缘可达的背景区域被移除，适合深色内容图标 |

> **提示**：自动模式默认使用连通填充策略（等同于 `--flood`），能正确处理内容色与背景色相近的图标。手动 `transparent` 命令默认使用全局匹配，添加 `--flood` 可切换。

**颜色值支持格式：**

| 格式 | 示例 | 说明 |
|------|------|------|
| 预设名称 | `white`、`black` | 白色或黑色 |
| 十六进制 RGB | `#FF0000` | 红色 |
| 十六进制 RGBA | `#FF000080` | 半透明红色 |

#### 示例

```powershell
# 去除白色背景（默认），输出为 photo_transparent.png
IconTool transparent photo.png

# 去除白色背景，指定输出文件名
IconTool transparent photo.jpg -o clean.png

# 去除红色背景
IconTool transparent banner.png -c "#FF0000"

# 去除黑色背景，加大容差
IconTool transparent dark_logo.png -c black -t 50

# 去除特定颜色背景
IconTool transparent card.png -c "#F0F0F0" -t 20 -o card_clean.png
```

#### 输出内容说明

执行后会输出处理过程和结果：

```
── 透明化处理 ──
  输入: photo.png
  去除背景色: white (R=255, G=255, B=255)
  颜色容差: 30
  输出: photo_transparent.png

  图像尺寸: 448x448
  已透明化像素: 15819/200704 (7.88%)
  输出文件大小: 198.3 KB
```

- **已透明化像素**：被去除背景色后变为透明的像素数量和占比
- **输出文件大小**：最终 PNG 文件的大小

处理完成后，工具会**自动对输出文件执行一次透明度检测**，输出与 `check` 命令相同的详细报告，方便验证效果。

#### 透明化算法说明

工具采用两级处理策略：

1. **完全匹配区域**：像素与目标背景色的 R/G/B 各通道差值均在容差 (`threshold`) 范围内时，该像素被设置为**完全透明** (A=0)
2. **边缘渐变过渡**：像素与目标色的欧氏距离在 `threshold` ~ `threshold×2` 范围内时，Alpha 值会按距离比例渐变，避免边缘出现生硬锯齿

**容差参数调节建议：**

| 容差值 | 适用场景 |
|--------|----------|
| `0-10` | 背景色非常精确且均匀（如纯白 #FFFFFF） |
| `20-40` | 一般场景（默认 30），适用于大多数纯色背景 |
| `50-80` | 背景色存在轻微渐变或噪点 |
| `>100` | 谨慎使用，可能误伤主体颜色 |

---

### 命令三：`crop` — 居中裁剪缩放

#### 用法

```
IconTool crop <文件路径> [选项]
```

将图片居中裁剪并缩放到指定的正方形尺寸。对于非正方形图片，取短边为基准居中裁剪为正方形，再缩放到目标尺寸。

#### 选项

| 选项 | 简写 | 默认值 | 说明 |
|------|------|--------|------|
| `--size <值>` | `-s` | `512` | 目标正方形边长（1-4096） |
| `--output <路径>` | `-o` | `原文件名_cropped.png` | 输出文件路径 |

#### 裁剪算法

```
原始图 (W×H)
    ↓
取 S = min(W, H) 作为正方形边长
    ↓
裁剪框 = (x=(W-S)/2, y=(H-S)/2, S×S) — 居中
    ↓
缩放到目标尺寸（默认 512×512）
```

- 如果原图比目标大 → 居中裁剪 + 缩小
- 如果原图比目标小 → 居中裁剪 + 放大
- 如果原图已是目标尺寸的正方形 → 直接返回

缩放使用 **Lanczos3** 重采样算法，保证缩放质量。

#### 示例

```powershell
# 裁剪到 512x512（默认尺寸）
IconTool crop photo.png

# 裁剪到 256x256
IconTool crop photo.png -s 256

# 裁剪并指定输出路径
IconTool crop photo.png -s 512 -o output/logo_512.png

# 对已透明化后的图标进行裁剪
IconTool crop app_transparent.png -s 512 -o app_final.png
```

#### 输出内容说明

```
── 居中裁剪缩放 ──
  输入: photo.png
  目标尺寸: 512x512
  输出: photo_cropped.png

  原始尺寸: 737x707
  裁剪后尺寸: 512x512
  输出文件大小: 198.3 KB

✓ 裁剪完成。
```

> **提示**：自动模式会在透明化后自动执行居中裁剪到 512×512，无需手动调用 `crop` 命令。`crop` 命令适合需要自定义尺寸或单独对某张图片裁剪的场景。

---

### 命令四：`extract` — 从 EXE/DLL 提取图标

#### 用法

```
IconTool extract <exe/dll路径> [选项]
```

从 Windows PE 可执行文件（.exe、.dll）中提取所有嵌入的图标资源，输出为标准 ICO 文件。每个图标组（RT_GROUP_ICON）生成一个独立的 ICO 文件，包含该组内所有尺寸。

#### 选项

| 选项 | 简写 | 默认值 | 说明 |
|------|------|--------|------|
| `--output <目录>` | `-o` | 当前目录 | 输出 ICO 文件的目录 |

#### 示例

```powershell
# 提取 explorer.exe 中所有图标到当前目录
IconTool extract "C:\Windows\explorer.exe"

# 提取到指定目录
IconTool extract "C:\Windows\explorer.exe" -o icons/

# 从 DLL 中提取
IconTool extract "C:\Windows\System32\shell32.dll" -o shell-icons/
```

#### 输出内容说明

```
── 从 PE 文件提取图标 ──
  输入: C:\Windows\explorer.exe
  输出目录: C:\icons

  [图标 1] 8 张 (256/64/48/40/32/24/20/16) → explorer_icon1.ico (57.3 KB)
  [图标 101] 9 张 (32/24/16/32/24/16/32/24/16) → explorer_icon101.ico (14.5 KB)
  ...

✓ 共提取 23 个图标文件。
```

每个图标文件名格式为 `{原文件名}_icon{资源ID}.ico`，包含该组所有尺寸的多分辨率图标。

#### 实现原理

- 纯字节解析 PE 文件的资源段（`.rsrc`），不依赖任何 Win32 API
- 读取 `RT_GROUP_ICON`（图标组描述）和 `RT_ICON`（单个图像数据）
- 将两者重组为标准 ICO 文件格式
- 支持 PE32 和 PE32+（64 位）可执行文件

---

### 命令五：`seticon` — 设置目录自定义图标

#### 用法

```
IconTool seticon <ico路径> <目录路径>
```

将指定的 ICO 文件设为目录的自定义图标，通过 `desktop.ini` 机制实现。

#### 示例

```powershell
# 将图标设为指定目录的图标
IconTool seticon app.ico "D:\Projects\MyApp"

# 设为当前目录图标
IconTool seticon logo.ico .

# 结合 extract 使用：先从 EXE 提取图标，再设为目录图标
IconTool extract app.exe -o .
IconTool seticon app_icon1.ico "D:\Projects\MyApp"
```

#### 执行流程

1. 将 ICO 文件复制到目标目录（如果不在同一位置）
2. 设置 ICO 文件属性为 Hidden + System
3. 创建/更新 `desktop.ini`，写入 `[.ShellClassInfo] IconResource=xxx.ico,0`
4. 设置 `desktop.ini` 属性为 Hidden + System
5. 设置目标目录属性为 ReadOnly（Windows Shell 要求此属性才会读取 desktop.ini）
6. 调用 `SHChangeNotify(SHCNE_UPDATEDIR)` + `SHChangeNotify(SHCNE_ASSOCCHANGED)` 通知资源管理器立即刷新

#### 输出内容说明

```
── 设置目录图标 ──
  图标: C:\icons\app.ico
  目录: D:\Projects\MyApp

  复制图标: app.ico → D:\Projects\MyApp
  写入: desktop.ini
  设置目录属性: ReadOnly
  已通知资源管理器刷新图标缓存

✓ 目录图标设置完成。

提示: 如果图标未立即显示，可尝试：
  1. 按 F5 刷新资源管理器
  2. 关闭并重新打开资源管理器窗口
```

#### 注意事项

- `desktop.ini` 和 ICO 文件会被设为隐藏+系统属性，在默认资源管理器设置下不可见
- 目录的 ReadOnly 属性**不影响目录内文件的读写**，仅用于告诉 Shell 读取 desktop.ini
- 通过 `SHChangeNotify` P/Invoke 通知 Shell 刷新，大多数情况下图标会立即更新
- 如果极少数情况下图标仍未更新，按 F5 或重新打开资源管理器窗口即可

---

### 命令六：`info` — 图片元数据查看

#### 用法

```
IconTool info <文件路径>
```

显示图片或 ICO 文件的详细元数据信息，包括尺寸、色深、文件大小等。对于 ICO 文件，会列出内部每一张嵌入图像的信息。

#### 示例

```powershell
# 查看 PNG 文件信息
IconTool info logo.png

# 查看 ICO 文件信息（显示所有嵌入图像）
IconTool info app.ico
```

#### 输出示例

**PNG 文件：**
```
── 图片信息 ──
  文件: logo.png
  格式: PNG
  尺寸: 256x256
  文件大小: 14.5 KB
```

**ICO 文件：**
```
── 图片信息 ──
  文件: app.ico
  格式: ICO
  文件大小: 57.3 KB
  包含 8 张嵌入图像:
    [1] 256x256, 32bpp, 数据大小=38912 字节
    [2] 64x64, 32bpp, 数据大小=8432 字节
    ...
```

---

### 命令七：`convert` — 格式转换

#### 用法

```
IconTool convert <文件路径> [选项]
```

在 ICO、PNG、BMP 格式之间互相转换。从 ICO 转换时，自动提取最大尺寸的图像。

#### 选项

| 选项 | 简写 | 默认值 | 说明 |
|------|------|--------|------|
| `--output <路径>` | `-o` | 自动根据目标格式命名 | 输出文件路径 |
| `--format <格式>` | `-f` | 根据输出扩展名推断 | 目标格式：`png`、`bmp`、`ico` |

#### 示例

```powershell
# ICO 转 PNG（提取最大尺寸图像）
IconTool convert app.ico -o app.png

# PNG 转 BMP
IconTool convert logo.png -f bmp

# PNG 转 ICO（单尺寸）
IconTool convert logo.png -o logo.ico
```

---

### 命令八：`resize` — 批量生成多尺寸

#### 用法

```
IconTool resize <文件路径> [选项]
```

从一张源图批量生成多个尺寸的图片，使用 Lanczos3 高质量重采样。

#### 选项

| 选项 | 简写 | 默认值 | 说明 |
|------|------|--------|------|
| `--sizes <尺寸列表>` | `-s` | `16,32,48,64,128,256` | 逗号分隔的目标尺寸 |
| `--output <目录>` | `-o` | 源文件所在目录下的 `resized` 子目录 | 输出目录 |

#### 示例

```powershell
# 使用默认尺寸生成 6 个文件
IconTool resize logo.png

# 指定自定义尺寸
IconTool resize logo.png -s "16,32,64,128,256,512"

# 输出到指定目录
IconTool resize logo.png -s "32,64,128" -o icons/
```

#### 输出说明

```
── 批量生成多尺寸 ──
  输入: logo.png (256x256)
  目标: 16,32,64,128,256

  [1] 16x16 → resized/logo_16x16.png (603 B)
  [2] 32x32 → resized/logo_32x32.png (1.7 KB)
  ...

✓ 共生成 5 个文件。
```

---

### 命令九：`pad` — 添加透明边距

#### 用法

```
IconTool pad <文件路径> [选项]
```

为图片四周添加透明边距，扩展画布尺寸。可按像素或百分比指定边距。

#### 选项

| 选项 | 简写 | 默认值 | 说明 |
|------|------|--------|------|
| `--pixels <值>` | `-p` | — | 边距像素值 |
| `--percent <值>` | — | `10` | 边距占原图短边的百分比（与 `-p` 二选一） |
| `--output <路径>` | `-o` | `原文件名_padded.png` | 输出文件路径 |

#### 示例

```powershell
# 按默认百分比（10%）加边距
IconTool pad logo.png

# 按像素加边距
IconTool pad logo.png -p 20

# 按百分比加边距
IconTool pad logo.png --percent 15 -o padded.png
```

#### 输出说明

```
── 加边距 ──
  输入: logo.png (256x256)
  边距: 51px
  输出尺寸: 358x358
  输出: padded.png (15.9 KB)
✓ 完成。
```

---

### 命令十：`round` — 添加圆角

#### 用法

```
IconTool round <文件路径> [选项]
```

为图片添加圆角效果，通过像素级 Alpha 遮罩实现平滑圆角。

#### 选项

| 选项 | 简写 | 默认值 | 说明 |
|------|------|--------|------|
| `--radius <值>` | `-r` | 短边的 20% | 圆角半径（像素） |
| `--output <路径>` | `-o` | `原文件名_rounded.png` | 输出文件路径 |

#### 示例

```powershell
# 使用默认圆角半径
IconTool round logo.png

# 指定圆角半径
IconTool round logo.png -r 50

# 指定输出路径
IconTool round logo.png -r 30 -o rounded.png
```

#### 输出说明

```
── 圆角处理 ──
  输入: logo.png (256x256)
  圆角半径: 50px
  输出: rounded.png (14.9 KB)
✓ 完成。
```

---

### 命令十一：`shadow` — 生成投影

#### 用法

```
IconTool shadow <文件路径> [选项]
```

为图片添加阴影效果。工具会自动扩展画布以容纳阴影区域，使用高斯模糊生成柔和投影。

#### 选项

| 选项 | 简写 | 默认值 | 说明 |
|------|------|--------|------|
| `--blur <值>` | `-b` | `10` | 模糊半径（像素，1-100） |
| `--offset <x,y>` | — | `4,4` | 阴影偏移量（像素） |
| `--color <RRGGBBAA>` | `-c` | `00000080` | 阴影颜色（十六进制 RGBA） |
| `--output <路径>` | `-o` | `原文件名_shadow.png` | 输出文件路径 |

#### 示例

```powershell
# 默认阴影效果
IconTool shadow logo.png

# 自定义模糊半径和偏移
IconTool shadow logo.png -b 15 --offset 6,6

# 自定义阴影颜色（半透明红色）
IconTool shadow logo.png -c "FF000060" -b 20 -o red_shadow.png
```

#### 输出说明

```
── 添加阴影 ──
  输入: logo.png (256x256)
  模糊半径: 15, 偏移: (6,6)
  画布扩展: 328x328
  输出: shadow.png (20.1 KB)
✓ 完成。
```

---

### 命令十二：`overlay` — 叠加角标/水印

#### 用法

```
IconTool overlay <底图> <叠加图> [选项]
```

在底图上叠加一张小图（角标、水印等）。叠加图会按比例缩放，放置到指定位置。

#### 选项

| 选项 | 简写 | 默认值 | 说明 |
|------|------|--------|------|
| `--position <位置>` | `-p` | `br` | 位置：`tl`（左上）、`tr`（右上）、`bl`（左下）、`br`（右下）、`c`/`center`（居中） |
| `--scale <值>` | `-s` | `30` | 叠加图占底图宽度的百分比（5-100） |
| `--margin <值>` | `-m` | `2` | 边距占底图宽度的百分比（0-50） |
| `--output <路径>` | `-o` | `原文件名_overlay.png` | 输出文件路径 |

#### 示例

```powershell
# 右下角叠加角标（默认位置）
IconTool overlay base.png badge.png

# 左上角叠加，缩放 20%
IconTool overlay base.png badge.png -p tl -s 20

# 居中叠加水印
IconTool overlay base.png watermark.png -p center -s 50 -o result.png
```

#### 输出说明

```
── 叠加角标 ──
  底图: base.png (256x256)
  叠加: badge.png → 76x76
  位置: br, 偏移: (175,175)
  输出: result.png (18.1 KB)
✓ 完成。
```

---

### 命令十三：`compose` — 多图合成 ICO

#### 用法

```
IconTool compose <文件1> <文件2> ... [选项]
```

将多张 PNG/BMP 图片合并为一个多尺寸 ICO 文件。每张输入图片作为 ICO 中的一个独立尺寸。

#### 选项

| 选项 | 简写 | 默认值 | 说明 |
|------|------|--------|------|
| `--output <路径>` | `-o` | `composed.ico` | 输出 ICO 文件路径 |

#### 示例

```powershell
# 将多个尺寸合成为一个 ICO
IconTool compose icon_16.png icon_32.png icon_64.png icon_256.png -o app.ico

# 结合 resize 使用：先生成多尺寸，再合成 ICO
IconTool resize logo.png -s "16,32,48,256" -o sizes/
IconTool compose sizes/logo_16x16.png sizes/logo_32x32.png sizes/logo_48x48.png sizes/logo_256x256.png -o app.ico
```

#### 输出说明

```
── 多图拼合成 ICO ──
  输入: 4 个文件
  [1] icon_16.png → 16x16
  [2] icon_32.png → 32x32
  [3] icon_64.png → 64x64
  [4] icon_256.png → 256x256

  输出: app.ico (19.1 KB, 4 张)
✓ 完成。
```

---

### 命令十四：`favicon` — Web Favicon 全套生成

#### 用法

```
IconTool favicon <文件路径> [选项]
```

从一张源图一键生成 Web 网站所需的全套 Favicon 文件，包括 ICO、多尺寸 PNG、Apple Touch Icon、Android Chrome 图标、site.webmanifest 和 HTML `<link>` 标签片段。

#### 选项

| 选项 | 简写 | 默认值 | 说明 |
|------|------|--------|------|
| `--output <目录>` | `-o` | 源文件所在目录下的 `favicon` 子目录 | 输出目录 |

#### 示例

```powershell
# 一键生成全套 Favicon
IconTool favicon logo.png

# 输出到指定目录
IconTool favicon logo.png -o public/
```

#### 生成文件列表

| 文件 | 说明 |
|------|------|
| `favicon.ico` | 16/32/48 三尺寸合一 ICO |
| `favicon-16x16.png` ~ `favicon-256x256.png` | 标准尺寸 PNG (16/32/48/64/96/128/256) |
| `apple-touch-icon.png` | 180×180 Apple Touch Icon |
| `android-chrome-192x192.png` | Android Chrome 192px |
| `android-chrome-512x512.png` | Android Chrome 512px |
| `site.webmanifest` | PWA 清单文件 |
| `_head.html` | 可直接复制到 `<head>` 中的 `<link>` 标签片段 |

---

### 命令十五：`sheet` — Sprite Sheet 合并

#### 用法

```
IconTool sheet [选项] <文件1> <文件2> ... 
IconTool sheet [选项] <目录>
```

将多张图片合并为一张 Sprite Sheet（精灵图），同时生成 JSON 坐标映射和 CSS 样式文件。支持传入目录自动展开其中的图片文件。

#### 选项

| 选项 | 简写 | 默认值 | 说明 |
|------|------|--------|------|
| `--output <路径>` | `-o` | `spritesheet.png` | 输出精灵图路径 |
| `--json <路径>` | — | 与输出同名 `.json` | JSON 坐标文件路径 |
| `--size <值>` | `-s` | 自动（最大图像的边长） | 单元格尺寸（像素） |

#### 示例

```powershell
# 合并目录中所有图片
IconTool sheet icons/ -o spritesheet.png

# 指定文件列表
IconTool sheet icon1.png icon2.png icon3.png -o sprites.png

# 指定单元格尺寸
IconTool sheet icons/ -s 64 -o sprites.png
```

#### 输出说明

```
── Sprite Sheet 合并 ──
  输入: 5 个文件
  单元格: 256x256
  网格: 3x2 (768x512)
  [1] icon_128x128 (128x128) → (0,0)
  [2] icon_16x16 (16x16) → (1,0)
  ...

  输出: spritesheet.png (32.3 KB)
  坐标: spritesheet.json
  CSS: spritesheet.css

✓ 共合并 5 个图标。
```

生成的 JSON 包含每个精灵的名称和位置坐标，CSS 包含 `background-position` 样式类，可直接在 Web 项目中使用。

---

## 四、退出码

| 退出码 | 含义 |
|--------|------|
| `0` | 成功 |
| `1` | 参数错误或处理失败 |

---

## 五、常见问题

### Q: JPG/JPEG 可以检测透明度吗？

JPG 格式本身不支持透明通道，`check` 命令只支持 `.png` 和 `.ico`。但 `transparent` 命令可以接受 JPG 作为输入，去除背景色后输出为 PNG。

### Q: 路径中包含空格或中文怎么办？

用双引号包裹路径即可：

```powershell
IconTool check "C:\我的图片\应用图标.png"
```

### Q: 输出的 PNG 透明后背景变成黑色？

这不是工具的问题，是图片查看器的显示行为。部分查看器在透明区域显示黑色或灰色棋盘格。可以用支持透明的查看器（如 Windows 照片、浏览器）验证。

### Q: 怎样确认透明化效果是否满意？

`transparent` 命令执行后会自动运行 `check` 检测。重点关注：
- **判定结果**是否符合预期
- **主体区域采样**中主体是否仍为不透明 (A=255)
- **透明像素占比**是否合理

如果主体像素被误伤（出现半透明），尝试降低容差值 (`-t`)。

### Q: 自动模式下 JPG 文件会怎样处理？

JPG 不支持透明通道。工具会将透明化后的结果保存为同名 `.png` 文件，原 JPG 备份到时间子目录后删除。

### Q: 自动模式跳过了文件怎么办？

查看备份目录下的 `处理日志.txt`，会记录每个文件被跳过的具体原因（已透明/颜色不一致）。如果需要强制处理，可以用 `transparent` 命令手动指定颜色：

```powershell
IconTool transparent myfile.png -c "#F0F0F0" -t 40 -o myfile.png
```

### Q: IconGen 和 IconTool 有什么关系？

IconGen 是原有的构建时自动图标生成工具（`tools/IconGen`），仅做 `PNG → ICO` 转换。IconTool 是增强版独立工具，集成了透明度检测、背景透明化、ICO 生成三合一功能。主项目构建中的 IconGen 自动步骤已禁用，改用 IconTool 手动处理。

---

## 六、开发踩坑记录

以下记录开发过程中遇到的关键问题及修复方案，供后续维护参考。

### 1. ImageSharp `SaveAsPng()` 默认会丢弃 Alpha 通道

**问题现象**：对图片执行透明化处理后，内存中像素的 Alpha 值已正确设置为 0，但保存的 PNG 文件仍然不透明。文件大小与处理前完全一致，用 `check` 命令检测显示 0 个透明像素。

**根本原因**：`SixLabors.ImageSharp` 的 `SaveAsPng()` 方法在未指定编码器时，会根据**源图像的元数据**自动选择 PNG 颜色类型。如果源文件是 RGB PNG（无 Alpha）或 JPG，编码器默认选择 `PngColorType.Rgb`，**静默丢弃**所有运行时修改的 Alpha 值。

**修复方案**：所有 `SaveAsPng()` 调用必须显式指定编码器：

```csharp
// ✗ 错误：Alpha 通道可能被丢弃
image.SaveAsPng(outputPath);

// ✓ 正确：强制 RGBA 输出
image.SaveAsPng(outputPath, new PngEncoder { ColorType = PngColorType.RgbWithAlpha });
```

**验证方法**：处理后的文件大小应明显变化（透明像素的 PNG 压缩率不同）。如果处理前后文件大小完全一致，几乎可以确定 Alpha 通道没有被写入。

### 2. 边缘采样无法处理圆角矩形图标

**问题现象**：对黑色背景的圆角图标运行自动模式，工具报告"四周像素颜色不一致，跳过"，无法自动检测到黑色背景。

**根本原因**：初始版本的 `DetectBorderColor()` 沿图像四条边线均匀采样。圆角图标的图案内容延伸到边缘中段（例如边线中间的像素属于图标本体而非背景），导致采样到大量非背景色像素，黑色占比低于 80% 阈值。

**修复方案**：改为采样四个角落的矩形区域（各角区域边长 = 图像短边的 5%，每角约 100 个均匀采样点）。角落无论图标是正方形还是圆角矩形，都一定是纯背景色。

### 3. 自动模式容差过大导致深色内容被误删

**问题现象**：自动模式去黑色背景时，图标中深蓝色（如 RGB(1,14,73)）的内容区域也被透明化。

**根本原因**：自动模式的默认容差 (`threshold`) 为 30。深蓝色像素与黑色 (0,0,0) 的 R/G 通道差值（1、14）在容差范围内，虽然 B 通道（73）超出，但整体仍有部分像素被误判。

**修复方案**：将自动模式的容差降低为 10（手动 `transparent` 命令的默认值仍为 30，因为用户可以根据需要自行调整）。

### 4. 全局颜色匹配会删除图标内部的暗色像素

**问题现象**：对包含深色内容（暗蓝色暴风/海浪/夜空）的圆角图标去黑色背景时，图标主体中大量纯黑或近黑像素也被透明化，透明比例高达 58%（正确的背景面积应远小于此值）。

**根本原因**：原始算法对整张图片逐像素做全局颜色匹配（`IsColorMatch`）——只要像素颜色接近背景色就删。但深色艺术画面中本身就包含大量近黑像素（如 RGB(0,0,0) 到 RGB(3,3,3)），它们与背景色无法区分。

**尝试过的方案**：
1. ❌ 降低容差到 3 甚至 0 — 无效，因为图标画面中就有纯 (0,0,0) 像素
2. ❌ Flood Fill 从四角连通填充 — 无效，暗色像素与背景通过边缘过渡区连通，Flood Fill 沿该路径泄漏进图标内部
3. ❌ 深度限制 Flood Fill（25%）— 无效，留出的深度仍然太大
4. ❌ 单方向边缘扫描线（UNION）— 无效，某些列/行上暗色像素从边缘一直延伸到图标深处

**最终修复方案**：四向边缘扫描 + 交集策略（AND）：
- 水平方向：从左或从右扫描，碰到非背景像素即停
- 垂直方向：从上或从下扫描，碰到非背景像素即停
- **仅水平和垂直方向都标记为背景的像素才被透明化**
- 这确保只有真正在角落/边缘浅层的背景被移除（角落像素从两个方向都能到达），图标内部的深色像素最多只能从一个方向到达，被交集过滤掉

> **经验总结**：图像处理中的"显而易见"常常有坑。务必用实际图片验证输出文件（而不只看运行时内存状态），且要用独立工具交叉检测处理结果。全局颜色匹配不适合内容色与背景色相近的场景，需要利用**空间位置信息**来区分背景和内容。

### 5. 角区域采样区域过大导致紧凑图标检测失败

**问题现象**：白色背景的圆角图标（737×707，边距很小）运行自动模式时，报告"四周颜色不一致，跳过"。

**根本原因**：角区域采样边长为图像短边的 15%（707 × 15% = 106px）。而该图标的白色边距仅约 30~50px，106px 区域已延伸到圆角图标的内容区域，导致白色像素占比仅 57.9%，低于 80% 阈值。

**修复方案**：将角区域采样边长从 15% 缩小到 5%（707 × 5% = 35px），确保采样范围仅覆盖角落的纯背景区域。修改后白色占比 100%，检测成功。

> **经验总结**：角区域采样大小是一个精度与鲁棒性的平衡——太大会侵入图标内容（紧凑图标失败），太小又可能因噪点导致误判。5% 是一个适合大多数实际图标的经验值。
