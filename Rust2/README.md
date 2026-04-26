# 译见 Pro — Rust 改版 (Tauri 2)

基于 Tauri 2 + React 19 + Rust 后端的跨平台桌面应用，是原 C#/Avalonia 版本的完整重写。

## 技术栈

| 层 | 技术 | 版本 |
|---|---|---|
| 前端框架 | React + TypeScript | 19 / 5.6 |
| 构建 | Vite | 6 |
| UI 组件 | Radix UI + Lucide Icons + Framer Motion | - |
| 样式 | Tailwind CSS + CSS Variables 主题 | 3.4 |
| 状态管理 | Zustand | 5 |
| 国际化 | i18next + react-i18next | 26 / 17 |
| 桌面框架 | Tauri | 2 |
| 后端语言 | Rust (tokio async) | 2021 edition |
| HTTP | reqwest (json + stream) | 0.12 |
| 数据库 | rusqlite (bundled SQLite) | 0.32 |

## 架构概览

```
┌────────────────────────────────────┐
│         React 前端 (Vite)          │
│  8 Views · Radix UI · Zustand     │
├────────────────────────────────────┤
│       Tauri IPC (invoke)           │
├────────────────────────────────────┤
│         Rust 后端                   │
│  commands.rs ─ 所有 IPC 命令入口    │
│  models.rs   ─ 数据模型            │
│  state.rs    ─ AppState 全局状态    │
│  storage.rs  ─ SQLite 持久化       │
│  providers/  ─ AI Provider 插件    │
└────────────────────────────────────┘
```

### 前端 Views (8 个)

| View | 说明 | 实现程度 |
|---|---|---|
| SettingsView | 12 标签页设置（端点 CRUD、厂商模板、模型发现、连通性测试） | ✅ 完整 |
| MediaStudioView | AI 出图（文生图 / 图生图） | ✅ 基本功能 |
| LiveTranslationView | 实时语音翻译 | ✅ 完整（Speech 端点选择 + SDK 实时识别 + 事件流） |
| BatchProcessingView | 批量翻译任务 | 🔲 纯 UI 骨架 |
| AudioLabView | 语音工作室（TTS / STT） | 🔲 纯 UI 骨架 |
| TaskMonitorView | 后台任务监控 | 🔲 纯 UI 骨架 |
| AuthView | AAD 登录 | 🔲 纯 UI 骨架 |
| AboutView | 关于 / 帮助 | ✅ |

### Rust Provider 系统

| Provider | 能力 | 状态 |
|---|---|---|
| OpenAiChatProvider | AI 文本补全（流式 SSE） | ✅ |
| OpenAiImageProvider | 图片生成 | ✅ |
| AzureSpeechProvider | 实时语音翻译（连续识别 + 回调 → 事件流） | ✅ |
| Azure Speech (TTS/STT) | 语音合成 / 单次识别 | 🔲 未实现 |
| Azure Translator | 文本翻译 | 🔲 未实现 |
| DeepL / Google / 腾讯 | 翻译 | 🔲 未实现 |

### 端点测试系统（已完成，对标 C# 版 EndpointBatchTestService）

- 4 个内置厂商模板（Azure OpenAI / APIM 网关 / OpenAI 兼容 / Azure Speech）
- AI 端点：按模型 × 能力 逐项测试（Text / Image / STT）
- Speech 端点：通过 token issue API 测试订阅密钥+区域连通性
- 详细错误解析（OpenAI error format / 通用 JSON / 纯文本）
- 模型自动发现（`/v1/models`、`/models`、Azure 部署列表）

## 已实现功能

- ✅ **端点管理**：增删改查，厂商模板快速配置，模型列表管理，能力标签
- ✅ **Speech 与 AI 端点分离**：Speech 端点独立表单（订阅密钥+区域+终结点），不强制要求模型列表
- ✅ **端点连通性测试**：AI 端点按模型逐能力探测，Speech 端点 token issue 认证测试
- ✅ **模型自动发现**：探测多个候选 URL，解析 OpenAI / Azure 两种格式
- ✅ **实时语音翻译**：Speech 端点选择器 + Azure Speech SDK FFI 连续识别 + 翻译事件流推送
- ✅ **AI 文本补全**：流式 SSE 响应，支持 Azure OpenAI 和兼容端点
- ✅ **图片生成**：文生图，支持 DALL·E 和兼容模型
- ✅ **主题系统**：CSS Variables 暗色/亮色切换，玻璃拟态风格
- ✅ **设置页面**：12 标签页（通用、外观、快捷键、翻译、端点、语音、AudioLab、图片、通知、隐私、高级、实验性）
- ✅ **SQLite 持久化**：配置、端点、翻译历史、批量任务
- ✅ **i18n 框架**：i18next 已接入，中/英 locale 基础 key 已配

## 未实现 / 待完成

- 🔲 批量翻译任务执行引擎
- 🔲 Azure Storage 接入（批量转写๾ Blob 存储）
- 🔲 TTS / STT Provider
- 🔲 Azure Translator / DeepL / Google / 腾讯翻译 Provider
- 🔲 设置各分区 config 双向绑定（当前识别/存储/音频/AI洞察/图片等分区为静态 UI）
- 🔲 AAD 登录流程
- 🔲 视频生成（Sora）
- 🔲 音频实验室（多阶段处理流水线）
- 🔲 任务监控面板的真实数据联动
- 🔲 i18n locale key 全覆盖
- 🔲 自动更新
- 🔲 崩溃日志上报
- 🔲 commands.rs 拆分（当前 ~880 行，计划按领域拆分为 config/translation/media/testing）

## 快速开始

### 前置条件

- [Node.js](https://nodejs.org/) 18+
- [Rust](https://rustup.rs/) 最新 stable
- Windows: MSVC Build Tools（Visual Studio Installer 勾选「C++ 桌面开发」）
- 可选：LLVM（用于 speech-sdk bindgen 自动生成绑定，不装也能用预生成绑定）
- 推荐：`lld-link`（见下方"加速编译"）

### 开发运行

```powershell
cd Rust2
npm install          # 安装前端依赖（首次）
# 设置环境变量跳过 speech-sdk bindgen（无需安装 LLVM）
$env:MS_COG_SVC_SPEECH_SKIP_BINDGEN="1"
npx tauri dev        # 启动开发模式（前端热重载 + Rust 增量编译）
```

> 首次编译会自动从 NuGet 下载 Azure Speech SDK (~70MB)，后续增量编译不需要。

### 生产打包

```powershell
npx tauri build      # 产出安装包在 src-tauri/target/release/bundle/
```

> ⚠️ Release 构建启用了 `lto = true` + `codegen-units = 1`，编译时间会很长（5-15 分钟）。这是刻意的优化配置，为了最小化二进制体积和最大化运行性能。**日常开发不要使用 `--release`**。

## 编译加速指南（开发期）

默认配置下首次 `cargo build` 在 Windows 上可能需要 3-8 分钟，增量编译约 1-5 秒。以下优化可以将首次编译缩短至 1-3 分钟：

### 1. 使用 lld-link 替代默认链接器

编辑 `src-tauri/.cargo/config.toml`，取消注释即可启用。前提是安装 LLVM 工具链：

```powershell
rustup component add llvm-tools
```

然后取消 `config.toml` 中的注释：

```toml
[target.x86_64-pc-windows-msvc]
linker = "lld-link"
```

如果 `lld-link` 不可用导致编译失败，重新注释掉即可回退到默认链接器。

### 2. Cargo.toml 已有的优化空间

当前 `crate-type = ["staticlib", "cdylib", "rlib"]` 会编译 3 种输出格式。桌面端只需要 `cdylib` + `rlib`，`staticlib` 是给 iOS 用的。如果不需要 iOS 支持可以去掉：

```toml
crate-type = ["cdylib", "rlib"]
```

### 3. 可选：依赖项 dev 优化

在 `Cargo.toml` 末尾添加，让依赖库在 dev 模式也做基本优化（减少运行时开销，编译速度影响很小）：

```toml
[profile.dev.package."*"]
opt-level = 1
```

### 4. 编译耗时大户

| 依赖 | 原因 | 备注 |
|---|---|---|
| `rusqlite` (bundled) | 从源码编译 SQLite C 库 (~15 万行) | 只在首次/clean 后编译 |
| `reqwest` + TLS | hyper + rustls 依赖链很长 | 增量编译不受影响 |
| `tauri` | 框架本体 + WebView 绑定 | 首次编译大头 |
| `tokio` (full) | 拉满所有 feature | 可精简为实际用到的 feature |

## 类型检查 & 验证

```powershell
# 前端 TypeScript 类型检查（不产出文件，只检查错误）
npx tsc --noEmit

# Rust 编译检查（不链接，最快）
cd src-tauri && cargo check

# Rust 完整编译
cd src-tauri && cargo build

# 两步联合验证（推荐改完代码后执行）
npx tsc --noEmit; cd src-tauri; cargo build; cd ..
```

## 目录结构

```
Rust2/
├── src/                          # React 前端
│   ├── App.tsx                   # 根组件
│   ├── main.tsx                  # 入口
│   ├── index.css                 # 全局样式 + CSS Variables 主题
│   ├── components/
│   │   ├── ui.tsx                # 通用 UI 组件库（Button/Badge/Card/Input/...）
│   │   └── AppLayout.tsx         # 应用布局（侧栏 + 内容区）
│   ├── lib/
│   │   ├── tauri-api.ts          # Tauri IPC 类型定义 + 调用封装
│   │   └── utils.ts              # 工具函数
│   ├── stores/
│   │   ├── app-store.ts          # 应用全局状态 (Zustand)
│   │   └── theme-store.ts        # 主题状态
│   └── views/                    # 8 个页面视图
│       ├── SettingsView.tsx
│       ├── MediaStudioView.tsx
│       ├── LiveTranslationView.tsx
│       ├── BatchProcessingView.tsx
│       ├── AudioLabView.tsx
│       ├── TaskMonitorView.tsx
│       ├── AuthView.tsx
│       └── AboutView.tsx
├── src-tauri/                    # Rust 后端
│   ├── Cargo.toml
│   ├── .cargo/config.toml        # 链接器加速配置
│   └── src/
│       ├── lib.rs                # Tauri 入口 + Provider 注册
│       ├── main.rs               # main()
│       ├── commands.rs           # IPC 命令（配置/翻译/AI/端点测试/模型发现）
│       ├── models.rs             # 数据模型（AiEndpoint/AiModelEntry/VendorProfile/...）
│       ├── state.rs              # AppState（RwLock<Config> + Provider Registry）
│       ├── storage.rs            # SQLite 数据库操作
│       └── providers/
│           ├── mod.rs            # trait 定义
│           ├── registry.rs       # Provider 注册表
│           ├── openai_chat.rs    # OpenAI 兼容文本补全（流式 SSE）
│           ├── openai_image.rs   # OpenAI 兼容图片生成
│           └── azure_speech.rs   # Azure Speech 实时语音翻译（speech-sdk FFI）
├── package.json
├── vite.config.ts
├── tailwind.config.js
├── tsconfig.json
└── index.html
```

## 与 C# 版的对应关系

| C# 版 | Rust 改版 | 备注 |
|---|---|---|
| Avalonia + FluentAvalonia | React + Radix UI + Tailwind | 完全重写 UI |
| CommunityToolkit.Mvvm | Zustand + React hooks | MVVM → 函数式 |
| AiEndpoint.cs | models.rs `AiEndpoint` | 结构对齐（含 Speech 专属字段） |
| EndpointBatchTestService | commands.rs `test_endpoint` | 已对标 |
| EndpointProfile (4 厂商) | commands.rs `get_vendor_profiles` | 已对标 |
| ModelDiscoveryService | commands.rs `discover_models` | 已对标 |
| AiCompletionService | providers/openai_chat.rs | 流式 SSE |
| AiImageGenService | providers/openai_image.rs | 基础功能 |
| ConfigurationService | storage.rs + state.rs | SQLite |
| SpeechTranslationService | providers/azure_speech.rs + LiveTranslationView | ✅ 前后端全链路 |
| BatchProcessingViewModel | — | 🔲 未移植 |

## License

见根目录 LICENSE 文件。

## 架构规范（项目公约）

### 端点模型规范
- `AiEndpoint` 统一承载 AI 端点和 Speech 端点，通过 `endpoint_type` 区分
- Speech 端点使用专属字段 `speech_subscription_key / speech_region / speech_endpoint`，不复用 `url / api_key`
- UI 表单根据 `endpoint_type` 分支渲染，Speech 不显示模型列表和 URL
- 保存验证：AI 要求 `url + api_key + models >= 1`；Speech 要求 `subscription_key + region`
- 端点测试按类型走不同逻辑：AI 走 HTTP 请求；Speech 走 token issue API

### Provider trait 规范
- 6 个 trait slot: `TextTranslationSlot / RealtimeSpeechSlot / SpeechToTextSlot / TextToSpeechSlot / AiCompletionSlot / ImageGenSlot`
- 新 Provider 实现 trait → 在 `lib.rs::register_providers_from_config` 中注册
- Provider 通过 `ProviderRegistry` 集中管理，按 endpoint ID 查找

### 做与不做
- **做**: 小步修改，每次改完 `cargo build` + `npx tsc --noEmit` 通过
- **做**: 新增域按 commands 子模块拆分（config / translation / media / testing）
- **不做**: 不在 frontend 直接调 HTTP API（所有请求经过 Tauri IPC → Rust 后端）
- **不做**: 不在 View 层直接操作 Provider（View → api.invoke → commands → Provider）
