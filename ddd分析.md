# TrueFluentPro C# → Rust2 迁移 DDD 分析

> 分析日期: 2026-04-27  
> 分析范围: C# 主程序（排除 Rust2/rustbackend/rustnew-backend/backend）

---

## 一、NDepend 式代码复杂度与依赖分析

### 1.1 总体规模

| 指标 | 数值 |
|------|------|
| C# 主程序总文件数 | **375** |
| C# 主程序总行数 | **74,882** |
| Rust2 已迁移行数 | **5,709**（Rust后端）+ **~6,200**（TypeScript前端） |
| **迁移覆盖率** | **~15.9%** |

### 1.2 各层规模分布

| 层 | 文件数 | 行数 | 占比 | 说明 |
|----|--------|------|------|------|
| **Services** | 152 | 28,243 | 37.7% | 核心业务逻辑 + 基础设施 |
| **ViewModels** | 44 | 22,971 | 30.7% | UI 状态管理 + 部分业务逻辑下沉 |
| **Views** | 71 | 12,848 | 17.2% | AXAML UI 定义 + code-behind |
| **Controls** | 35 | 5,016 | 6.7% | 自定义控件 |
| **Models** | 60 | 3,719 | 5.0% | 领域实体 / DTO |
| **Helpers** | 4 | 351 | 0.5% | 工具函数 |
| **Styles** | 3 | 312 | 0.4% | 样式资源 |

### 1.3 Services 子模块拆分

| 子目录 | 文件数 | 行数 | 领域归属 |
|--------|--------|------|----------|
| Services/Storage | 25 | 3,745 | 通用子域 — 持久化 |
| Services/Audio | 20 | 2,586 | 通用子域 — 音频设备/录音 |
| Services/EndpointProfiles | 8 | 1,665 | 核心域 — 终结点管理 |
| Services/EndpointTesting | 3 | 1,351 | 核心域 — 终结点管理 |
| Services/WebSearch | 14 | 1,389 | 通用子域 — 搜索 |
| Services/ImagePipeline | 8 | 453 | 核心域 — 媒体生成 |
| Services/Cloud | 6 | 398 | 通用子域 — 云模式 |
| Services/Speech | 1 | 283 | 核心域 — 语音合成 |
| Services/（根级） | 67 | ~16,373 | 混合 — AI服务/翻译/批处理/配置 |

### 1.4 圈复杂度热点（Top 16）

> 文件行数 ≥ 500 行即标为热点，红色 = God Class / 需重构后迁移

| 行数 | 文件 | 风险 | 问题 |
|------|------|------|------|
| **3,674** | ViewModels/MediaSessionViewModel.cs | 🔴 极高 | **God Class** — 聊天/图片/视频/搜索/计费全混合 |
| **3,283** | ViewModels/BatchProcessingViewModel.cs | 🔴 极高 | **God Class** — 批量转录+复盘+字幕+包管理 |
| **2,632** | ViewModels/MediaCenterV2ViewModel.cs | 🔴 高 | 工作区+会话管理+SQLite 持久化混入 VM |
| **1,959** | ViewModels/AudioLabViewModel.cs | 🟡 中 | 会话管理+多阶段 AI 调用 |
| **1,403** | Services/SpeechTranslationService.cs | 🟡 中 | SDK 回调+音频设备+文件写入+断线重连 |
| **1,260** | Services/EndpointBatchTestService.cs | 🟡 中 | 多能力并行测试 |
| **1,208** | Services/EndpointProfileUrlBuilder.cs | 🟢 低 | URL 模板展开（纯逻辑，虽长但清晰） |
| **1,202** | Services/AiImageGenService.cs | 🟡 中 | 多路由探测 + V1/V2 双模式 |
| **1,178** | Controls/Markdown/MarkdownRenderer.cs | 🟡 中 | 复杂 UI 渲染（Rust侧由前端处理） |
| **1,059** | Services/OpenAiRealtimeTranslationService.cs | 🟡 中 | WebSocket 全双工协议 |
| **1,009** | ViewModels/MediaStudioViewModel.cs | 🟡 中 | 会话列表 + LRU 缓存 |
| **998** | ViewModels/MainWindowViewModel.cs | 🟡 中 | 应用壳 + 导航 |
| **886** | Services/AiVideoGenService.cs | 🟡 中 | 异步轮询 + 多路由 |
| **827** | Services/AiInsightService.cs | 🟡 中 | 流式 SSE + 多协议 |
| **821** | Services/AudioTaskStageHandlerService.cs | 🟡 中 | 8 个阶段分派 |
| **622** | Models/AzureSpeechConfig.cs | 🟡 中 | **配置巨石** — 全局配置根对象 |

### 1.5 NuGet 依赖分类

#### Avalonia UI 框架（迁移时完全替换 → Tauri + React）

| 包 | 版本 |
|----|------|
| Avalonia | 11.3.11 |
| Avalonia.Desktop | 11.3.11 |
| Avalonia.Themes.Fluent | 11.3.11 |
| Avalonia.Fonts.Inter | 11.3.11 |
| Avalonia.Diagnostics | 11.3.11 |
| Avalonia.AvaloniaEdit | — |
| FluentAvaloniaUI | — |
| Projektanker.Icons.Avalonia.FontAwesome | — |
| Svg.Controls.Skia.Avalonia | — |

#### Azure SDK / 认证（需在 Rust 找等价 crate）

| 包 | 版本 | Rust 等价 |
|----|------|-----------|
| Microsoft.CognitiveServices.Speech | 1.48.2 | speech-sdk FFI crate（已有） |
| Microsoft.CognitiveServices.Speech.Extension.MAS | — | 需扩展 FFI |
| Azure.Identity | 1.13.2 | azure_identity crate |
| Azure.Storage.Blobs | 12.22.2 | azure_storage_blobs crate |
| Microsoft.Identity.Client | 4.83.3 | oauth2 / msal-rs |

#### 数据 / 序列化

| 包 | Rust 等价 |
|----|-----------|
| Microsoft.Data.Sqlite | rusqlite（已用） |
| Newtonsoft.Json | serde_json（已用） |

#### 音频处理

| 包 | Rust 等价 |
|----|-----------|
| NAudio 2.2.1 | cpal + hound |
| NAudio.Lame 2.1.0 | lame-sys / mp3lame-encoder |
| SoundFlow.Extensions.WebRtc.Apm | webrtc-audio-processing |

#### 文本处理 / 渲染

| 包 | Rust 等价方案 |
|----|---------------|
| Markdig | 前端 React Markdown 组件（已迁移） |
| ReverseMarkdown | comrak 或前端处理 |
| AngleSharp | scraper crate |
| SmartReader | readability crate |
| PuppeteerSharp | chromiumoxide 或 headless_chrome |
| SkiaSharp | image crate 或 skia-safe |

#### MVVM / DI

| 包 | Rust 等价 |
|----|-----------|
| CommunityToolkit.Mvvm | Tauri commands + Zustand store（已迁移） |
| Microsoft.Extensions.DependencyInjection | Rust provider registry（已迁移） |

#### Windows 专属

| 包 | Rust 等价 |
|----|-----------|
| Vortice.MediaFoundation | ffmpeg-next |

### 1.6 框架耦合度分析

| 耦合度 | 范围 | 行数估计 | 占比 |
|--------|------|----------|------|
| 🔴 **强耦合 Avalonia**（必须重写） | Views + Controls + ViewModels + 部分 Models | ~40,835 | 54.5% |
| 🟡 **轻耦合**（逻辑可复用，需适配接口） | Services 根级 AI 服务 + 部分 Models | ~20,800 | 27.8% |
| 🟢 **完全框架无关**（可直接移植逻辑） | Storage + 纯 Models + Audio 处理 + 计费 | ~13,247 | 17.7% |

强耦合点明细：
- 所有 ViewModels 继承 `ObservableObject`，使用 `Dispatcher.UIThread`
- `AiEndpoint` / `TranslationItem` / `BatchTaskItem` 等 Models 实现 `INotifyPropertyChanged`
- `EndpointProfileCatalogService` 使用 `Avalonia.Platform.AssetLoader`
- `FloatingSubtitleManager` / `FloatingInsightManager` 直接操作 Avalonia Window
- `TaskEventBus` 使用 `Dispatcher.UIThread` 调度事件

---

## 二、DDD 限界上下文识别

### 2.1 领域地图

```
╔══════════════════════════════════════════════════════════════════════════╗
║                     TrueFluentPro 领域地图                              ║
╠══════════════════════════════════════════════════════════════════════════╣
║                                                                        ║
║  ┌─────────────────────────────────────────────────────────────┐       ║
║  │                    核心域 (Core Domain)                      │       ║
║  │  优先迁移 — 这些是产品核心竞争力                              │       ║
║  │                                                              │       ║
║  │  ① 终结点管理 (Endpoint Management)           3,016 行       │       ║
║  │  ② 实时语音翻译 (Realtime Speech Translation)  2,462 行      │       ║
║  │  ③ 创作工坊 / 媒体生成 (Media Studio)         2,541 行       │       ║
║  │  ④ 听析中心 / 音频分析 (Audio Lab)            1,256 行       │       ║
║  └─────────────────────────────────────────────────────────────┘       ║
║                                                                        ║
║  ┌─────────────────────────────────────────────────────────────┐       ║
║  │                   支撑子域 (Supporting Subdomain)             │       ║
║  │  可暂不迁移 — 依赖核心域，可在核心域完成后逐步迁移            │       ║
║  │                                                              │       ║
║  │  ⑤ 批处理中心 (Batch Processing)              ~2,800 行      │       ║
║  │  ⑥ AI 洞察/对话 (AI Insight & Chat)            ~827 行       │       ║
║  │  ⑦ 计费审计 (Billing Audit)                    ~600 行       │       ║
║  │  ⑧ 网页搜索 / 搜索代理 (Web Search)           1,389 行       │       ║
║  └─────────────────────────────────────────────────────────────┘       ║
║                                                                        ║
║  ┌─────────────────────────────────────────────────────────────┐       ║
║  │                   通用子域 (Generic Subdomain)                │       ║
║  │  可重用库 — 框架无关，可作为独立模块移植                      │       ║
║  │                                                              │       ║
║  │  ⑨ 配置管理 (Configuration)                    ~622 行       │       ║
║  │  ⑩ 持久化 / 存储 (Storage / SQLite)           3,745 行       │       ║
║  │  ⑪ 音频设备 / 录音 (Audio Device & Recording) 2,586 行       │       ║
║  │  ⑫ 云模式 (Cloud SaaS)                         ~398 行       │       ║
║  │  ⑬ 认证 (AAD / MSAL)                           ~381 行       │       ║
║  │  ⑭ 文本处理 (Markdown / Subtitle Parsing)      ~600 行       │       ║
║  └─────────────────────────────────────────────────────────────┘       ║
║                                                                        ║
╚══════════════════════════════════════════════════════════════════════════╝
```

### 2.2 核心域详细分析（优先迁移）

#### ① 终结点管理 (Endpoint Management)

| 维度 | 内容 |
|------|------|
| **聚合根** | `AiEndpoint` |
| **实体/值对象** | `EndpointProfileDefinition`, `EndpointProfileCapabilities`, `EndpointProfileDefaults`, `EndpointPolicySchemaModels`, `AiModelEntry`, `ModelReference`, `SpeechResource`, `EndpointTemplateDefinition`, `EndpointInspectionDetails` |
| **关键服务** | `EndpointProfileCatalogService` (资料包目录), `EndpointProfileRuntimeResolver` (运行时解析), `EndpointProfileUrlBuilder` (URL模板展开), `EndpointCapabilityPolicyResolver`, `ModelRuntimeResolver`, `EndpointBatchTestService`, `EndpointTemplateService` |
| **代码行数** | Models: ~300 + Services: ~3,016 = **~3,316** |
| **Rust2 迁移状态** | ✅ `profile_loader.rs`(584行) + `registry.rs`(240行) + `commands.rs`(部分) — 基础已建立 |
| **迁移难度** | 🟢 低 — 纯逻辑，JSON 解析 + URL 模板，已有基础 |
| **核心理由** | 所有 AI 能力的入口和路由中枢，其他子域全部依赖此域来发现可用的模型和构建 API URL |

**依赖关系**：
```
AiEndpoint ──→ EndpointProfileDefinition (vendor 行为模板)
           ──→ EndpointProfileCapabilities (能力声明)
           ──→ ModelRuntimeResolver (模型选择)
           ──→ EndpointProfileUrlBuilder (URL 构建)
           ──→ EndpointBatchTestService (连通性验证)
```

#### ② 实时语音翻译 (Realtime Speech Translation)

| 维度 | 内容 |
|------|------|
| **聚合根** | `TranslationItem` |
| **实体/值对象** | `RealtimeConnectionSpec`, `SubtitleCue`, `AudioDeviceInfo`, `RecordingMode`, `AudioSourceMode` |
| **关键服务** | `SpeechTranslationService` (MS SDK 桥接, 1403行), `OpenAiRealtimeTranslationService` (WebSocket, 1059行), `RealtimeTranslationServiceFactory`, `RealtimeConnectionSpecResolver`, `SpeechResourceRuntimeResolver` |
| **代码行数** | **~2,462** |
| **Rust2 迁移状态** | ⚠️ `azure_speech.rs`(273行) — FFI 桩已建立，但 OpenAI Realtime 未开始 |
| **迁移难度** | 🔴 高 — 双通道(Speech SDK FFI + WebSocket)，音频流实时处理，断线重连 |
| **核心理由** | 产品标志性功能——实时语音翻译+浮窗字幕，直接决定用户体验 |

**双通道架构**：
```
RealtimeTranslationServiceFactory
    ├── SpeechTranslationService ──→ MS Speech SDK (FFI/DLL)
    │       └── TranslationRecognizer ──→ 回调 → 事件 → UI
    └── OpenAiRealtimeTranslationService ──→ WebSocket
            └── Realtime API (gpt-4o-realtime) ──→ JSON 事件 → UI
```

#### ③ 创作工坊 / 媒体生成 (Media Studio)

| 维度 | 内容 |
|------|------|
| **聚合根** | `MediaGenSession` |
| **实体/值对象** | `MediaGenTask`, `MediaGenConfig`, `MediaChatMessage`, `MediaAssetRecord`, `MediaStudioIndex`, `MediaFileItem`, `AspectRatioPreset`, `ImageModelCapabilities`, `VideoCapabilityProfile`, `VideoApiMode` |
| **关键服务** | `AiImageGenService` (1202行), `AiVideoGenService` (886行), `AiMediaServiceBase` (322行, 抽象基类), `ImagePipelineRunner` + Steps (453行), `FileIdCache`, `VideoCapabilityResolver`, `ImageCropService`, `ImageModelCatalogService` |
| **代码行数** | **~2,863 + Pipeline 453 = ~3,316** |
| **Rust2 迁移状态** | ⚠️ `openai_image.rs`(213行) + `image_pipeline/`(176行) — 基础框架有，但图片编辑/视频未完成 |
| **迁移难度** | 🟡 中 — 纯 HTTP，但涉及 multipart 上传、SSE 流式、轮询、多路由策略 |
| **核心理由** | AI 图片/视频生成是产品第二大功能模块，用户交互最频繁 |

**图片管线架构**：
```
ImagePipelineRunner
    ├── RouteStep      → 选择 API 路由 (v1/images vs Responses API)
    ├── UploadStep      → 上传参考图到 /v1/files
    ├── BuildRequestStep → 构建请求体
    ├── ExecuteStep     → 发送 HTTP 请求
    └── LandStep        → 解析结果、存储资产
```

#### ④ 听析中心 / 音频分析 (Audio Lab)

| 维度 | 内容 |
|------|------|
| **聚合根** | `AudioTaskRecord` |
| **实体/值对象** | `AudioLifecycleStage`, `AudioLabStagePreset`, `AudioLabStagePresetDefaults`, `AudioFileProcessingSnapshot`, `TranscriptSegment`, `StageContentState`, `AudioTaskDependencies`, `TaskExecutionRecord`, `TaskStagingEntry` |
| **关键服务** | `AudioLifecyclePipelineService`, `AudioTaskExecutor` (435行), `AudioTaskQueueService`, `AudioTaskStageHandlerService` (821行), `TaskEventBus`, `AiAudioTranscriptionService` (578行) |
| **代码行数** | **~1,834 + 转录578 = ~2,412** |
| **Rust2 迁移状态** | ⚠️ `task_engine.rs`(360行) — 队列框架已有，阶段处理器未完成 |
| **迁移难度** | 🟡 中 — 8个阶段的管道模式，依赖转录+AI+TTS |
| **核心理由** | 端到端音频处理管线（转录→翻译→审校→TTS），差异化功能 |

**8 阶段管道**：
```
AudioTaskRecord 生命周期:
  Idle → Transcribing → Translating → Reviewing → 
  SpeechSynthesizing → Mixing → Exporting → Completed
  
AudioTaskStageHandlerService 为每个阶段分派不同的 AI 服务
```

---

### 2.3 支撑子域详细分析（可暂不迁移）

#### ⑤ 批处理中心 (Batch Processing)

| 维度 | 内容 |
|------|------|
| **聚合根** | `BatchTaskItem` |
| **实体/值对象** | `BatchQueueItem`, `BatchPackageItem`, `BatchSubtaskItem`, `BatchBucketNavItem`, `ReviewSheetPreset`, `ProcessingDisplayState` |
| **关键服务** | `BatchPackageStateService`, `BatchTranscriptionParser`, `SpeechBatchApiClient`, `SpeechFastTranscriptionClient`, `BatchLogService` |
| **ViewModel** | `BatchProcessingViewModel` (**3,283行 God Class**) |
| **暂缓理由** | 强依赖听析中心(④)和转录服务，ViewModel 需大规模重构拆分后再迁移 |

#### ⑥ AI 洞察 / 对话 (AI Insight & Chat)

| 维度 | 内容 |
|------|------|
| **关键服务** | `AiInsightService` (827行), `SearchAgentService` |
| **Rust2 迁移状态** | ✅ `openai_chat.rs`(290行) — 基础对话已实现 |
| **暂缓理由** | 基础功能已在 Rust2 实现，高级功能（搜索增强、引用面板）可后补 |

#### ⑦ 计费审计 (Billing Audit)

| 维度 | 内容 |
|------|------|
| **聚合根** | `BillingLedgerEntry` |
| **关键服务** | `BillingTiersService`, `ImageBillingHelper`, `BillingLedgerWriter` |
| **暂缓理由** | 纯计算逻辑但需要完整的媒体生成链路才有意义 |

#### ⑧ 网页搜索 (Web Search)

| 维度 | 内容 |
|------|------|
| **实现** | 6 个搜索引擎 Provider（Bing/Google/Baidu/DuckDuckGo/MCP/BingNews）+ `IntentAnalysisService` + `WebPageFetcher` + `EdgeHeadlessBrowser` |
| **暂缓理由** | 辅助功能，依赖 PuppeteerSharp（需替换为 Rust headless browser） |

---

### 2.4 通用子域详细分析（可重用库）

#### ⑨ 配置管理 (Configuration)

| 维度 | 内容 |
|------|------|
| **核心类** | `AzureSpeechConfig` (**622行**, 全局配置根), `AiConfig`, `MediaGenConfig`, `SpeechAdvancedOptions` |
| **服务** | `ConfigurationService` (JSON 文件持久化) |
| **Rust2 状态** | ✅ `models.rs`(825行) 中已有 `AppConfig` — 需拆分对齐 |
| **迁移策略** | Rust 侧应将 `AzureSpeechConfig` 拆分为多个 Config Section，避免巨石 |

#### ⑩ 持久化 / 存储 (Storage)

| 维度 | 内容 |
|------|------|
| **基础设施** | `SqliteDbService` (638行, Schema 迁移), `StoragePathResolver`, `SqliteExtensions` |
| **Repository 层** | `CreativeSessionRepository`, `SessionContentRepository`, `SessionMessageRepository`, `AudioLibraryRepository`, `AudioLifecycleRepository`, `AudioTaskRepository`, `TranslationHistoryRepository`, `BillingLedgerWriter`, `TaskExecutionRepository`, `TaskStagingWriter`, `LegacyImportService` |
| **代码行数** | **3,745** |
| **Rust2 状态** | ✅ `storage.rs`(720行) — 基础表已有，Repository 拆分待补 |
| **迁移策略** | 可作为独立 Rust module，表结构直接对齐 C# Schema |

#### ⑪ 音频设备 / 录音 (Audio Device & Recording)

| 维度 | 内容 |
|------|------|
| **核心组件** | `WasapiPcm16AudioSource` (637行), `HighQualityRecorder` (610行), `AudioProcessingCoordinator`, `AudioDeviceEnumerator` |
| **处理管线** | `MasAudioPipeline`, `WebRtcApmPreProcessor`, `AutoGainProcessor`, `VadGateController`, `Pcm16AudioMixer` |
| **代码行数** | **2,586** |
| **Rust 等价** | cpal (采集) + webrtc-audio-processing (APM) + hound (WAV) + mp3lame-encoder (MP3) |
| **迁移策略** | Windows 专属 WASAPI 采集需用 cpal 跨平台替代 |

#### ⑫ 云模式 (Cloud SaaS)

| 维度 | 内容 |
|------|------|
| **核心类** | `ServiceModeManager`, `CloudApiClient`, `CloudAuthService`, `CloudSettings`, `ServiceMode` |
| **代码行数** | **~398** |
| **迁移策略** | 轻量，可在 Rust 中作为独立 module 实现 |

#### ⑬ 认证 (AAD / MSAL)

| 维度 | 内容 |
|------|------|
| **核心类** | `AzureTokenProvider` (381行), `AzureTokenProviderStore`, `AzureSubscriptionValidator` |
| **迁移策略** | 使用 azure_identity crate 或 oauth2 crate 替代 MSAL |

#### ⑭ 文本处理 (Markdown / Subtitle)

| 维度 | 内容 |
|------|------|
| **核心类** | `SubtitleFileParser`, `SubtitleSyncService`, `FastTranscriptionParser`, `TranscriptionDataHelper` |
| **迁移策略** | Markdown 渲染已由 React 前端处理，字幕解析可用 Rust 纯逻辑 |

---

## 三、服务依赖关系图

```
                           ┌────────────────────────┐
                           │    AiMediaServiceBase   │ (抽象基类: HttpClient, Auth, URL构建)
                           └────────┬───────────────┘
                      ┌─────────────┼──────────────────┐
                      ▼             ▼                  ▼
              AiImageGenService  AiVideoGenService  AiAudioTranscriptionService
                      │             │                  │
                      ▼             │                  │
              ImagePipelineRunner   │                  │
              (5-step pipeline)     │                  │
                      │             │                  │
                      └──────┬──────┘                  │
                             ▼                         │
                    ┌─────────────────┐                │
                    │ MediaSession VM │                │
                    │  (God Class)    │                │
                    └─────────────────┘                │
                                                       │
          ┌────────────────────────────────────────────┘
          ▼
  AudioTaskStageHandlerService ──→ AiInsightService
          │                    ──→ SpeechSynthesisService
          ▼
  AudioTaskExecutor ──→ AudioTaskRepository
          │
          ▼
  AudioTaskQueueService ──→ TaskEventBus ──→ UI (Dispatcher)


  RealtimeTranslationServiceFactory
      ├── SpeechTranslationService ──→ MS Speech SDK (FFI)
      │       └── AudioProcessingCoordinator ──→ WasapiPcm16AudioSource
      └── OpenAiRealtimeTranslationService ──→ WebSocket
              └── RealtimeConnectionSpecResolver


  ConfigurationService ←─── (全局被消费)
          │
          ├── MainWindowViewModel (应用壳)
          ├── SettingsViewModel ──→ EndpointProfileCatalogService
          ├── ConfigViewModel     ──→ EndpointBatchTestService
          └── All other VMs


  SqliteDbService (Schema 管理)
      ├── CreativeSessionRepository ←── MediaCenterV2ViewModel
      ├── SessionContentRepository  ←── MediaSessionViewModel
      ├── AudioLibraryRepository    ←── AudioLabViewModel
      ├── TranslationHistoryRepository ←── LiveTranslationView
      ├── BillingLedgerWriter       ←── ImageBillingHelper
      └── TaskExecutionRepository   ←── AudioTaskExecutor


  EndpointProfileCatalogService (JSON 资源)
      └── EndpointProfileRuntimeResolver
              └── EndpointProfileUrlBuilder ←── AiMediaServiceBase
              └── ModelRuntimeResolver      ←── All AI Services
```

---

## 四、迁移优先级矩阵

### 4.1 总览

| 优先级 | 子域 | 行数 | 依赖 | Rust2 现状 | 迁移难度 |
|--------|------|------|------|-----------|---------|
| **P0** | ① 终结点管理 + ⑨ 配置 | ~3,938 | 无外部依赖 | ✅ 70% 完成 | 🟢 低 |
| **P0** | ⑩ SQLite 存储 | ~3,745 | 无外部依赖 | ✅ 40% 完成 | 🟢 低 |
| **P1** | ⑥ AI 对话/洞察 | ~827 | ①⑩ | ✅ 60% 完成 | 🟢 低 |
| **P1** | ③ 媒体生成(图片) | ~1,655 | ①⑩ | ⚠️ 30% 完成 | 🟡 中 |
| **P2** | ③ 媒体生成(视频) | ~886 | ①⑩ | ❌ 未开始 | 🟡 中 |
| **P2** | ⑦ 计费审计 | ~600 | ③ | ❌ 未开始 | 🟢 低 |
| **P3** | ② 实时翻译(Speech SDK) | ~1,403 | ①⑪ | ⚠️ FFI 桩 | 🔴 高 |
| **P3** | ② 实时翻译(WebSocket) | ~1,059 | ① | ❌ 未开始 | 🟡 中 |
| **P3** | ⑪ 音频设备/录音 | ~2,586 | 无 | ❌ 未开始 | 🔴 高 |
| **P4** | ④ 听析中心 | ~2,412 | ②③⑥⑪ | ⚠️ 框架有 | 🟡 中 |
| **P5** | ⑤ 批处理 | ~2,800 | ④⑩ | ❌ 未开始 | 🟡 中 |
| **P5** | ⑧ 搜索 + ⑫ 云 + ⑬ 认证 | ~2,168 | ① | ❌ 未开始 | 🟡 中 |

### 4.2 迁移路线图

```
Phase 1 (P0): 基础设施层 ──────────────────────────────────────
  ✅ 终结点管理（补全 URL Builder + Batch Test）
  ✅ 配置管理（拆分 AzureSpeechConfig → 多 Section）
  ✅ SQLite 存储（补全 Repository 层）
  
Phase 2 (P1): 核心 AI 能力 ────────────────────────────────────
  ✅ AI 对话/洞察（补全 SSE 流式 + 搜索增强）
  ✅ 图片生成（补全 V2 Responses API + 图片编辑）
  
Phase 3 (P2): 媒体扩展 ────────────────────────────────────────
  🔨 视频生成（新增 Sora/CogVideoX 轮询）
  🔨 计费审计（纯逻辑移植）

Phase 4 (P3): 实时翻译 ────────────────────────────────────────
  🔨 音频设备采集（cpal 替代 NAudio）
  🔨 Speech SDK FFI 完整集成
  🔨 OpenAI Realtime WebSocket
  
Phase 5 (P4-P5): 长尾功能 ─────────────────────────────────────
  🔨 听析中心管道
  🔨 批处理中心（重构 God Class 后迁移）
  🔨 搜索代理 + 云模式 + AAD 认证
```

---

## 五、关键重构建议（迁移前置）

### 5.1 God Class 拆分（迁移必要前置）

| God Class | 行数 | 建议拆分 |
|-----------|------|----------|
| `MediaSessionViewModel` (3,674) | → `ChatController` + `ImageGenController` + `VideoGenController` + `SearchController` + `BillingTracker` |
| `BatchProcessingViewModel` (3,283) | → `BatchTaskManager` + `SubtitleEditor` + `ReviewManager` + `PackageManager` |
| `MediaCenterV2ViewModel` (2,632) | → `WorkspaceManager` + `SessionFactory` + `SessionPersistence` |

### 5.2 配置巨石拆分

```
AzureSpeechConfig (622行) → 拆分为：
  ├── EndpointConfig       (终结点列表)
  ├── SpeechConfig         (翻译语言对、语音资源)
  ├── AudioDeviceConfig    (录音设备偏好)
  ├── MediaGenConfig       (图片/视频生成参数)
  ├── UiPreferencesConfig  (主题、布局偏好)
  └── CloudConfig          (云模式设置)
```

### 5.3 领域事件去 UI 化

```
当前: TaskEventBus → Dispatcher.UIThread.Post() → ViewModel
迁移: TaskEventBus → Tauri app_handle.emit() → React useEffect
```

---

## 六、Rust2 建议目标架构

```
Rust2/src-tauri/src/
├── commands.rs              ← Tauri IPC 命令层（拆分为多文件）
├── models.rs                ← 全域 DTO（对齐 C# Models/）
├── state.rs                 ← AppState (RwLock<Config> + Providers)
├── lib.rs                   ← Tauri 启动 + Provider 注册
│
├── providers/               ← ① 终结点 Provider 系统
│   ├── registry.rs          (trait 定义 + 注册表)
│   ├── openai_chat.rs       (对话/洞察)
│   ├── openai_image.rs      (图片生成)
│   ├── openai_video.rs      (视频生成 — 新增)
│   ├── openai_realtime.rs   (WebSocket 翻译 — 新增)
│   ├── azure_speech.rs      (Speech SDK FFI 翻译)
│   ├── azure_tts.rs         (语音合成)
│   └── azure_stt.rs         (语音识别)
│
├── domain/                  ← DDD 领域层
│   ├── endpoint_profiles/   (资料包 + URL Builder + 能力解析)
│   ├── audio_pipeline/      (听析中心: 队列+执行器+阶段处理)
│   ├── batch/               (批处理: 任务包+转录解析)
│   ├── billing/             (计费: 阶梯+记账)
│   ├── search/              (搜索代理: 多引擎+意图分析)
│   ├── cloud/               (云模式: API客户端+服务模式)
│   └── media_pipeline/      (图片管线: Route→Upload→Build→Execute→Land)
│
├── storage/                 ← ⑩ SQLite 持久化层
│   ├── mod.rs               (SqliteDb: Schema迁移)
│   ├── session_repo.rs      (会话+消息+资产)
│   ├── audio_repo.rs        (音频库+任务+生命周期)
│   ├── translation_repo.rs  (翻译历史)
│   ├── billing_repo.rs      (计费台账)
│   └── config_repo.rs       (KV 配置)
│
├── audio/                   ← ⑪ 音频设备层（cpal）
│   ├── capture.rs           (WASAPI/CoreAudio/ALSA 采集)
│   ├── recorder.rs          (WAV/MP3 录制)
│   └── preprocessing.rs     (WebRTC APM + VAD)
│
└── image_pipeline/          ← (已有) 图片管线
    ├── catalog.rs
    ├── pipeline.rs
    └── mod.rs
```

---

## 七、风险矩阵

| 风险项 | 影响 | 概率 | 缓解措施 |
|--------|------|------|----------|
| Speech SDK FFI 不稳定 | 🔴 高 | 🟡 中 | rustnew-backend 已验证，1.45.0 SDK 可用 |
| God Class 迁移失控 | 🔴 高 | 🔴 高 | 先拆分再迁移，Rust 侧不复制 God Class |
| 音频采集跨平台差异 | 🟡 中 | 🟡 中 | cpal 已有跨平台抽象 |
| WebRTC APM 缺 Rust 绑定 | 🟡 中 | 🟡 中 | webrtc-audio-processing crate 或 FFI |
| PuppeteerSharp → Rust headless | 🟡 中 | 🟢 低 | chromiumoxide crate 成熟 |
| MSAL → Rust OAuth | 🟢 低 | 🟢 低 | oauth2 crate 成熟 |
| 批处理 ViewModel 逻辑纠缠 | 🟡 中 | 🔴 高 | 业务逻辑下沉到 Rust commands, 前端仅持有 UI 状态 |

---

## 八、量化对照（C# vs Rust2 已完成）

| 子域 | C# 行数 | Rust2 行数 | 覆盖率 | 缺口 |
|------|---------|-----------|--------|------|
| 终结点管理 | 3,316 | ~824 | 25% | URL Builder, Batch Test |
| 实时翻译 | 2,462 | ~273 | 11% | OpenAI Realtime, 完整 SDK 集成 |
| 媒体生成 | 3,316 | ~389 | 12% | 视频, 图片编辑, V2 API |
| 听析中心 | 2,412 | ~360 | 15% | 阶段处理器, 转录 |
| AI 对话 | 827 | ~290 | 35% | SSE 流式, 搜索增强 |
| 存储 | 3,745 | ~720 | 19% | Repository 拆分 |
| 配置 | 622 | ~825* | 100%+ | 已超出（包含 DTO） |
| 计费 | 600 | 0 | 0% | 全部 |
| 批处理 | 2,800 | 0 | 0% | 全部 |
| 音频设备 | 2,586 | 0 | 0% | 全部 |
| 搜索 | 1,389 | 0 | 0% | 全部 |
| 云模式 | 398 | 0 | 0% | 全部 |

**总计**: C# ~24,473 行核心逻辑 → Rust2 ~3,681 行 → **整体迁移进度约 15%**

> *注: Rust 代码通常比 C# 更紧凑，1:0.6~0.8 的行数比是正常的。实际功能覆盖率高于行数比。*
