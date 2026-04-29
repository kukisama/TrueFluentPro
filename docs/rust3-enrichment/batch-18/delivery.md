# 批次 18 交付报告
> 提交日期：2026-04-29
> 全局进度：参见 docs/rust3-enrichment/batch-progress.md

## ⚠️ 施工单异议

| 施工单要求 | 问题 | 我的修正 | Spec 依据 |
|-----------|------|---------|-----------|
| T-005: "AppLayout.tsx — 无任何 keyboard listener" | App.tsx 已有 Ctrl+1..6 + F5/F6 快捷键（batch-17 残留） | 将已有逻辑重构为 `useKeyboardShortcuts` hook，更新映射为 Spec 要求的顺序，添加 input 防冲突 | MainWindow.md § 键盘快捷键 |
| floating.rs 已有 `FloatingWindowState` 结构体名 | 名称与新增的位置记忆 struct 冲突 | 将旧事件 payload 重命名为 `FloatingWindowOpenEvent`，新 `FloatingWindowState` 从 tfp-core 导入 | 无冲突，纯实现决策 |

## 任务完成状态
- [x] T-001: UiConfig 扩展 — FloatingWindowState + last_active_view + auto_collapse_sidebar_width
  - 证据: crates/tfp-core/src/models/config.rs:369-421
  - Spec 对照: MainWindow.md § 导航栏展开/收起 + App.md § Shell 启动偏好 ✅ 一致
- [x] T-002: 浮动窗口位置记忆后端命令 (save/get/restore)
  - 证据: src-tauri/src/commands/floating.rs:182-216
  - Spec 对照: FloatingWindows.md § SetInitialPosition ✅ 一致
- [x] T-003: 浮动窗口透明度控制 (opacity slider + CSS-level)
  - 证据: src/views/FloatingSubtitleWindow.tsx (opacity state + slider), src/views/FloatingInsightWindow.tsx (同上)
  - Spec 对照: FloatingWindows.md § Background Transparent ✅ 一致 (D-01: CSS alpha 实现)
- [x] T-004: 来源字幕分离 (SubtitlePayload.source_label)
  - 证据: src-tauri/src/commands/floating.rs:134-139, translate.rs:165
  - Spec 对照: MainWindowViewModel.TranslationAndUi.md § 流程6 ✅ 一致
- [x] T-005: 键盘快捷键 (useKeyboardShortcuts hook)
  - 证据: src/hooks/useKeyboardShortcuts.ts (全文)
  - Spec 对照: MainWindow.md § 键盘快捷键 ✅ 一致
- [x] T-006: 导航状态持久化 + 自动收起侧栏
  - 证据: src/stores/app-store.ts (persistActiveView/persistSidebarCollapsed), src/components/AppLayout.tsx (resize listener), src/App.tsx (last_active_view restore)
  - Spec 对照: MainWindow.md § OnSizeChanged + App.md § Shell 启动偏好 ✅ 一致
- [x] T-007: api.ts + types.ts 补全
  - 证据: src/lib/types.ts (FloatingWindowState interface + UiConfig 新字段), src/lib/api.ts (3 new API wrappers)
  - Spec 对照: 横跨所有任务 ✅ 一致

## 编译状态
```
cargo check --workspace: ✅ 0 errors (3 pre-existing dead_code warnings in audio.rs)
```

## 测试状态
```
cargo test --workspace: ✅ 563 passed, 0 failed, 2 ignored
- 新增测试 9 个:
  - tfp-core: test_ui_config_floating_state_serde, test_ui_config_default_last_view,
    test_ui_config_backwards_compatible_deserialization, test_floating_window_state_default
  - src-tauri: test_subtitle_payload_has_source_label, test_floating_window_open_event_serde,
    test_floating_window_state_opacity_clamp (+ 2 existing updated)
- 原有测试 554 个，无回归
```

## TypeScript 状态
```
npx tsc --noEmit: 2 pre-existing errors (synthesizeSpeech + speakSegment in LiveTranslationView) — 无新增
```

## 新增/修改文件清单
| 文件 | 操作 | 变更行数 |
|------|------|----------|
| crates/tfp-core/src/models/config.rs | 修改 | +50 -14 |
| crates/tfp-core/src/models/mod.rs | 修改 | +50 |
| src-tauri/src/commands/floating.rs | 修改 | +170 -50 |
| src-tauri/src/commands/translate.rs | 修改 | +1 |
| src-tauri/src/lib.rs | 修改 | +3 |
| src/hooks/useKeyboardShortcuts.ts | 新增 | +87 |
| src/App.tsx | 修改 | +20 -30 |
| src/stores/app-store.ts | 修改 | +50 -15 |
| src/components/AppLayout.tsx | 修改 | +15 -2 |
| src/views/FloatingSubtitleWindow.tsx | 修改 | +35 -5 |
| src/views/FloatingInsightWindow.tsx | 修改 | +30 -5 |
| src/lib/types.ts | 修改 | +12 -3 |
| src/lib/api.ts | 修改 | +8 -2 |

## 已知局限
- 浮动窗口位置恢复依赖 Tauri 窗口 API，需要实际启动应用验证 UI 效果
- F5 触发翻译开始使用 CustomEvent 间接调用（LiveTranslationView 需监听 `tfp:start-translation`）
- 透明度实现为 CSS background alpha，非窗口系统级别 opacity（Tauri 2 设计限制）
- 自动收起侧栏仅在 window resize 触发时生效，不会在配置阈值变化后立即生效
