# 批次 15 交付报告
> 提交日期：2026-04-29
> 全局进度：参见 docs/rust3-enrichment/batch-progress.md

## 任务完成状态
- [x] T-001: CenterWorkspace 模型扩展 + 存储层更新
  - 证据: crates/tfp-core/src/models/center.rs:6-34, crates/tfp-storage/src/center_repo/mod.rs:13-41
  - Spec 对照: MediaCenterV2ViewModel.md § 状态与数据 ✅ 一致
- [x] T-002: center_update_workspace_mode 命令
  - 证据: center_repo/mod.rs:453-461, commands/center.rs:310-324
  - Spec 对照: MediaCenterV2ViewModel.md § 流程3 ✅ 一致
- [x] T-003: center_derive_workspace 命令（EditAsset 流程）
  - 证据: center_repo/mod.rs:465-540, commands/center.rs:327-341
  - Spec 对照: MediaCenterV2ViewModel.md § 流程5 ✅ 一致
- [x] T-004: center_get_all_assets 查询
  - 证据: center_repo/mod.rs:544-567, commands/center.rs:344-353
  - Spec 对照: MediaCenterV2ViewModel.md § 流程4 ✅ 一致
- [x] T-005: 参考图数量限制验证
  - 证据: crates/tfp-media/src/center_validation.rs (全文), commands/center.rs:119-120, :215-218
  - Spec 对照: MediaCenterV2View.md § 参考素材卡片 ✅ 一致
- [x] T-006: CenterWorkspaceBundle 扩展 + 全轮次摘要
  - 证据: tfp-core/models/center.rs:51-77, center_repo/mod.rs bundle query L164-198
  - Spec 对照: MediaCenterV2ViewModel.md § 可观察属性 ✅ 一致
- [x] T-007: center_repo 全覆盖测试 (14 新测试)
  - 证据: crates/tfp-storage/src/center_repo/tests.rs (全文)
  - 所有 10+ 个要求的测试用例均已实现
- [x] T-008: 前端 Center 编辑流程集成
  - 证据: src/views/MediaCenterView.tsx, src/lib/api.ts, src/lib/types.ts
  - 功能: 右键菜单"编辑此图片"、canvas mode badge、lineage文本、all-assets面板、参考图数量前端校验
  - Spec 对照: MediaCenterV2View.md § 右键菜单 ✅ 一致

## 编译状态
```
cargo check --workspace: 0 errors, 0 new warnings
(pre-existing: 4 warnings in tfp-providers + 3 warnings in src-tauri, none from batch-15 code)
```

## 测试状态
```
cargo test --workspace: 544 passed, 0 failed, 2 ignored
- 新增测试: 19 个 (14 center_repo + 4 center_validation + 1 model serde update)
- 原有测试: 525 个，无回归
```

## TypeScript 状态
```
npx tsc --noEmit: 0 new errors
(pre-existing: 2 errors in LiveTranslationView.tsx — unrelated speakSegment/synthesizeSpeech)
```

## 新增/修改文件清单
| 文件 | 操作 | 变更行数 |
|------|------|----------|
| crates/tfp-core/src/models/center.rs | 修改 | +26 (新字段 + RoundPromptSummary struct) |
| crates/tfp-core/src/models/mod.rs | 修改 | +8 (serde test 补新字段) |
| crates/tfp-storage/src/center_repo/mod.rs | 修改 | +160 (新方法 + 查询扩展) |
| crates/tfp-storage/src/center_repo/tests.rs | 重写 | +280 (从 2 个测试扩展到 14 个) |
| crates/tfp-media/src/center_validation.rs | 新建 | +60 |
| crates/tfp-media/src/lib.rs | 修改 | +1 |
| src-tauri/src/commands/center.rs | 修改 | +55 (3 新命令 + 2 处验证) |
| src-tauri/src/lib.rs | 修改 | +3 (注册新命令) |
| src/lib/types.ts | 修改 | +15 (新接口字段 + RoundPromptSummary) |
| src/lib/api.ts | 修改 | +6 (3 新 API 函数) |
| src/views/MediaCenterView.tsx | 修改 | +60 (编辑流程、badge、all-assets面板) |

## 已知局限
- 前端 i18n keys (mediaCenter.editThisImage, mediaCenter.editOf, mediaCenter.allAssets, mediaCenter.allAssetsTitle) 尚未添加到 locales 文件 — 运行时会 fallback 显示 key name
- studio_add_reference_image 命令（studio.rs）在 center 上下文使用时的验证依赖前端校验（后端已有 center_start_*_round 入口验证）
- `has_more_rounds` 使用硬编码阈值 50（足够实际使用场景）
