# 译见 Pro：补充说明（面向维护者/发布者）

这份文档用来承载 README 不必展开的细节：依赖、平台取舍、构建说明、架构概览、发布建议等。

## 这是什么

译见 Pro 是一个基于 Avalonia UI 的桌面应用，集成 Azure Speech、OpenAI / Azure OpenAI 等云服务：

- 采集音频（麦克风 / 系统回环 Loopback / 混合模式）并推送到 Azure Speech 做识别与翻译
- 实时显示中间结果与最终结果，浮动字幕置顶显示
- AI 复盘与洞察：对会话内容进行 AI 分析，提炼摘要、行动项、风险
- 创作工坊：AI 对话（多轮对话、联网搜索、引用标注）、图片生成（DALL·E）、视频生成（Sora 2）
- 批量字幕中心：基于 Azure Speech Batch API 批量生成 SRT/VTT
- 可选同步录音，支持 MP3 自动转码

## 功能概览

- 实时识别/翻译（含中间结果/最终结果），语言自动检测
- 输入源选择：默认麦克风 / WASAPI 设备选择 / WASAPI Loopback / 回环+麦克风混合
- WebRTC 音频预处理：回音消除（AEC）、降噪（NS）、自动增益（AGC）
- 浮动字幕（可置顶，可调透明度/字号/颜色）
- 浮动洞察窗口（独立置顶，实时显示 AI 分析结果）
- 会话录音：写 WAV，支持 MP3 自动转码（NAudio.Lame / Media Foundation）
- AI 复盘：预设洞察按钮、自定义 Prompt、自动洞察模式
- 创作工坊（对话）：多会话 AI 对话，SQLite 持久化，联网搜索+引用标注
- 创作工坊（媒体）：AI 图片生成（DALL·E）、视频生成（Sora 2）、参考图裁剪、视频帧预览
- 批量字幕中心：Azure Speech Batch API + Azure Blob Storage 集成
- 网页搜索：Google / Bing / Bing News / DuckDuckGo / 百度 / MCP 协议
- 端点资料包：预置 Azure OpenAI / APIM / OpenAI Compatible 等端点模板
- AAD（Microsoft Entra ID）无密钥认证
- 配置中心（13 个模块）、配置导入导出
- 自动更新：检查 GitHub Releases 并通过内置 Updater 完成升级
- 历史记录：按会话积累文本
- 配置持久化：本地 JSON 保存，启动自动加载

## 平台说明

- WASAPI Loopback 是 Windows 能力：把“系统正在播放的声音”作为输入源
- WAV → MP3 转码依赖 Windows Media Foundation 的编码器能力；不同 Windows 版本/精简系统可能存在差异
- 视频帧提取基于 Vortice.MediaFoundation（仅 Windows）
- 代码整体按跨平台习惯组织，但目前主要在 Windows 上验证；其它平台请先测试关键链路

## 运行要求

- .NET 10（开发用 SDK；运行用 Desktop Runtime）
- Azure Speech Services 订阅（Key + Region）
- AI 端点（OpenAI / Azure OpenAI）用于复盘与创作功能（可选）
- Azure Blob Storage 用于批量字幕（可选）

## 快速开始（开发/本地运行）

```bash
dotnet restore
dotnet run
```

首次运行按界面提示填写 Azure Speech 的订阅信息，并在端点管理中配置 AI 端点。

## 平台与限制（务必先读）

- WASAPI Loopback、WAV→MP3（Media Foundation）、视频帧提取（Vortice.MediaFoundation）主要面向 Windows
- Windows on ARM：依赖层面支持 `win-arm64`，但建议真机验证音频链路（录音/环回/识别）

## 依赖与版本

项目依赖版本以 NuGet 引用为准（见 TrueFluentPro.csproj），以下为当前核心组件与版本：

**跨平台通用：**

| 包名 | 版本 | 用途 |
| --- | --- | --- |
| Avalonia | 11.3.11 | UI 框架 |
| Avalonia.Desktop | 11.3.11 | 桌面宿主 |
| Avalonia.Themes.Fluent | 11.3.11 | Fluent 主题 |
| Avalonia.Fonts.Inter | 11.3.11 | Inter 字体 |
| Avalonia.AvaloniaEdit | 11.4.1 | 代码/文本编辑器 |
| FluentAvaloniaUI | 2.5.0 | Fluent 组件库 |
| CommunityToolkit.Mvvm | 8.4.0 | MVVM 工具包 |
| Microsoft.CognitiveServices.Speech | 1.48.2 | Azure Speech SDK |
| Microsoft.CognitiveServices.Speech.Extension.MAS | 1.48.2 | 多语言音频流扩展 |
| Azure.Identity | 1.13.2 | AAD / Entra ID 认证 |
| Azure.Storage.Blobs | 12.22.2 | Azure Blob Storage |
| Microsoft.Extensions.DependencyInjection | 10.0.3 | DI 容器 |
| NAudio | 2.2.1 | 音频处理 |
| NAudio.Lame | 2.1.0 | MP3 编码 |
| Microsoft.Data.Sqlite | 9.0.4 | SQLite 本地数据库 |
| Newtonsoft.Json | 13.0.3 | JSON 序列化 |
| Markdig | 1.1.1 | Markdown 解析 |
| AngleSharp | 1.4.0 | HTML 解析（网页搜索） |
| SmartReader | 0.11.0 | 网页正文提取 |
| ReverseMarkdown | 5.2.0 | HTML → Markdown |
| SkiaSharp | 2.88.9 | 2D 图形 |
| Svg.Controls.Skia.Avalonia | 11.3.9.2 | SVG 渲染 |
| SoundFlow.Extensions.WebRtc.Apm | 1.4.0 | WebRTC 回音消除/降噪 |
| Projektanker.Icons.Avalonia.FontAwesome | 9.6.2 | 图标库 |

**仅 Windows：**

| 包名 | 版本 | 用途 |
| --- | --- | --- |
| Vortice.MediaFoundation | 3.8.3 | 视频帧提取 |

## 构建期工具

### Updater（自动更新程序）

- 独立 .NET 10 控制台项目，位于 `tools/Updater/`
- 构建主程序时自动编译并拷贝到输出目录（仅 Windows）
- 从 GitHub Releases 下载更新包，完成程序替换、备份与恢复

## 内容更新方式（关于/帮助）

关于与帮助页面内容来自 Markdown：

- 优先读取可执行文件同目录的 `About.md` / `Help.md`
- 外部文件不存在时回退到应用内置资源（`Assets/` 作为 AvaloniaResource 内嵌）

发布时如果希望用户可直接改文案，可在打包脚本中使用 `-CopyExternalDocs` 将 `Assets/About.md`、`Assets/Help.md` 外置到发布目录。

## 发布建议（适合 GitHub Releases）

推荐默认发布 FDD（framework-dependent）：
