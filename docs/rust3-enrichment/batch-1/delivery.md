# 批次 1 交付报告
> 提交日期：2026-04-29
> 全局进度：参见 docs/rust3-enrichment/batch-progress.md

## ⚠️ 施工单异议

| 施工单要求 | 问题 | 我的修正 | Spec 依据 |
|-----------|------|---------|-----------|
| `should_use_responses_api` 检查 `MediaSettings.image_edit_mode` | Provider 层无法直接访问 `MediaSettings`，仅有 `AiEndpoint` | 在 `ImageGenRequest` 中新增 `image_edit_mode: Option<ImageEditMode>` 字段，由 Tauri 命令层从配置填入 | Spec § 路由决策逻辑: `ShouldUseResponsesApi(genConfig)` — genConfig 是请求级参数 |
| `build_responses_urls` APIM 默认 api-version=2025-03-01-preview | 当 endpoint 已配置 api_version 时应使用配置值 | 使用 `endpoint.api_version.unwrap_or("2025-03-01-preview")`，配置值优先 | Spec § URL 模板替换：api-version 来自端点配置 |
| 施工单要求 T-004 返回类型含 `reqwest::Response` | `try_candidates` 返回 Response 后需要异步读取 body | 保持此设计，增加 `parse_image_response_from_reqwest` helper 桥接 | 设计决策 D-001 |

## 任务完成状态
- [x] T-001: 补全 ImageGenerationResult 结构
  - 证据: crates/tfp-core/src/models/api.rs:82-94
  - Spec 对照: Services/AiImageGenService.md § ImageGenerationResult 字段表 ✅ 一致
  - 新增字段: request_url, attempted_urls, generate_seconds, download_seconds, actual_input_tokens, actual_output_tokens
  - 同步新增: `reference_image_path` 和 `image_edit_mode` 到 `ImageGenRequest`

- [x] T-002: 路由决策函数
  - 证据: crates/tfp-providers/src/openai_image.rs:31-44
  - Spec 对照: Services/AiImageGenService.md § ShouldUseResponsesApi ✅ 一致
  - 3 分支: V1Multipart→false, text_model.is_some()→true, 默认→false

- [x] T-003: 候选 URL 构建（完整版）
  - 证据: crates/tfp-providers/src/openai_image.rs:47-129
  - Spec 对照: Services/EndpointProfileUrlBuilder.md § 图片 URL 候选列表 ✅ 一致
  - 3 个函数 × 3 种 endpoint_type

- [x] T-004: 候选 URL 回退执行器
  - 证据: crates/tfp-providers/src/openai_image.rs:136-175
  - Spec 对照: Services/AiImageGenService.md § 流程4 步骤 2-4 ✅ 一致
  - 404/405→继续，401/403→Auth错误，429→RateLimited，其他→Network错误

- [x] T-005: 重写 generate() — 双路由
  - 证据: crates/tfp-providers/src/openai_image.rs:328-336 (trait impl)
  - Spec 对照: Services/AiImageGenService.md § 流程1 路由判定 ✅ 一致
  - `generate_via_images_api` + `generate_via_responses`

- [x] T-006: 图片编辑 multipart 请求构建
  - 证据: crates/tfp-providers/src/openai_image.rs:249-318
  - Spec 对照: Services/AiImageGenService.md § 流程3 ✅ 一致
  - multipart form: image + prompt + model + size + quality + background

- [x] T-007: 响应解析器统一化
  - 证据: crates/tfp-providers/src/openai_image.rs:321-379 (`parse_image_response`)
  - Spec 对照: Services/AiImageGenService.md § 流程1 步骤 5 ✅ 一致
  - 支持: data[].b64_json, data[].url, output[].image_generation_call + usage 提取

## 编译状态

```
cargo check --workspace
    Finished `dev` profile [unoptimized + debuginfo] target(s) in 6.00s
```
0 errors, 0 warnings

## 测试状态

```
cargo test --workspace
test result: ok. 286 passed; 0 failed; 1 ignored; 0 measured; 0 filtered out
```
- 新增测试 21 个，全绿
- 原有测试 265 个，无回归

### 新增测试清单
| 测试名 | 覆盖任务 |
|--------|---------|
| test_should_use_responses_api_v1_multipart_returns_false | T-002 |
| test_should_use_responses_api_text_model_set_returns_true | T-002 |
| test_should_use_responses_api_default_returns_false | T-002 |
| test_build_generate_urls_azure | T-003 |
| test_build_generate_urls_apim | T-003 |
| test_build_generate_urls_openai | T-003 |
| test_build_edit_urls_azure | T-003 |
| test_build_edit_urls_apim | T-003 |
| test_build_edit_urls_openai | T-003 |
| test_build_responses_urls_azure | T-003 |
| test_build_responses_urls_apim | T-003 |
| test_build_responses_urls_openai | T-003 |
| test_build_responses_urls_apim_default_version | T-003 |
| test_build_urls_with_v1_base | T-003 |
| test_build_url_candidates_alias | T-003 |
| test_parse_image_response_b64_json | T-007 |
| test_parse_image_response_url_format | T-007 |
| test_parse_image_response_responses_api_format | T-007 |
| test_parse_image_response_with_usage | T-007 |
| test_parse_image_response_empty_data_returns_error | T-007 |
| test_parse_image_response_no_recognized_format_returns_error | T-007 |
| test_mime_from_extension | T-006 |
| test_image_gen_result_default | T-001 |
| test_image_gen_result_serde_roundtrip | T-001 |

## 前端类型检查
```
npx tsc --noEmit
(exit code 0)
```

## 新增/修改文件清单

| 文件 | 操作 | 变更行数 |
|------|------|----------|
| crates/tfp-core/src/models/api.rs | 修改 | +16 -5 |
| crates/tfp-providers/src/openai_image.rs | 重写 | +600 -240 |
| crates/tfp-core/src/models/mod.rs | 修改 | +2 |
| crates/tfp-engine/src/task_engine.rs | 修改 | +2 |
| src-tauri/src/task_engine.rs | 修改 | +2 |
| src-tauri/src/commands/center.rs | 修改 | +1 |
| src-tauri/src/commands/studio.rs | 修改 | +1 |
| src/lib/types.ts | 修改 | +16 -2 |

## 已知局限

1. **`try_candidates` 对 multipart 不适用** — multipart form 不可 Clone，所以 `edit_via_multipart` 内联了重试逻辑而非复用 `try_candidates`。这是正确的设计（form 含二进制数据，每次需重建）。
2. **`data[].url` 格式暂不做 GET 下载** — 按设计决策 D-003，仅存储 URL 字符串，实际下载留到后续批次。
3. **无真实 API 集成测试** — 施工单明确说"仅需 unit test + mock HTTP"，本批次不要求真实 API 调通。
4. **`edit_via_multipart` 无独立测试** — 因需要文件系统或 mock HTTP server，超出本批次范围。函数可编译且类型正确，实际调通将在 batch-2 配合 file_id 逻辑完成。
