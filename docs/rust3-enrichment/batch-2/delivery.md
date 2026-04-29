# 批次 2 交付报告
> 提交日期：2026-04-29
> 全局进度：参见 docs/rust3-enrichment/batch-progress.md

## 任务完成状态
- [x] T-001: ImageGenRequest 新增 uploaded_file_ids 字段
  - 证据: crates/tfp-core/src/models/api.rs:79-80
  - Spec 对照: Services/AiImageGenService.md § 流程2 步骤 2 ✅ 一致
- [x] T-002: build_file_upload_urls() 方法
  - 证据: crates/tfp-providers/src/openai_image.rs (build_file_upload_urls)
  - Spec 对照: EndpointProfileUrlBuilder.md § BuildFileUploadUrlCandidates ✅ 一致
- [x] T-003: upload_file() 方法
  - 证据: crates/tfp-providers/src/openai_image.rs (upload_file)
  - Spec 对照: Services/AiImageGenService.md § 流程5 ✅ 一致
- [x] T-004: edit_via_responses_api() 方法
  - 证据: crates/tfp-providers/src/openai_image.rs (edit_via_responses_api)
  - Spec 对照: Services/AiImageGenService.md § 流程2 ✅ 一致
- [x] T-005: generate() 4 条路由分支
  - 证据: crates/tfp-providers/src/openai_image.rs (ImageGenSlot::generate impl)
  - Spec 对照: Services/AiImageGenService.md § 流程1 步骤 2 ✅ 一致
- [x] T-006: AppState 集成 FileIdCache + upload_image_file Tauri 命令
  - 证据: src-tauri/src/commands/media.rs (upload_image_file), src-tauri/src/lib.rs (handler registration)
  - 备注: AppState.file_id_cache 已存在（batch-0 遗留），本批次新增 Tauri 命令 + trait 方法
- [x] T-007: 前端类型同步
  - 证据: src/lib/types.ts (uploaded_file_ids), src/lib/api.ts (uploadImageFile)
  - Spec 对照: 前后端类型一致 ✅

## 编译状态
```
cargo check — Finished `dev` profile in 6.74s
0 errors, 0 warnings
```

## 测试状态
```
cargo test — 298 passed, 0 failed, 1 ignored
新增测试 12 个，全绿
原有测试 286 个，无回归
```

新增测试清单：
1. test_build_file_upload_urls_azure
2. test_build_file_upload_urls_apim
3. test_build_file_upload_urls_openai
4. test_build_file_upload_urls_openai_with_v1_base
5. test_edit_via_responses_api_body_construction
6. test_generate_routing_no_ref_no_responses
7. test_generate_routing_no_ref_with_responses
8. test_generate_routing_with_ref_path_no_responses
9. test_generate_routing_with_file_ids_with_responses
10. test_generate_routing_with_file_ids_v1_multipart_forced
11. test_image_gen_request_missing_uploaded_file_ids_defaults_empty
12. test_image_gen_request_with_uploaded_file_ids

## 前端类型检查
```
npx tsc --noEmit — 0 errors
```

## 新增/修改文件清单
| 文件 | 操作 | 变更行数 |
|------|------|----------|
| crates/tfp-core/src/models/api.rs | 修改 | +3 |
| crates/tfp-core/src/models/mod.rs | 修改 | +1 |
| crates/tfp-providers/src/traits.rs | 修改 | +10 |
| crates/tfp-providers/src/openai_image.rs | 修改 | +250 |
| crates/tfp-engine/src/task_engine.rs | 修改 | +1 |
| src-tauri/src/commands/media.rs | 修改 | +32 |
| src-tauri/src/commands/center.rs | 修改 | +1 |
| src-tauri/src/commands/studio.rs | 修改 | +1 |
| src-tauri/src/task_engine.rs | 修改 | +1 |
| src-tauri/src/lib.rs | 修改 | +1 |
| src/lib/types.ts | 修改 | +1 |
| src/lib/api.ts | 修改 | +3 |

## 已知局限
- upload_file() 集成测试未运行（需要有效 APIM endpoint 的 API key）
- edit_via_responses_api() 未真实调通（依赖有效的 APIM endpoint + 已部署 gpt-image-1/gpt-4o）
- 不含 mask 图支持（per 施工单禁止事项）
- 不含进度回调（per 施工单禁止事项）
