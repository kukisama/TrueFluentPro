# Rust3 充实计划 — 21 轮实施路线图
> 创建日期：2026-04-29
> 最后更新：2026-04-29
> 总轮次：21 | 已完成：17 | 当前：batch-17

## 总体目标

基于 98 份 C# 功能说明书（.exchange/docs/），将 Rust3 从骨架状态（27,101 行）充实为功能与 C# 主程序对齐的完整产品（预估 ~57,000 行）。

## 项目基线

| 指标 | 数值 |
|------|------|
| C# 主程序总行数 | 74,659 |
| Rust3 现有行数 | 27,101 |
| 差距 | ~47,558 (63.7%) |
| 预计新增行数 | ~30,000 |
| 预计最终行数 | ~57,000 (C# 的 76%) |

### 现有 Rust3 代码分布

| 模块 | 行数 | 实现深度 |
|------|------|----------|
| tfp-core | 2,216 | 模型定义中等，枚举/字段需补全 |
| tfp-providers | 2,897 | 6 slot trait 已定义，实现有骨架 |
| tfp-storage | 3,642 | SQLite DAL 较充实 |
| tfp-engine | 806 | TaskEngine 基本可用 |
| tfp-media | 1,860 | pipeline/studio/center 有逻辑 |
| tfp-audiolab | 585 | 仅纯函数（格式化/估算） |
| tfp-chat | 222 | 流式基础 |
| tfp-speech | 271 | 语言列表+文本过滤 |
| tfp-search | 8 | 空壳 |
| src-tauri 命令层 | 6,080 | 薄壳完成 |
| 前端 TS/TSX | 8,514 | View 存在但功能浅 |

### 可用测试基础设施

- `cargo test` — 241 tests passing（领域 crate 单元测试）
- `src-tauri/tests/common.rs` — 可从 Rust2 SQLite 加载真实 endpoint（含 API key）
- 前端 API key 已通过 Tauri IPC 可用
- `.exchange/docs/` — 98 份 C# spec 文档作为功能基线

---

## 阶段规划总览

| 阶段 | 批次范围 | 轮次 | 预估增量 | 完成定义 |
|------|---------|------|----------|----------|
| Phase 1 | batch 0-5 | 6 | ~5,500 | 核心 API 管线全通，图片/视频/聊天端到端可调 |
| Phase 2 | batch 6-8 | 3 | ~5,100 | 语音翻译全功能可用 |
| Phase 3 | batch 9-12 | 4 | ~7,200 | 听析中心 + 批处理端到端 |
| Phase 4 | batch 13-16 | 4 | ~7,000 | Studio/Center 功能完整 |
| Phase 5 | batch 17-20 | 4 | ~5,200 | 基础设施 + 收尾 |

---

## Phase 1: 核心管线补全（batch 0-5）

> 完成定义：所有核心 API（图片/视频/聊天/端点测试/配置/模型发现）的后端管线可调通，cargo test 覆盖关键路径。

| 批次 | 功能 | 对口 Spec | 预估增量 | 退出标准 |
|------|------|-----------|----------|----------|
| **0** ✅ | Models 补全 | AiEndpointAndConfig.md, EnumsAndSmallModels.md | ~600 | 已完成 |
| **1** ✅ | 图片生成多路由 | AiImageGenService.md (路由逻辑) | ~800 | 已完成 |
| **2** ✅ | 图片编辑 + file_id 上传 | AiImageGenService.md (编辑+上传) | ~700 | 已完成 |
| **3** ✅ | 视频生成完善 | AiVideoGenService.md | ~600 | 视频创建→轮询→下载全流程，状态机测试 |
| **4** | AI 聊天完善 + 端点连通性测试 | AiInsightService.md, EndpointBatchTestService.md | ~1300 | complete() 全参数 + 流式测试 + test_runner 报告 |
| **5** | 配置持久化 + 模型发现计费 | SettingsImportExportService.md, EndpointTemplateService.md, AiEndpointModelDiscoveryService.md, ImageBillingHelper.md | ~1500 | 配置 round-trip + 模型发现 mock + 计费统计 |

---

## Phase 2: 语音与翻译域（batch 6-8）

> 完成定义：Azure Speech SDK 实时翻译、OpenAI Realtime WebSocket 翻译、STT、TTS 全部可用。

| 批次 | 功能 | 对口 Spec | 预估增量 | 退出标准 |
|------|------|-----------|----------|----------|
| **6** | Speech SDK 实时翻译（核心+完善） | SpeechTranslationService.md | ~1800 | 翻译会话创建、事件接收、自动重连、SRT/VTT 导出 |
| **7** | OpenAI Realtime WebSocket + STT | OpenAiRealtimeTranslationService.md, AiAudioTranscriptionService.md, FastTranscriptionParser.md, BatchTranscriptionParser.md | ~1900 | WebSocket 翻译 + 音频转录 |
| **8** | TTS + 实时翻译前端 | SpeechSynthesisService.md, LiveTranslationView code-behind, FloatingSubtitleWindow.md | ~1400 | 语音合成 + 前端翻译全流程 |

---

## Phase 3: 听析中心 + 批处理（batch 9-12）

> 完成定义：AudioLab 8 阶段 pipeline 端到端可跑，批处理 Package 队列可执行。

| 批次 | 功能 | 对口 Spec | 预估增量 | 退出标准 |
|------|------|-----------|----------|----------|
| **9** | AudioLab 文件管理 + 8 阶段执行器 1-3 | AudioLabViewModel.md, AudioSubsystem.md, AudioTaskStageHandlerService.md (阶段 1-3) | ~1800 | 文件加载 + 前 3 阶段可运行 |
| **10** | 8 阶段执行器 4-8 + AudioLab 前端 | AudioTaskStageHandlerService.md (阶段 4-8), AudioLabView code-behind | ~1800 | 全 8 阶段可运行 + 所有 Tab 可操作 |
| **11** | 批处理状态机 + 字幕链式任务 | BatchProcessingViewModel.md | ~2200 | Package 创建→排队→执行→完成 + 链式触发 |
| **12** | 批处理前端 + Blob/BatchApi | BatchCenterView/ReviewModeView code-behind, BlobStorageService.md, SpeechBatchApiClient.md | ~1400 | 前端全流程 + Azure Blob + Batch API |

---

## Phase 4: 媒体创作坊 + 中心（batch 13-16）

> 完成定义：Studio 聊天/搜索/内嵌图片完整，Center 工作区/轮次/导出完整。

| 批次 | 功能 | 对口 Spec | 预估增量 | 退出标准 |
|------|------|-----------|----------|----------|
| **13** | Studio 聊天完善 + 内嵌图片搜索 | MediaSessionViewModel.md | ~1800 | 分页加载 + 编辑 + 分支 + 聊天内图片 |
| **14** | Web 搜索增强 + Studio 前端深化 | WebSearch/ 全部文档, MediaStudioView.md, MediaStudioViewModel.md | ~2200 | 搜索增强可用 + 参数面板 + 参考图 |
| **15** | Center 工作区 + 轮次资产 | MediaCenterV2ViewModel.md | ~1600 | 工作区 CRUD + 画布 + 轮次→资产 |
| **16** | Center 导出能力 + Studio/Center 前端 | MediaCenterV2ViewModel.md (导出/能力), MediaCenterV2View.md | ~1400 | 资产导出 + Center 前端全流程 |

---

## Phase 5: 基础设施 + 收尾（batch 17-20）

> 完成定义：AAD 登录、设置、浮动窗口、控件库、i18n 全覆盖、集成测试。

| 批次 | 功能 | 对口 Spec | 预估增量 | 退出标准 |
|------|------|-----------|----------|----------|
| **17** | AAD 登录 + 设置系统 | AzureTokenProvider.md, SettingsView.md, SettingsSubsections.md | ~1800 | AAD 全流程 + 所有设置 Tab |
| **18** | 浮动窗口 + 主窗口导航 | FloatingSubtitleWindow.md, FloatingInsightWindow.md, MainWindow.md, App.md | ~1300 | 位置记忆 + 透明度 + 无边框 |
| **19** | 控件库 + i18n 全量覆盖 | MarkdownModule.md, InteractiveControls.md, 全量 grep | ~1200 | 所有控件 + 0 硬编码字符串 |
| **20** | 集成测试 + 清理 + Capabilities | 全功能域, PLATFORM-NOTES.md | ~900 | 每域≥1 E2E + capabilities 审计 |

---

## 风险登记

| 风险 | 影响 | 概率 | 缓解策略 |
|------|------|------|----------|
| Speech SDK FFI 稳定性 | Phase 2 卡住 | 中 | batch 6 先做独立 PoC |
| WebSocket Realtime API 变更 | batch 7 需调整 | 低 | 对照最新 API 文档 |
| 批处理并发状态机复杂度 | batch 11 超时 | 中 | 必要时拆回两轮 |
| 前端 MindMap/Markdown 控件 | batch 19 工作量不确定 | 中 | 可用 npm 包替代自研 |
| C# spec 与实际代码不同步 | 产出与预期不符 | 低 | 实施时对照 C# 源码验证 |

## 当前进度

- 当前阶段：Phase 5 — 基础设施 + 收尾
- 当前批次：batch-18（施工单已下发）
- 已完成：18/21 轮
- 预计剩余：3 轮
- 代码量：40,228 行 | 测试：557 个