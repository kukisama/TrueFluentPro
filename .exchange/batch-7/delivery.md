# 批次 7 交付报告（修订版）
> 初次提交：2026-04-28 | 修订提交：2026-04-28（修复审查 3 项打回）

## 审查修复记录

| # | 文件 | 问题 | 修复内容 | 验证 |
|---|------|------|---------|------|
| 1 | src/lib/types.ts L406 | SessionMessage.mode 应为必需字段 | `mode?: string` → `mode: string` | 已读取确认 L406 |
| 2 | src/lib/types.ts L132-133 | AppConfig 缺少 task_engine_concurrency 和 task_engine_timeout_secs | 新增 `task_engine_concurrency?: number` 和 `task_engine_timeout_secs?: number` | 已读取确认 L132-133 |
| 3 | src/lib/api.ts L137-138 | onSegmentUpdated payload 类型错误（Rust 发射 string，TS 期望 object） | 回调改为 `(segmentId: string) => void`，使用 `listen<string>(...)` | 已读取确认 L137-138 |

## 任务完成状态
- [x] T-001: lib/types.ts — 类型定义（31 个接口/类型，精确对齐 Rust 后端） — 证据：`Rust3/src/lib/types.ts`:L1-443
- [x] T-002: lib/api.ts — invoke 封装（40 个命令 + 6 个事件监听器） — 证据：`Rust3/src/lib/api.ts`:L38-172
- [x] T-003: lib/utils.ts — cn() 工具函数 — 证据：`Rust3/src/lib/utils.ts`:L1-6
- [x] T-004: stores/theme-store.ts — 主题状态管理 — 证据：`Rust3/src/stores/theme-store.ts`:L1-93
- [x] T-005: stores/app-store.ts — 全局应用状态 — 证据：`Rust3/src/stores/app-store.ts`:L1-114
- [x] T-006: App.tsx 引入 stores — 证据：`Rust3/src/App.tsx`:L1-27
- [x] T-007: 编译验证 — 证据：`tsc --noEmit` 0 errors，.gitkeep 已删除

## 编译状态
`tsc --noEmit` 输出：退出码 0，无错误输出（修复后重新编译确认）

## 自检清单
- [x] 编译通过（0 errors — 修复后重新运行 tsc --noEmit 确认）
- [x] 无死代码（api 对象被 tauri-api.ts 导出供视图层使用；types 被 api.ts 和 app-store.ts 导入；cn() 被 App.tsx 导入；两个 store 被 App.tsx 导入）
- [x] 测试通过（tsc --noEmit 严格模式 + noUnusedLocals）
- [x] 所有文件 ≤ 500 行（types.ts: 443, api.ts: 172, 其余均 < 120）
- [x] 接口字段名 snake_case（匹配 Rust serde 输出）
- [x] invoke 参数名 camelCase（匹配 Tauri 2 rename_all 默认行为）
- [x] api 对象恰好 40 个 invoke + 6 个事件监听器 = 46 个 API 方法
- [x] stores 中不直接调用 api
- [x] 无 any 类型
- [x] .gitkeep 文件已删除（stores/, views/, components/）
- [x] SessionMessage.mode 为必需字段（匹配 Rust session_repo.rs:L25）
- [x] AppConfig 包含 task_engine_concurrency/task_engine_timeout_secs（匹配 config.rs:L41-43）
- [x] onSegmentUpdated 使用 listen<string>（匹配 Rust live.rs:L42 裸字符串 emit）

## 关键实现决策

### 文件拆分
tauri-api.ts 原始实现 560 行 > 500 行限制，按施工单要求拆分为：
- `types.ts` — 纯接口/类型定义（443 行）
- `api.ts` — invoke 封装 + 事件监听器（172 行）
- `tauri-api.ts` — barrel re-export（3 行），保持现有 import 路径不变

### 类型对齐
- `LanguageInfo` 接口对齐 Rust `LanguageInfo` struct（`get_supported_languages` 返回 `Vec<LanguageInfo>`，含 code/name/native_name）
- `SessionMessage` 对齐 Rust `Message` struct（含 content_hash 之外的所有字段，mode 为必需）
- `ChatMessagePayload` 用于 CompletionRequest.messages（区分于存储层的 SessionMessage）
- `RealtimeEvent` 保持 tagged enum 格式 `{type, data}`
- `VendorProfile.endpoint_type` 在 TS 侧为 string（Rust 侧是 enum，serde 输出为 snake_case 字符串）

### 命令参数名映射
- Rust `add_session_message(message: Message)` → TS `addMessage(message)` → invoke "add_session_message" with `{ message }`
- Rust `refresh_providers()` → 返回 `void`（TS 侧对齐 Rust 实际返回 `Result<(), String>`）

## 已知局限
- `TranslationHistory` 接口在 Rust3 后端暂无对应 storage/command 实现（用于 app-store 状态占位）
- `RealtimeSessionConfig`、`AudioDeviceInfo` 等类型已定义但暂无对应 Rust3 命令（留给后续批次实现的实时翻译功能）

## 新增/修改的文件清单
- `Rust3/src/lib/types.ts` — 新增+修订：31 个 TypeScript 接口/类型定义，对齐 Rust 后端模型；修复 SessionMessage.mode 必需、AppConfig 新增 2 字段
- `Rust3/src/lib/api.ts` — 新增+修订：40 个 invoke 封装 + 6 个事件监听器；修复 onSegmentUpdated payload 类型
- `Rust3/src/lib/tauri-api.ts` — 修改：从占位注释改为 barrel re-export（types + api）
- `Rust3/src/lib/utils.ts` — 新增：cn() tailwind-merge + clsx 工具函数
- `Rust3/src/stores/theme-store.ts` — 新增：Zustand 主题状态管理（mode/resolved/fontSize/transitionDuration + 4 方法）
- `Rust3/src/stores/app-store.ts` — 新增：Zustand 全局应用状态（导航/配置/翻译/流式/InfoBar/历史/加载）
- `Rust3/src/App.tsx` — 修改：引入 useThemeStore + useAppStore + cn()，渲染主题感知占位 UI
- `Rust3/src/stores/.gitkeep` — 删除
- `Rust3/src/views/.gitkeep` — 删除
- `Rust3/src/components/.gitkeep` — 删除
