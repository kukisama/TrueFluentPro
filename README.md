# 译见 Pro

![译见 Pro Logo](Assets/AppIcon.png)

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![Avalonia 11.3.11](https://img.shields.io/badge/Avalonia-11.3.11-2D9CDB.svg)](https://avaloniaui.net/)
[![Azure Speech](https://img.shields.io/badge/Azure-Speech-0078D4.svg)](https://learn.microsoft.com/azure/ai-services/speech-service/)

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
- AI 图片与视频生成：通过创作工坊使用 DALL·E / Sora 2 等模型创作内容
- 合规记录：录音前请确认已征得同意，并遵循所在地法律法规

---

## 🧩 功能概览

| 模块 | 说明 |
| --- | --- |
| 🎙️ 实时翻译（Live） | 实时识别/翻译、中间结果与最终结果并行显示、语言自动检测 |
| 🎧 音频输入 | 麦克风 / 设备选择 / 回环（Loopback）/ 回环+麦克风混合模式 |
| 🪟 浮动字幕 | 置顶显示，可调透明度与字号，适配边看边译场景 |
| 🧠 复盘与洞察（Review） | AI 分析会话，提炼摘要、行动项、风险，支持预设与自动洞察 |
| 📦 批量字幕 | 基于 Azure Speech Batch API 批量生成 SRT/VTT |
| 🎨 创作工坊 | AI 图片与视频生成（DALL·E / Sora 2），支持参考图与图生视频 |
| ⚙️ 配置中心 | 多订阅管理、Azure 中国区支持、AI 后端配置、录音与音频处理参数 |

### 🎙️ 实时翻译（Live 模式）

### 实时翻译（Live 模式）

- 实时识别/翻译：中间结果与最终结果并行呈现，支持语言自动检测
- 多输入源：麦克风 / 设备选择 / 系统回环（Loopback），支持回环+麦克风混合模式
- 浮动字幕：置顶、可调透明度与字号，不打断当前工作
- 会话录音：与字幕同步留存 WAV，停止后自动转 MP3
- 历史记录：按会话积累文本，方便后续整理

### 🧠 复盘洞察与批量字幕（Review 模式）

- 批量字幕生成：基于 Azure Speech Batch API，对录音文件批量生成 SRT/VTT 字幕
- AI 复盘：对会话内容进行 AI 分析，提炼摘要、行动项、风险等
- 预设洞察按钮：会议总结、知识点提取、客诉识别、行动项提取、情绪分析等
- 自动洞察模式：定时或基于新数据触发，持续输出分析结果
- Azure Blob Storage 集成：批处理音频与结果的云端存储

### 🎨 创作工坊

- AI 图片生成：支持 OpenAI / Azure OpenAI 后端，可挂载参考图（最多 8 张），内置裁剪工具
- AI 视频生成：支持 Sora 2 等模型，支持图生视频，可配置分辨率与时长
- 多会话管理：独立会话持久化，支持重命名、复制、删除

### ⚙️ 配置中心

- 多订阅管理：支持添加、验证、测速多个 Azure Speech 订阅
- 中国区 Azure 支持：兼容 `.azure.cn` 终结点
- AI 后端配置：OpenAI 兼容 / Azure OpenAI，支持 AAD（Microsoft Entra ID）认证
- 录音与音频处理：MP3 码率、自动增益（AGC）等高级参数
- 配置持久化：一次设置，随时可用

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
