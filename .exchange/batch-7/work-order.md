# 批次 7 施工单 — 前端 IPC 层 + 状态管理基础
> 日期：2026-04-28

## 目标
建立前端与 Rust 后端的完整通信桥梁：tauri-api.ts（类型定义 + 40 个命令的 invoke 封装 + 事件监听器）、Zustand 状态管理（app-store + theme-store）、工具函数 utils.ts，使后续批次可直接编写 UI 视图。

## 前置条件
批次 6 已通过（40 个 Tauri 命令全部注册且编译通过）

## 依赖方向

    src/ (TypeScript/React)
      ├── lib/tauri-api.ts   ← @tauri-apps/api/core (invoke), @tauri-apps/api/event (listen)
      ├── lib/utils.ts       ← clsx, tailwind-merge
      ├── stores/app-store.ts   ← zustand, tauri-api
      └── stores/theme-store.ts ← zustand

**无新 npm 依赖**：zustand, clsx, tailwind-merge 已在 package.json 中。

---

## 任务清单

### T-001: lib/tauri-api.ts — 类型定义（接口部分）

- **读取**: Rust2/src/lib/tauri-api.ts:L1-670（接口定义段）
- **参考**: Rust3/crates/tfp-core/src/models/ 下所有 .rs 文件（以 Rust 结构体为准）
- **产出**: Rust3/src/lib/tauri-api.ts（接口部分）
- **契约**:

  必须定义以下接口/类型（字段与 Rust 后端 **精确对齐**）：

  **配置层**:
  - `ModelReference` { endpoint_id, model_id }
  - `AiEndpoint` { id, name, endpoint_type, url, api_key, api_version?, region?, models, enabled, auth_header_mode, auth_mode, azure_tenant_id, azure_client_id, speech_subscription_key, speech_region, speech_endpoint }
  - `AiModelEntry` { model_id, display_name, deployment_name?, capabilities }
  - `ModelCapability` = "text" | "image" | "video" | "speech_to_text" | "text_to_speech"
  - `AudioConfig` { input_device_id?, loopback_device_id?, sample_rate, enable_aec, enable_ns, enable_agc }
  - `UiConfig` { theme, sidebar_collapsed, font_size, language }
  - `AiSettings` { insight_model, summary_model, quick_model, review_model, conversation_model, intent_model, insight_system_prompt, enable_reasoning, max_conversation_turns }
  - `MediaSettings` { image_model, video_model, image_quality, image_format, image_size, image_count, image_background, video_aspect_ratio, video_resolution, video_seconds, video_variants, video_poll_interval_ms }
  - `StorageSettings` { batch_storage_connection_string, batch_storage_is_valid, batch_audio_container_name, batch_result_container_name, enable_recording, recording_mp3_bitrate_kbps, export_vtt_subtitles, export_srt_subtitles }
  - `RecognitionSettings` { filter_modal_particles, max_history_items, realtime_max_length, chunk_duration_ms, enable_auto_timeout, initial_silence_timeout_seconds, end_silence_timeout_seconds, enable_no_response_restart, no_response_restart_seconds, audio_activity_threshold, audio_level_gain, show_reconnect_marker }
  - `WebSearchSettings` { provider_id, trigger_mode, max_results, enable_intent_analysis, enable_result_compression, mcp_endpoint, mcp_tool_name, mcp_api_key, debug_mode }
  - `AppConfig` { endpoints, default_source_lang, default_target_langs, audio, ui, ai, media, storage, recognition, web_search }

  **端点测试**:
  - `EndpointTestReport` { endpoint_id, endpoint_name, endpoint_type_name, items, duration_ms, total_count, success_count, failed_count, skipped_count }
  - `EndpointTestItem` { model_id, capability, status, summary, detail?, request_url?, request_summary?, duration_ms, test_branch?, urls_tried }
  - `EndpointTestProgress` { endpoint_id, endpoint_name, total_count, pending_count, running_count, success_count, failed_count, skipped_count, items, is_completed, started_at }
  - `DiscoveredModel` { id, display_name?, owned_by? }

  **翻译**:
  - `TranslateRequest` { text, source_lang, target_lang, endpoint_id? }
  - `TranslateResponse` { translated_text, source_lang, target_lang, confidence?, provider }

  **实时翻译**:
  - `RealtimeSessionConfig` { source_lang, target_langs, endpoint_id, enable_partial, profanity_filter, initial_silence_timeout_seconds?, end_silence_timeout_seconds? }
  - `RealtimeEvent` { type, data } — type = "SessionStarted" | "Recognizing" | "Recognized" | "Translated" | "SessionStopped" | "Error"
  - `TranslationSession` { id, started_at, stopped_at, source_lang, target_langs, provider, status }
  - `TranslationSegment` { id, session_id, sequence, original_text, translated_text, target_lang, started_at, ended_at, is_bookmarked, bookmark_note, audio_path, raw_event_json }
  - `SupportedLanguage` { code, label, kind }

  **AI 媒体**:
  - `ImageGenRequest` { prompt, width, height, model, quality?, output_format?, background?, n?, endpoint_id, text_model?, image_model?, previous_response_id? }
  - `ImageGenResult` { url?, base64?, revised_prompt?, response_id? }
  - `SaveImageRequest` { base64, prompt, revised_prompt?, format, width?, height?, model_id?, endpoint_id?, generate_seconds?, source }
  - `SavedImage` { id, prompt, revised_prompt?, file_path, file_size, width?, height?, model_id?, endpoint_id?, generate_seconds?, source, created_at }
  - `VideoGenRequest` { prompt, model, endpoint_id, size?, duration_seconds?, api_mode?, reference_image_path?, n? }
  - `VideoGenResult` { video_id, status, download_url?, file_path?, generate_seconds? }

  **AI 补全**:
  - `CompletionRequest` { messages, model, temperature?, max_tokens?, endpoint_id }
  - `CompletionResponse` { content, model, usage? }
  - `ChatMessage` (invoke 参数用) { role, content }
  - `ContentPart` = { type: "text", text } | { type: "image_url", image_url: { url, detail? } }
  - `StreamTokenEvent` { stream_id, token?, reasoning?, usage?, done?, error? }

  **Provider**:
  - `VendorProfile`（完整字段，对齐 Rust2 定义）
  - `ProviderInfo` { id, name, capabilities }

  **会话**:
  - `Session` { id, title, session_type, created_at, updated_at, message_count, token_total }
  - `SessionMessage` { id, session_id, role, content, mode?, reasoning_text?, prompt_tokens?, completion_tokens?, image_base64?, attachments?, created_at }

  **系统**:
  - `AppInfo` { version, platform, arch, data_dir }

  **音频设备**:
  - `AudioDeviceInfo` { id, name, device_type, is_default }

- **测试**: `tsc --noEmit` 通过

### T-002: lib/tauri-api.ts — invoke 封装（api 对象）

- **读取**: Rust2/src/lib/tauri-api.ts:L1031-1331（api 对象）
- **参考**: Rust3/src-tauri/src/lib.rs:L58-111（invoke_handler 注册的 40 个命令）
- **产出**: 同 T-001 文件末尾的 `export const api = { ... }`
- **契约**:

  **仅封装 Rust3 已实现的 40 个命令**（不得添加 Rust3 未实现的命令）：

  `
  // Config (5)
  getConfig, updateConfig, addEndpoint, removeEndpoint, updateEndpoint

  // Provider (3)
  listProviders, refreshProviders, getVendorProfiles

  // System (1)
  getAppInfo

  // Translation (2)
  translateText, getSupportedLanguages

  // Sessions (6)
  listSessions, createSession, deleteSession, renameSession, getSessionMessages, addMessage

  // AI Completion (2)
  aiComplete, aiCompleteStream

  // Endpoint test (2)
  testEndpoint, discoverModels

  // Image (3)
  generateImage, saveImage, listSavedImages

  // Video (1)
  generateVideo

  // Prompt (1)
  optimizePrompt

  // Live translation (9)
  liveGetActiveSession, liveGetRecentSegments, liveBookmarkSegment, liveUnbookmarkSegment,
  liveListSupportedLanguages, liveListSessions, liveGetSessionSegments, liveExportSubtitles,
  liveClearSessionSegments

  // Floating (5)
  liveShowFloatingSubtitle, liveHideFloatingSubtitle, liveToggleFloatingSubtitle,
  liveShowFloatingInsight, liveHideFloatingInsight
  `

  **事件监听器**（基于 listen + unlisten 模式）：
  - `onRealtimeEvent` → "realtime-event"
  - `onStreamToken` → "ai-stream-token"
  - `onSegmentUpdated` → "segment-updated"
  - `onFloatingWindowStateChanged` → "floating-window-state-changed"
  - `onVideoProgress` → "video-progress"
  - `onTestProgress` → "endpoint-test-progress"

  每个 invoke 封装的参数名必须使用 **camelCase**（Tauri 2 的 rename_all = "camelCase" 默认行为）。

  总计：40 invoke 方法 + 6 事件监听器 = 46 个 API 方法。

- **测试**: `tsc --noEmit` 通过

### T-003: lib/utils.ts — 工具函数

- **读取**: Rust2/src/lib/utils.ts
- **产出**: Rust3/src/lib/utils.ts
- **契约**:
  - `cn(...inputs: ClassValue[]): string` — tailwind-merge + clsx 合并
  - 从 clsx 导入 ClassValue 类型
- **测试**: `tsc --noEmit` 通过

### T-004: stores/theme-store.ts — 主题状态管理

- **读取**: Rust2/src/stores/theme-store.ts（完整 96 行）
- **产出**: Rust3/src/stores/theme-store.ts
- **契约**:
  - ThemeMode = "system" | "light" | "dark"
  - ThemeState: mode, resolved, fontSize, transitionDuration
  - 方法: setMode, setFontSize, setTransitionDuration, cycleTheme
  - resolveTheme(): 使用 window.matchMedia 检测系统主题
  - applyTheme(): 操作 document.documentElement classList
  - applyFontSize(): 设置 root fontSize
  - localStorage 持久化: theme-mode, font-size, transition-duration
  - 监听 prefers-color-scheme 变化
  - fontSize 范围: [12, 20]
  - transitionDuration 范围: [0, 1000]
- **测试**: `tsc --noEmit` 通过

### T-005: stores/app-store.ts — 全局应用状态

- **读取**: Rust2/src/stores/app-store.ts（完整 115 行）
- **产出**: Rust3/src/stores/app-store.ts
- **契约**:
  - AppView 类型: "live-translation" | "media-studio" | "media-center" | "audio-lab" | "task-monitor" | "settings" | "about" | "help" | "auth"
  - 状态:
    - 导航: activeView (默认 "media-studio"), sidebarCollapsed
    - 配置: config (AppConfig | null)
    - Provider: providers (ProviderInfo[])
    - 实时翻译: isTranslating, sessionId, recognizedSegments
    - AI 流式: streamingContent, isStreaming
    - InfoBar: infoBarOpen, infoBarMessage, infoBarSeverity
    - 翻译历史: history (TranslationHistory[])（注：TranslationHistory 接口需在 tauri-api.ts 中定义）
    - 全局: loading, error
  - 方法: setActiveView, toggleSidebar, setConfig, setProviders, setTranslating, setSessionId, addSegment, clearSegments, appendStreamToken, clearStream, setStreaming, showInfoBar, hideInfoBar, setHistory, setLoading, setError
  - 从 tauri-api 导入: AppConfig, ProviderInfo, TranslationHistory
- **测试**: `tsc --noEmit` 通过

### T-006: 更新 App.tsx 引入 stores

- **产出**: Rust3/src/App.tsx（最小修改）
- **契约**:
  - 保留现有 i18n useTranslation
  - 导入 useThemeStore（使用 resolved 主题渲染 className）
  - 导入 useAppStore（使用 activeView 决定渲染内容，但此批次仅显示占位文本）
  - 不创建任何视图组件（留到后续批次）
  - 结构示例:
    `
    <div className={cn("min-h-screen", resolved === "dark" ? "dark bg-neutral-900 text-white" : "bg-white text-neutral-900")}>
      <p>{t("app.name")} — {activeView}</p>
    </div>
    `
- **测试**: `tsc --noEmit` 通过

### T-007: 编译验证

- **产出**: 无新文件
- **契约**:
  1. `tsc --noEmit` — 0 errors（strict 模式，noUnusedLocals）
  2. 删除 stores/.gitkeep, views/.gitkeep, components/.gitkeep（已有实际文件/后续会有）
  3. tauri-api.ts 中 api 对象恰好 40 个 invoke + 6 个事件监听器
  4. 所有接口字段名使用 snake_case（匹配 Rust serde 输出）
  5. 所有 invoke 参数名使用 camelCase（匹配 Tauri 2 默认 rename）
  6. stores/ 下 .gitkeep 已删除

---

## 禁止事项

- 不封装 Rust3 未实现的命令（如 start_realtime_translation、studio_*、center_*、audiolab_*、monitor_* 等）
- 不创建任何 React 视图组件（views/ 和 components/ 留到后续批次）
- 不添加 npm 依赖（所有需要的包已在 package.json 中）
- 不修改 Rust 后端文件
- tauri-api.ts 单文件不超过 500 行；超限则拆为 types.ts + api.ts
- 不使用 any 类型（用 unknown 或具体类型）
- 不在 stores 中直接调用 api（stores 仅管理状态，api 调用在视图层）

## 退出标准

1. `tsc --noEmit` — 0 errors
2. tauri-api.ts 导出 api 对象，包含恰好 40 个 invoke 方法 + 6 个事件监听器
3. theme-store.ts 导出 useThemeStore，含 mode/resolved/fontSize 状态 + 4 个方法
4. app-store.ts 导出 useAppStore，含导航/配置/翻译/流式/InfoBar 状态
5. utils.ts 导出 cn() 函数
6. App.tsx 引用 stores 且 tsc 通过
7. 所有文件 ≤ 500 行
