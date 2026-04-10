# 译见 Pro

![译见 Pro Logo](Assets/AppIcon.png)

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![Avalonia 11.3.11](https://img.shields.io/badge/Avalonia-11.3.11-2D9CDB.svg)](https://avaloniaui.net/)
[![Azure Speech 1.48.2](https://img.shields.io/badge/Azure%20Speech-1.48.2-0078D4.svg)](https://learn.microsoft.com/azure/ai-services/speech-service/)

我们做“译见 Pro”，不是为了一次翻译，而是为了让每一次交流都有价值被保存、被理解、被延展。
声音是最自然的语言，但它稍纵即逝；信息在现场生成，却常常在会后消散。于是我们开始思考：能否让声音留下结构、让对话沉淀洞察？

这就是“译见 Pro”的起点。
它把现场的声音转化为字幕，让信息即时可见；又用智能复盘把零散的片段整理为可追溯的结论、行动与风险。
不是为了替代思考，而是让思考被承载、被成文、被继续。

译以载言，智以成文。

> 🪟 说明：项目基于 Avalonia，但目前主要在 Windows 下开发；其他平台暂缺对应实现。

---

## ✨ 你会如何使用

- 线上会议与访谈：实时字幕与会话记录同步积累，支持会后 AI 复盘
- 跨语种内容理解：视频/课程/直播边看边译，信息不丢帧
- 现场记录与留存：录音与字幕联动，形成可追溯的资料
- 批量字幕与复盘：对录音文件批量生成字幕、AI 提炼会议摘要与行动项
- AI 对话与内容创作：通过创作工坊进行 AI 对话、图片生成（DALL·E）、视频生成（Sora 2）
- AI 辅助搜索：对话中联网搜索并自动生成带引用的回复
- 合规记录：录音前请确认已征得同意，并遵循所在地法律法规

---

## 🧩 功能概览

| 模块 | 说明 |
| --- | --- |
| 🎙️ 实时翻译（Live） | 实时识别/翻译、中间结果与最终结果并行显示、语言自动检测 |
| 🎧 音频输入 | 麦克风 / 设备选择 / 回环（Loopback）/ 回环+麦克风混合模式，WebRTC 回音消除与降噪 |
| 🪟 浮动字幕 | 置顶显示，可调透明度与字号，适配边看边译场景 |
| 🧠 复盘与洞察（Review） | AI 分析会话，提炼摘要、行动项、风险，支持预设、自定义与自动洞察 |
| 🔮 浮动洞察 | 独立置顶窗口，实时查看 AI 分析结果，不遮挡主界面 |
| 📦 批量字幕中心 | 基于 Azure Speech Batch API 批量生成 SRT/VTT，Azure Blob Storage 集成 |
| 💬 创作工坊（对话） | 多会话 AI 对话，支持联网搜索、引用标注、拖拽上传、快捷短语 |
| 🎨 创作工坊（媒体） | AI 图片生成（DALL·E）与视频生成（Sora 2），支持参考图、图生视频、裁剪工具 |
| 🔍 网页搜索 | 多引擎（Google / Bing / DuckDuckGo / 百度 / MCP），搜索结果带引用 |
| 🔑 端点资料包 | 预置 Azure OpenAI / APIM / OpenAI Compatible 等端点模板，自动匹配 API 版本与能力 |
| ⚙️ 配置中心 | 13 个模块：订阅、端点、AI、图片、视频、洞察、复盘、搜索、存储、文本、录音、导入导出、关于 |

### 🎙️ 实时翻译（Live 模式）

- 实时识别/翻译：中间结果与最终结果并行呈现，支持语言自动检测
- 多输入源：麦克风 / 设备选择 / 系统回环（Loopback），支持回环+麦克风混合模式
- WebRTC 音频预处理：回音消除（AEC）、降噪（NS）、自动增益（AGC）
- 浮动字幕：置顶、可调透明度与字号，不打断当前工作
- 会话录音：与字幕同步留存 WAV，支持 MP3 自动转码（NAudio.Lame / Media Foundation）
- 历史记录：按会话积累，支持后续 AI 复盘

### 🧠 复盘洞察（Review 模式）

- AI 复盘：对会话内容进行 AI 分析，提炼摘要、行动项、风险等
- 预设洞察按钮：会议总结、知识点提取、客诉识别、行动项提取、情绪分析等
- 自动洞察模式：定时或基于新数据触发，持续输出分析结果
- 浮动洞察窗口：独立置顶窗口显示分析结果

### 📦 批量字幕中心

- 基于 Azure Speech Batch API，对录音文件批量生成 SRT/VTT 字幕
- Azure Blob Storage 集成：批处理音频的上传、结果下载与管理
- 批任务队列管理、状态追踪、日志记录

### 💬 创作工坊（对话）

- 多会话 AI 对话：独立会话持久化（SQLite），支持重命名、复制、删除
- 联网搜索：对话中可触发网页搜索，AI 回复自动标注引用来源（[1][2]）
- 拖拽上传：文件与图片直接拖入对话
- 快捷短语与 Prompt 预设

### 🎨 创作工坊（媒体）

- AI 图片生成：支持 OpenAI / Azure OpenAI 后端，可挂载参考图（最多 8 张），内置裁剪工具
- AI 视频生成：支持 Sora 2 等模型，支持图生视频，可配置分辨率与时长
- 视频帧预览：基于 Media Foundation 的视频帧提取
- 多工作区管理：独立工作区持久化

### ⚙️ 配置中心

- **订阅管理**：添加、验证、测速多个 Azure Speech 订阅，支持中国区终结点
- **端点管理**：基于端点资料包（Endpoint Profiles）创建 AI 端点，自动匹配 API 版本与能力
- **AI 后端**：OpenAI 兼容 / Azure OpenAI，支持 AAD（Microsoft Entra ID）无密钥认证
- **网页搜索**：搜索引擎选择（Google / Bing / DuckDuckGo / 百度 / MCP），触发模式与意图分析
- **录音与音频**：MP3 码率、AGC、活动检测阈值、WebRTC 回音消除
- **图片 / 视频生成**：模型、分辨率、时长等参数
- **洞察与复盘**：Prompt 自定义、预设按钮编辑、自动洞察触发
- **配置导入导出**：一键导出/导入全量配置，方便迁移

---

## 📝 内容更新（关于/帮助）

应用内“关于/帮助”内容来自 Markdown：

- 优先读取可执行文件同目录的 `About.md` / `Help.md`
- 外部文件不存在时回退到应用内置资源

如果你希望发布后可直接改文案（不重编译），可以在打包脚本里加 `-CopyExternalDocs`。

---

## 📚 相关文档

- 详细说明与依赖版本/构建说明/平台限制：见 [PROJECT_DETAILS.md](Assets/PROJECT_DETAILS.md)
- 发布说明：见 [RELEASE_NOTES.md](Assets/RELEASE_NOTES.md)

---

## 📝 许可证

MIT，见 [LICENSE](LICENSE)

## 🙏 致谢

- [Avalonia UI](https://avaloniaui.net/)
- [Azure Speech Services](https://learn.microsoft.com/azure/ai-services/speech-service/)
- [Azure OpenAI Service](https://learn.microsoft.com/azure/ai-services/openai/)
- [FluentAvalonia](https://github.com/amwx/FluentAvalonia)
- [CommunityToolkit.Mvvm](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/)
- [NAudio](https://github.com/naudio/NAudio)
- [SmartReader](https://github.com/nickreynke/SmartReader)
